using System.Text.Json;

namespace WpaFusion.Plugin.Sep;

/// <summary>
/// Offline join of ETW CPU samples (with stacks) and SEP samples: one row per ETW sample plus synthetic
/// rows for SEP samples that no ETW sample covers. Columnar so the sidecar stays compact.
/// </summary>
public sealed class SampledStackAttribution
{
    public const string Suffix = ".sampledstacks.json";
    public const int NoStack = -1;

    public int Version { get; init; } = 1;
    public string Tb7Sha256 { get; init; } = string.Empty;
    public long EtlLength { get; init; }
    public long EtlZeroFileTimeUtc { get; init; }

    /// <summary>Unique frames (module, function).</summary>
    public SampleLocation[] Frames { get; init; } = Array.Empty<SampleLocation>();

    /// <summary>Unique stacks as frame indices, leaf first.</summary>
    public int[][] Stacks { get; init; } = Array.Empty<int[]>();

    public long[] TimeNs { get; init; } = Array.Empty<long>();
    public int[] Cpu { get; init; } = Array.Empty<int>();
    public uint[] Pid { get; init; } = Array.Empty<uint>();
    public uint[] Tid { get; init; } = Array.Empty<uint>();

    /// <summary>Index into <see cref="Stacks"/>, or <see cref="NoStack"/> for rows with no ETW sample.</summary>
    public int[] StackId { get; init; } = Array.Empty<int>();

    /// <summary>Sampling interval the ETW sample represents; 0 for rows with no ETW sample.</summary>
    public long[] WeightNs { get; init; } = Array.Empty<long>();

    /// <summary>Sparse SEP sample counts: row, event index and number of samples, in parallel.</summary>
    public int[] CountRow { get; init; } = Array.Empty<int>();
    public int[] CountEvent { get; init; } = Array.Empty<int>();
    public int[] CountSamples { get; init; } = Array.Empty<int>();

    public int RowCount => TimeNs.Length;

    public static SampledStackAttribution? Load(string tb7, int eventCount, int sepSampleCount)
    {
        var path = tb7 + Suffix;
        if (!File.Exists(path))
        {
            return null;
        }

        using var file = File.OpenRead(path);
        var map = JsonSerializer.Deserialize<SampledStackAttribution>(file)
            ?? throw new InvalidDataException($"Empty sampled-stack map: {path}");
        if (!map.IsValid(eventCount, sepSampleCount) || map.Tb7Sha256 != SampleAttribution.Hash(tb7))
        {
            throw new InvalidDataException($"Invalid or mismatched sampled-stack map: {path}");
        }

        return map;
    }

    private bool IsValid(int eventCount, int sepSampleCount)
    {
        if (Version != 1 || Frames is null || Stacks is null || TimeNs is null || Cpu is null || Pid is null ||
            Tid is null || StackId is null || WeightNs is null || CountRow is null || CountEvent is null || CountSamples is null)
        {
            return false;
        }

        var rows = TimeNs.Length;
        if (Cpu.Length != rows || Pid.Length != rows || Tid.Length != rows || StackId.Length != rows || WeightNs.Length != rows ||
            CountEvent.Length != CountRow.Length || CountSamples.Length != CountRow.Length ||
            Frames.Any(f => f is null || string.IsNullOrWhiteSpace(f.Module) || string.IsNullOrWhiteSpace(f.Function)) ||
            Stacks.Any(s => s is null || s.Length == 0 || s.Any(f => f < 0 || f >= Frames.Length)) ||
            StackId.Any(s => s != NoStack && (s < 0 || s >= Stacks.Length)))
        {
            return false;
        }

        long total = 0;
        for (var i = 0; i < CountRow.Length; i++)
        {
            if (CountRow[i] < 0 || CountRow[i] >= rows || CountEvent[i] < 0 || CountEvent[i] >= eventCount || CountSamples[i] <= 0)
            {
                return false;
            }

            total += CountSamples[i];
        }

        return total == sepSampleCount;
    }
}
