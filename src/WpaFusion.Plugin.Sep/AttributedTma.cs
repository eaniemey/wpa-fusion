using WpaFusion.Tma;

namespace WpaFusion.Plugin.Sep;

/// <summary>Identity of a sampled TMA bucket at process, module, or function scope.</summary>
public sealed record TmaAttributionKey(string Scope, uint Pid, long ProcessStartNs, string Module, string Function, string ProcessName = "");

/// <summary>One attributed TMA result, either for a time slice or for the whole run.</summary>
public sealed record AttributedTmaWindow(TmaAttributionKey Key, bool IsSummary, TmaWindow Window);

/// <summary>Re-evaluates core TMA formulas from each entity's sampled event counts, never from percentages.</summary>
public static class AttributedTma
{
    /// <summary>Conservative sparse-bucket guard, not a statistical confidence guarantee.</summary>
    public const int MinimumSamples = 100;

    public static IReadOnlyList<AttributedTmaWindow> Compute(
        SepData data, TmaMetricSet metrics, EventCatalog catalog, MultiplexScales scales,
        TmaConstants constants, long sliceNs)
    {
        if (sliceNs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceNs));
        }

        var buckets = new Dictionary<(TmaAttributionKey Key, int Slice), Dictionary<int, long>>();
        for (var i = 0; i < data.Count; i++)
        {
            var location = data.Attribution is { } map ? map.Locations[map.SampleLocations[i]] : null;
            var module = location?.Module ?? "[Attribution not generated]";
            var function = location?.Function ?? "[Attribution not generated]";
            var slice = checked((int)((data.TimeNs[i] - data.FirstNs) / sliceNs));
            foreach (var scope in Scopes)
            {
                var key = new TmaAttributionKey(scope, data.Pid[i], location?.ProcessStartNs ?? long.MinValue,
                    scope == "Process" ? string.Empty : module, scope == "Function" ? function : string.Empty,
                    location?.ProcessName ?? string.Empty);
                Add(key, -1, data.EventIndex[i]);
                Add(key, slice, data.EventIndex[i]);
            }
        }

        void Add(TmaAttributionKey key, int slice, int ev)
        {
            if (!buckets.TryGetValue((key, slice), out var counts))
            {
                counts = new Dictionary<int, long>();
                buckets.Add((key, slice), counts);
            }

            counts[ev] = counts.GetValueOrDefault(ev) + 1;
        }

        var results = new List<AttributedTmaWindow>(buckets.Count);
        foreach (var (bucket, samples) in buckets)
        {
            var counts = new EventCounts(catalog);
            for (var e = 0; e < data.Events.Count; e++)
            {
                var scale = scales.PerEvent[e];
                if (!double.IsNaN(scale))
                {
                    counts.Add(data.Events[e].Name, samples.GetValueOrDefault(e) * (double)data.Events[e].SampleAfterValue * scale);
                }
            }

            var summary = bucket.Slice == -1;
            var sampleCount = samples.Values.Sum();
            var start = summary ? data.FirstNs : data.FirstNs + bucket.Slice * sliceNs;
            var duration = Math.Max(1, summary ? data.LastNs - start : Math.Min(sliceNs, data.LastNs - start));
            var evaluated = TmaEvaluator.EvaluateAll(metrics, counts, constants).Select(result =>
            {
                if (bucket.Key.Scope != "Process" &&
                    (bucket.Key.Module.StartsWith("[", StringComparison.Ordinal) ||
                     bucket.Key.Scope == "Function" && bucket.Key.Function.StartsWith("[", StringComparison.Ordinal)))
                {
                    return new TmaResult(result.Node, double.NaN, new[] { "module/function attribution unavailable" });
                }

                var node = result.Node;
                var unsupported = node.EventsByAlias.Values.Any(e =>
                    e.StartsWith("UNC", StringComparison.OrdinalIgnoreCase)) ||
                    node.ConstantsByAlias.Values.Any(c =>
                        c.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                        c.Contains("DURATION", StringComparison.OrdinalIgnoreCase) ||
                        c.Contains("SOCKET", StringComparison.OrdinalIgnoreCase));
                if (unsupported)
                {
                    return new TmaResult(node, double.NaN, new[] { "metric not supported at attributed scope" });
                }

                if (!result.IsAvailable)
                {
                    return result;
                }

                if (sampleCount < MinimumSamples)
                {
                    return new TmaResult(node, double.NaN,
                        new[] { $"insufficient supporting samples (<{MinimumSamples})" });
                }

                if (!double.IsFinite(result.Value))
                {
                    return result with { MissingInputs = new[] { "insufficient sampled inputs or zero denominator" } };
                }

                if (result.Value < 0 || result.Value > 100)
                {
                    return new TmaResult(node, double.NaN,
                        new[] { "estimate outside 0..100%; sparse or biased sampling" });
                }

                return result;
            }).ToArray();
            var window = new TmaWindow(start, duration, sampleCount, evaluated)
            {
                EstimatedCpuUsagePercent = data.CpuUsage.ForSampleCount(
                    data.CpuUsage.EventIndex < 0 ? 0 : samples.GetValueOrDefault(data.CpuUsage.EventIndex), duration),
            };
            results.Add(new AttributedTmaWindow(bucket.Key, summary, window));
        }

        return results;
    }

    private static readonly string[] Scopes = { "Process", "Module", "Function" };
}
