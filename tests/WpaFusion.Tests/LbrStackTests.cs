using System.Text.Json;
using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Sep;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tbrw;
using WpaFusion.Tma;
using Xunit;

namespace WpaFusion.Tests;

public class LbrStackBuilderTests
{
    [Fact]
    public void Stack_is_the_sample_ip_then_each_call_site_newest_first()
    {
        var builder = new LbrStackBuilder();
        var id = builder.Add(7, 100, 0xAAA, new ulong[] { 0x111, 0x222, 0x333 });
        Assert.Equal(new ulong[] { 0xAAA, 0x111, 0x222, 0x333 }, builder.Stacks[id].Addresses);
        Assert.Equal(7u, builder.Stacks[id].Pid);
    }

    [Fact]
    public void Identical_stacks_in_one_process_share_an_id_but_other_processes_do_not()
    {
        var builder = new LbrStackBuilder();
        var a = builder.Add(1, 10, 0xA, new ulong[] { 0x1, 0x2 });
        var b = builder.Add(1, 99, 0xA, new ulong[] { 0x1, 0x2 });
        var c = builder.Add(2, 10, 0xA, new ulong[] { 0x1, 0x2 });
        var d = builder.Add(1, 10, 0xA, new ulong[] { 0x1 });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(3, builder.Stacks.Count);
        Assert.Equal(10, builder.Stacks[a].TimeNs); // kept from the first sample
    }

    [Fact]
    public void Empty_lbr_gives_an_ip_only_stack_and_depth_is_capped()
    {
        var builder = new LbrStackBuilder();
        Assert.Equal(new ulong[] { 0xA }, builder.Stacks[builder.Add(1, 0, 0xA, ReadOnlySpan<ulong>.Empty)].Addresses);
        var deep = builder.Add(1, 0, 0xA, Enumerable.Range(1, 32).Select(i => (ulong)i).ToArray(), maxDepth: 4);
        Assert.Equal(new ulong[] { 0xA, 1, 2, 3, 4 }, builder.Stacks[deep].Addresses);
    }
}

public class MultiplexEstimatorSingleGroupTests
{
    [Fact]
    public void Single_event_group_is_not_multiplexed_and_needs_no_reference_event()
    {
        var events = new[] { new SampledEvent("BR_INST_RETIRED.ALL_BRANCHES", 3_000_003, 1) };
        var scales = MultiplexEstimator.Estimate(events, new ushort[] { 0, 1 }, new ulong[] { 10, 20 }, new ushort[] { 0, 0 });
        Assert.Equal(1.0, Assert.Single(scales.PerEvent));
    }
}

[Collection(Fixtures.Tb7Collection)]
public class LbrStackSidecarTests
{
    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-lbr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Runs the real reader and builder over the LBR fixture, naming each distinct address as its own frame.</summary>
    private static LbrStackAttribution Build(string tb7, SepData data)
    {
        var builder = new LbrStackBuilder();
        var ids = new int[data.Count];
        using (var file = TbrwFile.Open(tb7))
        {
            file.ReadLbrSamples((long index, in PmuSample s, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to) =>
                ids[index] = builder.Add(data.Pid[index], data.TimeNs[index], s.Ip, from));
        }

        var frames = new List<SampleLocation>();
        var frameIndex = new Dictionary<ulong, int>();
        var stacks = builder.Stacks.Select(s => s.Addresses.Select(a =>
        {
            if (!frameIndex.TryGetValue(a, out var f))
            {
                f = frames.Count;
                frames.Add(new SampleLocation("m.dll", $"Fn{a:X}"));
                frameIndex.Add(a, f);
            }

            return f;
        }).ToArray()).ToArray();
        return new LbrStackAttribution { Tb7Sha256 = SampleAttribution.Hash(tb7), Frames = frames.ToArray(), Stacks = stacks, StackId = ids };
    }

