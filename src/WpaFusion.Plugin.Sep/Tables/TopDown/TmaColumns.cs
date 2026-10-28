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

    public static ColumnConfiguration Value(string tableGuidSuffix, AggregationMode aggregation) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-00{"09"}-4E1A-9C3D-{tableGuidSuffix}}}"), "% of Slots", "Share of pipeline slots (percent). NaN when inputs are missing."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = aggregation, CellFormat = "N1" });

    public static ColumnConfiguration EstimatedCpuUsage(string tableGuidSuffix) => new(
        new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-0013-4E1A-9C3D-{tableGuidSuffix}}}"), "Est. % CPU Usage",
            "Estimated share of total logical-processor capacity from CPU_CLK_UNHALTED.REF_TSC samples in this window."),
        new UIHints { Width = 140, IsVisible = true, AggregationMode = AggregationMode.Max, CellFormat = "N3",
            SortOrder = SortOrder.Descending, SortPriority = 1 });

    public static string PathPart(TmaNode node, int level) => level <= node.Path.Count ? node.Path[level - 1] : string.Empty;

    public static string DeeperPart(TmaNode node) => node.Path.Count > 3 ? string.Join(" > ", node.Path.Skip(3)) : string.Empty;

    public static string StatusOf(TmaResult r) => r.IsAvailable ? "ok" : "missing " + string.Join(", ", r.MissingInputs);

    private static ColumnConfiguration Col(string n, string suffix, string name, string description, int width) =>
        new(new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-00{n}-4E1A-9C3D-{suffix}}}"), name, description), new UIHints { Width = width, IsVisible = true });
}
