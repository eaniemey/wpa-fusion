using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

internal static class SepCpuUsageColumn
{
    public static ColumnConfiguration Create(Guid id, AggregationMode aggregation) => new(
        new ColumnMetadata(id, "Est. % CPU Usage",
            "Estimated share of total logical-processor capacity from CPU_CLK_UNHALTED.REF_TSC sample weights."),
        new UIHints
        {
            Width = 140,
            IsVisible = true,
            AggregationMode = aggregation,
            CellFormat = "N3",
            SortOrder = SortOrder.Descending,
            SortPriority = 1,
        });
}