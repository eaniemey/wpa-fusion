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
        "Original-order platform TMA hierarchy at every depth, including unavailable branches. Values come from Intel MPP; all EDP metrics remain available in a separate flat preset.",
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
    internal static readonly ColumnConfiguration Value = TmaColumns.Value(Suffix, AggregationMode.Average, allMetrics: true);
    internal static readonly ColumnConfiguration Category = TmaColumns.Category(Suffix);
    internal static readonly ColumnConfiguration Description = TmaColumns.Description(Suffix);
    internal static readonly ColumnConfiguration AverageValue = TmaColumns.AverageValue(Suffix);
    internal static readonly ColumnConfiguration ValidIntervals = TmaColumns.ValidIntervals(Suffix);
    internal static readonly ColumnConfiguration SourceRun = TmaColumns.SourceRun(Suffix);
    internal static readonly ColumnConfiguration DefinitionOrder = TmaColumns.DefinitionOrder(Suffix);

    internal static readonly ColumnConfiguration SliceStart = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0010-4E1A-9C3D-{Suffix}}}"), "Slice Start", "Start of the time slice on the ETW timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration SliceDuration = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0011-4E1A-9C3D-{Suffix}}}"), "Slice Duration", "Length of the time slice."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Max });

    internal static readonly ColumnConfiguration SliceSamples = new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0012-4E1A-9C3D-{Suffix}}}"), "EMON Intervals", "Number of EMON measurement intervals represented by this row."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Max });

    internal static void Build(ITableBuilder tableBuilder, TmaAnalysis analysis)
    {
        var slices = analysis.Slices;
        var nodesPerSlice = analysis.Metrics?.Nodes.Count ?? 0;
        var rowCount = slices.Count * nodesPerSlice;
        var levels = new[] { Level1, Level2, Level3 }.Concat(
            Enumerable.Range(4, Math.Max(0, (analysis.Metrics?.MaxLevel ?? 0) - 3))
                .Select(level => TmaColumns.HierarchyLevel(Suffix, level))).ToArray();
        var treeColumns = levels.Concat(new[] { TableConfiguration.PivotColumn, DefinitionOrder, Metric, SourceRun,
            Category, Depth, Status, Description, AverageValue, ValidIntervals, SliceSamples, SliceDuration,
            SliceStart, TableConfiguration.GraphColumn, Value }).ToArray();

        var tree = new TableConfiguration("Top-Down Tree Over Time")
        {
            Columns = treeColumns,
            ChartType = ChartType.Line,
            InitialFilterQuery = "[Category]:=\"TMA\"",
            InitialFilterShouldKeep = true,
        };
        tree.AddColumnRole(ColumnRole.StartTime, SliceStart);
        tree.AddColumnRole(ColumnRole.Duration, SliceDuration);

        var all = new TableConfiguration("All EDP Metrics Over Time")
        {
            Columns = new[] { Metric, SourceRun, TableConfiguration.PivotColumn, Category, Depth, Status, Description, AverageValue, ValidIntervals, SliceSamples, SliceDuration, SliceStart, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
            InitialFilterQuery = "[Status]:=\"ok\"",
            InitialFilterShouldKeep = true,
        };
        all.AddColumnRole(ColumnRole.StartTime, SliceStart);
        all.AddColumnRole(ColumnRole.Duration, SliceDuration);

        var tma = new TableConfiguration("TMA Metrics Over Time")
        {
            Columns = treeColumns,
            ChartType = ChartType.Line,
            InitialFilterQuery = "[Category]:=\"TMA\"",
            InitialFilterShouldKeep = true,
        };
        tma.AddColumnRole(ColumnRole.StartTime, SliceStart);
        tma.AddColumnRole(ColumnRole.Duration, SliceDuration);

        var builder = tableBuilder
            .AddTableConfiguration(tree)
            .AddTableConfiguration(all)
            .AddTableConfiguration(tma)
            .SetDefaultTableConfiguration(tma)
            .SetRowCount(rowCount)
            .AddColumn(DefinitionOrder, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node.DefinitionOrder))
            .AddColumn(SourceRun, Projection.Constant(analysis.SourceRun))
            .AddColumn(AverageValue, Projection.CreateUsingFuncAdaptor(i => analysis.MetricAverages.ElementAtOrDefault(i % nodesPerSlice)?.Value ?? double.NaN))
            .AddColumn(ValidIntervals, Projection.CreateUsingFuncAdaptor(i => analysis.ValidIntervalCounts.ElementAtOrDefault(i % nodesPerSlice)))
            .AddColumn(Category, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node.Groups))
            .AddColumn(Description, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node.Description))
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
            .AddColumn(SliceSamples, Projection.CreateUsingFuncAdaptor(i => slices[i / nodesPerSlice].Samples));

        for (var index = 3; index < levels.Length; index++)
        {
            var level = index + 1;
            builder.AddColumn(levels[index], Projection.CreateUsingFuncAdaptor(i =>
                TmaColumns.PathPart(slices[i / nodesPerSlice].Results[i % nodesPerSlice].Node, level)));
        }
    }
}
