using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Sep.Tables;
using WpaFusion.Tbrw;

namespace WpaFusion.Plugin.Sep;

/// <summary>
/// WPA processing source that reads bound SEP .tb7 files through TBRW and exposes sample, stack,
/// tables aligned to the ETW timeline.
/// </summary>
[ProcessingSource("{9F2B6C4D-8E1A-4B3C-A5D7-6E0F1C2B3A49}", "WPA-Fusion SEP", "Intel SEP hardware event samples and stacks, aligned to the ETW timeline.")]
[FileDataSource(".tb7", "Intel SEP sampling result (.tb7)")]
public sealed class SepProcessingSource : ProcessingSource
{
    protected override IProcessingSourceTableProvider CreateTableProvider() =>
        new SourceTableProvider(SepSampleTable.TableDescriptor, SepCpuUsageTable.TableDescriptor,
            SepLbrCallStackTable.TableDescriptor, SepLbrStackFramesTable.TableDescriptor);

    protected override bool IsDataSourceSupportedCore(IDataSource dataSource) =>
        dataSource is FileDataSource file && string.Equals(Path.GetExtension(file.FullPath), ".tb7", StringComparison.OrdinalIgnoreCase);

    protected override ICustomDataProcessor CreateProcessorCore(IEnumerable<IDataSource> dataSources, IProcessorEnvironment processorEnvironment, ProcessorOptions options) =>
        new SepProcessor(dataSources.OfType<FileDataSource>().First().FullPath, options, ApplicationEnvironment, processorEnvironment);

    public override ProcessingSourceInfo GetAboutInfo() => new()
    {
        Owners = Array.Empty<ContactInfo>(),
        CopyrightNotice = "Internal tool. Not for redistribution. Uses Intel SEP's TBRW library from the local SEP install.",
        AdditionalInformation = new[]
        {
            $"SEP install: {SepInstall.Root} (override with {SepInstall.EnvironmentVariable}).",
            "TMA/EDP metric tables belong exclusively to the WPA-Fusion EMON processing source.",
        },
    };
}

/// <summary>Processes one SEP file, loads optional attribution sidecars, and supplies data to SEP tables.</summary>
internal sealed class SepProcessor : CustomDataProcessor
{
    private readonly string _path;
    private SepData? _data;

    public SepProcessor(string path, ProcessorOptions options, IApplicationEnvironment applicationEnvironment, IProcessorEnvironment processorEnvironment)
        : base(options, applicationEnvironment, processorEnvironment)
    {
        _path = path;
    }

    public override DataSourceInfo GetDataSourceInfo()
    {
        var d = _data ?? throw new InvalidOperationException("Processing has not run.");
        var first = d.FirstNs;
        var last = Math.Max(d.FirstNs + 1, d.LastNs);
        if (d.Manifest is { Etl: not null } manifest)
        {
            first = Math.Min(0, first);
            var captureEnd = Timeline.FromManifest(manifest).QpcToNanoseconds(manifest.SyncEnd!.Qpc);
            last = Math.Max(last, checked(captureEnd + (manifest.Alignment?.OffsetNs ?? 0)));
        }

        return new DataSourceInfo(first, last, d.ZeroUtc.AddTicks(first / 100));
    }

    protected override Task ProcessAsyncCore(IProgress<int> progress, CancellationToken cancellationToken)
    {
        TbrwLibrary.Initialize(SepInstall.Bin64);
        _data = SepData.Load(_path, cancellationToken);
        progress.Report(60);
        Logger?.Info($"WPA-Fusion SEP: {Path.GetFileName(_path)}: {_data.Count} samples, {_data.Events.Count} events, time from {_data.TimeSource}.");
        Logger?.Info(_data.Attribution is null
            ? "WPA-Fusion SEP: no attribution sidecar; run wpafusion-attribute for module/function sample attribution."
            : $"WPA-Fusion SEP: verified attribution sidecar with {_data.Attribution.Locations.Length} locations.");

        Logger?.Info(_data.SampledStacks is null
            ? "WPA-Fusion SEP: no sampled-stack sidecar; run wpafusion-attribute for SEP Event Counts by ETW Sample."
            : $"WPA-Fusion SEP: verified sampled-stack sidecar with {_data.SampledStacks.RowCount} rows.");

        Logger?.Info(_data.LbrStacks is null
            ? "WPA-Fusion SEP: no LBR stack sidecar; collect with sep -lbr call_stack:usr and run wpafusion-attribute for CPU Usage (SEP LBR Stack Frames)."
            : $"WPA-Fusion SEP: verified LBR stack sidecar with {_data.LbrStacks.Stacks.Length} distinct stacks.");

        progress.Report(100);
        return Task.CompletedTask;
    }

    protected override void BuildTableCore(TableDescriptor tableDescriptor, ITableBuilder tableBuilder)
    {
        var data = _data ?? throw new InvalidOperationException("Processing has not run.");
        if (tableDescriptor.Guid == SepSampleTable.TableDescriptor.Guid)
        {
            SepSampleTable.Build(tableBuilder, data);
        }
        else if (tableDescriptor.Guid == SepCpuUsageTable.TableDescriptor.Guid)
        {
            SepCpuUsageTable.Build(tableBuilder, data);
        }
        else if (tableDescriptor.Guid == SepLbrCallStackTable.TableDescriptor.Guid)
        {
            SepLbrCallStackTable.Build(tableBuilder, data);
        }
        else if (tableDescriptor.Guid == SepLbrStackFramesTable.TableDescriptor.Guid)
        {
            SepLbrStackFramesTable.Build(tableBuilder, data);
        }
    }

    public override bool DoesTableHaveData(TableDescriptor table)
    {
        if (table.Guid == SepCpuUsageTable.TableDescriptor.Guid)
        {
            return _data?.SampledStacks is { RowCount: > 0 };
        }

        if (table.Guid == SepLbrCallStackTable.TableDescriptor.Guid || table.Guid == SepLbrStackFramesTable.TableDescriptor.Guid)
        {
            return _data?.LbrStacks is not null;
        }

        return table.Guid == SepSampleTable.TableDescriptor.Guid && _data is { Count: > 0 };
    }
}
