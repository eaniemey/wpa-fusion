using Microsoft.Performance.SDK.Processing;
using WpaFusion.Plugin.Typeperf;
using WpaFusion.Plugin.Sep;
using Xunit;

namespace WpaFusion.Tests;

public class PluginMetadataTests
{
    [Fact]
    public void Launcher_can_enumerate_owners_for_all_processing_sources()
    {
        var typeperf = new TypeperfProcessingSource();
        var session = new SessionProcessingSource();
        var sep = new SepProcessingSource();

        foreach (var source in new ProcessingSource[] { typeperf, session, sep })
        {
            // WPA's launcher calls Select on Owners without a null check.
            Assert.NotNull(source.GetAboutInfo().Owners);
        }
    }
}