    private static void Cleanup(string directory)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            File.Delete(file);
        }

        Directory.Delete(directory);
    }

    [Fact]
    public void Valid_sidecar_loads_and_table_builds_one_row_per_sample()
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "lbr.tb7");
            File.Copy(Fixtures.LbrTb7, tb7);
            var original = SepData.Load(tb7);
            Assert.Null(original.LbrStacks);
            File.WriteAllText(tb7 + LbrStackAttribution.Suffix, JsonSerializer.Serialize(Build(tb7, original)));

            var data = SepData.Load(tb7);
            Assert.NotNull(data.LbrStacks);
            Assert.Equal(5224, data.LbrStacks!.StackId.Length);
            Assert.Contains(data.LbrStacks.Stacks, s => s.Length > 3);

            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            var table = EngineHarness.BuildTable(plugins, SepLbrCallStackTable.TableDescriptor, tb7);
            Assert.Equal(5224, table.RowCount);
            Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Est. % CPU Usage");
            Assert.DoesNotContain(table.Columns, c => c.Configuration.Metadata.Name == "Thread Name");
            Assert.Contains(table.Columns, c => c.Configuration.Metadata.Name == "Frame 16");
            Assert.Equal(2, table.BuiltInTableConfigurations.Count());
            var presets = table.BuiltInTableConfigurations.ToArray();
            Assert.Equal("Call Tree by Process, Thread, Event (LBR)", presets[0].Name);
            Assert.Equal(new[] { "Process", "Thread ID", "Event" },
                presets[0].Columns.Take(3).Select(column => column.Metadata.Name));
            Assert.Equal(new[] { "Process", "Thread ID", "Event" },
                presets[1].Columns.Take(3).Select(column => column.Metadata.Name));
            foreach (var preset in presets)
            {
                var columns = preset.Columns.ToList();
                var separator = columns.IndexOf(TableConfiguration.GraphColumn);
                Assert.Equal("Event Count", Assert.Single(columns.Skip(separator + 1)).Metadata.Name);
                Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == preset.ColumnRoles[ColumnRole.StartTime]);
            }
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Sep_samples_views_carry_the_lbr_columns_and_a_call_tree_preset()
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "lbr.tb7");
            File.Copy(Fixtures.LbrTb7, tb7);
            File.WriteAllText(tb7 + LbrStackAttribution.Suffix, JsonSerializer.Serialize(Build(tb7, SepData.Load(tb7))));

            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            var table = EngineHarness.BuildTable(plugins, SepSampleTable.TableDescriptor, tb7);
            Assert.Equal(5224, table.RowCount);
            var names = table.Columns.Select(c => c.Configuration.Metadata.Name).ToList();
            Assert.Contains("Stack", names);
            Assert.Contains("Module", names);
            Assert.Contains("Function", names);
            Assert.Contains("Frame 16", names);
            Assert.Contains("Est. % CPU Usage", names);
            Assert.DoesNotContain("SEP Thread Name", names);
            Assert.Equal(4, table.BuiltInTableConfigurations.Count());
            foreach (var preset in table.BuiltInTableConfigurations)
            {
                var columns = preset.Columns.ToList();
                var separator = columns.IndexOf(TableConfiguration.GraphColumn);
                Assert.Equal("Event Count", Assert.Single(columns.Skip(separator + 1)).Metadata.Name);
                Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == preset.ColumnRoles[ColumnRole.StartTime]);
                Assert.Contains(columns, c => c.Metadata.Name == "Frame 1");
            }

            // Data columns of the original three views must still sit left of the graph separator.
            Assert.Contains(table.BuiltInTableConfigurations, p => p.Name.Contains("LBR", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Stack_frames_table_has_one_row_per_sample_and_frame_with_valid_presets()
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "lbr.tb7");
            File.Copy(Fixtures.LbrTb7, tb7);
            var map = Build(tb7, SepData.Load(tb7));
            File.WriteAllText(tb7 + LbrStackAttribution.Suffix, JsonSerializer.Serialize(map));

            var plugins = EngineHarness.LoadPlugins("WpaFusion.Plugin.Sep");
            var table = EngineHarness.BuildTable(plugins, SepLbrStackFramesTable.TableDescriptor, tb7);
            Assert.Equal(map.StackId.Sum(id => map.Stacks[id].Length), table.RowCount);
            Assert.True(table.RowCount > map.StackId.Length); // stacks are deeper than one frame on average
            var names = table.Columns.Select(c => c.Configuration.Metadata.Name).ToList();
            foreach (var expected in new[] { "Sample", "Depth", "Frame", "Module", "Function", "Samples", "Event Count", "Est. % CPU Usage" })
            {
                Assert.Contains(expected, names);
            }

            Assert.DoesNotContain("Thread Name", names);
            Assert.Equal(3, table.BuiltInTableConfigurations.Count());
            foreach (var preset in table.BuiltInTableConfigurations)
            {
                var columns = preset.Columns.ToList();
                var separator = columns.IndexOf(TableConfiguration.GraphColumn);
                Assert.Equal("Event Count", Assert.Single(columns.Skip(separator + 1)).Metadata.Name);
                Assert.Contains(columns.Take(separator), c => c.Metadata.Guid == preset.ColumnRoles[ColumnRole.StartTime]);
            }
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("stack")]
    [InlineData("frame")]
    [InlineData("empty")]
    public void Tampered_sidecar_is_rejected(string tamper)
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        var directory = NewDirectory();
        try
        {
            var tb7 = Path.Combine(directory, "lbr.tb7");
            File.Copy(Fixtures.LbrTb7, tb7);
            var good = Build(tb7, SepData.Load(tb7));
            var bad = tamper switch
            {
                "version" => Copy(good, version: 2),
                "hash" => Copy(good, hash: "0000"),
                "count" => Copy(good, stackId: good.StackId[1..]),
                "stack" => Copy(good, stackId: good.StackId.Select(_ => 999_999).ToArray()),
                "frame" => Copy(good, stacks: good.Stacks.Select(_ => new[] { 999_999 }).ToArray()),
                _ => Copy(good, frames: good.Frames.Select(_ => new SampleLocation("", "x")).ToArray()),
            };
            File.WriteAllText(tb7 + LbrStackAttribution.Suffix, JsonSerializer.Serialize(bad));
            Assert.Throws<InvalidDataException>(() => SepData.Load(tb7));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static LbrStackAttribution Copy(LbrStackAttribution m, int version = 1, string? hash = null, SampleLocation[]? frames = null, int[][]? stacks = null, int[]? stackId = null) => new()
    {
        Version = version,
        Tb7Sha256 = hash ?? m.Tb7Sha256,
        Frames = frames ?? m.Frames,
        Stacks = stacks ?? m.Stacks,
        StackId = stackId ?? m.StackId,
    };
}

