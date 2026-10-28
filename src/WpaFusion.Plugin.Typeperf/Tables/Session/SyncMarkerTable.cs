using System.Globalization;
using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Typeperf.Tables.Session;

/// <summary>Clock sync markers on the ETW timeline. Lines up with the WPA-Fusion-Capture events in Generic Events.</summary>
[Table]
public static class SyncMarkerTable
{
    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{3F6A2D10-9B4C-4E7D-A1F2-5C8E9B0D7A61}"),
        "Sync Markers",
        "WPA-Fusion clock sync markers. Time should match the WPA-Fusion-Capture events in Generic Events.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Marker = new(
        new ColumnMetadata(Guid.Parse("{3F6A2D10-0001-4E7D-A1F2-5C8E9B0D7A61}"), "Marker", "Marker name and index."),
        new UIHints { Width = 100, IsVisible = true });

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Guid.Parse("{3F6A2D10-0002-4E7D-A1F2-5C8E9B0D7A61}"), "Time", "The marker event's ETW timestamp on the timeline."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration QpcTime = new(
        new ColumnMetadata(Guid.Parse("{3F6A2D10-0003-4E7D-A1F2-5C8E9B0D7A61}"), "QPC Read Time", "Time of the QPC read taken just before writing the marker."),
        new UIHints { Width = 120, IsVisible = true, AggregationMode = AggregationMode.Min });

    internal static readonly ColumnConfiguration EtwLagUs = new(
        new ColumnMetadata(Guid.Parse("{3F6A2D10-0004-4E7D-A1F2-5C8E9B0D7A61}"), "ETW Lag (us)", "ETW event time minus the QPC read before it."),
        new UIHints { Width = 100, IsVisible = true, AggregationMode = AggregationMode.Max, CellFormat = "N2" });

    internal static readonly ColumnConfiguration TscResidualNs = new(
        new ColumnMetadata(Guid.Parse("{3F6A2D10-0005-4E7D-A1F2-5C8E9B0D7A61}"), "TSC-QPC Residual (ns)", "Time of the rdtsc read (through the clock map) minus time of the QPC read."),
        new UIHints { Width = 140, IsVisible = true, AggregationMode = AggregationMode.Max });

    internal static void Build(ITableBuilder tableBuilder, SessionView view)
    {
        var m = view.Markers;
        var config = new TableConfiguration("Markers")
        {
            Columns = new[] { Marker, TableConfiguration.PivotColumn, EtwLagUs, TscResidualNs, QpcTime, TableConfiguration.GraphColumn, Time },
            ChartType = ChartType.PointInTime,
        };
        config.AddColumnRole(ColumnRole.StartTime, Time);

        tableBuilder
            .AddTableConfiguration(config)
            .SetDefaultTableConfiguration(config)
            .SetRowCount(m.Count)
            .AddColumn(Marker, Projection.CreateUsingFuncAdaptor(i => m[i].Name))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(m[i].TimeNs)))
            .AddColumn(QpcTime, Projection.CreateUsingFuncAdaptor(i => new Timestamp(m[i].QpcNs)))
            .AddColumn(EtwLagUs, Projection.CreateUsingFuncAdaptor(i => m[i].EtwNs is long e ? (e - m[i].QpcNs) / 1000.0 : double.NaN))
            .AddColumn(TscResidualNs, Projection.CreateUsingFuncAdaptor(i => m[i].TscNs - m[i].QpcNs));
    }
}

/// <summary>Run metadata as name/value rows.</summary>
[Table]
public static class SessionInfoTable
{
    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{5A9C1E33-7D2B-4B6F-9E04-3C1A8F6D2B97}"),
        "Session Info",
        "WPA-Fusion run metadata from session.json.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.Table,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Name = new(
        new ColumnMetadata(Guid.Parse("{5A9C1E33-0001-4B6F-9E04-3C1A8F6D2B97}"), "Property", "Property name."),
        new UIHints { Width = 200, IsVisible = true });

    internal static readonly ColumnConfiguration Value = new(
        new ColumnMetadata(Guid.Parse("{5A9C1E33-0002-4B6F-9E04-3C1A8F6D2B97}"), "Value", "Property value."),
        new UIHints { Width = 600, IsVisible = true });

    internal static void Build(ITableBuilder tableBuilder, SessionView view)
    {
        var s = view.Manifest;
        var t = view.Timeline;
        var rows = new List<(string, string)>
        {
            ("Run", s.RunId),
            ("Machine", s.Machine),
            ("Time zero", t.Origin == Common.TimelineOrigin.EtlZero ? "ETL time zero (read back from trace)" : "Session start sync point"),
            ("Time zero (UTC)", t.ZeroUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("QPC frequency (Hz)", s.QpcFrequency.ToString(CultureInfo.InvariantCulture)),
            ("TSC frequency (Hz)", t.Clocks.TscFrequency.ToString("F0", CultureInfo.InvariantCulture)),
            ("Sync start spread (TSC ticks)", s.SyncStart?.TscSpread.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            ("Sync end spread (TSC ticks)", s.SyncEnd?.TscSpread.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            ("ETL", s.Files.Etl ?? "(none)"),
            ("Typeperf CSV", s.Files.TypeperfCsv ?? "(none)"),
            ("SEP .tb7", s.Files.Tb7 ?? "(none)"),
        };
        rows.AddRange(s.Tools.Select(kv => ($"Command: {kv.Key}", kv.Value)));

        var config = new TableConfiguration("Session") { Columns = new[] { Name, Value } };
        tableBuilder
            .AddTableConfiguration(config)
            .SetDefaultTableConfiguration(config)
            .SetRowCount(rows.Count)
            .AddColumn(Name, Projection.CreateUsingFuncAdaptor(i => rows[i].Item1))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => rows[i].Item2));
    }
}
