using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Typeperf;
using WpaFusion.Plugin.Typeperf.Parsing;
using WpaFusion.Plugin.Typeperf.Tables.Counters;
using Xunit;

namespace WpaFusion.Tests;

public class TypeperfPluginTests
{
    [Fact]
    public void Parses_smoke_csv_with_blank_first_rate_sample()
    {
        var csv = TypePerfCsv.Load(Fixtures.SmokeTypeperfCsv);

        Assert.Equal(2, csv.Counters.Count);
        Assert.Equal(360, csv.TimeZoneBiasMinutes);
        Assert.Equal("Processor", csv.Counters[0].Object);
        Assert.Equal("_Total", csv.Counters[0].Instance);
        Assert.Equal("% Processor Time", csv.Counters[0].Counter);
        Assert.Equal("MHPRICE-DESK2", csv.Counters[0].Machine);
        Assert.Equal(3, csv.SampleTimesUtc.Count);

        // First % Processor Time cell is blank: no sample, never zero.
        Assert.DoesNotContain(csv.Samples, s => s.SampleIndex == 0 && s.CounterIndex == 0);
        Assert.Contains(csv.Samples, s => s.SampleIndex == 0 && s.CounterIndex == 1 && s.Value == 247137);

        // 10/17/2026 11:00:58.046 local (UTC-6) = 17:00:58.046 UTC.
        Assert.Equal(new DateTime(2026, 10, 17, 17, 0, 58, 46, DateTimeKind.Utc), DateTime.FromFileTimeUtc(csv.SampleTimesUtc[0]));
    }

    [Theory]
    [InlineData(@"\\HOST\Process(svchost (1))\% Processor Time", "HOST", "Process", "svchost (1)", "% Processor Time")]
    [InlineData(@"\\HOST\Processor Information(0,12)\Processor Frequency", "HOST", "Processor Information", "0,12", "Processor Frequency")]
    [InlineData(@"\\HOST\Memory\Available MBytes", "HOST", "Memory", "", "Available MBytes")]
    [InlineData(@"\Cache\Copy Reads/sec", "", "Cache", "", "Copy Reads/sec")]
    public void Parses_counter_paths(string path, string machine, string obj, string instance, string counter)
    {
        var p = TypeperfCounterPath.Parse(path);
        Assert.Equal((machine, obj, instance, counter), (p.Machine, p.Object, p.Instance, p.Counter));
    }

    [Fact]
    public void Tolerates_row_cut_off_by_stopping_typeperf()
    {
        var text = "\"(PDH-CSV 4.0) (Mountain Daylight Time)(360)\",\"\\\\H\\Memory\\Available MBytes\"\n" +
                   "\"10/17/2026 11:00:58.046\",\"100\"\n" +
                   "\"10/17/2026 11:00:59.046\",\"1";
        var csv = TypePerfCsv.Load(new StringReader(text));
        Assert.Single(csv.SampleTimesUtc);
        Assert.Equal(1, csv.SkippedRows);
    }

    [Fact]
    public void Environment_setting_falls_back_to_legacy_perfscope_name()
    {
        const string name = "WPA_FUSION_TEST_SETTING";
        const string legacy = "PERFSCOPE_TEST_SETTING";
        try
        {
            Environment.SetEnvironmentVariable(legacy, "old");
            Assert.Equal("old", EnvironmentSetting.Get(name));
            Environment.SetEnvironmentVariable(name, "new");
            Assert.Equal("new", EnvironmentSetting.Get(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(legacy, null);
        }
    }

    [Fact]
    public void Session_manifest_writes_typeperf_keys_and_reads_legacy_pdh_keys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpa-fusion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifest = new SessionManifest
            {
                QpcFrequency = 10_000_000,
                SyncStart = new SyncPoint(1, 1, 1, 0),
                SyncEnd = new SyncPoint(2, 2, 2, 0),
            };
            manifest.Files.TypeperfCsv = "typeperf.csv";
            manifest.Files.TypeperfCounters = "typeperfinput.txt";
            var path = Path.Combine(dir, SessionManifest.FileName);
            manifest.Save(path);

            var json = File.ReadAllText(path);
            Assert.Contains("\"typeperfCsv\"", json);
            Assert.DoesNotContain("pdh", json, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, json.Replace("\"typeperfCsv\"", "\"pdhCsv\"").Replace("\"typeperfCounters\"", "\"pdhCounters\""));
            var legacy = SessionManifest.Load(path);
            Assert.Equal("typeperf.csv", legacy.Files.TypeperfCsv);
            Assert.Equal("typeperfinput.txt", legacy.Files.TypeperfCounters);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Plugin_loads_in_engine_and_builds_counter_table()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Typeperf");
        var table = EngineHarness.BuildTable(plugins, TypeperfCounterTable.TableDescriptor, Fixtures.SmokeTypeperfCsv);

        // 2 counters x 3 samples, minus the blank first % Processor Time cell.
        Assert.Equal(5, table.RowCount);
        Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Value");
        var time = table.Columns.Single(c => c.Configuration.Metadata.Name == "Time");
        Assert.Equal(new Timestamp(0), (Timestamp)time.Project(0));
    }

    [Fact]
    public void Graph_presets_put_time_left_of_graph_and_values_hold_until_next_sample()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Typeperf");
        var table = EngineHarness.BuildTable(plugins, TypeperfCounterTable.TableDescriptor, Fixtures.SmokeTypeperfCsv);

        Assert.NotEmpty(table.BuiltInTableConfigurations);
        foreach (var configuration in table.BuiltInTableConfigurations)
        {
            var columns = configuration.Columns.ToList();
            var separator = columns.IndexOf(TableConfiguration.GraphColumn);
            Assert.True(separator >= 0);
            Assert.Equal("Value", Assert.Single(columns.Skip(separator + 1)).Metadata.Name);
            Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == configuration.ColumnRoles[ColumnRole.StartTime]);
            Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == configuration.ColumnRoles[ColumnRole.Duration]);
        }

        var data = TypeperfData.Load(Fixtures.SmokeTypeperfCsv, null);
        for (var i = 0; i < data.RowCount; i++)
        {
            var next = Enumerable.Range(i + 1, data.RowCount - i - 1).FirstOrDefault(j => data.RowCounter[j] == data.RowCounter[i], -1);
            var expected = next >= 0 ? data.RowTimeNs[next] - data.RowTimeNs[i] : data.SampleIntervalNs;
            Assert.Equal(expected, data.RowDurationNs[i]);
            Assert.True(data.RowDurationNs[i] > 0);
        }
    }
}
