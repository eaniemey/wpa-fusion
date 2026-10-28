namespace WpaFusion.Common;

/// <summary>
/// One simultaneous reading of the three clocks: TSC (rdtsc), QPC (QueryPerformanceCounter) and
/// precise UTC wall clock (GetSystemTimePreciseAsFileTime, as FILETIME ticks).
/// </summary>
/// <param name="Tsc">TSC at the midpoint of the read window.</param>
/// <param name="Qpc">QPC ticks.</param>
/// <param name="FileTimeUtc">UTC wall clock in 100 ns FILETIME ticks.</param>
/// <param name="TscSpread">TSC ticks between the rdtsc reads that bracket the QPC and wall clock reads; the uncertainty of this point.</param>
public sealed record SyncPoint(ulong Tsc, long Qpc, long FileTimeUtc, ulong TscSpread)
{
    public DateTime Utc => DateTime.FromFileTimeUtc(FileTimeUtc);

    /// <summary>Logical CPU (group * 64 + number) the reads ran on; null in older manifests.</summary>
    public int? Cpu { get; init; }
}
