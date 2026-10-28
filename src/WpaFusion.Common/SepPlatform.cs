using System.Globalization;
using System.Text.RegularExpressions;

namespace WpaFusion.Common;

/// <summary>
/// The SEP platform a run was collected on: SEP's platform type, PMU name and CPU name. Capture reads it
/// from "sep -version" on the collection machine; older runs resolve it from the .tb7 CPUID via "sep -pl".
/// </summary>
public sealed record SepPlatform(int PlatformType, string Pmu, string CpuName)
{
    private static readonly string[] EdpVariants = { string.Empty, "_nda", "_private" };

    private static readonly Regex PlatformListLine = new(
        @"Platform:\s*(\d+),\s*PMU:\s*([^,]+),\s*Signature:\s*0x([0-9a-fA-F]+),\s*CPU name:\s*(.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Lazy<string?> PlatformList = new(() => SepInstall.Query("-pl"));

    /// <summary>Parses "sep -version" output; null if it reports no PMU.</summary>
    public static SepPlatform? ParseVersion(string? output)
    {
        string? Field(string name)
        {
            var m = Regex.Match(output ?? string.Empty, $@"^\s*{Regex.Escape(name)}:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : null;
        }

        var pmu = Field("PMU");
        if (string.IsNullOrEmpty(pmu))
        {
            return null;
        }

        _ = int.TryParse(Field("Platform type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type);
        return new SepPlatform(type, pmu, Field("CPU name") ?? string.Empty);
    }

    /// <summary>
    /// Finds the platform for a CPUID leaf 1 signature in "sep -pl" output. List entries carry a stepping
    /// only where it distinguishes platforms (Cascade Lake 0x50655 vs Skylake server 0x50650), so the
    /// entry with the highest listed stepping not above the CPU's wins.
    /// </summary>
    public static SepPlatform? FromSignature(string? platformListOutput, uint cpuIdSignature)
    {
        var signature = cpuIdSignature & 0x0FFF0FFFu;
        return PlatformListLine.Matches(platformListOutput ?? string.Empty)
            .Select(m => (
                Signature: uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Platform: new SepPlatform(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value.Trim(), m.Groups[4].Value)))
            .Where(e => (e.Signature & ~0xFu) == (signature & ~0xFu) && (e.Signature & 0xFu) <= (signature & 0xFu))
            .OrderByDescending(e => e.Signature & 0xFu)
            .Select(e => e.Platform)
            .FirstOrDefault();
    }

    /// <summary>The platform recorded at capture, else the local SEP's entry for the captured CPUID signature.</summary>
    public static SepPlatform? Resolve(SessionManifest? manifest, uint cpuIdSignature) =>
        manifest?.Platform ?? FromSignature(PlatformList.Value, cpuIdSignature);

    /// <summary>SEP EDP metric file for this PMU under SEP_ROOT\config\edp (public, then nda, then private); null if none.</summary>
    public string? EdpMetricsPath(string sepRoot)
    {
        // The PMU name can come from session.json; only plain identifiers are used as file names.
        if (!Regex.IsMatch(Pmu, "^[A-Za-z0-9_]+$"))
        {
            return null;
        }

        return EdpVariants
            .Select(v => Path.Combine(sepRoot, "config", "edp", Pmu + v + ".xml"))
            .FirstOrDefault(File.Exists);
    }

    public override string ToString() => $"{CpuName} (SEP platform {PlatformType}, PMU {Pmu})";
}
