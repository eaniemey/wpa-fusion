using WpaFusion.Common;
using WpaFusion.Tbrw;

namespace WpaFusion.Plugin.Sep;

/// <summary>
/// All samples of a .tb7 in compact parallel arrays (file order), mapped onto the WPA-Fusion timeline.
/// </summary>
public sealed class SepData
{
    private readonly Dictionary<uint, string> _processNames;
    private readonly Dictionary<(uint, uint), string> _threadNames;
    private readonly Lazy<EstimatedCpuUsage> _cpuUsage;

    private SepData(
        string path,
        IReadOnlyList<TbrwEventInfo> events,
        TbrwSystemInfo system,
        long[] timeNs,
        ulong[] tsc,
        ushort[] cpu,
        ushort[] eventIndex,
        uint[] pid,
        uint[] tid,
        ulong[] ip,
        Dictionary<uint, string> processNames,
        Dictionary<(uint, uint), string> threadNames,
        DateTime zeroUtc,
        string timeSource,
        double tscFrequency)
    {
        Path = path;
        Events = events;
        System = system;
        TimeNs = timeNs;
        Tsc = tsc;
        Cpu = cpu;
        EventIndex = eventIndex;
        Pid = pid;
        Tid = tid;
        Ip = ip;
        _processNames = processNames;
        _threadNames = threadNames;
        ZeroUtc = zeroUtc;
        TimeSource = timeSource;
        TscFrequency = tscFrequency;
        _cpuUsage = new Lazy<EstimatedCpuUsage>(() => EstimatedCpuUsage.Create(this));
    }

    public string Path { get; }

    public IReadOnlyList<TbrwEventInfo> Events { get; }

    public TbrwSystemInfo System { get; }

    /// <summary>Sample time on the timeline, nanoseconds.</summary>
    public long[] TimeNs { get; }

    /// <summary>CPU-0-normalized TSC of each sample.</summary>
    public ulong[] Tsc { get; }

    public ushort[] Cpu { get; }

    public ushort[] EventIndex { get; }

    public uint[] Pid { get; }

    public uint[] Tid { get; }

    public ulong[] Ip { get; }

    public int Count => TimeNs.Length;

    public SampleAttribution? Attribution { get; private init; }

    /// <summary>ETW CPU sample stacks joined with SEP samples; null when no sidecar exists.</summary>
    public SampledStackAttribution? SampledStacks { get; private init; }

    /// <summary>Call stacks from SEP's own LBR data, one id per sample; null when no sidecar exists.</summary>
    public LbrStackAttribution? LbrStacks { get; private init; }

    public DateTime ZeroUtc { get; }

    /// <summary>How sample times were mapped.</summary>
    public string TimeSource { get; }

    /// <summary>TSC ticks per second used for the mapping.</summary>
    public double TscFrequency { get; }

    internal EstimatedCpuUsage CpuUsage => _cpuUsage.Value;

    public long FirstNs { get; private init; }

    public long LastNs { get; private init; }

    /// <summary>session.json next to the .tb7, or null.</summary>
    public SessionManifest? Manifest { get; private init; }

    public string ProcessName(uint pid)
    {
        var name = _processNames.TryGetValue(pid, out var value) && value.Length > 0 ? value : "Unknown";
        return Manifest?.Alignment is { } alignment ? $"{name} [SEP run {alignment.SourceRunId}; cross-run aligned]" : name;
    }

    public string ThreadName(uint pid, uint tid) => _threadNames.TryGetValue((pid, tid), out var n) ? n : string.Empty;

