using System.Runtime.CompilerServices;
using WpaFusion.Common;

namespace WpaFusion.Capture.Clocks;

/// <summary>
/// Takes TSC/QPC/UTC sync points. QPC ticks at 10 MHz (100 ns), so each point is aligned to a QPC
/// edge: spin until QPC increments and take the TSC midway between the last read of the old value
/// and the first read of the new one. That pins the TSC-QPC relation to a few tens of nanoseconds
/// instead of the 100 ns QPC quantum.
/// </summary>
internal static class SyncSampler
{
    public const int DefaultAttempts = 16;
    private const int SurveyAttempts = 4;

    public static SyncPoint Capture(int attempts = DefaultAttempts)
    {
        // Pin to one CPU so all rdtsc reads come from the same TSC.
        using var affinity = ThreadAffinity.PinToCurrentProcessor();
        return Best(attempts) with { Cpu = affinity.Cpu };
    }

    /// <summary>
    /// Takes an edge-aligned point on every logical CPU, so per-CPU TSC offsets can be measured
    /// directly against QPC (which is global) instead of trusting SEP's estimates.
    /// </summary>
    public static List<SyncPoint> SurveyAllCpus()
    {
        var points = new List<SyncPoint>();
        foreach (var (group, number, flat) in ThreadAffinity.AllProcessors())
        {
            using var pin = ThreadAffinity.PinTo(group, number, flat);
            points.Add(Best(SurveyAttempts) with { Cpu = flat });
        }

        return points;
    }

    /// <summary>
    /// An edge-aligned point on the CPU the caller is already pinned to. The first attempts also
    /// warm caches, so the reads are not delayed by cold misses (which cost ~1 us on single reads).
    /// </summary>
    public static SyncPoint OnPinnedCpu(int attempts = 3) => Best(attempts);

    private static SyncPoint Best(int attempts)
    {
        SyncPoint? best = null;
        for (var i = 0; i < attempts; i++)
        {
            var point = CaptureOne();
            if (best is null || point.TscSpread < best.TscSpread)
            {
                best = point;
            }
        }

        return best!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SyncPoint CaptureOne()
    {
        // Find a QPC edge: the previous read saw q0, this read sees q1 > q0.
        var prevBefore = NativeClock.ReadTsc();
        var q0 = NativeClock.ReadQpc();
        var prevAfter = NativeClock.ReadTsc();
        ulong before, after;
        long q1;
        while (true)
        {
            before = NativeClock.ReadTsc();
            q1 = NativeClock.ReadQpc();
            after = NativeClock.ReadTsc();
            if (q1 != q0)
            {
                break;
            }

            prevBefore = before;
            prevAfter = after;
        }

        var ft = NativeClock.ReadFileTimeUtc();

        // The edge lies between the midpoints of the last q0 read and the first q1 read.
        var lastOld = prevBefore + ((prevAfter - prevBefore) / 2);
        var firstNew = before + ((after - before) / 2);
        return new SyncPoint(lastOld + ((firstNew - lastOld) / 2), q1, ft, firstNew - lastOld);
    }
}
