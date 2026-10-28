using System.Security.Cryptography;
using System.Text.Json;

namespace WpaFusion.Plugin.Sep;

/// <summary>A resolved or explicitly unresolved sample location, optionally tied to a process instance.</summary>
public sealed record SampleLocation(string Module, string Function, long ProcessStartNs = long.MinValue, string ProcessName = "");

/// <summary>Offline ETL symbol lookup, indexed in the original TBRW sample order.</summary>
public sealed class SampleAttribution
{
    public const string Suffix = ".attribution.json";
    public int Version { get; init; } = 1;
    public string Tb7Sha256 { get; init; } = string.Empty;
    public SampleLocation[] Locations { get; init; } = Array.Empty<SampleLocation>();
    public int[] SampleLocations { get; init; } = Array.Empty<int>();

    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(file));
    }

    public static SampleAttribution? Load(string tb7, int sampleCount)
    {
        var path = tb7 + Suffix;
        if (!File.Exists(path))
        {
            return null;
        }

        using var file = File.OpenRead(path);
        var map = JsonSerializer.Deserialize<SampleAttribution>(file)
            ?? throw new InvalidDataException($"Empty attribution map: {path}");
        if (map.Version != 1 || map.Tb7Sha256 != Hash(tb7) ||
            map.SampleLocations is null || map.Locations is null ||
            map.SampleLocations.Length != sampleCount ||
            map.Locations.Any(l => l is null || string.IsNullOrWhiteSpace(l.Module) || string.IsNullOrWhiteSpace(l.Function)) ||
            map.SampleLocations.Any(i => i < 0 || i >= map.Locations.Length))
        {
            throw new InvalidDataException($"Invalid or mismatched attribution map: {path}");
        }

        return map;
    }
}
