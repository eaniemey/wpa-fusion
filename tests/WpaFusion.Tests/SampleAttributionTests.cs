using System.Text.Json;
using WpaFusion.Plugin.Sep;
using Xunit;

namespace WpaFusion.Tests;

public class SampleAttributionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("index")]
    [InlineData("version")]
    public void Sidecar_is_bound_to_exact_input_and_sample_order(string test)
    {
        var path = Path.GetTempFileName();
        var sidecar = path + SampleAttribution.Suffix;
        try
        {
            File.WriteAllText(path, "sample identity");
            var map = new SampleAttribution
            {
                Version = test == "version" ? 2 : 1,
                Tb7Sha256 = test == "hash" ? "wrong" : SampleAttribution.Hash(path),
                Locations = new[] { new SampleLocation("module.dll", "Function") },
                SampleLocations = new[] { test == "index" ? 1 : 0 },
            };
            File.WriteAllText(sidecar, JsonSerializer.Serialize(map));
            if (test == "valid")
            {
                Assert.Equal("Function", SampleAttribution.Load(path, 1)!.Locations[0].Function);
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => SampleAttribution.Load(path, test == "count" ? 2 : 1));
            }
        }
        finally
        {
            File.Delete(sidecar);
            File.Delete(path);
        }
    }
}
