using WpaFusion.Common;
using WpaFusion.Tma;

namespace WpaFusion.Plugin.Sep;

/// <summary>TMA results for one time window.</summary>
/// <param name="StartNs">Window start on the timeline.</param>
/// <param name="DurationNs">Window length.</param>
/// <param name="Samples">Samples in the window.</param>
/// <param name="Results">One result per TMA node.</param>
public sealed record TmaWindow(long StartNs, long DurationNs, long Samples, IReadOnlyList<TmaResult> Results)
{
    public double EstimatedCpuUsagePercent { get; init; } = double.NaN;
}

/// <summary>
/// Top-down (TMA) metrics computed from a .tb7's samples: estimated event counts are
/// samples x sample-after value x multiplexing scale, evaluated with SEP EDP formulas for the collection platform.
/// </summary>
public sealed class TmaAnalysis
{
    public const string SliceEnvironmentVariable = "WPA_FUSION_TMA_SLICE_MS";
    public const long DefaultSliceNs = 1_000_000_000;

    private TmaAnalysis(string status)
    {
        Status = status;
    }

    /// <summary>Human-readable status: which metric file was used, or why TMA is unavailable.</summary>
    public string Status { get; private set; }

    public bool IsAvailable => Metrics is not null;

    public TmaMetricSet? Metrics { get; private set; }

    public MultiplexScales? Scales { get; private set; }

    /// <summary>Whole-run results.</summary>
    public TmaWindow? Summary { get; private set; }

    /// <summary>Results per time slice.</summary>
    public IReadOnlyList<TmaWindow> Slices { get; private set; } = Array.Empty<TmaWindow>();

    private Lazy<IReadOnlyList<AttributedTmaWindow>>? _attributed;

    public IReadOnlyList<AttributedTmaWindow> Attributed => _attributed?.Value ?? Array.Empty<AttributedTmaWindow>();

    /// <summary>
    /// Multiplexing check (Info_System_MUX): estimated programmable unhalted cycles over
    /// fixed-counter unhalted cycles. Near 1 means group scaling is sound; NaN if not measurable.
    /// </summary>
    public double MultiplexCheck { get; private set; } = double.NaN;

    public static long SliceNsFromEnvironment()
    {
        var text = EnvironmentSetting.Get(SliceEnvironmentVariable);
        return long.TryParse(text, out var ms) && ms > 0 ? ms * 1_000_000 : DefaultSliceNs;
    }

    /// <summary>
    /// Evaluates the SEP EDP metrics of the platform the run was collected on: the platform in session.json,
    /// else the local SEP's platform for the .tb7's CPUID signature.
    /// </summary>
    public static TmaAnalysis Compute(SepData data, long sliceNs)
    {
        var sepRoot = SepInstall.Root;
        var platform = SepPlatform.Resolve(data.Manifest, data.System.CpuIdSignature);
        if (platform is null)
        {
            return new TmaAnalysis($"TMA unavailable: the SEP install at {sepRoot} does not know this CPU (CPUID 0x{data.System.CpuIdSignature:X}).");
        }

        var edp = platform.EdpMetricsPath(sepRoot);
        if (edp is null)
        {
            return new TmaAnalysis($"TMA unavailable: no SEP EDP metric file for PMU '{platform.Pmu}' under {Path.Combine(sepRoot, "config", "edp")}.");
        }

        return Compute(data, TmaMetricSet.LoadSepEdpXml(edp), $"SEP EDP ({Path.GetFileName(edp)}) for {platform.CpuName}", sliceNs);
    }

