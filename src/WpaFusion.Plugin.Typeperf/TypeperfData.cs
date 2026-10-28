using WpaFusion.Common;
using WpaFusion.Plugin.Typeperf.Parsing;

namespace WpaFusion.Plugin.Typeperf;

/// <summary>
/// Typeperf counter data mapped onto the WPA-Fusion timeline: one row per non-blank (sample, counter) value.
/// Rows are ordered by sample time.
/// </summary>
public sealed class TypeperfData
{
    private TypeperfData(IReadOnlyList<TypeperfCounterPath> counters, long[] rowTimeNs, int[] rowCounter, double[] rowValue, long[] sampleTimeNs, DateTime zeroUtc, string timeSource, int skippedRows)
    {
        Counters = counters;
        RowTimeNs = rowTimeNs;
        RowCounter = rowCounter;
        RowValue = rowValue;
        SampleTimeNs = sampleTimeNs;
        ZeroUtc = zeroUtc;
        TimeSource = timeSource;
        SkippedRows = skippedRows;
    }

    public IReadOnlyList<TypeperfCounterPath> Counters { get; }

    public IReadOnlyList<long> RowTimeNs { get; }

    public IReadOnlyList<int> RowCounter { get; }

    public IReadOnlyList<double> RowValue { get; }

    /// <summary>Timeline position of each typeperf sample row.</summary>
    public IReadOnlyList<long> SampleTimeNs { get; }

    /// <summary>UTC wall clock of timeline zero.</summary>
    public DateTime ZeroUtc { get; }

    /// <summary>How times were mapped: via session.json or from the CSV's own clock.</summary>
    public string TimeSource { get; }

    public int SkippedRows { get; }

    public int RowCount => RowTimeNs.Count;

    private long[]? rowDurationNs;

    /// <summary>
    /// How long each value holds on the timeline: until the same counter's next value, or one
    /// sample interval for its last value. Lets WPA draw counters as continuous series.
    /// </summary>
    public IReadOnlyList<long> RowDurationNs => rowDurationNs ??= ComputeDurations();

    /// <summary>Median gap between typeperf samples; 1 s when there are fewer than two samples.</summary>
    public long SampleIntervalNs
    {
        get
        {
            var gaps = new List<long>();
            for (var i = 1; i < SampleTimeNs.Count; i++)
            {
                var gap = SampleTimeNs[i] - SampleTimeNs[i - 1];
                if (gap > 0)
                {
                    gaps.Add(gap);
                }
            }

            if (gaps.Count == 0)
            {
                return 1_000_000_000;
            }

            gaps.Sort();
            return gaps[gaps.Count / 2];
        }
    }

    private long[] ComputeDurations()
    {
        var interval = SampleIntervalNs;
        var durations = new long[RowCount];
        var nextTime = new Dictionary<int, long>();
        for (var i = RowCount - 1; i >= 0; i--)
        {
            var counter = RowCounter[i];
            var time = RowTimeNs[i];
            durations[i] = nextTime.TryGetValue(counter, out var next) && next > time ? next - time : interval;
            nextTime[counter] = time;
        }

        return durations;
    }

    /// <summary>
    /// Loads a typeperf CSV. With a timeline (from session.json), wall-clock sample times are mapped
    /// through it; otherwise time zero is the first sample and alignment relies on wall clock only.
    /// </summary>
    public static TypeperfData Load(string csvPath, Timeline? timeline)
    {
        var csv = TypePerfCsv.Load(csvPath);
        var sampleNs = new long[csv.SampleTimesUtc.Count];
        DateTime zeroUtc;
        string source;
        if (timeline is not null)
        {
            for (var i = 0; i < sampleNs.Length; i++)
            {
                sampleNs[i] = timeline.FileTimeToNanoseconds(csv.SampleTimesUtc[i]);
            }

            zeroUtc = timeline.ZeroUtc;
            source = timeline.Origin == TimelineOrigin.EtlZero ? "session.json (ETL time zero)" : "session.json (session start)";
        }
        else
        {
            var first = csv.SampleTimesUtc.Count > 0 ? csv.SampleTimesUtc[0] : 0;
            for (var i = 0; i < sampleNs.Length; i++)
            {
                sampleNs[i] = (csv.SampleTimesUtc[i] - first) * 100;
            }

            zeroUtc = DateTime.FromFileTimeUtc(first);
            source = "CSV wall clock (no session.json)";
        }

        var n = csv.Samples.Count;
        var rowTime = new long[n];
        var rowCounter = new int[n];
        var rowValue = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = csv.Samples[i];
            rowTime[i] = sampleNs[s.SampleIndex];
            rowCounter[i] = s.CounterIndex;
            rowValue[i] = s.Value;
        }

        return new TypeperfData(csv.Counters, rowTime, rowCounter, rowValue, sampleNs, zeroUtc, source, csv.SkippedRows);
    }

    /// <summary>Appends another file's rows (several CSVs opened together).</summary>
    public static TypeperfData Merge(IReadOnlyList<TypeperfData> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        var counters = new List<TypeperfCounterPath>();
        var time = new List<long>();
        var counter = new List<int>();
        var value = new List<double>();
        var samples = new List<long>();
        foreach (var p in parts)
        {
            var offset = counters.Count;
            counters.AddRange(p.Counters);
            time.AddRange(p.RowTimeNs);
            counter.AddRange(p.RowCounter.Select(c => c + offset));
            value.AddRange(p.RowValue);
            samples.AddRange(p.SampleTimeNs);
        }

        samples.Sort();
        return new TypeperfData(counters, time.ToArray(), counter.ToArray(), value.ToArray(), samples.ToArray(), parts[0].ZeroUtc,
            string.Join("; ", parts.Select(p => p.TimeSource).Distinct()), parts.Sum(p => p.SkippedRows));
    }
}
