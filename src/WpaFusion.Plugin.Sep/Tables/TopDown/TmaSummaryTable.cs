using Microsoft.Performance.SDK.Processing;
using Microsoft.Performance.SDK;
using WpaFusion.Tma;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>Whole-run TMA top-down breakdown: one row per TMA node.</summary>
[Table]
public static class TmaSummaryTable
{
    private const string Suffix = "5E6F7A8B9C01";

    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{D4B8F2C6-1A2B-4E1A-9C3D-5E6F7A8B9C01}"),
        "Top-Down Summary (TMA)",
        "Original-order platform TMA hierarchy with valid-interval arithmetic averages; unavailable branches remain visible and official MPP aggregates are retained separately.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.Table,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Level1 = TmaColumns.Level1(Suffix);
    internal static readonly ColumnConfiguration Level2 = TmaColumns.Level2(Suffix);
    internal static readonly ColumnConfiguration Level3 = TmaColumns.Level3(Suffix);
    internal static readonly ColumnConfiguration Deeper = TmaColumns.Deeper(Suffix);
    internal static readonly ColumnConfiguration Metric = TmaColumns.Metric(Suffix);
    internal static readonly ColumnConfiguration Depth = TmaColumns.Depth(Suffix);
    internal static readonly ColumnConfiguration Status = TmaColumns.Status(Suffix);
    internal static readonly ColumnConfiguration Description = TmaColumns.Description(Suffix);
    internal static readonly ColumnConfiguration Value = TmaColumns.AverageValue(Suffix);
    internal static readonly ColumnConfiguration MppAggregate = TmaColumns.Value(Suffix, AggregationMode.Max, allMetrics: true, name: "MPP Aggregate");
    internal static readonly ColumnConfiguration ValidIntervals = TmaColumns.ValidIntervals(Suffix);
    internal static readonly ColumnConfiguration SourceRun = TmaColumns.SourceRun(Suffix);
    internal static readonly ColumnConfiguration DefinitionOrder = TmaColumns.DefinitionOrder(Suffix);
    internal static readonly ColumnConfiguration Category = TmaColumns.Category(Suffix);

    internal static void Build(ITableBuilder tableBuilder, TmaAnalysis analysis)
    {
        var rows = analysis.Summary?.Results ?? Array.Empty<TmaResult>();
        var levels = new[] { Level1, Level2, Level3 }.Concat(
            Enumerable.Range(4, Math.Max(0, (analysis.Metrics?.MaxLevel ?? 0) - 3))
                .Select(level => TmaColumns.HierarchyLevel(Suffix, level))).ToArray();
        var tree = new TableConfiguration("Top-Down Tree")
        {
            Columns = levels.Concat(new[] { TableConfiguration.PivotColumn, DefinitionOrder, Metric, SourceRun, Category,
                Depth, Status, Description, ValidIntervals, MppAggregate, TableConfiguration.GraphColumn, Value }).ToArray(),
            InitialFilterQuery = "[Category]:=\"TMA\"",
            InitialFilterShouldKeep = true,
        };
        var flat = new TableConfiguration("All Nodes")
        {
            Columns = new[] { Metric, SourceRun, TableConfiguration.PivotColumn, Depth, Level1, Level2, Level3, Status, Description, ValidIntervals, MppAggregate, TableConfiguration.GraphColumn, Value },
        };

        var builder = tableBuilder
            .AddTableConfiguration(tree)
            .AddTableConfiguration(flat)
            .SetDefaultTableConfiguration(tree)
            .SetRowCount(rows.Count)
            .AddColumn(DefinitionOrder, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.DefinitionOrder))
            .AddColumn(Category, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Groups))
            .AddColumn(SourceRun, Projection.Constant(analysis.SourceRun))
            .AddColumn(Level1, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 1)))
            .AddColumn(Level2, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 2)))
            .AddColumn(Level3, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 3)))
            .AddColumn(Deeper, Projection.CreateUsingFuncAdaptor(i => TmaColumns.DeeperPart(rows[i].Node)))
            .AddColumn(Metric, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Name))
            .AddColumn(Depth, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Level))
            .AddColumn(Status, Projection.CreateUsingFuncAdaptor(i => TmaColumns.StatusOf(rows[i])))
            .AddColumn(Description, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Description))
            .AddColumn(ValidIntervals, Projection.CreateUsingFuncAdaptor(i => analysis.ValidIntervalCounts.ElementAtOrDefault(i)))
            .AddColumn(MppAggregate, Projection.CreateUsingFuncAdaptor(i => analysis.MppAggregates.ElementAtOrDefault(i)?.Value ?? double.NaN))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => rows[i].Value));

        for (var index = 3; index < levels.Length; index++)
        {
            var level = index + 1;
            builder.AddColumn(levels[index], Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, level)));
        }
    }
}
