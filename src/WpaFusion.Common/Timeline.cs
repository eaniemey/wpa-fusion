namespace WpaFusion.Common;

/// <summary>
/// The time frame every WPA-Fusion plugin reports in. With a session.json that carries the ETL's
/// clock reference, time zero is the ETL's own zero in WPA, both as QPC and as UTC, so plugin rows
/// line up with ETW events whether WPA aligns sources by relative time or by wall clock.
/// </summary>
public sealed class Timeline
{
    private Timeline(ClockMap clocks, long zeroQpc, long zeroFileTimeUtc, TimelineOrigin origin, SessionManifest? manifest)
    {
        Clocks = clocks;
        ZeroQpc = zeroQpc;
        ZeroFileTimeUtc = zeroFileTimeUtc;
        Origin = origin;
        Manifest = manifest;
    }

    public ClockMap Clocks { get; }

    /// <summary>QPC value of time zero.</summary>
    public long ZeroQpc { get; }

    /// <summary>UTC wall clock of time zero, FILETIME ticks.</summary>
    public long ZeroFileTimeUtc { get; }

    public TimelineOrigin Origin { get; }

    public SessionManifest? Manifest { get; }

    public DateTime ZeroUtc => DateTime.FromFileTimeUtc(ZeroFileTimeUtc);

    /// <summary>Builds the timeline from a session manifest.</summary>
    public static Timeline FromManifest(SessionManifest manifest)
    {
        var clocks = ClockMap.FromManifest(manifest);
        if (manifest.Etl is { } etl)
        {
            return new Timeline(clocks, etl.ZeroQpc, etl.ZeroFileTimeUtc, TimelineOrigin.EtlZero, manifest);
        }

        var start = manifest.SyncStart!;
        return new Timeline(clocks, start.Qpc, start.FileTimeUtc, TimelineOrigin.SessionStart, manifest);
    }

    /// <summary>Loads session.json next to <paramref name="dataFile"/>, or returns null if there is none.</summary>
    public static Timeline? TryForDataFile(string dataFile)
    {
        var path = SessionManifest.FindFor(dataFile);
        return path is null ? null : FromManifest(SessionManifest.Load(path));
    }

    /// <summary>
    /// Returns a timeline whose TSC conversions expect CPU-0 TSC values. The sync points' TSC reads
    /// are shifted by the offset of the CPU they ran on (offset = TSC on CPU N minus TSC on CPU 0).
    /// </summary>
    public Timeline WithCpu0Tsc(Func<int, long> tscOffsetOfCpu)
    {
        var m = Manifest ?? throw new InvalidOperationException("Timeline has no manifest.");
        SyncPoint Adjust(SyncPoint p) => p with { Tsc = (ulong)((long)p.Tsc - tscOffsetOfCpu(p.Cpu ?? 0)) };
        var clocks = new ClockMap(Adjust(m.SyncStart!), Adjust(m.SyncEnd!), m.QpcFrequency);
        return new Timeline(clocks, ZeroQpc, ZeroFileTimeUtc, Origin, m);
    }

    /// <summary>Nanoseconds from time zero for a QPC value.</summary>
    public long QpcToNanoseconds(double qpc) => (long)Math.Round(Clocks.QpcToNanosecondsFrom(qpc, ZeroQpc));

    /// <summary>Nanoseconds from time zero for a CPU-0-relative TSC value.</summary>
    public long TscToNanoseconds(ulong tsc) => QpcToNanoseconds(Clocks.TscToQpc(tsc));

    /// <summary>Nanoseconds from time zero for a UTC wall-clock time (FILETIME ticks).</summary>
    public long FileTimeToNanoseconds(long fileTimeUtc) => QpcToNanoseconds(Clocks.FileTimeToQpc(fileTimeUtc));

    /// <summary>UTC wall clock of a timeline position, for DataSourceInfo.</summary>
    public DateTime NanosecondsToUtc(long nanoseconds) => DateTime.FromFileTimeUtc(ZeroFileTimeUtc + (nanoseconds / 100));
}

/// <summary>What time zero refers to.</summary>
public enum TimelineOrigin
{
    /// <summary>The ETL's time zero in WPA, read back from the trace by wpafusion-capture.</summary>
    EtlZero,

    /// <summary>The session's start sync point (no ETL was captured).</summary>
    SessionStart,
}
