using System.Globalization;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>SEP PMU samples on the ETW timeline: one row per sample.</summary>
[Table]
public static class SepSampleTable
{
    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{C2A7E1B4-3D5F-4A60-8B9C-0E1F2A3B4C5D}"),
        "SEP Samples",
        "Intel SEP hardware event samples. Event Count is the sample-after value (events represented by the sample).",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Event = Column("{C2A7E1B4-0001-4A60-8B9C-0E1F2A3B4C5D}", "Event", "Sampled hardware event.", 260);
    internal static readonly ColumnConfiguration Process = Column("{C2A7E1B4-0002-4A60-8B9C-0E1F2A3B4C5D}", "Process", "Process name and ID.", 220);
    internal static readonly ColumnConfiguration ProcessName = Column("{C2A7E1B4-0003-4A60-8B9C-0E1F2A3B4C5D}", "Process Name", "Process image name.", 160);
    internal static readonly ColumnConfiguration ProcessId = Column("{C2A7E1B4-0004-4A60-8B9C-0E1F2A3B4C5D}", "Process ID", "Process ID.", 80);
    internal static readonly ColumnConfiguration ThreadId = Column("{C2A7E1B4-0005-4A60-8B9C-0E1F2A3B4C5D}", "Thread ID", "Thread ID.", 80);
    internal static readonly ColumnConfiguration Cpu = Column("{C2A7E1B4-0007-4A60-8B9C-0E1F2A3B4C5D}", "CPU", "Logical CPU the sample was taken on.", 50);
    internal static readonly ColumnConfiguration Address = Column("{C2A7E1B4-0008-4A60-8B9C-0E1F2A3B4C5D}", "Address", "Instruction pointer.", 140);
    internal static readonly ColumnConfiguration Module = Column("{C2A7E1B4-000C-4A60-8B9C-0E1F2A3B4C5D}", "Module", "ETL image path from the offline attribution map.", 260);
    internal static readonly ColumnConfiguration Function = Column("{C2A7E1B4-000D-4A60-8B9C-0E1F2A3B4C5D}", "Function", "Flat sampled function, not a call stack.", 260);

    internal static readonly ColumnConfiguration Stack = Column("{C2A7E1B4-000E-4A60-8B9C-0E1F2A3B4C5D}", "Stack", "LBR call stack, leaf first (needs sep -lbr and the .lbrstacks.json sidecar).", 400);

