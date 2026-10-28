using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Typeperf.Tables.Counters;

/// <summary>Typeperf counter values: one row per (sample time, counter instance).</summary>
[Table]
public static class TypeperfCounterTable
{
    public static TableDescriptor TableDescriptor { get; } = new(
        Guid.Parse("{8E1F4B2C-6A3D-4C5E-B7F8-1D2E3F4A5B60}"),
        "Typeperf Counters",
        "Windows performance counter values sampled by typeperf.",
        "WPA-Fusion",
        isMetadataTable: false,
        defaultLayout: TableLayoutStyle.GraphAndTable,
        requiredDataCookers: null);

    internal static readonly ColumnConfiguration Machine = Column("{8E1F4B2C-0001-4C5E-B7F8-1D2E3F4A5B60}", "Machine", "Computer that recorded the counter.", 120);
    internal static readonly ColumnConfiguration Object = Column("{8E1F4B2C-0002-4C5E-B7F8-1D2E3F4A5B60}", "Object", "Typeperf object, e.g. Processor or Memory.", 160);
    internal static readonly ColumnConfiguration Counter = Column("{8E1F4B2C-0003-4C5E-B7F8-1D2E3F4A5B60}", "Counter", "Typeperf counter name.", 220);
    internal static readonly ColumnConfiguration Instance = Column("{8E1F4B2C-0004-4C5E-B7F8-1D2E3F4A5B60}", "Instance", "Counter instance, e.g. _Total or a process name. Empty for single-instance objects.", 140);
    internal static readonly ColumnConfiguration CounterPath = Column("{8E1F4B2C-0005-4C5E-B7F8-1D2E3F4A5B60}", "Counter Path", "Full Typeperf counter path.", 300);

    internal static readonly ColumnConfiguration Time = new(
        new ColumnMetadata(Guid.Parse("{8E1F4B2C-0006-4C5E-B7F8-1D2E3F4A5B60}"), "Time", "Sample time on the ETW timeline."),
        new UIHints { Width = 120, AggregationMode = AggregationMode.Min, IsVisible = true });

    internal static readonly ColumnConfiguration Duration = new(
        new ColumnMetadata(Guid.Parse("{8E1F4B2C-0009-4C5E-B7F8-1D2E3F4A5B60}"), "Duration", "How long the value holds: until the counter's next sample."),
        new UIHints { Width = 100, AggregationMode = AggregationMode.Max, IsVisible = true });

    internal static readonly ColumnConfiguration Value = new(
        new ColumnMetadata(Guid.Parse("{8E1F4B2C-0007-4C5E-B7F8-1D2E3F4A5B60}"), "Value", "Counter value at the sample time."),
        new UIHints { Width = 120, AggregationMode = AggregationMode.Average, IsVisible = true, CellFormat = "N2" });

    internal static readonly ColumnConfiguration Count = new(
        new ColumnMetadata(Guid.Parse("{8E1F4B2C-0008-4C5E-B7F8-1D2E3F4A5B60}"), "Count", "Number of samples."),
        new UIHints { Width = 70, AggregationMode = AggregationMode.Sum, IsVisible = true });

    internal static void Build(ITableBuilder tableBuilder, TypeperfData data)
    {
        var counters = data.Counters;
        var rowCounter = data.RowCounter;
        var rowTime = data.RowTimeNs;
        var rowValue = data.RowValue;
        var rowDuration = data.RowDurationNs;

        var byObject = new TableConfiguration("By Object, Counter, Instance")
        {
            Columns = new[] { Object, Counter, Instance, TableConfiguration.PivotColumn, Machine, CounterPath, Count, Time, Duration, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
        };
        AddTimeRoles(byObject);

        var byPath = new TableConfiguration("By Counter Path")
        {
            Columns = new[] { CounterPath, TableConfiguration.PivotColumn, Machine, Object, Counter, Instance, Count, Time, Duration, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
        };
        AddTimeRoles(byPath);

        var processorTime = Preset("Processor % Time", "[Object]:=\"Processor\" AND [Counter]:=\"% Processor Time\"");
        var memory = Preset("Memory", "[Object]:=\"Memory\"");
        var disk = Preset("Disk", "[Object]:=\"LogicalDisk\" OR [Object]:=\"PhysicalDisk\"");
        var processCpu = Preset("Process % Processor Time", "[Object]:=\"Process\" AND [Counter]:=\"% Processor Time\"");

        tableBuilder
            .AddTableConfiguration(byObject)
            .AddTableConfiguration(byPath)
            .AddTableConfiguration(processorTime)
            .AddTableConfiguration(memory)
            .AddTableConfiguration(disk)
            .AddTableConfiguration(processCpu)
            .SetDefaultTableConfiguration(byObject)
            .SetRowCount(data.RowCount)
            .AddColumn(Machine, Projection.CreateUsingFuncAdaptor(i => counters[rowCounter[i]].Machine))
            .AddColumn(Object, Projection.CreateUsingFuncAdaptor(i => counters[rowCounter[i]].Object))
            .AddColumn(Counter, Projection.CreateUsingFuncAdaptor(i => counters[rowCounter[i]].Counter))
            .AddColumn(Instance, Projection.CreateUsingFuncAdaptor(i => counters[rowCounter[i]].Instance))
            .AddColumn(CounterPath, Projection.CreateUsingFuncAdaptor(i => counters[rowCounter[i]].FullPath))
            .AddColumn(Time, Projection.CreateUsingFuncAdaptor(i => new Timestamp(rowTime[i])))
            .AddColumn(Duration, Projection.CreateUsingFuncAdaptor(i => new TimestampDelta(rowDuration[i])))
            .AddColumn(Value, Projection.CreateUsingFuncAdaptor(i => rowValue[i]))
            .AddColumn(Count, Projection.Constant(1));
    }

    private static TableConfiguration Preset(string name, string filter)
    {
        var config = new TableConfiguration(name)
        {
            Columns = new[] { Object, Counter, Instance, TableConfiguration.PivotColumn, Machine, CounterPath, Count, Time, Duration, TableConfiguration.GraphColumn, Value },
            ChartType = ChartType.Line,
            InitialFilterQuery = filter,
            InitialFilterShouldKeep = true,
        };
        AddTimeRoles(config);
        return config;
    }

    private static void AddTimeRoles(TableConfiguration configuration)
    {
        configuration.AddColumnRole(ColumnRole.StartTime, Time);
        configuration.AddColumnRole(ColumnRole.Duration, Duration);
    }

    private static ColumnConfiguration Column(string guid, string name, string description, int width) =>
        new(new ColumnMetadata(Guid.Parse(guid), name, description), new UIHints { Width = width, IsVisible = true });
}
