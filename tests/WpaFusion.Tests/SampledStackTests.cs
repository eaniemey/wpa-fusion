using System.Text.Json;
using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Sep;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tbrw;
using Xunit;

namespace WpaFusion.Tests;

public class SampledStackJoinTests
{
    private static readonly SampleLocation[] Frames = { new("a.dll", "Leaf"), new("a.dll", "Root") };
    private static readonly int[][] Stacks = { new[] { 0, 1 } };

    private static SampledStackAttribution Join(EtwCpuSample[] etw, long[] time, ushort[] cpu, uint[] tid, ushort[] ev, out SampledStackJoinStats stats) =>
        SampledStackJoin.Join(etw, new SepSampleSet(time, cpu, tid, new uint[time.Length], ev, 2), Frames, Stacks, "hash", 0, 0, out stats);

    [Fact]
    public void Sample_goes_to_first_etw_sample_at_or_after_it_on_the_same_cpu()
    {
        var etw = new[]
        {
            new EtwCpuSample(1000, 0, 1, 7, 0, 1000),
            new EtwCpuSample(2000, 0, 1, 7, 0, 1000),
            new EtwCpuSample(1500, 1, 1, 7, 0, 1000),
        };
        var map = Join(etw, new long[] { 1000, 1001, 2000, 2001, 1200 }, new ushort[] { 0, 0, 0, 0, 1 },
            new uint[] { 7, 7, 7, 7, 7 }, new ushort[] { 0, 0, 1, 1, 0 }, out var stats);

        Assert.Equal(4, stats.Matched);
        Assert.Equal(1, stats.Unmatched); // t=2001 is past the last sample on CPU 0
        Assert.Equal(4, map.RowCount); // 3 ETW rows + 1 synthetic
        int Samples(int row, int ev) => Enumerable.Range(0, map.CountRow.Length)
            .Where(i => map.CountRow[i] == row && map.CountEvent[i] == ev).Sum(i => map.CountSamples[i]);
        Assert.Equal(1, Samples(0, 0)); // t=1000 (inclusive boundary)
        Assert.Equal(1, Samples(1, 0)); // t=1001
        Assert.Equal(1, Samples(1, 1)); // t=2000 (inclusive boundary)
        Assert.Equal(1, Samples(2, 0)); // CPU 1
        Assert.Equal(SampledStackAttribution.NoStack, map.StackId[3]);
        Assert.Equal(2001, map.TimeNs[3]);
    }

    [Fact]
    public void Thread_mismatch_and_uncovered_cpu_become_synthetic_rows_and_totals_are_conserved()
    {
        var etw = new[] { new EtwCpuSample(1000, 0, 1, 7, 0, 1000) };
        var map = Join(etw, new long[] { 900, 900, 900 }, new ushort[] { 0, 0, 5 },
            new uint[] { 7, 8, 7 }, new ushort[] { 0, 1, 1 }, out var stats);

        Assert.Equal(1, stats.Matched);
        Assert.Equal(2, stats.Unmatched);
        Assert.Equal(3, map.CountSamples.Sum());
        Assert.Equal(1, map.StackId.Count(s => s != SampledStackAttribution.NoStack));
        Assert.All(Enumerable.Range(1, 2), row => Assert.Equal(0, map.WeightNs[row]));
    }
}

[Collection(Fixtures.Tb7Collection)]
public class SampledStackSidecarTests
{
    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpa-fusion-stacks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static SampledStackAttribution Build(SepData data, string tb7)
    {
        // An ETW sample for every 50th SEP sample, on the same CPU and thread.
        var etw = Enumerable.Range(0, data.Count).Where(i => i % 50 == 0)
            .Select(i => new EtwCpuSample(data.TimeNs[i], data.Cpu[i], data.Pid[i], data.Tid[i], i / 50 % 2, 1_000_000)).ToArray();
        return SampledStackJoin.Join(etw, new SepSampleSet(data.TimeNs, data.Cpu, data.Tid, data.Pid, data.EventIndex, data.Events.Count),
            new[] { new SampleLocation("m.dll", "Leaf"), new SampleLocation("m.dll", "Mid"), new SampleLocation("m.dll", "Root") },
            new[] { new[] { 0, 1, 2 }, new[] { 1, 2 } }, SampleAttribution.Hash(tb7), 0, 0, out _);
    }

    [Fact]
    public void Valid_sidecar_loads_and_table_reconciles_with_sep_totals()
    {
        Assert.Equal("SEP Event Counts by ETW Sample", SepCpuUsageTable.TableDescriptor.Name);
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "samples.tb7");
            File.Copy(Fixtures.SmokeTb7, tb7);
            var original = SepData.Load(tb7);
            File.WriteAllText(tb7 + SampledStackAttribution.Suffix, JsonSerializer.Serialize(Build(original, tb7)));

            var data = SepData.Load(tb7);
            Assert.NotNull(data.SampledStacks);
            Assert.Equal(original.Count, data.SampledStacks!.CountSamples.Sum());

            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            var table = EngineHarness.BuildTable(plugins, SepCpuUsageTable.TableDescriptor, tb7);
            Assert.Equal(data.SampledStacks.RowCount, table.RowCount);
            Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Est. % CPU Usage");
            Assert.DoesNotContain(table.Columns, c => c.Configuration.Metadata.Name == "Thread Name");
            foreach (var ev in data.Events)
            {
                Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == ev.Name);
            }

            Assert.Equal(3, table.BuiltInTableConfigurations.Count());
            foreach (var preset in table.BuiltInTableConfigurations)
            {
                var columns = preset.Columns.ToList();
                var separator = columns.IndexOf(TableConfiguration.GraphColumn);
                Assert.Single(columns.Skip(separator + 1));
                Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == preset.ColumnRoles[ColumnRole.StartTime]);
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Delete(file);
            }

            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("stack")]
    [InlineData("event")]
    [InlineData("total")]
    public void Tampered_sidecar_is_rejected(string tamper)
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "samples.tb7");
            File.Copy(Fixtures.SmokeTb7, tb7);
            var original = SepData.Load(tb7);
            var good = Build(original, tb7);
            var bad = tamper switch
            {
                "version" => new SampledStackAttribution { Version = 2, Tb7Sha256 = good.Tb7Sha256 },
                "hash" => Copy(good, "0000"),
                "stack" => Copy(good, good.Tb7Sha256, stackId: Enumerable.Repeat(99, good.RowCount).ToArray()),
                "event" => Copy(good, good.Tb7Sha256, countEvent: good.CountEvent.Select(_ => 99).ToArray()),
                _ => Copy(good, good.Tb7Sha256, countSamples: good.CountSamples.Select(c => c + 1).ToArray()),
            };
            File.WriteAllText(tb7 + SampledStackAttribution.Suffix, JsonSerializer.Serialize(bad));
            Assert.Throws<InvalidDataException>(() => SepData.Load(tb7));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Delete(file);
            }

            Directory.Delete(directory);
        }
    }

    private static SampledStackAttribution Copy(SampledStackAttribution m, string hash, int[]? stackId = null, int[]? countEvent = null, int[]? countSamples = null) => new()
    {
        Tb7Sha256 = hash,
        Frames = m.Frames,
        Stacks = m.Stacks,
        TimeNs = m.TimeNs,
        Cpu = m.Cpu,
        Pid = m.Pid,
        Tid = m.Tid,
        StackId = stackId ?? m.StackId,
        WeightNs = m.WeightNs,
        CountRow = m.CountRow,
        CountEvent = countEvent ?? m.CountEvent,
        CountSamples = countSamples ?? m.CountSamples,
    };
}
