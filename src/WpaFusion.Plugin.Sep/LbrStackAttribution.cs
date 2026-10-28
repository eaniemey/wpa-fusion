using System.Text.Json;

namespace WpaFusion.Plugin.Sep;

/// <summary>
/// Call stacks recovered from SEP's own LBR data, resolved offline against the matching ETL. One stack id per
/// SEP sample in the original TBRW order, so the plugin needs no join.
/// </summary>
public sealed class LbrStackAttribution
{
    public const string Suffix = ".lbrstacks.json";

    public int Version { get; init; } = 1;
    public string Tb7Sha256 { get; init; } = string.Empty;

    /// <summary>Unique frames (module, function).</summary>
    public SampleLocation[] Frames { get; init; } = Array.Empty<SampleLocation>();

    /// <summary>Unique stacks as frame indices, leaf first (sample IP, then caller call sites).</summary>
    public int[][] Stacks { get; init; } = Array.Empty<int[]>();

    /// <summary>Index into <see cref="Stacks"/> per SEP sample.</summary>
    public int[] StackId { get; init; } = Array.Empty<int>();

    public static LbrStackAttribution? Load(string tb7, int sampleCount)
    {
        var path = tb7 + Suffix;
        if (!File.Exists(path))
        {
            return null;
        }

        using var file = File.OpenRead(path);
        var map = JsonSerializer.Deserialize<LbrStackAttribution>(file)
            ?? throw new InvalidDataException($"Empty LBR stack map: {path}");
        if (!map.IsValid(sampleCount) || map.Tb7Sha256 != SampleAttribution.Hash(tb7))
        {
            throw new InvalidDataException($"Invalid or mismatched LBR stack map: {path}");
        }

        return map;
    }

    private bool IsValid(int sampleCount) =>
        Version == 1 && Frames is not null && Stacks is not null && StackId is not null &&
        StackId.Length == sampleCount &&
        Frames.All(f => f is not null && !string.IsNullOrWhiteSpace(f.Module) && !string.IsNullOrWhiteSpace(f.Function)) &&
        Stacks.All(s => s is not null && s.Length > 0 && s.All(f => f >= 0 && f < Frames.Length)) &&
        StackId.All(s => s >= 0 && s < Stacks.Length);
}
