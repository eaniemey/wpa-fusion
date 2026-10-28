namespace WpaFusion.Tma;

/// <summary>A sampled event as configured for collection.</summary>
/// <param name="Name">Event name in SEP spelling.</param>
/// <param name="SampleAfterValue">Events per sample.</param>
/// <param name="Group">Multiplexing group; group 0 holds always-on (fixed counter) events.</param>
public sealed record SampledEvent(string Name, ulong SampleAfterValue, int Group);

/// <summary>
/// Estimates how long each multiplexing group was active, so sample counts of multiplexed events
/// can be scaled to whole-run estimates.
/// </summary>
/// <remarks>
/// SEP rotates programmable event groups per CPU, triggered by unhalted cycles, so busy CPUs rotate
/// often and idle CPUs rarely. A group's share of runtime is therefore not 1/groups. For each CPU,
/// the group active at any instant is inferred from the group of the nearest programmable samples
/// (switching at the midpoint between samples of different groups). Each sample of the reference
/// event (always-on unhalted cycles) is attributed to the group active at its time; a group's share
/// is its fraction of reference samples, and its scale is 1 / share.
/// </remarks>
public static class MultiplexEstimator
{
    public const string DefaultReferenceEvent = "CPU_CLK_UNHALTED.THREAD";

    /// <summary>
    /// Computes the scale factor for each event: 1 for group 0, 1/share for multiplexed groups,
    /// and NaN for groups that never became active (their counts cannot be estimated).
    /// </summary>
    /// <param name="events">Event definitions indexed by event index.</param>
    /// <param name="cpu">Per-sample CPU.</param>
    /// <param name="tsc">Per-sample timestamp (any monotonic per-CPU clock).</param>
    /// <param name="eventIndex">Per-sample event index.</param>
    /// <param name="referenceEvent">Always-on event used to measure group activity.</param>
    public static MultiplexScales Estimate(
        IReadOnlyList<SampledEvent> events,
        ReadOnlySpan<ushort> cpu,
        ReadOnlySpan<ulong> tsc,
        ReadOnlySpan<ushort> eventIndex,
        string referenceEvent = DefaultReferenceEvent)
    {
        var groups = events.Where(e => e.Group > 0).Select(e => e.Group).Distinct().OrderBy(g => g).ToArray();
        var perEvent = new double[events.Count];
        // At most one programmable group cannot be time-multiplexed, so there is nothing to scale.
        if (groups.Length <= 1)
        {
            Array.Fill(perEvent, 1.0);
            return new MultiplexScales(perEvent, new Dictionary<int, double>(), 0, 0);
        }

        var reference = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Group == 0 && string.Equals(events[i].Name, referenceEvent, StringComparison.OrdinalIgnoreCase))
            {
                reference = i;
            }
        }

        if (reference < 0)
        {
            throw new InvalidOperationException($"Multiplexed collection has no always-on '{referenceEvent}' samples to measure group activity.");
        }

        // Order samples by (cpu, tsc).
        var order = new int[cpu.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var cpuArr = cpu.ToArray();
        var tscArr = tsc.ToArray();
        Array.Sort(order, (a, b) =>
        {
            var c = cpuArr[a].CompareTo(cpuArr[b]);
            return c != 0 ? c : tscArr[a].CompareTo(tscArr[b]);
        });

        var groupOfEvent = events.Select(e => e.Group).ToArray();
        var refPerGroup = new Dictionary<int, long>();
        long attributed = 0, unattributed = 0;

        var start = 0;
        while (start < order.Length)
        {
            var end = start;
            var thisCpu = cpuArr[order[start]];
            while (end < order.Length && cpuArr[order[end]] == thisCpu)
            {
                end++;
            }

            // Programmable samples on this CPU in time order.
            var progIdx = new List<int>();
            for (var k = start; k < end; k++)
            {
                if (groupOfEvent[eventIndex[order[k]]] > 0)
                {
                    progIdx.Add(order[k]);
                }
            }

            var p = 0;
            for (var k = start; k < end; k++)
            {
                var s = order[k];
                if (eventIndex[s] != reference)
                {
                    continue;
                }

                if (progIdx.Count == 0)
                {
                    unattributed++;
                    continue;
                }

                // Advance p to the last programmable sample at or before this time.
                while (p + 1 < progIdx.Count && tscArr[progIdx[p + 1]] <= tscArr[s])
                {
                    p++;
                }

                int group;
                var prev = progIdx[p];
                if (tscArr[prev] > tscArr[s])
                {
                    group = groupOfEvent[eventIndex[prev]]; // Before the first programmable sample.
                }
                else if (p + 1 < progIdx.Count)
                {
                    var next = progIdx[p + 1];
                    var gPrev = groupOfEvent[eventIndex[prev]];
                    var gNext = groupOfEvent[eventIndex[next]];
                    var mid = tscArr[prev] + ((tscArr[next] - tscArr[prev]) / 2);
                    group = gPrev == gNext || tscArr[s] <= mid ? gPrev : gNext;
                }
                else
                {
                    group = groupOfEvent[eventIndex[prev]]; // After the last programmable sample.
                }

                refPerGroup[group] = refPerGroup.TryGetValue(group, out var n) ? n + 1 : 1;
                attributed++;
            }

            start = end;
        }

        var scaleByGroup = new Dictionary<int, double>();
        foreach (var g in groups)
        {
            var share = attributed == 0 ? 0 : (double)refPerGroup.GetValueOrDefault(g) / attributed;
            scaleByGroup[g] = share > 0 ? 1.0 / share : double.NaN;
        }

        for (var i = 0; i < events.Count; i++)
        {
            perEvent[i] = events[i].Group == 0 ? 1.0 : scaleByGroup[events[i].Group];
        }

        return new MultiplexScales(perEvent, scaleByGroup, attributed, unattributed);
    }
}

/// <summary>Result of <see cref="MultiplexEstimator.Estimate"/>.</summary>
/// <param name="PerEvent">Scale factor by event index.</param>
/// <param name="PerGroup">Scale factor by multiplexed group.</param>
/// <param name="ReferenceSamplesAttributed">Reference samples assigned to a group.</param>
/// <param name="ReferenceSamplesUnattributed">Reference samples on CPUs with no programmable samples.</param>
public sealed record MultiplexScales(
    IReadOnlyList<double> PerEvent,
    IReadOnlyDictionary<int, double> PerGroup,
    long ReferenceSamplesAttributed,
    long ReferenceSamplesUnattributed);
