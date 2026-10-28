using System.Reflection;
using System.Text;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>Guards the default typeperf counter list embedded in wpafusion-capture.</summary>
public class CaptureCounterSetTests
{
    private const string ResourceName = "WpaFusion.Capture.typeperfinput.txt";

    private static byte[] ReadEmbedded()
    {
        // The capture app's entry assembly is referenced by the test project.
        var capture = Assembly.Load("wpafusion-capture");
        using var stream = capture.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string RepoFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpaFusion.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "WpaFusion.Capture", "Counters", "typeperfinput.txt");
    }

    [Fact]
    public void Embedded_counter_list_matches_repo_file()
    {
        Assert.Equal(File.ReadAllBytes(RepoFile()), ReadEmbedded());
    }

    [Fact]
    public void Embedded_counter_list_has_59_quoted_ascii_paths()
    {
        var bytes = ReadEmbedded();
        Assert.All(bytes, b => Assert.True(b < 0x80, "counter file must be plain ASCII"));

        var paths = Encoding.ASCII.GetString(bytes)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        Assert.Equal(59, paths.Count);
        Assert.All(paths, p => Assert.Matches("^\"\\\\[^\"]+\"$", p));
    }
}
