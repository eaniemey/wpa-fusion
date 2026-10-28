using System.Globalization;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>
/// One row per (SEP sample, stack frame). Grouped by Sample, each sample is a collapsed row that expands into its
/// own LBR call stack: depth 0 is the sampled instruction and larger depths are callers further out.
/// </summary>
[Table]
public static class SepLbrStackFramesTable
{
    private const string Suffix = "9C3D4E5F6A03";

    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{A7B3C1D9-0000-4E2F-8A6B-9C3D4E5F6A03}"),
        "SEP Raw LBR Sample Stacks",
        "One row per SEP sample and LBR stack frame. Group by Sample and expand a sample to see its call stack, innermost frame (depth 0) first. Event Count and Samples are on the depth 0 row only so sums count each sample once.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Sample = Column(1, "Sample", "SEP sample number (original file order).", 90);
    internal static readonly ColumnConfiguration Event = Column(2, "Event", "Sampled hardware event.", 260);
    internal static readonly ColumnConfiguration Process = Column(3, "Process", "Process name and ID.", 220);
    internal static readonly ColumnConfiguration ThreadId = Column(4, "Thread ID", "Thread ID.", 80);
    internal static readonly ColumnConfiguration Cpu = Column(5, "CPU", "Logical CPU the sample was taken on.", 50);
    internal static readonly ColumnConfiguration Depth = Column(6, "Depth", "0 = the sampled instruction; larger numbers are callers further out.", 60);
    internal static readonly ColumnConfiguration Frame = Column(7, "Frame", "Module!Function of this stack frame.", 360);
    internal static readonly ColumnConfiguration Module = Column(8, "Module", "Image path of this frame.", 260);
    internal static readonly ColumnConfiguration Function = Column(9, "Function", "Function of this frame (needs symbols).", 260);

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Id(10), "Time", "Sample time on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration EventCount = new(
        new ColumnMetadata(Id(11), "Event Count", "Events represented by the sample (sample-after value), on the depth 0 row only."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Sum, CellFormat = "N0" });

    internal static readonly ColumnConfiguration Samples = new(
        new ColumnMetadata(Id(12), "Samples", "1 on the depth 0 row of each sample, else 0."),
        new UIHints { Width = 70, IsVisible = true, AggregationMode = AggregationMode.Sum });

    internal static readonly ColumnConfiguration EstimatedCpuUsage = SepCpuUsageColumn.Create(
        Id(13), AggregationMode.Sum);

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
        var stacks = map.Stacks;
        var stackId = map.StackId;

        var frameLabel = map.Frames.Select(f => string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(f.Module)}!{f.Function}")).ToArray();
        var processLabel = pid.Distinct().ToDictionary(p => p, p => string.Create(CultureInfo.InvariantCulture, $"{data.ProcessName(p)} ({p})"));

        // Row r belongs to sample rowSample[r] at stack depth rowDepth[r] (leaf first).
        var total = 0L;
        for (var i = 0; i < data.Count; i++)
        {
            total += stacks[stackId[i]].Length;
        }

        var rowSample = new int[total];
        var rowDepth = new byte[total];
        var row = 0;
        for (var i = 0; i < data.Count; i++)
        {
            var length = stacks[stackId[i]].Length;
            for (var d = 0; d < length; d++)
            {
                rowSample[row] = i;
                rowDepth[row] = (byte)Math.Min(d, byte.MaxValue);
                row++;
            }
        }

        int FrameOf(int r) => stacks[stackId[rowSample[r]]][rowDepth[r]];

        TableConfiguration Preset(string name, ColumnConfiguration[] keys, ColumnConfiguration[] rest)
        {
            var config = new TableConfiguration(name)
            {
                Columns = keys.Concat(new[] { TableConfiguration.PivotColumn }).Concat(rest)
                    .Concat(new[] { Time, TableConfiguration.GraphColumn, EventCount }).ToArray(),
                ChartType = ChartType.StackedLine,
            };
            config.AddColumnRole(ColumnRole.StartTime, Time);
            return config;
        }

        var stacksByThread = Preset("Stacks by Event, Process, Thread, Sample",
            new[] { Event, Process, ThreadId, Sample }, new[] { Depth, Frame, Module, Function, Cpu, Samples, EstimatedCpuUsage });
        var bySample = Preset("Stacks by Sample",
            new[] { Sample }, new[] { Event, Process, ThreadId, Cpu, Depth, Frame, Module, Function, Samples, EstimatedCpuUsage });
        var byFrame = Preset("Hottest Frames by Event, Function",
            new[] { Event, Function }, new[] { Module, Process, ThreadId, Depth, Sample, Samples, EstimatedCpuUsage });

        tableBuilder
            .AddTableConfiguration(stacksByThread)
            .AddTableConfiguration(bySample)
            .AddTableConfiguration(byFrame)
            .SetDefaultTableConfiguration(stacksByThread)
            .SetRowCount((int)Math.Min(total, int.MaxValue))
            .AddColumn(Sample, Projection.CreateUsingFuncAdaptor(r => rowSample[r]))
            .AddColumn(Event, Projection.CreateUsingFuncAdaptor(r => events[ev[rowSample[r]]].Name))
            .AddColumn(Process, Projection.CreateUsingFuncAdaptor(r => processLabel[pid[rowSample[r]]]))
            .AddColumn(ThreadId, Projection.CreateUsingFuncAdaptor(r => tid[rowSample[r]]))
            .AddColumn(Cpu, Projection.CreateUsingFuncAdaptor(r => (int)cpu[rowSample[r]]))
            .AddColumn(Depth, Projection.CreateUsingFuncAdaptor(r => (int)rowDepth[r]))
            .AddColumn(Frame, Projection.CreateUsingFuncAdaptor(r => frameLabel[FrameOf(r)]))
            .AddColumn(Module, Projection.CreateUsingFuncAdaptor(r => map.Frames[FrameOf(r)].Module))
            .AddColumn(Function, Projection.CreateUsingFuncAdaptor(r => map.Frames[FrameOf(r)].Function))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(r => new Timestamp(time[rowSample[r]])))
            .AddColumn(EventCount, Projection.CreateUsingFuncAdaptor(r => rowDepth[r] == 0 ? (long)events[ev[rowSample[r]]].SampleAfterValue : 0L))
            .AddColumn(Samples, Projection.CreateUsingFuncAdaptor(r => rowDepth[r] == 0 ? 1 : 0))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(r =>
                double.IsNaN(estimatedCpuUsage[rowSample[r]]) ? double.NaN :
                rowDepth[r] == 0 ? estimatedCpuUsage[rowSample[r]] : 0));
    }

    private static Guid Id(int n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"{{A7B3C1D9-{n:X4}-4E2F-8A6B-{Suffix}}}"));

    private static ColumnConfiguration Column(int n, string name, string description, int width) =>
        new(new ColumnMetadata(Id(n), name, description), new UIHints { Width = width, IsVisible = true });
}
