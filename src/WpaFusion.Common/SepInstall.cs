using System.Diagnostics;

namespace WpaFusion.Common;

/// <summary>Locates the pinned SEP install (see DECISIONS.md).</summary>
public static class SepInstall
{
    public const string EnvironmentVariable = "WPA_FUSION_SEP_ROOT";

    public const string DefaultRoot = @"C:\Program Files (x86)\IntelSWTools\sep_private_5.52_win_0302224644853d4c6";

    /// <summary>The TBRW version the struct layouts were ported and validated against.</summary>
    public const string ValidatedTbrwVersion = "3.52";

    private const string InstallParent = @"C:\Program Files (x86)\IntelSWTools";

    /// <summary>
    /// SEP root: WPA_FUSION_SEP_ROOT if set, else the pinned default if present, else the newest
    /// IntelSWTools\sep* folder that contains bin64\sep.exe (an unvalidated version; see
    /// <see cref="ValidatedTbrwVersion"/>), else the pinned default.
    /// </summary>
    public static string Root
    {
        get
        {
            var fromEnv = EnvironmentSetting.Get(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim().Trim('"');
            }

            if (Directory.Exists(DefaultRoot) || !Directory.Exists(InstallParent))
            {
                return DefaultRoot;
            }

            return Directory.GetDirectories(InstallParent, "sep*")
                .Where(d => File.Exists(Path.Combine(d, "bin64", "sep.exe")))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? DefaultRoot;
        }
    }

    public static string Bin64 => Path.Combine(Root, "bin64");

    public static string SepExe => Path.Combine(Bin64, "sep.exe");

    /// <summary>Runs sep.exe with read-only query arguments (e.g. -version, -pl); returns its output, or null if SEP is missing or fails.</summary>
    public static string? Query(string arguments)
    {
        if (!File.Exists(SepExe))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(SepExe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                p.Kill();
                return null;
            }

            return stdout + stderr.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
