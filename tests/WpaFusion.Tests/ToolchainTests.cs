using System.Reflection;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>Guards the toolchain pins recorded in DECISIONS.md.</summary>
public class ToolchainTests
{
    [Fact]
    public void Sdk_matches_version_bundled_with_target_wpa()
    {
        var sdk = typeof(Microsoft.Performance.SDK.Processing.ProcessingSource).Assembly;
        var version = sdk.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(version);
        Assert.StartsWith("1.2.2-preview", version);
    }

    [Fact]
    public void Tests_run_on_net6_like_wpa()
    {
        Assert.Equal(6, Environment.Version.Major);
    }
}