    // LBR call-stack frames, outermost recorded caller first, to match the call-tree presets.
    internal static readonly ColumnConfiguration[] Frames = Enumerable.Range(1, LbrStackView.FrameDepth)
        .Select(n => Column(string.Create(CultureInfo.InvariantCulture, $"{{C2A7E1B4-{0x10 + n:X4}-4A60-8B9C-0E1F2A3B4C5D}}"), $"Frame {n}",
            n == 1 ? "Outermost recorded LBR caller." : $"LBR stack frame {n} from the outermost recorded caller.", 240))
        .ToArray();

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Guid.Parse("{C2A7E1B4-0009-4A60-8B9C-0E1F2A3B4C5D}"), "Time", "Sample time on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration EventCount = new(
        new ColumnMetadata(Guid.Parse("{C2A7E1B4-000A-4A60-8B9C-0E1F2A3B4C5D}"), "Event Count", "Events represented by the sample (sample-after value)."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Sum, CellFormat = "N0" });

    internal static readonly ColumnConfiguration Count = new(
        new ColumnMetadata(Guid.Parse("{C2A7E1B4-000B-4A60-8B9C-0E1F2A3B4C5D}"), "Count", "Number of samples."),
        new UIHints { Width = 70, IsVisible = true, AggregationMode = AggregationMode.Sum });

    internal static readonly ColumnConfiguration EstimatedCpuUsage = SepCpuUsageColumn.Create(
        Guid.Parse("{C2A7E1B4-000F-4A60-8B9C-0E1F2A3B4C5D}"), AggregationMode.Sum);

    internal static void Build(ITableBuilder tableBuilder, SepData data)
    {
        var ev = data.EventIndex;
        var events = data.Events;
        var pid = data.Pid;
        var tid = data.Tid;
        var cpu = data.Cpu;
        var ip = data.Ip;
        var time = data.TimeNs;
        var processLabel = new Dictionary<uint, string>();
        string Label(uint p)
        {
            if (!processLabel.TryGetValue(p, out var l))
            {
                l = string.Create(CultureInfo.InvariantCulture, $"{data.ProcessName(p)} ({p})");
                processLabel[p] = l;
            }

            return l;
        }

        // Pre-compute labels so projections stay allocation-free and thread-safe.
        foreach (var p in pid.Distinct())
        {
            Label(p);
        }

        var view = new LbrStackView(data.LbrStacks);
        var lbr = new[] { Module, Function, Stack }.Concat(Frames).ToArray();

        var byEvent = new TableConfiguration("By Event, Process, Thread")
        {
            Columns = new[] { Event, Process, ThreadId, TableConfiguration.PivotColumn, Cpu, Address, Count }.Concat(lbr)
                .Concat(new[] { EstimatedCpuUsage, Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
            ChartType = ChartType.StackedLine,
        };
        byEvent.AddColumnRole(ColumnRole.StartTime, Time);

        var byProcess = new TableConfiguration("By Process, Event")
        {
            Columns = new[] { Process, Event, TableConfiguration.PivotColumn, ThreadId, Cpu, Address, Count }.Concat(lbr)
                .Concat(new[] { EstimatedCpuUsage, Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
            ChartType = ChartType.StackedLine,
        };
        byProcess.AddColumnRole(ColumnRole.StartTime, Time);

        var byCpu = new TableConfiguration("By CPU, Event")
        {
            Columns = new[] { Cpu, Event, TableConfiguration.PivotColumn, Process, ThreadId, Address, Count }.Concat(lbr)
                .Concat(new[] { EstimatedCpuUsage, Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
            ChartType = ChartType.StackedLine,
        };
        byCpu.AddColumnRole(ColumnRole.StartTime, Time);

        // LBR call tree: Frame 1 is the outermost recorded caller; drill down towards the sampled function.
        var callTree = new TableConfiguration("Call Tree by Event, Process, Thread (LBR)")
        {
            Columns = new[] { Event, Process, ThreadId }.Concat(Frames)
                .Concat(new[] { TableConfiguration.PivotColumn, Function, Cpu, Count, EstimatedCpuUsage, Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
            ChartType = ChartType.StackedLine,
        };
        callTree.AddColumnRole(ColumnRole.StartTime, Time);

        var builder = tableBuilder
            .AddTableConfiguration(byEvent)
            .AddTableConfiguration(byProcess)
            .AddTableConfiguration(byCpu)
            .AddTableConfiguration(callTree)
            .SetDefaultTableConfiguration(byEvent)
            .SetRowCount(data.Count)
            .AddColumn(Event, Projection.CreateUsingFuncAdaptor(i => events[ev[i]].Name))
            .AddColumn(Process, Projection.CreateUsingFuncAdaptor(i => processLabel[pid[i]]))
            .AddColumn(ProcessName, Projection.CreateUsingFuncAdaptor(i => data.ProcessName(pid[i])))
            .AddColumn(ProcessId, Projection.CreateUsingFuncAdaptor(i => pid[i]))
            .AddColumn(ThreadId, Projection.CreateUsingFuncAdaptor(i => tid[i]))
            .AddColumn(Cpu, Projection.CreateUsingFuncAdaptor(i => (int)cpu[i]))
            .AddColumn(Address, Projection.CreateUsingFuncAdaptor(i => new Address(ip[i])))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(time[i])))
            .AddColumn(EventCount, Projection.CreateUsingFuncAdaptor(i => events[ev[i]].SampleAfterValue))
            .AddColumn(Count, Projection.Constant(1))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i => data.CpuUsage.PerSamplePercent[i]))
            .AddColumn(Module, Projection.CreateUsingFuncAdaptor(i => data.Attribution is { } map
                ? map.Locations[map.SampleLocations[i]].Module : "[No attribution: run scripts\\open-run.ps1]"))
            .AddColumn(Function, Projection.CreateUsingFuncAdaptor(i => data.Attribution is { } map
                ? map.Locations[map.SampleLocations[i]].Function : "[No attribution: run scripts\\open-run.ps1]"))
            .AddColumn(Stack, Projection.CreateUsingFuncAdaptor(i => view.Stack(i)));
        for (var d = 0; d < Frames.Length; d++)
        {
            var depth = d;
            builder = builder.AddColumn(Frames[d], Projection.CreateUsingFuncAdaptor(i => view.FrameAt(i, depth)));
        }
    }

    private static ColumnConfiguration Column(string guid, string name, string description, int width) =>
        new(new ColumnMetadata(Guid.Parse(guid), name, description), new UIHints { Width = width, IsVisible = true });
}
