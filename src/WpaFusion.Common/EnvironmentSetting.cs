namespace WpaFusion.Common;

/// <summary>Reads WPA_FUSION_* settings, falling back to the PERFSCOPE_* names used before the rename.</summary>
public static class EnvironmentSetting
{
    public const string Prefix = "WPA_FUSION_";
    public const string LegacyPrefix = "PERFSCOPE_";

    /// <summary>Value of <paramref name="name"/> (a WPA_FUSION_* name), else its PERFSCOPE_* alias; null if neither is set.</summary>
    public static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value) || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(LegacyPrefix + name.Substring(Prefix.Length));
    }
}
