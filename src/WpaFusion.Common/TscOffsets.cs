namespace WpaFusion.Common;

/// <summary>
/// Per-CPU TSC offsets (TSC on CPU N minus TSC on CPU 0 at the same instant), measured by
/// wpafusion-capture against QPC, which Windows keeps consistent across processors.
/// </summary>
public static class TscOffsets
{
    /// <summary>
    /// Offsets from the session's TSC survey, or null if the manifest has none. Each survey point is
    /// compared with the CPU 0 point, projected along the TSC/QPC rate of the session.
    /// </summary>
    public static IReadOnlyDictionary<int, long>? FromSurvey(SessionManifest manifest)
    {
        if (manifest.TscSurvey.Count == 0 || manifest.SyncStart is null || manifest.SyncEnd is null)
        {
            return null;
        }

        var reference = manifest.TscSurvey.FirstOrDefault(p => p.Cpu == 0) ?? manifest.TscSurvey[0];
        var tscPerQpc = (double)(manifest.SyncEnd.Tsc - manifest.SyncStart.Tsc) / (manifest.SyncEnd.Qpc - manifest.SyncStart.Qpc);
        var offsets = new Dictionary<int, long>();
        foreach (var p in manifest.TscSurvey)
        {
            if (p.Cpu is not int cpu)
            {
                continue;
            }

            var expected = (double)reference.Tsc + ((p.Qpc - reference.Qpc) * tscPerQpc);
            offsets[cpu] = (long)Math.Round((double)p.Tsc - expected);
        }

        return offsets;
    }

    /// <summary>Looks up an offset, 0 for unknown CPUs.</summary>
    public static long Of(this IReadOnlyDictionary<int, long> offsets, int cpu) => offsets.TryGetValue(cpu, out var o) ? o : 0;
}
