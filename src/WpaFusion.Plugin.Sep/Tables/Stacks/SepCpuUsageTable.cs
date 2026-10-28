using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>
/// CPU Usage (Sampled)-style table: one row per ETW CPU sample with its stack, plus the SEP event counts
/// that accumulated in that sample's interval. Built from the offline sampled-stack sidecar.
/// </summary>
[Table]
public static class SepCpuUsageTable
{
    internal const int FrameDepth = 8;
    internal const string NoSample = "[no ETW sample]";
    private const string Suffix = "7A1B2C3D4E01";

    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{E5C9A3D7-0000-4B2C-8D4E-7A1B2C3D4E01}"),
        "SEP Event Counts by ETW Sample",
        "Fallback view: needs ETW CPU sampling during the SEP run. ETW CPU samples with stacks plus the SEP hardware event counts accumulated in each sample's interval. Counts are attributed statistically, not at the exact instruction.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Process = Column(1, "Process", "Process name and ID.", 220);
    internal static readonly ColumnConfiguration ThreadId = Column(2, "Thread ID", "Thread ID of the ETW sample.", 80);
    internal static readonly ColumnConfiguration Cpu = Column(3, "CPU", "Logical CPU.", 50);
    internal static readonly ColumnConfiguration LeafModule = Column(4, "Leaf Module", "Module of the top stack frame.", 200);
    internal static readonly ColumnConfiguration LeafFunction = Column(5, "Leaf Function", "Function of the top stack frame.", 260);
    internal static readonly ColumnConfiguration Stack = Column(6, "Stack", "Full stack, leaf first.", 400);

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Id(7), "Time", "ETW sample time on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration WeightMs = new(
        new ColumnMetadata(Id(8), "Weight (ms)", "Sampling interval the ETW sample represents."),
        new UIHints { Width = 90, IsVisible = true, AggregationMode = AggregationMode.Sum, CellFormat = "N3" });

    internal static readonly ColumnConfiguration Count = new(
        new ColumnMetadata(Id(9), "Count", "Number of ETW samples (synthetic rows count 0)."),
        new UIHints { Width = 70, IsVisible = true, AggregationMode = AggregationMode.Sum });

    internal static readonly ColumnConfiguration EstimatedCpuUsage = SepCpuUsageColumn.Create(
        Id(10), AggregationMode.Sum);

    internal static readonly ColumnConfiguration[] Frames = Enumerable.Range(1, FrameDepth)
        .Select(n => Column(0x10 + n, $"Frame {n}", n == 1 ? "Root frame (e.g. thread start)." : $"Stack frame {n} from the root.", 240))
        .ToArray();

    internal static void Build(ITableBuilder tableBuilder, SepData data)
    {
        var map = data.SampledStacks ?? throw new InvalidOperationException("No sampled-stack sidecar.");
        var rows = map.RowCount;
        var events = data.Events;

        var frameLabel = map.Frames.Select(f => string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(f.Module)}!{f.Function}")).ToArray();
        var stackText = map.Stacks.Select(s => string.Join(" <- ", s.Select(f => frameLabel[f]))).ToArray();

        // Samples per (event, row); dense so projections are plain array reads.
        var perEvent = new long[events.Count][];
        for (var e = 0; e < events.Count; e++)
        {
            perEvent[e] = new long[rows];
        }

        for (var i = 0; i < map.CountRow.Length; i++)
        {
            perEvent[map.CountEvent[i]][map.CountRow[i]] += (long)((ulong)map.CountSamples[i] * events[map.CountEvent[i]].SampleAfterValue);
        }

        var processLabel = map.Pid.Distinct().ToDictionary(p => p, p => string.Create(CultureInfo.InvariantCulture, $"{data.ProcessName(p)} ({p})"));
        var pid = map.Pid;
        var stackId = map.StackId;
        var stacks = map.Stacks;

        string FrameAt(int row, int depth)
        {
            if (stackId[row] == SampledStackAttribution.NoStack)
            {
                return depth == 0 ? NoSample : string.Empty;
            }

            var s = stacks[stackId[row]];
            var k = s.Length - 1 - depth;
            return k >= 0 ? frameLabel[s[k]] : string.Empty;
        }

        var eventColumns = events.Select(e => new ColumnConfiguration(
            new ColumnMetadata(EventGuid(e.Index, e.Name), e.Name, $"SEP '{e.Name}' events in the sample's interval (samples x sample-after value)."),
            new UIHints { Width = 130, IsVisible = true, AggregationMode = AggregationMode.Sum, CellFormat = "N0" })).ToArray();

        ColumnConfiguration[] Group(params ColumnConfiguration[] c) => c;
        var dataColumns = new[] { Cpu, Count, EstimatedCpuUsage }.Concat(eventColumns).ToArray();

        TableConfiguration Preset(string name, ColumnConfiguration[] group, ColumnConfiguration[] rest, ColumnConfiguration metric)
        {
            var config = new TableConfiguration(name)
            {
                Columns = group.Concat(new[] { TableConfiguration.PivotColumn }).Concat(rest)
                    .Concat(new[] { Time, TableConfiguration.GraphColumn, metric }).ToArray(),
                ChartType = ChartType.StackedLine,
            };
            config.AddColumnRole(ColumnRole.StartTime, Time);
            return config;
        }

        var tree = Group(new[] { Process, ThreadId }.Concat(Frames).ToArray());
        var treeRest = dataColumns.Concat(new[] { WeightMs, LeafFunction }).ToArray();
        var byWeight = Preset("Call Tree by Process, Thread (CPU Weight)", tree, treeRest, WeightMs);
        var byLeaf = Preset("By Leaf Module, Function", Group(LeafModule, LeafFunction),
            new[] { Process, ThreadId, WeightMs }.Concat(dataColumns).ToArray(), WeightMs);

        tableBuilder
            .AddTableConfiguration(byWeight)
            .AddTableConfiguration(byLeaf)
            .SetDefaultTableConfiguration(byWeight);
        if (eventColumns.Length > 0)
        {
            tableBuilder.AddTableConfiguration(Preset("Call Tree by Process, Thread (First Event)", tree,
                dataColumns.Concat(new[] { WeightMs, LeafFunction }).ToArray(), eventColumns[0]));
        }

        var builder = tableBuilder
            .SetRowCount(rows)
            .AddColumn(Process, Projection.CreateUsingFuncAdaptor(i => processLabel[pid[i]]))
            .AddColumn(ThreadId, Projection.CreateUsingFuncAdaptor(i => map.Tid[i]))
            .AddColumn(Cpu, Projection.CreateUsingFuncAdaptor(i => map.Cpu[i]))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(map.TimeNs[i])))
            .AddColumn(WeightMs, Projection.CreateUsingFuncAdaptor(i => map.WeightNs[i] / 1e6))
            .AddColumn(Count, Projection.CreateUsingFuncAdaptor(i => stackId[i] == SampledStackAttribution.NoStack ? 0 : 1))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i =>
                data.CpuUsage.EventIndex < 0 ? double.NaN :
                data.CpuUsage.Percent(perEvent[data.CpuUsage.EventIndex][i], map.WeightNs[i])))
            .AddColumn(LeafModule, Projection.CreateUsingFuncAdaptor(i => stackId[i] == SampledStackAttribution.NoStack
                ? NoSample : map.Frames[stacks[stackId[i]][0]].Module))
            .AddColumn(LeafFunction, Projection.CreateUsingFuncAdaptor(i => stackId[i] == SampledStackAttribution.NoStack
                ? NoSample : map.Frames[stacks[stackId[i]][0]].Function))
            .AddColumn(Stack, Projection.CreateUsingFuncAdaptor(i => stackId[i] == SampledStackAttribution.NoStack
                ? NoSample : stackText[stackId[i]]));
        for (var d = 0; d < FrameDepth; d++)
        {
            var depth = d;
            builder = builder.AddColumn(Frames[d], Projection.CreateUsingFuncAdaptor(i => FrameAt(i, depth)));
        }

        for (var e = 0; e < eventColumns.Length; e++)
        {
            var counts = perEvent[e];
            builder = builder.AddColumn(eventColumns[e], Projection.CreateUsingFuncAdaptor(i => counts[i]));
        }
    }

    private static Guid Id(int n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"{{E5C9A3D7-{n:X4}-4B2C-8D4E-{Suffix}}}"));

    // Stable per event index and name, so saved WPA profiles keep working across traces with the same events.
    private static Guid EventGuid(int index, string name)
    {
        // Seed kept from before the WPA-Fusion rename so generated event GUIDs, and saved profiles, stay stable.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"PerfScope.SepEvent:{index}:{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static ColumnConfiguration Column(int n, string name, string description, int width) =>
        new(new ColumnMetadata(Id(n), name, description), new UIHints { Width = width, IsVisible = true });
}
