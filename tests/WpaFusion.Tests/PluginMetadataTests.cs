using Microsoft.Performance.SDK.Processing;
using WpaFusion.Plugin.Typeperf;
using WpaFusion.Plugin.Sep;
using WpaFusion.Plugin.Sep.Tables;
using System.Reflection;
using Xunit;

namespace WpaFusion.Tests;

public class PluginMetadataTests
{
    [Fact]
    public void Sep_and_emon_table_providers_have_unique_ownership()
    {
        static TableDescriptor[] Tables(ProcessingSource source)
        {
            var factory = source.GetType().GetMethod("CreateTableProvider", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var provider = (IProcessingSourceTableProvider)factory.Invoke(source, null)!;
            return provider.Discover(null!).ToArray();
        }

        var sep = Tables(new SepProcessingSource());
        var emon = Tables(new EmonProcessingSource());
        Assert.Equal(4, sep.Length);
        Assert.Equal(2, emon.Length);
        Assert.Contains(sep, table => table.Guid == SepLbrStackFramesTable.TableDescriptor.Guid);
        Assert.DoesNotContain(emon, table => table.Guid == SepLbrStackFramesTable.TableDescriptor.Guid);
        Assert.Contains(emon, table => table.Guid == TmaTimelineTable.TableDescriptor.Guid);
        Assert.DoesNotContain(sep, table => table.Guid == TmaTimelineTable.TableDescriptor.Guid);
        Assert.DoesNotContain(sep.Concat(emon), table => table.Guid == TmaAttributedTable.TableDescriptor.Guid);
        Assert.All(sep.Concat(emon).GroupBy(table => table.Guid), group => Assert.Single(group));
    }

    [Fact]
    public void Launcher_can_enumerate_owners_for_all_processing_sources()
    {
        var typeperf = new TypeperfProcessingSource();
        var session = new SessionProcessingSource();
        var sep = new SepProcessingSource();

        foreach (var source in new ProcessingSource[] { typeperf, session, sep, new EmonProcessingSource() })
        {
            // WPA's launcher calls Select on Owners without a null check.
            Assert.NotNull(source.GetAboutInfo().Owners);
        }
    }
}
