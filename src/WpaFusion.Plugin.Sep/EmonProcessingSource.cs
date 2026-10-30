using Microsoft.Performance.SDK.Processing;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tma;

namespace WpaFusion.Plugin.Sep;

[ProcessingSource("{21B468A1-45C7-40B4-8AA1-ED1E0F81D641}", "WPA-Fusion EMON", "Intel MPP metrics from EMON EDP interval counters, aligned to ETW.")]
[FileDataSource(".json", "WPA-Fusion EMON metrics descriptor (*.emon.json)")]
public sealed class EmonProcessingSource : ProcessingSource
{
    protected override IProcessingSourceTableProvider CreateTableProvider() =>
        new SourceTableProvider(TmaTimelineTable.TableDescriptor, TmaSummaryTable.TableDescriptor);

    protected override bool IsDataSourceSupportedCore(IDataSource dataSource) =>
        dataSource is FileDataSource file && file.FullPath.EndsWith(".emon.json", StringComparison.OrdinalIgnoreCase);

    protected override ICustomDataProcessor CreateProcessorCore(IEnumerable<IDataSource> dataSources,
        IProcessorEnvironment processorEnvironment, ProcessorOptions options) =>
        new EmonProcessor(dataSources.OfType<FileDataSource>().First().FullPath, options, ApplicationEnvironment, processorEnvironment);

    public override ProcessingSourceInfo GetAboutInfo() => new()
    {
        Owners = Array.Empty<ContactInfo>(),
        CopyrightNotice = "Internal Intel tool. EMON/MPP must be installed separately.",
        AdditionalInformation = new[] { "TMA values are calculated by Intel MPP; WPA-Fusion imports its interval and aggregated CSV outputs." },
    };
}

internal sealed class EmonProcessor : CustomDataProcessor
{
    private readonly string _path;
    private EmonMetricData? _data;
    private TmaAnalysis? _analysis;

    public EmonProcessor(string path, ProcessorOptions options, IApplicationEnvironment applicationEnvironment,
        IProcessorEnvironment processorEnvironment) : base(options, applicationEnvironment, processorEnvironment) => _path = path;

    protected override Task ProcessAsyncCore(IProgress<int> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _data = EmonMetricData.Load(_path);
        _analysis = TmaAnalysis.FromEmon(_data);
        Logger?.Info(_data.DefinitionSource is { } definition
            ? $"WPA-Fusion EMON hierarchy: original platform definition {definition}; omitted TMA nodes remain unavailable placeholders."
            : "WPA-Fusion EMON hierarchy: MPP CSV names only; no platform definition snapshot was available to restore omitted nodes.");
        Logger?.Info($"WPA-Fusion EMON source: {_data.Provenance}; cross-run alignment is comparison of separate executions, not process attribution.");
        Logger?.Info($"WPA-Fusion EMON: {_data.Metrics.Nodes.Count} MPP metrics, {_data.Intervals.Count} intervals; summary shows valid-interval arithmetic means with MPP aggregates retained separately.");
        progress.Report(100);
        return Task.CompletedTask;
    }

    public override DataSourceInfo GetDataSourceInfo()
    {
        var data = _data ?? throw new InvalidOperationException("EMON processing has not run.");
        return new DataSourceInfo(data.FirstNs, data.LastNs, data.ZeroUtc.AddTicks(data.FirstNs / 100));
    }

    public override bool DoesTableHaveData(TableDescriptor table) => _analysis is { IsAvailable: true } &&
        (table.Guid == TmaTimelineTable.TableDescriptor.Guid || table.Guid == TmaSummaryTable.TableDescriptor.Guid);

    protected override void BuildTableCore(TableDescriptor tableDescriptor, ITableBuilder tableBuilder)
    {
        var analysis = _analysis ?? throw new InvalidOperationException("EMON processing has not run.");
        if (tableDescriptor.Guid == TmaTimelineTable.TableDescriptor.Guid)
        {
            TmaTimelineTable.Build(tableBuilder, analysis);
        }
        else if (tableDescriptor.Guid == TmaSummaryTable.TableDescriptor.Guid)
        {
            TmaSummaryTable.Build(tableBuilder, analysis);
        }
    }
}