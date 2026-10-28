using System.Globalization;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>
/// SEP samples with call stacks recovered from the CPU's last-branch records (sep -lbr call_stack:usr): one row
/// per sample. Built from the offline LBR sidecar; needs no ETW CPU sampling.
/// </summary>
[Table]
public static class SepLbrCallStackTable
{
    internal const int FrameDepth = LbrStackView.FrameDepth;
    private const string Suffix = "8B2C3D4E5F02";

    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{F6D0B4E8-0000-4C3D-9E5F-8B2C3D4E5F02}"),
        "CPU Usage (SEP LBR Stack Frames)",
        "SEP samples with user-mode call stacks from LBR call-stack mode. Stacks are the pending call chain at the sample (at most 32 deep); Frame 1 is the outermost recorded caller. Samples with an empty LBR show only their own frame.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Event = Column(1, "Event", "Sampled hardware event.", 260);
    internal static readonly ColumnConfiguration Process = Column(2, "Process", "Process name and ID.", 220);
    internal static readonly ColumnConfiguration ThreadId = Column(3, "Thread ID", "Thread ID.", 80);
    internal static readonly ColumnConfiguration Cpu = Column(4, "CPU", "Logical CPU the sample was taken on.", 50);
    internal static readonly ColumnConfiguration LeafModule = Column(5, "Leaf Module", "Module of the sampled instruction.", 200);
    internal static readonly ColumnConfiguration LeafFunction = Column(6, "Leaf Function", "Function of the sampled instruction.", 260);
    internal static readonly ColumnConfiguration Stack = Column(7, "Stack", "Full stack, leaf first.", 400);

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Id(8), "Time", "Sample time on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration EventCount = new(
        new ColumnMetadata(Id(9), "Event Count", "Events represented by the sample (sample-after value)."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Sum, CellFormat = "N0" });

    internal static readonly ColumnConfiguration Count = new(
        new ColumnMetadata(Id(10), "Count", "Number of samples."),
        new UIHints { Width = 70, IsVisible = true, AggregationMode = AggregationMode.Sum });

    internal static readonly ColumnConfiguration EstimatedCpuUsage = SepCpuUsageColumn.Create(
        Id(11), AggregationMode.Sum);

    internal static readonly ColumnConfiguration[] Frames = Enumerable.Range(1, FrameDepth)
        .Select(n => Column(0x10 + n, $"Frame {n}", n == 1 ? "Outermost recorded caller." : $"Stack frame {n} from the outermost recorded caller.", 240))
        .ToArray();

    internal static void Build(ITableBuilder tableBuilder, SepData data)
    {
        var map = data.LbrStacks ?? throw new InvalidOperationException("No LBR stack sidecar.");
        var events = data.Events;
        var ev = data.EventIndex;
        var pid = data.Pid;
        var tid = data.Tid;
        var cpu = data.Cpu;
        var time = data.TimeNs;
        var estimatedCpuUsage = data.CpuUsage.PerSamplePercent;
        var view = new LbrStackView(map);
        var processLabel = pid.Distinct().ToDictionary(p => p, p => string.Create(CultureInfo.InvariantCulture, $"{data.ProcessName(p)} ({p})"));

        TableConfiguration Preset(string name, ColumnConfiguration[] group, ColumnConfiguration[] rest)
        {
            var config = new TableConfiguration(name)
            {
                Columns = group.Concat(new[] { TableConfiguration.PivotColumn }).Concat(rest)
                    .Concat(new[] { Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
                ChartType = ChartType.StackedLine,
            };
            config.AddColumnRole(ColumnRole.StartTime, Time);
            return config;
        }

        var tree = Preset("Call Tree by Process, Thread, Event (LBR)",
            new[] { Process, ThreadId, Event }.Concat(Frames).ToArray(), new[] { LeafFunction, Cpu, Count, EstimatedCpuUsage });
        var leaf = Preset("By Process, Thread, Event, Module, Function",
            new[] { Process, ThreadId, Event, LeafModule, LeafFunction }, new[] { Cpu, Count, EstimatedCpuUsage });

        tableBuilder
            .AddTableConfiguration(tree)
            .AddTableConfiguration(leaf)
            .SetDefaultTableConfiguration(tree);

        var builder = tableBuilder
            .SetRowCount(data.Count)
            .AddColumn(Event, Projection.CreateUsingFuncAdaptor(i => events[ev[i]].Name))
            .AddColumn(Process, Projection.CreateUsingFuncAdaptor(i => processLabel[pid[i]]))
            .AddColumn(ThreadId, Projection.CreateUsingFuncAdaptor(i => tid[i]))
            .AddColumn(Cpu, Projection.CreateUsingFuncAdaptor(i => (int)cpu[i]))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(time[i])))
            .AddColumn(EventCount, Projection.CreateUsingFuncAdaptor(i => events[ev[i]].SampleAfterValue))
            .AddColumn(Count, Projection.Constant(1))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i => estimatedCpuUsage[i]))
            .AddColumn(LeafModule, Projection.CreateUsingFuncAdaptor(i => view.LeafModule(i)))
            .AddColumn(LeafFunction, Projection.CreateUsingFuncAdaptor(i => view.LeafFunction(i)))
            .AddColumn(Stack, Projection.CreateUsingFuncAdaptor(i => view.Stack(i)));
        for (var d = 0; d < FrameDepth; d++)
        {
            var depth = d;
            builder = builder.AddColumn(Frames[d], Projection.CreateUsingFuncAdaptor(i => view.FrameAt(i, depth)));
        }
    }

    private static Guid Id(int n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"{{F6D0B4E8-{n:X4}-4C3D-9E5F-{Suffix}}}"));

    private static ColumnConfiguration Column(int n, string name, string description, int width) =>
        new(new ColumnMetadata(Id(n), name, description), new UIHints { Width = width, IsVisible = true });
}
