namespace WpaFusion.Plugin.Sep;

/// <summary>One ETW CPU sample: when it fired, where, on which thread, and its stack.</summary>
public readonly record struct EtwCpuSample(long TimeNs, int Cpu, uint Pid, uint Tid, int StackId, long WeightNs);

/// <summary>SEP samples in parallel arrays, as in <see cref="SepData"/>.</summary>
public sealed record SepSampleSet(long[] TimeNs, ushort[] Cpu, uint[] Tid, uint[] Pid, ushort[] EventIndex, int EventCount);

/// <summary>Counts of matched, unmatched, and rejected SEP-to-ETW sample associations.</summary>
public sealed record SampledStackJoinStats(int Matched, int Unmatched, int NoCoveringSample, int ThreadMismatch, int OutOfInterval, long MedianGapNs);

/// <summary>
/// Assigns each SEP sample to the first ETW CPU sample at or after it on the same CPU (the ETW sample's
/// interval is the preceding <see cref="EtwCpuSample.WeightNs"/>), provided the thread matches. Samples with no
/// covering ETW sample get a synthetic row so per-event totals always equal the SEP totals.
/// </summary>
public static class SampledStackJoin
{
    public static SampledStackAttribution Join(
        IReadOnlyList<EtwCpuSample> etw,
        SepSampleSet sep,
        SampleLocation[] frames,
        int[][] stacks,
        string tb7Sha256,
        long etlLength,
        long etlZeroFileTimeUtc,
        out SampledStackJoinStats stats)
    {
        var order = Enumerable.Range(0, etw.Count).ToArray();
        Array.Sort(order, (a, b) =>
        {
            var c = etw[a].Cpu.CompareTo(etw[b].Cpu);
            return c != 0 ? c : etw[a].TimeNs.CompareTo(etw[b].TimeNs);
        });

        var cpuRange = new Dictionary<int, (int Start, int End)>();
        for (var i = 0; i < order.Length; i++)
        {
            var cpu = etw[order[i]].Cpu;
            cpuRange[cpu] = cpuRange.TryGetValue(cpu, out var r) ? (r.Start, i + 1) : (i, i + 1);
        }

        var timeNs = new List<long>(etw.Count);
        var cpus = new List<int>(etw.Count);
        var pids = new List<uint>(etw.Count);
        var tids = new List<uint>(etw.Count);
        var stackIds = new List<int>(etw.Count);
        var weights = new List<long>(etw.Count);
        foreach (var s in etw)
        {
            timeNs.Add(s.TimeNs);
            cpus.Add(s.Cpu);
            pids.Add(s.Pid);
            tids.Add(s.Tid);
            stackIds.Add(s.StackId);
            weights.Add(s.WeightNs);
        }

        var counts = new SortedDictionary<(int Row, int Event), int>();
        var matched = 0;
        var unmatched = 0;
        var noCover = 0;
        var mismatch = 0;
        var outOfInterval = 0;
        var gaps = new List<long>();
        for (var i = 0; i < sep.TimeNs.Length; i++)
        {
            var t = sep.TimeNs[i];
            var row = -1;
            if (cpuRange.TryGetValue(sep.Cpu[i], out var range))
            {
                var lo = range.Start;
                var hi = range.End;
                while (lo < hi)
                {
                    var mid = (lo + hi) >>> 1;
                    if (etw[order[mid]].TimeNs < t)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                if (lo < range.End)
                {
                    var gap = etw[order[lo]].TimeNs - t;
                    gaps.Add(gap);
                    if (gap > etw[order[lo]].WeightNs)
                    {
                        outOfInterval++;
                    }
                    else if (etw[order[lo]].Tid == sep.Tid[i])
                    {
                        row = order[lo];
                    }
                    else
                    {
                        mismatch++;
                    }
                }
                else
                {
                    noCover++;
                }
            }

            if (row < 0)
            {
                if (!cpuRange.ContainsKey(sep.Cpu[i]))
                {
                    noCover++;
                }

                row = timeNs.Count;
                timeNs.Add(t);
                cpus.Add(sep.Cpu[i]);
                pids.Add(sep.Pid[i]);
                tids.Add(sep.Tid[i]);
                stackIds.Add(SampledStackAttribution.NoStack);
                weights.Add(0);
                unmatched++;
            }
            else
            {
                matched++;
            }

            var key = (row, (int)sep.EventIndex[i]);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        gaps.Sort();
        stats = new SampledStackJoinStats(matched, unmatched, noCover, mismatch, outOfInterval, gaps.Count > 0 ? gaps[gaps.Count / 2] : 0);
        return new SampledStackAttribution
        {
            Tb7Sha256 = tb7Sha256,
            EtlLength = etlLength,
            EtlZeroFileTimeUtc = etlZeroFileTimeUtc,
            Frames = frames,
            Stacks = stacks,
            TimeNs = timeNs.ToArray(),
            Cpu = cpus.ToArray(),
            Pid = pids.ToArray(),
            Tid = tids.ToArray(),
            StackId = stackIds.ToArray(),
            WeightNs = weights.ToArray(),
            CountRow = counts.Keys.Select(k => k.Row).ToArray(),
            CountEvent = counts.Keys.Select(k => k.Event).ToArray(),
            CountSamples = counts.Values.ToArray(),
        };
    }
}
