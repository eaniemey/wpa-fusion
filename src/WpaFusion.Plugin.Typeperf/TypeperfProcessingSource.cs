using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Typeperf.Parsing;
using WpaFusion.Plugin.Typeperf.Tables.Counters;

namespace WpaFusion.Plugin.Typeperf;

/// <summary>Opens typeperf CSV files (PDH-CSV 4.0).</summary>
[ProcessingSource("{6C3D3A8E-2B4F-4E0B-9A6E-7F1C5D2A9B31}", "WPA-Fusion Typeperf Counters", "Windows performance counters recorded with typeperf, aligned to the ETW timeline.")]
[FileDataSource(".csv", "typeperf CSV (performance counters)")]
public sealed class TypeperfProcessingSource : ProcessingSource
{
    protected override IProcessingSourceTableProvider CreateTableProvider() =>
        TableDiscovery.CreateForNamespace(typeof(TypeperfCounterTable).Namespace, this);

    protected override bool IsDataSourceSupportedCore(IDataSource dataSource) =>
        dataSource is FileDataSource file && TypePerfCsv.LooksLikeTypePerf(file.FullPath);

    protected override ICustomDataProcessor CreateProcessorCore(IEnumerable<IDataSource> dataSources, IProcessorEnvironment processorEnvironment, ProcessorOptions options) =>
        new TypeperfProcessor(dataSources.OfType<FileDataSource>().Select(f => f.FullPath).ToList(), options, ApplicationEnvironment, processorEnvironment);

    public override ProcessingSourceInfo GetAboutInfo() => WpaFusionAbout.Info;
}

/// <summary>Loads the CSVs and serves the counter table.</summary>
internal sealed class TypeperfProcessor : CustomDataProcessor
{
    private readonly IReadOnlyList<string> _paths;
    private TypeperfData? _data;

    public TypeperfProcessor(IReadOnlyList<string> paths, ProcessorOptions options, IApplicationEnvironment applicationEnvironment, IProcessorEnvironment processorEnvironment)
        : base(options, applicationEnvironment, processorEnvironment)
    {
        _paths = paths;
    }

    public override DataSourceInfo GetDataSourceInfo()
    {
        var data = _data ?? throw new InvalidOperationException("Processing has not run.");
        if (data.SampleTimeNs.Count == 0)
        {
            return new DataSourceInfo(0, 1, data.ZeroUtc);
        }

        var first = data.SampleTimeNs[0];
        var last = Math.Max(first + 1, data.SampleTimeNs[^1]);
        return new DataSourceInfo(first, last, data.ZeroUtc.AddTicks(first / 100));
    }

    protected override Task ProcessAsyncCore(IProgress<int> progress, CancellationToken cancellationToken)
    {
        var parts = new List<TypeperfData>();
        foreach (var path in _paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeline = Timeline.TryForDataFile(path);
            var part = TypeperfData.Load(path, timeline);
            Logger?.Info($"WPA-Fusion Typeperf: {Path.GetFileName(path)}: {part.Counters.Count} counters, {part.SampleTimeNs.Count} samples, time from {part.TimeSource}, {part.SkippedRows} unreadable rows.");
            parts.Add(part);
        }

        _data = TypeperfData.Merge(parts);
        progress.Report(100);
        return Task.CompletedTask;
    }

    protected override void BuildTableCore(TableDescriptor tableDescriptor, ITableBuilder tableBuilder)
    {
        if (tableDescriptor.Guid == TypeperfCounterTable.TableDescriptor.Guid)
        {
            TypeperfCounterTable.Build(tableBuilder, _data ?? throw new InvalidOperationException("Processing has not run."));
        }
    }
}
