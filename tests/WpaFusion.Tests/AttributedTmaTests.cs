using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Sep;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tbrw;
using WpaFusion.Tma;
using Xunit;

namespace WpaFusion.Tests;

[Collection(Fixtures.Tb7Collection)]
public class AttributedTmaTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    public void Sparse_bucket_cutoff_is_enforced_at_the_exact_boundary(int samples, bool available)
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(Fixtures.SmokeTb7);
        for (var i = 0; i < data.Count; i++)
        {
            data.Pid[i] = i < samples ? 2u : 1u;
        }

        var path = WriteEdp("50");
        try
        {
            var scales = MultiplexEstimator.Estimate(
                data.Events.Select(e => new SampledEvent(e.Name, e.SampleAfterValue, e.GroupId)).ToArray(),
                data.Cpu, data.Tsc, data.EventIndex);
            var rows = AttributedTma.Compute(data, TmaMetricSet.LoadSepEdpXml(path), EventCatalog.Empty, scales,
                new TmaConstants(), 1_000_000_000);
            var row = rows.Single(r => r.IsSummary && r.Key.Scope == "Process" && r.Key.Pid == 2);
            Assert.Equal(samples, row.Window.Samples);
            var result = Assert.Single(row.Window.Results);
            Assert.Equal(available, result.IsAvailable);
            Assert.Equal(available ? 50.0 : double.NaN, result.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loaded_module_function_map_partitions_samples_without_losing_unresolved_data()
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = Path.Combine(Path.GetTempPath(), "wpa-fusion-attribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tb7 = Path.Combine(directory, "samples.tb7");
            File.Copy(Fixtures.SmokeTb7, tb7);
            var original = SepData.Load(tb7);
            var map = new SampleAttribution
            {
                Tb7Sha256 = SampleAttribution.Hash(tb7),
                Locations = new[]
                {
                    new SampleLocation("test.dll", "First"),
                    new SampleLocation("test.dll", "Second"),
                    new SampleLocation("[Unresolved module]", "[Unresolved function]"),
                },
                SampleLocations = Enumerable.Range(0, original.Count).Select(i => i % 3).ToArray(),
            };
            File.WriteAllText(tb7 + SampleAttribution.Suffix, JsonSerializer.Serialize(map));
            var data = SepData.Load(tb7);
            Assert.NotNull(data.Attribution);
            Array.Fill(data.Pid, 1u);
            var metricsPath = WriteEdp("100 * a / (a + b)", ("a", data.Events[0].Name), ("b", data.Events[1].Name));
            TmaAnalysis analysis;
            try
            {
                analysis = TmaAnalysis.Compute(data, TmaMetricSet.LoadSepEdpXml(metricsPath), "test", 1_000_000_000);
            }
            finally
            {
                File.Delete(metricsPath);
            }

            Assert.True(analysis.IsAvailable);
            foreach (var scope in new[] { "Process", "Module", "Function" })
            {
                var rows = analysis.Attributed.Where(r => r.IsSummary && r.Key.Scope == scope).ToArray();
                Assert.Equal(data.Count, rows.Sum(r => r.Window.Samples));
                foreach (var row in rows)
                {
                    var expected = Enumerable.Range(0, data.Count).Count(i =>
                    {
                        var location = map.Locations[map.SampleLocations[i]];
                        return scope == "Process" || location.Module == row.Key.Module &&
                            (scope == "Module" || location.Function == row.Key.Function);
                    });
                    Assert.Equal(expected, row.Window.Samples);
                }
            }

            var unresolved = analysis.Attributed.Single(r => r.IsSummary && r.Key.Scope == "Function" && r.Key.Module.StartsWith("[", StringComparison.Ordinal));
            Assert.All(unresolved.Window.Results, result => Assert.False(result.IsAvailable));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "samples.tb7") + SampleAttribution.Suffix);
            File.Delete(Path.Combine(directory, "samples.tb7"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Reference_cycle_samples_estimate_system_normalized_cpu_usage()
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(Fixtures.SmokeTb7);
        var cpuUsage = data.CpuUsage;
        Assert.True(cpuUsage.EventIndex >= 0);
        var referenceEvent = data.Events[cpuUsage.EventIndex];
        Assert.Equal(0, referenceEvent.GroupId);

        var sample = Array.FindIndex(data.EventIndex, index => index == cpuUsage.EventIndex);
        Assert.True(sample >= 0);
        var duration = Math.Max(1, data.LastNs - data.FirstNs);
        var expected = cpuUsage.Percent(referenceEvent.SampleAfterValue, duration);
        Assert.Equal(expected, cpuUsage.PerSamplePercent[sample], 10);
        Assert.All(Enumerable.Range(0, data.Count).Where(i => data.EventIndex[i] != cpuUsage.EventIndex),
            i => Assert.Equal(0, cpuUsage.PerSamplePercent[i]));
    }

    [Fact]
    public void Sapphire_rapids_sep_edp_xml_loads_full_tma_hierarchy()
    {
        var path = Path.Combine(SepInstall.Root, "config", "edp", "sapphirerapids_server.xml");
        if (!File.Exists(path))
        {
            return;
        }

        var metrics = TmaMetricSet.LoadSepEdpXml(path);
        Assert.Equal(125, metrics.Nodes.Count);
        Assert.Equal("Frontend_Bound", metrics.Nodes[0].Name);
        Assert.Equal(1, metrics.Nodes[0].Level);
        Assert.Equal("Fetch_Latency", metrics.Nodes[1].Name);
        Assert.Equal("Frontend_Bound", metrics.Nodes[1].Parent?.Name);
    }

    [Fact]
    public void Every_installed_sep_edp_file_with_tma_metrics_parses()
    {
        var edp = Path.Combine(SepInstall.Root, "config", "edp");
        if (!Directory.Exists(edp))
        {
            return;
        }

        var loaded = 0;
        foreach (var path in Directory.GetFiles(edp, "*.xml"))
        {
            try
            {
                Assert.NotEmpty(TmaMetricSet.LoadSepEdpXml(path).Nodes);
                loaded++;
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("No TMA metrics", StringComparison.Ordinal))
            {
            }
        }

        Assert.True(loaded > 0);
    }

    [Fact]
    public void Sep_edp_node_with_a_missing_intermediate_parent_hangs_on_its_nearest_ancestor()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                <root>
                  <metric name="metric_TMA_Backend_Bound(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_....Region_0(%)"><category>TMA</category><formula>1</formula></metric>
                </root>
                """);
            var child = TmaMetricSet.LoadSepEdpXml(path).Nodes[1];
            Assert.Equal(2, child.Level);
            Assert.Equal("Backend_Bound", child.Parent?.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sep_platform_is_parsed_from_sep_version_output()
    {
        var platform = SepPlatform.ParseVersion("""
            Sampling Enabling Product Version: 5.62  (private) built by x
            Platform type: 131
            CPU name: Intel(R) Xeon(R) Processor code named Sapphirerapids
            PMU: sapphirerapids_server
            """);

        Assert.Equal(new SepPlatform(131, "sapphirerapids_server", "Intel(R) Xeon(R) Processor code named Sapphirerapids"), platform);
        Assert.Null(SepPlatform.ParseVersion("SEP Driver Version: 5.62"));
    }

    [Theory]
    [InlineData(0x806F8u, "sapphirerapids_server")]
    [InlineData(0x50657u, "cascadelake_server")]
    [InlineData(0x50654u, "skylake_server")]
    [InlineData(0x816F8u, "sapphirerapids_server")]
    [InlineData(0x906A3u, null)]
    public void Sep_platform_is_resolved_from_a_cpuid_signature(uint signature, string? pmu)
    {
        const string list = """
            Platform: 111, PMU: skylake_server, Signature: 0x50650, CPU name: Intel(R) Xeon(R) Processor code named Skylake
            Platform: 127, PMU: cascadelake_server, Signature: 0x50655, CPU name: Intel(R) Xeon(R) Processor code named Cascadelake
            Platform: 131, PMU: sapphirerapids_server, Signature: 0x806f0, CPU name: Intel(R) Xeon(R) Processor code named Sapphirerapids
            """;

        Assert.Equal(pmu, SepPlatform.FromSignature(list, signature)?.Pmu);
    }

    [Fact]
    public void Sep_platform_selects_the_edp_file_named_by_its_pmu()
    {
        var expected = Path.Combine(SepInstall.Root, "config", "edp", "sapphirerapids_server.xml");
        if (File.Exists(expected))
        {
            Assert.Equal(expected, new SepPlatform(131, "sapphirerapids_server", "SPR").EdpMetricsPath(SepInstall.Root));
        }

        Assert.Null(new SepPlatform(0, "no_such_pmu", "x").EdpMetricsPath(SepInstall.Root));
        Assert.Null(new SepPlatform(0, @"..\edp\sapphirerapids_server", "x").EdpMetricsPath(SepInstall.Root));
    }

    [Fact]
    public void Tma_analysis_uses_sep_edp_for_sapphire_rapids_capture()
    {
        var path = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_TB7");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(path);
        var analysis = TmaAnalysis.Compute(data, 1_000_000_000);

        Assert.True(analysis.IsAvailable);
        Assert.Contains("SEP EDP (sapphirerapids_server.xml)", analysis.Status);
        Assert.Equal(125, analysis.Metrics!.Nodes.Count);
        Assert.NotEmpty(analysis.Attributed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Process_ratios_are_recomputed_from_counts_not_copied_from_global_percentages(bool outOfRange)
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(Fixtures.SmokeTb7);
        for (var i = 0; i < data.Count; i++)
        {
            data.Pid[i] = data.EventIndex[i] == 0
                ? (i % 4 == 0 ? 2u : 1u)
                : (i % 4 == 0 ? 1u : 2u);
        }

        var path = WriteEdp(outOfRange ? "200 + a" : "100 * a / (a + b)", ("a", data.Events[0].Name), ("b", data.Events[1].Name));
        try
        {
            var metrics = TmaMetricSet.LoadSepEdpXml(path);
            var scales = MultiplexEstimator.Estimate(
                data.Events.Select(e => new SampledEvent(e.Name, e.SampleAfterValue, e.GroupId)).ToArray(),
                data.Cpu, data.Tsc, data.EventIndex);
            var rows = AttributedTma.Compute(data, metrics, EventCatalog.Empty, scales, new TmaConstants(), 1_000_000_000);
            var processes = rows.Where(r => r.IsSummary && r.Key.Scope == "Process").ToArray();
            Assert.Equal(2, processes.Length);
            Assert.Equal(data.Count, processes.Sum(r => r.Window.Samples));
            foreach (var row in processes)
            {
                double Count(int ev) => Enumerable.Range(0, data.Count)
                    .Count(i => data.Pid[i] == row.Key.Pid && data.EventIndex[i] == ev) *
                    (double)data.Events[ev].SampleAfterValue * scales.PerEvent[ev];
                var result = Assert.Single(row.Window.Results);
                if (outOfRange)
                {
                    Assert.False(result.IsAvailable);
                    Assert.True(double.IsNaN(result.Value));
                    Assert.Contains("outside 0..100%", Assert.Single(result.MissingInputs));
                }
                else
                {
                    Assert.Equal(100 * Count(0) / (Count(0) + Count(1)), result.Value, 8);
                }
                Assert.Equal(row.Window.Samples, rows.Where(r => !r.IsSummary && r.Key == row.Key).Sum(r => r.Window.Samples));
            }

            if (!outOfRange)
            {
                Assert.NotEqual(processes[0].Window.Results[0].Value, processes[1].Window.Results[0].Value);
            }
            Assert.All(rows.Where(r => r.Key.Scope != "Process"),
                row => Assert.All(row.Window.Results, result => Assert.False(result.IsAvailable)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Attributed_table_builds_with_valid_graph_presets()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var table = EngineHarness.BuildTable(plugins, TmaAttributedTable.TableDescriptor, Fixtures.SmokeTb7);
        Assert.Equal(6, table.BuiltInTableConfigurations.Count());
        foreach (var preset in table.BuiltInTableConfigurations)
        {
            var columns = preset.Columns.ToList();
            var separator = columns.IndexOf(TableConfiguration.GraphColumn);
            Assert.Equal("% of Slots", Assert.Single(columns.Skip(separator + 1)).Metadata.Name);
            Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == preset.ColumnRoles[ColumnRole.StartTime]);
        }

        Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Module");
        Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Function");
        var cpuUsage = Assert.Single(table.Columns.Where(c => c.Configuration.Metadata.Name == "Est. % CPU Usage"));
        Assert.Equal(SortOrder.Descending, cpuUsage.Configuration.DisplayHints.SortOrder);
        Assert.Equal(1, cpuUsage.Configuration.DisplayHints.SortPriority);
    }

    /// <summary>Writes a one-metric SEP EDP XML file to a temp path.</summary>
    private static string WriteEdp(string formula, params (string Alias, string Name)[] events)
    {
        var path = Path.GetTempFileName();
        new XDocument(new XElement("root", new XElement("metric",
            new XAttribute("name", "metric_TMA_Test(%)"),
            new XElement("category", "TMA"),
            events.Select(e => new XElement("event", new XAttribute("alias", e.Alias), e.Name)),
            new XElement("formula", formula)))).Save(path);
        return path;
    }
}
