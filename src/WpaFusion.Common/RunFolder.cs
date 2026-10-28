using System.Globalization;

namespace WpaFusion.Common;

/// <summary>
/// Creates the per-run artifact folder output/&lt;yyyyMMdd-HHmmss&gt;/ (see DECISIONS.md).
/// A run never reuses an existing folder.
/// </summary>
public static class RunFolder
{
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Default output root: the "output" folder of the repository containing the running binary
    /// (found by walking up to WpaFusion.sln), else "output" under the current directory.
    /// </summary>
    public static string DefaultOutputRoot(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WpaFusion.sln")))
            {
                return Path.Combine(dir.FullName, "output");
            }

            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "output");
    }

    /// <summary>Creates a new run folder named for <paramref name="localTime"/>. Throws if it exists.</summary>
    public static string Create(string outputRoot, DateTime localTime)
    {
        var name = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(outputRoot, name);
        if (Directory.Exists(path))
        {
            throw new IOException($"Run folder '{path}' already exists; refusing to reuse it.");
        }

        Directory.CreateDirectory(path);
        return path;
    }
}
