using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>Maps process-, module-, and function-scoped sampled TMA windows to WPA columns and presets.</summary>
[Table]
public static class TmaAttributedTable
{
    private const string Suffix = "5E6F7A8B9C03";
    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{D4B8F2C6-1A2B-4E1A-9C3D-5E6F7A8B9C03}"),
        "Top-Down by Process, Module, Function (TMA)",
        "Sampled TMA estimates recomputed per entity. Percentages are not additive; sparse samples and multiplexing can bias results.",
        "WPA-Fusion", isMetadataTable: false, defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    private static ColumnConfiguration Col(string id, string name, AggregationMode aggregation = AggregationMode.None) =>
        new(new ColumnMetadata(Guid.Parse($"{{D4B8F2C6-{id}-4E1A-9C3D-{Suffix}}}"), name),
            new UIHints { Width = 160, IsVisible = true, AggregationMode = aggregation });

    private static readonly ColumnConfiguration Scope = Col("0020", "Attribution Scope");
    private static readonly ColumnConfiguration Process = Col("0021", "Process");
    private static readonly ColumnConfiguration Module = Col("0022", "Module");
    private static readonly ColumnConfiguration Function = Col("0023", "Function");
    private static readonly ColumnConfiguration Window = Col("0024", "Window");
    private static readonly ColumnConfiguration Samples = Col("0025", "Supporting Samples", AggregationMode.Max);
    private static readonly ColumnConfiguration Time = Col("0026", "Slice Start", AggregationMode.Min);
    private static readonly ColumnConfiguration Duration = Col("0027", "Slice Duration", AggregationMode.Max);
    private static readonly ColumnConfiguration Instance = Col("0028", "Process Instance");
    private static readonly ColumnConfiguration Metric = TmaColumns.Metric(Suffix);
    private static readonly ColumnConfiguration Depth = TmaColumns.Depth(Suffix);
    private static readonly ColumnConfiguration Status = TmaColumns.Status(Suffix);
    private static readonly ColumnConfiguration Value = TmaColumns.Value(Suffix, AggregationMode.Max);
    private static readonly ColumnConfiguration EstimatedCpuUsage = TmaColumns.EstimatedCpuUsage(Suffix);

    internal static void Build(ITableBuilder builder, SepData data, TmaAnalysis analysis)
    {
        var windows = analysis.Attributed;
        var nodes = analysis.Metrics?.Nodes.Count ?? 0;
        AttributedTmaWindow Row(int i) => windows[i / nodes];
        var defaultConfiguration = new TableConfiguration("By Process Over Time");
        foreach (var scope in new[] { "Process", "Module", "Function" })
        {
            foreach (var summary in new[] { false, true })
            {
                var configuration = !summary && scope == "Process" ? defaultConfiguration :
                    new TableConfiguration($"By {scope} {(summary ? "Whole Run" : "Over Time")}");
                configuration.Columns = new[] { Process, Instance, Module, Function, Metric,
                    TableConfiguration.PivotColumn, Scope, Window, Depth, Status, Samples, Time, Duration,
                    EstimatedCpuUsage, TableConfiguration.GraphColumn, Value };
                configuration.ChartType = ChartType.Line;
                configuration.InitialFilterQuery = $"[Attribution Scope]:=\"{scope}\" AND [Window]:=\"{(summary ? "Summary" : "Slice")}\" AND [Depth]:=1";
                configuration.InitialFilterShouldKeep = true;
                configuration.AddColumnRole(ColumnRole.StartTime, Time);
                configuration.AddColumnRole(ColumnRole.Duration, Duration);
                builder.AddTableConfiguration(configuration);
            }
        }

        builder.SetDefaultTableConfiguration(defaultConfiguration)
            .SetRowCount(checked(windows.Count * nodes))
            .AddColumn(Scope, Projection.CreateUsingFuncAdaptor(i => Row(i).Key.Scope))
            .AddColumn(Process, Projection.CreateUsingFuncAdaptor(i =>
                $"{(string.IsNullOrEmpty(Row(i).Key.ProcessName) ? data.ProcessName(Row(i).Key.Pid) : Row(i).Key.ProcessName)} ({Row(i).Key.Pid})"))
            .AddColumn(Instance, Projection.CreateUsingFuncAdaptor(i => Row(i).Key.ProcessStartNs == long.MinValue
                ? "Start unavailable" : Row(i).Key.ProcessStartNs.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .AddColumn(Module, Projection.CreateUsingFuncAdaptor(i => Row(i).Key.Module))
            .AddColumn(Function, Projection.CreateUsingFuncAdaptor(i => Row(i).Key.Function))
            .AddColumn(Window, Projection.CreateUsingFuncAdaptor(i => Row(i).IsSummary ? "Summary" : "Slice"))
            .AddColumn(Samples, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.Samples))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(Row(i).Window.StartNs)))
            .AddColumn(Duration, Projection.CreateUsingFuncAdaptor(i => new TimestampDelta(Row(i).Window.DurationNs)))
            .AddColumn(Metric, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.Results[i % nodes].Node.Name))
            .AddColumn(Depth, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.Results[i % nodes].Node.Level))
            .AddColumn(Status, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.Results[i % nodes].IsAvailable
                ? "Sampled estimate; inspect supporting samples; global multiplex scaling"
                : TmaColumns.StatusOf(Row(i).Window.Results[i % nodes])))
            .AddColumn(EstimatedCpuUsage, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.EstimatedCpuUsagePercent))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => Row(i).Window.Results[i % nodes].Value));
    }
}
