using Microsoft.Performance.SDK.Processing;
using WpaFusion.Tma;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>Column definitions shared by the TMA tables.</summary>
internal static class TmaColumns
{
    public static ColumnConfiguration Level1(string tableGuidSuffix) => Col("01", tableGuidSuffix, "Level 1", "TMA level 1 category (Frontend_Bound, Bad_Speculation, Backend_Bound, Retiring).", 160);

    public static ColumnConfiguration Level2(string tableGuidSuffix) => Col("02", tableGuidSuffix, "Level 2", "TMA level 2 node; empty for level 1 rows.", 160);

    public static ColumnConfiguration Level3(string tableGuidSuffix) => Col("03", tableGuidSuffix, "Level 3", "TMA level 3 node; empty for shallower rows.", 160);

    public static ColumnConfiguration Deeper(string tableGuidSuffix) => Col("04", tableGuidSuffix, "Level 4+", "Deeper TMA path below level 3, joined with ' > '.", 200);

    public static ColumnConfiguration Metric(string tableGuidSuffix) => Col("05", tableGuidSuffix, "Metric", "TMA node name.", 200);

    public static ColumnConfiguration Depth(string tableGuidSuffix) => Col("06", tableGuidSuffix, "Depth", "TMA level of this node.", 50);

    public static ColumnConfiguration Status(string tableGuidSuffix) => Col("07", tableGuidSuffix, "Status", "'ok', or the event/constant that was not collected.", 260);

    public static ColumnConfiguration Description(string tableGuidSuffix) => Col("08", tableGuidSuffix, "Description", "SEP EDP metric description.", 400);

    public static ColumnConfiguration Category(string tableGuidSuffix) => Col("0A", tableGuidSuffix, "Category", "Category from the SEP EDP definition.", 160);
    public static ColumnConfiguration SourceRun(string tableGuidSuffix) => Col("0D", tableGuidSuffix, "Capture Run", "Original collector/run identity; cross-run aligned values come from separate workload executions.", 260);

    public static ColumnConfiguration HierarchyLevel(string tableGuidSuffix, int level) =>
        Col((0x20 + level).ToString("X2", System.Globalization.CultureInfo.InvariantCulture), tableGuidSuffix,
            $"Level {level}", "Original TMA hierarchy level from the platform EDP definition.", 180);

    public static ColumnConfiguration DefinitionOrder(string tableGuidSuffix) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-000E-4E1A-9C3D-{tableGuidSuffix}}}"), "Definition Order", "Original EDP definition order; Min preserves sibling order when branches are collapsed."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Min,
            SortOrder = SortOrder.Ascending, SortPriority = 1 });

    public static ColumnConfiguration AverageValue(string tableGuidSuffix) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-000B-4E1A-9C3D-{tableGuidSuffix}}}"), "Average Value", "Arithmetic mean of this metric's finite interval values across the whole run, excluding missing values. Not time-weighted; not the MPP aggregate."),
        new UIHints { Width = 130, IsVisible = true, AggregationMode = AggregationMode.Max, CellFormat = "N3" });

    public static ColumnConfiguration ValidIntervals(string tableGuidSuffix) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-000C-4E1A-9C3D-{tableGuidSuffix}}}"), "Valid Intervals", "Number of valid intervals used in this metric's whole-run average."),
        new UIHints { Width = 110, IsVisible = true, AggregationMode = AggregationMode.Max });

    public static ColumnConfiguration Value(string tableGuidSuffix, AggregationMode aggregation, bool allMetrics = false, string? name = null) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-00{"09"}-4E1A-9C3D-{tableGuidSuffix}}}"), name ?? (allMetrics ? "Value" : "% of Slots"), "EDP formula result in the metric's named units; top-down metrics are percentages. NaN when unavailable."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = aggregation, CellFormat = "N1" });

    public static ColumnConfiguration EstimatedCpuUsage(string tableGuidSuffix) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0013-4E1A-9C3D-{tableGuidSuffix}}}"), "Est. % CPU Usage",
            "Estimated share of total logical-processor capacity from CPU_CLK_UNHALTED.REF_TSC samples in this window."),
        new UIHints { Width = 140, IsVisible = true, AggregationMode = AggregationMode.Max, CellFormat = "N3",
            SortOrder = SortOrder.Descending, SortPriority = 1 });

    public static string PathPart(TmaNode node, int level)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.Level == level)
            {
                return current.Name;
            }
        }

        return string.Empty;
    }

    public static string DeeperPart(TmaNode node) => node.Path.Count > 3 ? string.Join(" > ", node.Path.Skip(3)) : string.Empty;

    public static string StatusOf(TmaResult r) => r.IsAvailable ? "ok" : "missing " + string.Join(", ", r.MissingInputs);

    private static ColumnConfiguration Col(string n, string suffix, string name, string description, int width) =>
        new(new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-00{n}-4E1A-9C3D-{suffix}}}"), name, description), new UIHints { Width = width, IsVisible = true });
}
