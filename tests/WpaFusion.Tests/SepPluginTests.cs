using Microsoft.Performance.SDK.Processing;
using WpaFusion.Plugin.Sep.Tables;
using Xunit;

namespace WpaFusion.Tests;

[Collection(Fixtures.Tb7Collection)]
public class SepPluginTests
{
    [Fact]
    public void Sep_and_emon_inputs_process_together_with_exclusive_table_owners()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var metrics = EngineHarness.BuildEmonTable(plugins, TmaTimelineTable.TableDescriptor, Fixtures.SmokeTb7);
        Assert.Equal(4, metrics.RowCount);
        var samples = EngineHarness.BuildEmonTable(plugins, SepSampleTable.TableDescriptor, Fixtures.SmokeTb7);
        Assert.Equal(24446, samples.RowCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Graph_presets_only_graph_the_numeric_metric(bool tma)
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var descriptor = tma ? TmaTimelineTable.TableDescriptor : SepSampleTable.TableDescriptor;
        var table = tma ? EngineHarness.BuildEmonTable(plugins, descriptor) :
            EngineHarness.BuildTable(plugins, descriptor, Fixtures.SmokeTb7);

        Assert.NotEmpty(table.BuiltInTableConfigurations);
        foreach (var configuration in table.BuiltInTableConfigurations)
        {
            var columns = configuration.Columns.ToList();
            var separator = columns.IndexOf(TableConfiguration.GraphColumn);
            Assert.True(separator >= 0);
            var metric = Assert.Single(columns.Skip(separator + 1));
            Assert.Equal(tma ? "Value" : "Event Count", metric.Metadata.Name);
            var startTime = configuration.ColumnRoles[ColumnRole.StartTime];
            Assert.Contains(columns.Take(separator), column => column.Metadata.Guid == startTime);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Plugin_loads_in_engine_and_builds_sample_table()
    {
        await Task.Run(() =>
        {
            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            var table = EngineHarness.BuildTable(plugins, SepSampleTable.TableDescriptor, Fixtures.SmokeTb7);

            // Same count as sfdump -samples on this file.
            Assert.Equal(24446, table.RowCount);
            var cpuUsage = Assert.Single(table.Columns.Where(c => c.Configuration.Metadata.Name == "Est. % CPU Usage"));
            Assert.Equal(SortOrder.Descending, cpuUsage.Configuration.DisplayHints.SortOrder);
            Assert.Equal(1, cpuUsage.Configuration.DisplayHints.SortPriority);
            var ev = table.Columns.Single(c => c.Configuration.Metadata.Name == "Event");
            Assert.Contains((string)ev.Project(0), new[] { "INST_RETIRED.ANY", "CPU_CLK_UNHALTED.THREAD", "CPU_CLK_UNHALTED.REF_TSC" });

            // With an ETL in the session, WPA injects its own "Thread Name" column into tables that have
            // Process ID/Thread ID; a plugin column with that name crashes WPA and hangs wpaexporter.
            Assert.DoesNotContain(table.Columns, c => c.Configuration.Metadata.Name == "Thread Name");
        });
    }
}