    /// <summary>Evaluates <paramref name="metrics"/> over the run; <paramref name="metricSource"/> is reported in <see cref="Status"/>.</summary>
    public static TmaAnalysis Compute(SepData data, TmaMetricSet metrics, string metricSource, long sliceNs)
    {
        var catalog = EventCatalog.Empty;
        var defs = data.Events.Select(e => new SampledEvent(e.Name, e.SampleAfterValue, e.GroupId)).ToList();
        var scales = MultiplexEstimator.Estimate(defs, data.Cpu, data.Tsc, data.EventIndex);

        var constantsBase = new TmaConstants
        {
            HyperThreadingOn = false,
            ThreadsPerCore = data.System.Cpus.Count == 0 ? 1 : data.System.Cpus.Max(c => c.HardwareThread) + 1,
            SocketCount = Math.Max(1, data.System.Cpus.Select(c => c.Package).Distinct().Count()),
            LogicalCpuCount = Math.Max(1, data.System.Cpus.Count),
            SystemTscFrequencyHz = data.TscFrequency,
        };

        // Count samples per (slice, event) in one pass.
        sliceNs = Math.Max(1_000_000, sliceNs);
        var first = data.FirstNs;
        var sliceCount = (int)Math.Max(1, ((data.LastNs - first) / sliceNs) + 1);
        var perSlice = new long[sliceCount, defs.Count];
        var totals = new long[defs.Count];
        var sliceSamples = new long[sliceCount];
        for (var i = 0; i < data.Count; i++)
        {
            var s = (int)((data.TimeNs[i] - first) / sliceNs);
            var e = data.EventIndex[i];
            perSlice[s, e]++;
            totals[e]++;
            sliceSamples[s]++;
        }

        EventCounts Counts(Func<int, long> samplesOf)
        {
            var counts = new EventCounts(catalog);
            for (var e = 0; e < defs.Count; e++)
            {
                var scale = scales.PerEvent[e];
                if (double.IsNaN(scale))
                {
                    continue; // Group never active: the event was not measured.
                }

                counts.Add(defs[e].Name, samplesOf(e) * (double)defs[e].SampleAfterValue * scale);
            }

            return counts;
        }

        var duration = Math.Max(1, data.LastNs - first);
        var totalCounts = Counts(e => totals[e]);
        var summary = new TmaWindow(first, duration, data.Count,
            TmaEvaluator.EvaluateAll(metrics, totalCounts, CopyWith(constantsBase, duration)))
        {
            EstimatedCpuUsagePercent = data.CpuUsage.ForSampleCount(
                data.CpuUsage.EventIndex < 0 ? 0 : totals[data.CpuUsage.EventIndex], duration),
        };

        var slices = new List<TmaWindow>(sliceCount);
        for (var s = 0; s < sliceCount; s++)
        {
            var slice = s;
            var start = first + (s * sliceNs);
            var length = Math.Min(sliceNs, Math.Max(1, data.LastNs - start));
            slices.Add(new TmaWindow(start, length, sliceSamples[s],
                TmaEvaluator.EvaluateAll(metrics, Counts(e => perSlice[slice, e]), CopyWith(constantsBase, length)))
            {
                EstimatedCpuUsagePercent = data.CpuUsage.ForSampleCount(
                    data.CpuUsage.EventIndex < 0 ? 0 : perSlice[slice, data.CpuUsage.EventIndex], length),
            });
        }

        var analysis = new TmaAnalysis($"{metricSource}; {metrics.Nodes.Count} nodes, {summary.Results.Count(r => r.IsAvailable)} computable; per-thread evaluation (smt_on = 0)")
        {
            Metrics = metrics,
            Scales = scales,
            Summary = summary,
            Slices = slices,
            _attributed = new Lazy<IReadOnlyList<AttributedTmaWindow>>(
                () => AttributedTma.Compute(data, metrics, catalog, scales, constantsBase, sliceNs)),
        };

        if (totalCounts.TryGet("CPU_CLK_UNHALTED.THREAD_P", out var threadP) && totalCounts.TryGet("CPU_CLK_UNHALTED.THREAD", out var thread) && thread > 0)
        {
            analysis.MultiplexCheck = threadP / thread;
        }

        return analysis;
    }

    private static TmaConstants CopyWith(TmaConstants c, long durationNs) => new()
    {
        HyperThreadingOn = c.HyperThreadingOn,
        ThreadsPerCore = c.ThreadsPerCore,
        SocketCount = c.SocketCount,
        LogicalCpuCount = c.LogicalCpuCount,
        SystemTscFrequencyHz = c.SystemTscFrequencyHz,
        DurationMilliseconds = durationNs / 1e6,
    };
}
