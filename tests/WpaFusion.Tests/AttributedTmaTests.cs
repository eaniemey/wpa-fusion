using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Capture.Clocks;
using WpaFusion.Plugin.Sep;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tbrw;
using WpaFusion.Tma;
using Xunit;

namespace WpaFusion.Tests;

[Collection(Fixtures.Tb7Collection)]
public class AttributedTmaTests
{
    [Fact]
    public void Emon_tma_restores_omitted_ancestors_in_original_xml_order_without_fabricating_values()
    {
        EngineHarness.WithEmonFixture(path =>
        {
            var folder = Path.GetDirectoryName(path)!;
            File.WriteAllText(Path.Combine(folder, "edp-metrics.xml"), """
                <root>
                  <metric name="metric_TMA_Frontend_Bound(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_..Fetch_Latency(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_....ICache_Misses(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_......Code_L2_Hit(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_........Deep_Fetch(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_..........Deepest_Fetch(%)"><category>TMA</category><formula>1</formula></metric>
                  <metric name="metric_TMA_Retiring(%)"><category>TMA</category><formula>1</formula></metric>
                </root>
                """);
            File.WriteAllText(path, """{"schemaVersion":1,"details":"details.csv","summary":"summary.csv","metricDefinitions":"edp-metrics.xml"}""");
            var data = EmonMetricData.Load(path);
            var tma = data.Metrics.Nodes.Where(node => node.IsTopDown).ToArray();
            Assert.Equal(new[] { "Frontend_Bound", "Fetch_Latency", "ICache_Misses", "Code_L2_Hit", "Deep_Fetch", "Deepest_Fetch", "Retiring" },
                tma.Select(node => node.Name));
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 1 }, tma.Select(node => node.Level));
            Assert.Equal("Frontend_Bound", tma[1].Parent?.Name);
            Assert.Equal("Deep_Fetch", tma[5].Parent?.Name);
            Assert.True(tma.Select(node => node.DefinitionOrder).SequenceEqual(tma.Select(node => node.DefinitionOrder).OrderBy(order => order)));
            var frontend = data.Summary.Single(result => result.Node.Name == "Frontend_Bound");
            Assert.False(frontend.IsAvailable);
            Assert.True(double.IsNaN(frontend.Value));
            Assert.Contains("omitted by Intel MPP", Assert.Single(frontend.MissingInputs));
            Assert.Contains("omitted by Intel MPP", Assert.Single(data.Averages.Single(result => result.Node.Name == "Frontend_Bound").MissingInputs));
            Assert.Equal(50, data.Summary.Single(result => result.Node.Name == "Retiring").Value);
            Assert.Equal(40, data.Intervals[0].Results.Single(result => result.Node.Name == "Retiring").Value);
            Assert.Equal(60, data.Intervals[1].Results.Single(result => result.Node.Name == "Retiring").Value);
            Assert.Equal(2, data.Averages.Single(result => result.Node.Name == "CPI").Value);
            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            foreach (var descriptor in new[] { TmaSummaryTable.TableDescriptor, TmaTimelineTable.TableDescriptor })
            {
                var table = EngineHarness.BuildTable(plugins, descriptor, path);
                foreach (var preset in table.BuiltInTableConfigurations.Where(preset => preset.Name is not ("All Nodes" or "All EDP Metrics Over Time")))
                {
                    Assert.Equal(Enumerable.Range(1, 6).Select(level => $"Level {level}"),
                        preset.Columns.TakeWhile(column => column != TableConfiguration.PivotColumn).Select(column => column.Metadata.Name));
                    Assert.Equal("[Category]:=\"TMA\"", preset.InitialFilterQuery);
                }

                var orderColumn = Assert.Single(table.Columns.Where(column => column.Configuration.Metadata.Name == "Definition Order"));
                Assert.Equal(SortOrder.Ascending, orderColumn.Configuration.DisplayHints.SortOrder);
                Assert.Equal(1, orderColumn.Configuration.DisplayHints.SortPriority);
                var deepest = data.Metrics.Nodes.Select((node, index) => (node, index)).Single(entry => entry.node.Name == "Deepest_Fetch").index;
                var deepestColumn = table.Columns.Single(column => column.Configuration.Metadata.Name == "Level 6");
                Assert.Equal("Deepest_Fetch", deepestColumn.Project(deepest));
            }
            return true;
        });
    }

    [Fact]
    public void Emon_metrics_use_mpp_summary_and_capture_time_zone_without_sample_estimation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-emon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var zero = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            new SessionManifest
            {
                RunId = "emon-test",
                CaptureTimeZoneId = "UTC",
                QpcFrequency = 10_000_000,
                SyncStart = new SyncPoint(1000, 100, zero.ToFileTimeUtc(), 1),
                SyncEnd = new SyncPoint(2000, 200, zero.AddSeconds(3).ToFileTimeUtc(), 1),
                Etl = new EtlClockReference(100, zero.ToFileTimeUtc()),
            }.Save(Path.Combine(directory, SessionManifest.FileName));
            File.WriteAllText(Path.Combine(directory, "summary.csv"), """
                name,aggregated,min,max,description
                metric_CPI,7,1,9,"Cycles, per instruction"
                metric_TMA_Backend_Bound(%),40,10,60,Backend
                """);
            File.WriteAllText(Path.Combine(directory, "details.csv"), """
                #sample,timestamp,metric_CPI,metric_TMA_Backend_Bound(%),$samplingTime
                1,2026-10-30T12:00:01,2,,1
                2,2026-10-30T12:00:02,4,50,1
                """);
            var descriptor = Path.Combine(directory, "metrics.emon.json");
            File.WriteAllText(descriptor, """{"schemaVersion":1,"summary":"summary.csv","details":"details.csv"}""");
            var data = EmonMetricData.Load(descriptor);
            Assert.Equal(2, data.Metrics.Nodes.Count);
            Assert.Equal(0, data.FirstNs);
            Assert.Equal(2_000_000_000, data.LastNs);
            Assert.Equal(7, data.Summary[0].Value);
            Assert.Equal("Cycles, per instruction", data.Metrics.Nodes[0].Description);
            Assert.Equal(2, data.Intervals[0].Results[0].Value);
            Assert.False(data.Intervals[0].Results[1].IsAvailable);
            Assert.Equal(50, data.Intervals[1].Results[1].Value);
            Assert.Equal(3, data.Averages[0].Value);
            Assert.Equal(50, data.Averages[1].Value);
            Assert.Equal(new long[] { 2, 1 }, data.ValidIntervalCounts);
            File.AppendAllText(Path.Combine(directory, "details.csv"), "\n3,2026-10-30T12:00:03,NaN,Infinity,1\n");
            var withInvalidInterval = EmonMetricData.Load(descriptor);
            Assert.Equal(3, withInvalidInterval.Averages[0].Value);
            Assert.Equal(50, withInvalidInterval.Averages[1].Value);
            Assert.Equal(new long[] { 2, 1 }, withInvalidInterval.ValidIntervalCounts);
            var analysis = TmaAnalysis.FromEmon(withInvalidInterval);
            Assert.Equal(3, analysis.Summary!.Results[0].Value);
            Assert.Equal(7, analysis.MppAggregates[0].Value);
            Assert.Equal(3, analysis.MetricAverages[0].Value);
            File.AppendAllText(Path.Combine(directory, "details.csv"), "4,2026-10-30T12:00:04,6,70,0\n");
            var withPoint = EmonMetricData.Load(descriptor);
            Assert.Equal(0, withPoint.Intervals[^1].DurationNs);
            Assert.Equal(4, withPoint.Averages[0].Value);
            Assert.Equal(60, withPoint.Averages[1].Value);
            Assert.Equal(new long[] { 3, 2 }, withPoint.ValidIntervalCounts);
            var manifestPath = Path.Combine(directory, SessionManifest.FileName);
            var native = SessionManifest.Load(manifestPath);
            native.Etl = null;
            native.Alignment = new CrossRunAlignment("emon-test", "sep-reference", 5_000_000_000,
                zero.AddHours(-2).ToFileTimeUtc(), 4_000_000_000, 4_000_000_000);
            native.Save(manifestPath);
            var aligned = EmonMetricData.Load(descriptor);
            Assert.Equal(5_000_000_000, aligned.FirstNs);
            Assert.Equal(zero.AddHours(-2), aligned.ZeroUtc);
            Assert.Equal(withPoint.Averages[0].Value, aligned.Averages[0].Value);
            Assert.Equal(withPoint.Intervals[0].DurationNs, aligned.Intervals[0].DurationNs);
            Assert.True(aligned.IsCrossRunComparison);
            Assert.Contains("emon-test", aligned.Provenance);
            File.WriteAllText(Path.Combine(directory, "details.csv"), """
                #sample,timestamp,metric_CPI,metric_TMA_Backend_Bound(%),$samplingTime
                1,2026-10-30T12:00:01,NaN,,1
                """);
            var unavailable = EmonMetricData.Load(descriptor);
            Assert.All(unavailable.Averages, average => Assert.False(average.IsAvailable));
            Assert.All(unavailable.Averages, average => Assert.True(double.IsNaN(average.Value)));
            Assert.Equal(new long[] { 0, 0 }, unavailable.ValidIntervalCounts);
            Assert.Equal(7, unavailable.Summary[0].Value);
            File.WriteAllText(descriptor, """{"schemaVersion":1,"summary":"../summary.csv","details":"details.csv"}""");
            Assert.Throws<InvalidDataException>(() => EmonMetricData.Load(descriptor));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
    public void Complete_edp_loading_keeps_every_definition_and_tma_hierarchy()
    {
        var path = Path.Combine(SepInstall.Root, "config", "edp", "sapphirerapids_server.xml");
        if (!File.Exists(path))
        {
            return;
        }

        var metrics = TmaMetricSet.LoadSepEdpXml(path, includeAllMetrics: true);
        Assert.Equal(XDocument.Load(path).Root!.Elements("metric").Count(), metrics.Nodes.Count);
        Assert.Equal(125, metrics.Nodes.Count(node => node.IsTopDown));
        Assert.Contains(metrics.Nodes, node => node.Name == "CPI" && node.Groups == "CPI");
        Assert.Equal("Frontend_Bound", metrics.Nodes.First(node => node.Name == "Fetch_Latency").Parent?.Name);
        var results = TmaEvaluator.EvaluateAll(metrics, new EventCounts(EventCatalog.Empty), new TmaConstants());
        Assert.Equal(metrics.Nodes.Count, results.Count);
        Assert.False(results.Single(result => result.Node.Name == "CPI").IsAvailable);
    }

    [Fact]
    public void Unsupported_edp_formulas_remain_visible_with_a_status()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                <root>
                  <metric name="metric_Custom"><category>Custom</category><formula>unsupported @ expression</formula></metric>
                </root>
                """);
            var metrics = TmaMetricSet.LoadSepEdpXml(path, includeAllMetrics: true);
            var result = Assert.Single(TmaEvaluator.EvaluateAll(metrics, new EventCounts(EventCatalog.Empty), new TmaConstants()));
            Assert.Equal("Custom", result.Node.Name);
            Assert.False(result.IsAvailable);
            Assert.True(double.IsNaN(result.Value));
            Assert.Contains("unsupported EDP formula", Assert.Single(result.MissingInputs));
        }
        finally
        {
            File.Delete(path);
        }
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
            var complete = TmaMetricSet.LoadSepEdpXml(path, includeAllMetrics: true);
            Assert.Equal(XDocument.Load(path).Root!.Elements("metric").Count(), complete.Nodes.Count);
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
            Assert.Equal(3, child.Level);
            Assert.Equal("Backend_Bound", child.Parent?.Name);
            Assert.Equal(string.Empty, TmaColumns.PathPart(child, 2));
            Assert.Equal("Region_0", TmaColumns.PathPart(child, 3));
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
    public void Sep_capture_does_not_supply_sample_estimated_tma_views()
    {
        var path = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_TB7");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(path);
        var analysis = TmaAnalysis.Compute(data, 1_000_000_000);

        Assert.False(analysis.IsAvailable);
        Assert.Contains("EMON", analysis.Status);
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        Assert.Throws<ArgumentException>(() => EngineHarness.BuildTable(plugins, TmaTimelineTable.TableDescriptor, path));
    }

    [Fact]
    public void Diagnostic_real_emon_metrics_build_summary_and_timeline_without_sep()
    {
        var path = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_EMON");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var data = EmonMetricData.Load(path);
        Assert.NotEmpty(data.Intervals);
        var folder = Path.GetDirectoryName(path)!;
        var manifest = SessionManifest.Load(Path.Combine(folder, SessionManifest.FileName));
        var definitionPath = EmonMetricDefinitions.Find(Path.Combine(folder, manifest.Files.EmonDat!), SepInstall.Root);
        if (definitionPath is not null)
        {
            var expected = TmaMetricSet.LoadSepEdpXml(definitionPath, includeAllMetrics: true).Nodes
                .Where(node => node.IsTopDown && node.OriginalName.EndsWith("(%)", StringComparison.Ordinal)).ToArray();
            var actual = data.Metrics.Nodes.Where(node => node.IsTopDown).ToArray();
            Assert.Equal(expected.Select(node => node.OriginalName), actual.Select(node => node.OriginalName));
            Assert.Equal(expected.Select(node => node.Level), actual.Select(node => node.Level));
            Assert.Equal(expected.Select(node => node.ParentName), actual.Select(node => node.ParentName));
        }
        Assert.Contains(data.Summary, result => result.Node.Name == "CPU utilization %" && result.IsAvailable);
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var timeline = EngineHarness.BuildTable(plugins, TmaTimelineTable.TableDescriptor, path);
        Assert.Equal(data.Metrics.Nodes.Count * data.Intervals.Count, timeline.RowCount);
        var summary = EngineHarness.BuildTable(plugins, TmaSummaryTable.TableDescriptor, path);
        Assert.Equal(data.Metrics.Nodes.Count, summary.RowCount);
        Assert.Contains(summary.Columns, column => column.Configuration.Metadata.Name == "Average Value");
        Assert.Contains(summary.Columns, column => column.Configuration.Metadata.Name == "MPP Aggregate");
        Assert.Contains(timeline.Columns, column => column.Configuration.Metadata.Name == "Average Value");
        Assert.Contains(timeline.Columns, column => column.Configuration.Metadata.Name == "Valid Intervals");
        var metricIndex = data.Metrics.Nodes.Select((node, index) => (node, index))
            .First(entry => entry.node.Name == "Backend_Bound").index;
        var intervalIndex = Enumerable.Range(0, data.Intervals.Count)
            .Last(index => data.Intervals[index].Results[metricIndex].IsAvailable);
        var rowIndex = intervalIndex * data.Metrics.Nodes.Count + metricIndex;
        var interval = data.Intervals[intervalIndex];
        var startColumn = timeline.Columns.Single(column => column.Configuration.Metadata.Name == "Slice Start");
        var durationColumn = timeline.Columns.Single(column => column.Configuration.Metadata.Name == "Slice Duration");
        var valueColumn = timeline.Columns.Single(column => column.Configuration.Metadata.Name == "Value");
        Assert.Equal(new Timestamp(interval.StartNs), (Timestamp)startColumn.Project(rowIndex));
        Assert.Equal(new TimestampDelta(interval.DurationNs), (TimestampDelta)durationColumn.Project(rowIndex));
        Assert.Equal(interval.Results[metricIndex].Value, (double)valueColumn.Project(rowIndex));
        Assert.True(interval.StartNs >= data.FirstNs + (data.LastNs - data.FirstNs) / 2);
        Assert.Empty(TmaAnalysis.FromEmon(data).Attributed);
    }

    [Fact]
    public void Diagnostic_fusion_preserves_sep_etl_pair_and_aligns_independent_emon()
    {
        var folder = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_FUSION");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var fused = FusedDatasetManifest.Load(Path.Combine(folder, FusedDatasetManifest.FileName));
        Assert.True(fused.SeparateExecutions);
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var files = new List<string>();
        var expectedSamples = 0;
        var expectedMetrics = 0;
        foreach (var source in fused.Sources)
        {
            var directory = CaptureBatchManifest.ResolveRun(folder, new CaptureBatchRun(source.Kind, source.Directory, source.RunId));
            var manifest = SessionManifest.Load(Path.Combine(directory, SessionManifest.FileName));
            Assert.Equal(source.RunId, manifest.Alignment!.SourceRunId);
            if (source.Kind == "SEP")
            {
                Assert.NotNull(manifest.Files.Etl);
                Assert.True(File.Exists(Path.Combine(directory, manifest.Files.Etl!)));
                Assert.NotNull(manifest.ProcessedFrom);
                var original = SessionManifest.Load(Path.Combine(manifest.ProcessedFrom!, SessionManifest.FileName));
                Assert.Equal(original.Etl, manifest.Etl);
                Assert.Equal(0, manifest.Alignment.OffsetNs);
                using (var nativeStream = File.OpenRead(Path.Combine(manifest.ProcessedFrom!, original.Files.Etl!)))
                using (var fusedStream = File.OpenRead(Path.Combine(directory, manifest.Files.Etl!)))
                using (var hash = SHA256.Create())
                {
                    Assert.Equal(hash.ComputeHash(nativeStream), hash.ComputeHash(fusedStream));
                }
                TbrwLibrary.Initialize(SepInstall.Bin64);
                var data = SepData.Load(Path.Combine(directory, manifest.Files.Tb7!));
                Assert.NotNull(data.Attribution);
                Assert.Contains("cross-run", data.TimeSource);
                Assert.Contains(source.RunId, data.ProcessName(data.Pid[0]));
                var table = EngineHarness.BuildTable(plugins, SepSampleTable.TableDescriptor, data.Path);
                Assert.Equal(data.Count, table.RowCount);
                files.Add(data.Path);
                expectedSamples += data.Count;
            }
            else
            {
                var path = Path.Combine(directory, manifest.Files.EmonMetrics!);
                var data = EmonMetricData.Load(path);
                Assert.True(data.IsCrossRunComparison);
                Assert.Equal(source.RunId, data.SourceRunId);
                Assert.Equal(DateTime.FromFileTimeUtc(source.Alignment.ReferenceZeroFileTimeUtc), data.ZeroUtc);
                var table = EngineHarness.BuildTable(plugins, TmaTimelineTable.TableDescriptor, path);
                Assert.Equal(data.Intervals.Count * data.Metrics.Nodes.Count, table.RowCount);
                Assert.Contains(table.Columns, column => column.Configuration.Metadata.Name == "Capture Run");
                files.Add(path);
                expectedMetrics += data.Intervals.Count * data.Metrics.Nodes.Count;
            }
        }

        Assert.Equal(expectedSamples, EngineHarness.BuildTable(plugins, SepSampleTable.TableDescriptor, files.ToArray()).RowCount);
        Assert.Equal(expectedMetrics, EngineHarness.BuildTable(plugins, TmaTimelineTable.TableDescriptor, files.ToArray()).RowCount);
    }

    [Fact]
    public void Diagnostic_timed_sep_run_resumes_after_warmup_and_records_five_second_window()
    {
        var path = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_TIMED_SEP");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var manifest = SessionManifest.Load(SessionManifest.FindFor(path)!);
        var start = Assert.Single(manifest.Markers.Where(marker => marker.Name == "workload-start"));
        var resume = Assert.Single(manifest.Markers.Where(marker => marker.Name == "sep-resume"));
        Assert.True((resume.Qpc - start.Qpc) / (double)manifest.QpcFrequency >= 30);
        var windowEnd = manifest.Markers.SingleOrDefault(marker => marker.Name == "sep-window-end");
        Assert.InRange(((windowEnd?.Qpc ?? manifest.SyncEnd!.Qpc) - resume.Qpc) / (double)manifest.QpcFrequency, 5, 5.5);
        Assert.NotNull(manifest.Etl);
        if (windowEnd is not null)
        {
            var workloadEnd = Assert.Single(manifest.Markers.Where(marker => marker.Name == "workload-end"));
            Assert.True(workloadEnd.Qpc > windowEnd.Qpc);
            Assert.True(manifest.SyncEnd!.Qpc >= workloadEnd.Qpc);
            Assert.Equal(0, manifest.WorkloadExitCode);
            var windowEndNs = Timeline.FromManifest(manifest).QpcToNanoseconds(windowEnd.Qpc);
            var etl = Path.Combine(Path.GetDirectoryName(path)!, manifest.Files.Etl!);
            Assert.Contains(EtlClockReader.ReadMarkers(etl), marker =>
                marker.Name == "workload-end" && marker.RelativeNanoseconds > windowEndNs);
        }
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var data = SepData.Load(path);
        Assert.True(data.Count > 0);
        var resumeNs = Timeline.FromManifest(manifest).QpcToNanoseconds(resume.Qpc);
        Assert.True(data.FirstNs >= resumeNs - 1_000_000_000);
        Assert.InRange(data.LastNs - data.FirstNs, 3_000_000_000, 7_000_000_000);
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
    public void Legacy_attributed_tma_table_is_not_registered()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        Assert.Throws<Microsoft.Performance.Toolkit.Engine.TableNotFoundException>(() =>
            EngineHarness.BuildTable(plugins, TmaAttributedTable.TableDescriptor, Fixtures.SmokeTb7));
    }

    [Fact]
    public void Timeline_presets_include_all_metrics_without_a_level_one_filter()
    {
        var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
        var table = EngineHarness.BuildEmonTable(plugins, TmaTimelineTable.TableDescriptor);
        Assert.Contains(table.BuiltInTableConfigurations, preset => preset.Name == "All EDP Metrics Over Time");
        Assert.Contains(table.BuiltInTableConfigurations, preset => preset.Name == "TMA Metrics Over Time");
        Assert.DoesNotContain(table.BuiltInTableConfigurations, preset => preset.Name == "Level 1 Over Time");
        Assert.All(table.BuiltInTableConfigurations, preset =>
        {
            Assert.Equal(preset.Name == "All EDP Metrics Over Time" ? "[Status]:=\"ok\"" : "[Category]:=\"TMA\"", preset.InitialFilterQuery);
            Assert.DoesNotContain("[Depth]", preset.InitialFilterQuery);
            Assert.True(preset.InitialFilterShouldKeep);
        });
        foreach (var preset in table.BuiltInTableConfigurations.Where(preset => preset.Name == "All EDP Metrics Over Time"))
        {
            Assert.Equal("Metric", preset.Columns.First().Metadata.Name);
        }
        Assert.Contains(table.Columns, column => column.Configuration.Metadata.Name == "Value");
        Assert.DoesNotContain(table.Columns, column => column.Configuration.Metadata.Name == "Function");
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