    /// <summary>
    /// Loads a bound .tb7. With session.json next to it, TSC is mapped through the session's clock
    /// map (after removing per-CPU TSC offsets); otherwise the nominal CPU frequency is used and the
    /// file's write time anchors the wall clock (approximate, logged as such).
    /// </summary>
    public static SepData Load(string tb7Path, CancellationToken cancellationToken = default)
    {
        using var file = TbrwFile.Open(tb7Path);
        var system = file.SystemInfo;
        var timeline = Timeline.TryForDataFile(tb7Path);

        // Per-CPU TSC offsets: measured by wpafusion-capture against QPC when available, else zero.
        // SEP's own cpu_tsc_offset values are not used: on the reference machine they grow with CPU
        // number up to ~70 us (an artifact of visiting CPUs in turn) while measured offsets are < 0.1 us.
        var surveyed = timeline?.Manifest is { } manifest ? TscOffsets.FromSurvey(manifest) : null;
        IReadOnlyDictionary<int, long> offsets = surveyed ?? new Dictionary<int, long>();
        var offsetSource = surveyed is not null ? "TSC offsets measured at capture" : "TSC offsets assumed zero";
        long OffsetOf(int cpu) => offsets.Of(cpu);

        var capacity = (int)Math.Min(int.MaxValue / 2, new FileInfo(tb7Path).Length / 48);
        var tscList = new List<ulong>(capacity);
        var cpuList = new List<ushort>(capacity);
        var evList = new List<ushort>(capacity);
        var pidList = new List<uint>(capacity);
        var tidList = new List<uint>(capacity);
        var ipList = new List<ulong>(capacity);
        file.ReadSamples((in PmuSample s) =>
        {
            tscList.Add((ulong)((long)s.Tsc - OffsetOf(s.Cpu)));
            cpuList.Add(s.Cpu);
            evList.Add(s.EventIndex);
            pidList.Add(s.Pid);
            tidList.Add(s.Tid);
            ipList.Add(s.Ip);
        });
        cancellationToken.ThrowIfCancellationRequested();

        var tsc = tscList.ToArray();
        var timeNs = new long[tsc.Length];
        DateTime zeroUtc;
        string source;
        double tscFrequency;
        if (timeline is not null)
        {
            var cpu0 = timeline.WithCpu0Tsc(OffsetOf);
            for (var i = 0; i < tsc.Length; i++)
            {
                timeNs[i] = cpu0.TscToNanoseconds(tsc[i]);
            }

            zeroUtc = cpu0.ZeroUtc;
            tscFrequency = cpu0.Clocks.TscFrequency;
            source = (timeline.Origin == TimelineOrigin.EtlZero ? "session.json (ETL time zero)" : "session.json (session start)") + ", " + offsetSource;
        }
        else
        {
            tscFrequency = system.Cpus.Count > 0 ? system.Cpus.Max(c => c.SpeedMhz) * 1e6 : 1e9;
            var min = tsc.Length > 0 ? tsc.Min() : 0;
            var max = tsc.Length > 0 ? tsc.Max() : 0;
            for (var i = 0; i < tsc.Length; i++)
            {
                timeNs[i] = (long)((tsc[i] - min) * 1e9 / tscFrequency);
            }

            var spanTicks = (long)((max - min) / tscFrequency * TimeSpan.TicksPerSecond);
            zeroUtc = File.GetLastWriteTimeUtc(tb7Path).AddTicks(-spanTicks);
            source = "nominal CPU frequency (no session.json; wall clock approximate)";
        }

        if (timeline?.Manifest?.Alignment is { } alignment)
        {
            for (var index = 0; index < timeNs.Length; index++)
            {
                timeNs[index] = checked(timeNs[index] + alignment.OffsetNs);
            }

            zeroUtc = DateTime.FromFileTimeUtc(alignment.ReferenceZeroFileTimeUtc);
            source += $", cross-run workload-start alignment to {alignment.ReferenceRunId}";
        }

        var processNames = file.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        var threadNames = file.Threads.GroupBy(t => (t.Pid, t.Tid)).ToDictionary(g => g.Key, g => g.First().Name);
        // TBRW holds an exclusive file handle; release it before hashing the sidecar's input.
        file.Dispose();
        return new SepData(file.Path, file.Events, system, timeNs, tsc, cpuList.ToArray(), evList.ToArray(), pidList.ToArray(),
            tidList.ToArray(), ipList.ToArray(), processNames, threadNames, zeroUtc, source, tscFrequency)
        {
            FirstNs = timeNs.Length > 0 ? timeNs.Min() : 0,
            LastNs = timeNs.Length > 0 ? timeNs.Max() : 1,
            Manifest = timeline?.Manifest,
            Attribution = SampleAttribution.Load(file.Path, timeNs.Length),
            SampledStacks = SampledStackAttribution.Load(file.Path, file.Events.Count, timeNs.Length),
            LbrStacks = LbrStackAttribution.Load(file.Path, timeNs.Length),
        };
    }
}
