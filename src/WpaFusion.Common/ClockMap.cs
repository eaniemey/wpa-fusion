namespace WpaFusion.Common;

/// <summary>
/// Converts between TSC, QPC and UTC wall clock using the start and end sync points of a session.
/// Each conversion is a linear fit through the two points, which absorbs constant clock-rate
/// differences and wall-clock slew over the run. Allocation-free.
/// </summary>
public sealed class ClockMap
{
    private const double FileTimeTicksPerSecond = 10_000_000.0;
    private readonly SyncPoint _start;
    private readonly double _qpcPerTsc;
    private readonly double _fileTimePerQpc;

    public ClockMap(SyncPoint start, SyncPoint end, long qpcFrequency)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        }

        if (end.Tsc <= start.Tsc || end.Qpc <= start.Qpc)
        {
            throw new ArgumentException("End sync point must be after start sync point.", nameof(end));
        }

        _start = start;
        QpcFrequency = qpcFrequency;
        _qpcPerTsc = (double)(end.Qpc - start.Qpc) / (end.Tsc - start.Tsc);
        _fileTimePerQpc = (double)(end.FileTimeUtc - start.FileTimeUtc) / (end.Qpc - start.Qpc);
        TscFrequency = qpcFrequency / _qpcPerTsc;
    }

    public long QpcFrequency { get; }

    /// <summary>TSC ticks per second, derived from the sync points.</summary>
    public double TscFrequency { get; }

    public static ClockMap FromManifest(SessionManifest manifest) =>
        new(manifest.SyncStart!, manifest.SyncEnd!, manifest.QpcFrequency);

    /// <summary>Maps a CPU-0-relative TSC value to QPC ticks (fractional).</summary>
    public double TscToQpc(ulong tsc) => _start.Qpc + (((double)(long)(tsc - _start.Tsc)) * _qpcPerTsc);

    /// <summary>Maps QPC ticks to UTC FILETIME ticks (fractional).</summary>
    public double QpcToFileTime(double qpc) => _start.FileTimeUtc + ((qpc - _start.Qpc) * _fileTimePerQpc);

    /// <summary>Nanoseconds from <paramref name="originQpc"/> to <paramref name="qpc"/>.</summary>
    public double QpcToNanosecondsFrom(double qpc, long originQpc) => (qpc - originQpc) * 1e9 / QpcFrequency;

    /// <summary>Maps UTC FILETIME ticks to QPC ticks (fractional); inverse of <see cref="QpcToFileTime"/>.</summary>
    public double FileTimeToQpc(long fileTimeUtc) => _start.Qpc + ((fileTimeUtc - _start.FileTimeUtc) / _fileTimePerQpc);

    /// <summary>Converts a FILETIME tick count to whole nanoseconds.</summary>
    public static double FileTimeTicksToNanoseconds(double ticks) => ticks * (1e9 / FileTimeTicksPerSecond);
}
