using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>TMA breakdown per time slice: one row per (slice, TMA node), graphable over time.</summary>
[Table]
public static class TmaTimelineTable
{
    private const string Suffix = "5E6F7A8B9C02";

    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{D4B8F2C6-1A2B-4E1A-9C3D-5E6F7A8B9C02}"),
        "Top-Down Over Time (TMA)",
        "Intel TMA metrics per time slice (default 1 s; set WPA_FUSION_TMA_SLICE_MS to change).",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Level1 = TmaColumns.Level1(Suffix);
    internal static readonly ColumnConfiguration Level2 = TmaColumns.Level2(Suffix);
    internal static readonly ColumnConfiguration Level3 = TmaColumns.Level3(Suffix);
    internal static readonly ColumnConfiguration Deeper = TmaColumns.Deeper(Suffix);
    internal static readonly ColumnConfiguration Metric = TmaColumns.Metric(Suffix);
    internal static readonly ColumnConfiguration Depth = TmaColumns.Depth(Suffix);
    internal static readonly ColumnConfiguration Status = TmaColumns.Status(Suffix);
    internal static readonly ColumnConfiguration Value = TmaColumns.Value(Suffix, AggregationMode.Average);
    internal static readonly ColumnConfiguration EstimatedCpuUsage = TmaColumns.EstimatedCpuUsage(Suffix);

    internal static readonly ColumnConfiguration SliceStart = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0010-4E1A-9C3D-{Suffix}}}"), "Slice Start", "Start of the time slice on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration SliceDuration = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0011-4E1A-9C3D-{Suffix}}}"), "Slice Duration", "Length of the time slice."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Max });

    internal static readonly ColumnConfiguration SliceSamples = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0012-4E1A-9C3D-{Suffix}}}"), "Slice Samples", "SEP samples in the slice; few samples mean noisy metrics."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Max });

    internal static void Build(ITableBuilder tableBuilder, TmaAnalysis analysis)
    {
        var slices = analysis.Slices;
        var nodesPerSlice = analysis.Metrics?.Nodes.Count ?? 0;
        var rowCount = slices.Count * nodesPerSlice;

        var level1 = new TableConfiguration("Level 1 Over Time")
        {
            Columns = new[] { Level1, TableConfiguration.PivotColumn, Depth, Status, SliceSamples, SliceDuration, SliceStart, EstimatedCpuUsage, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
            InitialFilterQuery = "[Depth]:=1",
            InitialFilterShouldKeep = true,
        };
        level1.AddColumnRole(ColumnRole.StartTime, SliceStart);
        level1.AddColumnRole(ColumnRole.Duration, SliceDuration);

        var tree = new TableConfiguration("Top-Down Tree Over Time")
        {
            Columns = new[] { Level1, Level2, Level3, Deeper, TableConfiguration.PivotColumn, Depth, Status, SliceSamples, SliceDuration, SliceStart, EstimatedCpuUsage, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
        };
        tree.AddColumnRole(ColumnRole.StartTime, SliceStart);
        tree.AddColumnRole(ColumnRole.Duration, SliceDuration);

        tableBuilder
            .AddTableConfiguration(level1)
            .AddTableConfiguration(tree)
            .SetDefaultTableConfiguration(level1)
            .SetRowCount(rowCount)
            .AddColumn(Level1, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node, 1)))
            .AddColumn(Level2, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node, 2)))
            .AddColumn(Level3, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node, 3)))
            .AddColumn(Deeper, Projection.CreateUsingFuncAdaptor(i => TmaColumns.DeeperPart(slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node)))
            .AddColumn(Metric, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node.Name))
            .AddColumn(Depth, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node.Level))
            .AddColumn(Status, Projection.CreateUsingFuncAdaptor(i => TmaColumns.StatusOf(slices[i / nodesPerSlice].Results[i % nodesPerSlice])))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Value))
            .AddColumn(SliceStart, Projection.CreateUsingFuncAdaptor(i => new Timestamp(slices[i / nodesPerSlice].StartNs)))
            .AddColumn(SliceDuration, Projection.CreateUsingFuncAdaptor(i => new TimestampDelta(slices[i / nodesPerSlice].DurationNs)))
            .AddColumn(SliceSamples, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Samples))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].EstimatedCpuUsagePercent));
    }
}
