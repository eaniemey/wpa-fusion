namespace WpaFusion.Plugin.Sep;

/// <summary>Estimates system-normalized CPU usage from always-on SEP reference-cycle samples.</summary>
internal sealed class EstimatedCpuUsage
{
    public const string ReferenceEventName = "CPU_CLK_UNHALTED.REF_TSC";

    private readonly double _sampleAfterValue;
    private readonly double _tscFrequencyHz;
    private readonly int _logicalCpuCount;
    private readonly long _runDurationNs;

    private EstimatedCpuUsage(int eventIndex, double sampleAfterValue, double tscFrequencyHz, int logicalCpuCount,
        long runDurationNs, double[] perSamplePercent)
    {
        EventIndex = eventIndex;
        _sampleAfterValue = sampleAfterValue;
        _tscFrequencyHz = tscFrequencyHz;
        _logicalCpuCount = logicalCpuCount;
        _runDurationNs = runDurationNs;
        PerSamplePercent = perSamplePercent;
    }

    public int EventIndex { get; }

    public double[] PerSamplePercent { get; }

    public double ForSampleCount(long sampleCount, long durationNs) =>
        EventIndex < 0 ? double.NaN : Percent(sampleCount * _sampleAfterValue, durationNs);

    public double ForRunSampleCount(long sampleCount) => ForSampleCount(sampleCount, _runDurationNs);

    public double Percent(double referenceCycles, long durationNs)
    {
        if (EventIndex < 0 || !double.IsFinite(referenceCycles) || referenceCycles < 0 ||
            durationNs <= 0 || _tscFrequencyHz <= 0 || _logicalCpuCount <= 0)
        {
            return double.NaN;
        }

        var availableCycles = _tscFrequencyHz * (durationNs / 1e9) * _logicalCpuCount;
        return availableCycles > 0 ? referenceCycles * 100 / availableCycles : double.NaN;
    }

    public static EstimatedCpuUsage Create(SepData data)
    {
        var index = -1;
        for (var i = 0; i < data.Events.Count; i++)
        {
            var candidate = data.Events[i];
            if (candidate.GroupId == 0 && string.Equals(candidate.Name, ReferenceEventName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var duration = Math.Max(1, data.LastNs - data.FirstNs);
        var perSample = new double[data.Count];
        if (index < 0)
        {
            Array.Fill(perSample, double.NaN);
            return new EstimatedCpuUsage(-1, 0, data.TscFrequency, data.System.Cpus.Count, duration, perSample);
        }

        var eventInfo = data.Events[index];
        var estimate = new EstimatedCpuUsage(index, eventInfo.SampleAfterValue, data.TscFrequency,
            data.System.Cpus.Count, duration, perSample);
        var samplePercent = estimate.ForSampleCount(1, duration);
        for (var i = 0; i < perSample.Length; i++)
        {
            perSample[i] = data.EventIndex[i] == index ? samplePercent : 0;
        }

        return estimate;
    }
}