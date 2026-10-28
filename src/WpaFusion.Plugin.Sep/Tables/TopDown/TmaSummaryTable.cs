using Microsoft.Performance.SDK.Processing;
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
        "Intel Top-down Microarchitecture Analysis over the whole SEP collection, from SEP EDP formulas for the collection platform.",
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
    internal static readonly ColumnConfiguration Value = TmaColumns.Value(Suffix, AggregationMode.Max);
    internal static readonly ColumnConfiguration EstimatedCpuUsage = TmaColumns.EstimatedCpuUsage(Suffix);

    internal static void Build(ITableBuilder tableBuilder, TmaAnalysis analysis)
    {
        var rows = analysis.Summary?.Results ?? Array.Empty<TmaResult>();
        var tree = new TableConfiguration("Top-Down Tree")
        {
            Columns = new[] { Level1, Level2, Level3, Deeper, TableConfiguration.PivotColumn, Depth, Status, Description, EstimatedCpuUsage, TableConfiguration.GraphColumn, Value },
        };
        var flat = new TableConfiguration("All Nodes")
        {
            Columns = new[] { Metric, TableConfiguration.PivotColumn, Depth, Level1, Level2, Level3, Status, Description, EstimatedCpuUsage, TableConfiguration.GraphColumn, Value },
        };

        tableBuilder
            .AddTableConfiguration(tree)
            .AddTableConfiguration(flat)
            .SetDefaultTableConfiguration(tree)
            .SetRowCount(rows.Count)
            .AddColumn(Level1, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 1)))
            .AddColumn(Level2, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 2)))
            .AddColumn(Level3, Projection.CreateUsingFuncAdaptor(i => TmaColumns.PathPart(rows[i].Node, 3)))
            .AddColumn(Deeper, Projection.CreateUsingFuncAdaptor(i => TmaColumns.DeeperPart(rows[i].Node)))
            .AddColumn(Metric, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Name))
            .AddColumn(Depth, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Level))
            .AddColumn(Status, Projection.CreateUsingFuncAdaptor(i => TmaColumns.StatusOf(rows[i])))
            .AddColumn(Description, Projection.CreateUsingFuncAdaptor(i => rows[i].Node.Description))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i => analysis.Summary?.EstimatedCpuUsagePercent ?? double.NaN))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => rows[i].Value));
    }
}