public class LbrStackViewTests
{
    private static LbrStackAttribution Map() => new()
    {
        Frames = new[] { new SampleLocation("C:/x/a.dll", "Leaf"), new SampleLocation("C:/x/b.dll", "Mid"), new SampleLocation("C:/x/c.dll", "Root") },
        Stacks = new[] { new[] { 0, 1, 2 }, new[] { 0 } },
        StackId = new[] { 0, 1 },
    };

    [Fact]
    public void Frames_run_outermost_first_and_the_stack_text_and_leaf_run_leaf_first()
    {
        var view = new LbrStackView(Map());
        Assert.Equal("c.dll!Root", view.FrameAt(0, 0));
        Assert.Equal("b.dll!Mid", view.FrameAt(0, 1));
        Assert.Equal("a.dll!Leaf", view.FrameAt(0, 2));
        Assert.Equal(string.Empty, view.FrameAt(0, 3));
        Assert.Equal("a.dll!Leaf <- b.dll!Mid <- c.dll!Root", view.Stack(0));
        Assert.Equal("a.dll!Leaf", view.FrameAt(1, 0)); // a one-frame stack starts at its only frame
        Assert.Equal("a.dll", Path.GetFileName(view.LeafModule(1)));
        Assert.Equal("Leaf", view.LeafFunction(1));
    }

    [Fact]
    public void Without_a_sidecar_every_cell_says_lbr_was_not_collected()
    {
        var view = new LbrStackView(null);
        Assert.False(view.HasStacks);
        Assert.Equal(LbrStackView.NotCollected, view.FrameAt(5, 0));
        Assert.Equal(string.Empty, view.FrameAt(5, 1));
        Assert.Equal(LbrStackView.NotCollected, view.Stack(5));
        Assert.Equal(LbrStackView.NotCollected, view.LeafFunction(5));
    }
}
