using Microsoft.Performance.SDK.Processing;
using WpaFusion.Common;
using WpaFusion.Plugin.Typeperf.Tables.Session;

namespace WpaFusion.Plugin.Typeperf;

/// <summary>
/// Opens a run's session.json and projects clock sync markers onto the ETW timeline alongside
/// the manifest's capture metadata.
/// </summary>
[ProcessingSource("{0B7E5D21-4C8A-4F3B-8E19-2A6D7C3F5E48}", "WPA-Fusion Session", "WPA-Fusion run metadata and clock sync markers (session.json).")]
[FileDataSource(".json", "WPA-Fusion session.json")]
public sealed class SessionProcessingSource : ProcessingSource
{
    protected override IProcessingSourceTableProvider CreateTableProvider() =>
        TableDiscovery.CreateForNamespace(typeof(SyncMarkerTable).Namespace, this);

    protected override bool IsDataSourceSupportedCore(IDataSource dataSource) =>
        dataSource is FileDataSource file && string.Equals(Path.GetFileName(file.FullPath), SessionManifest.FileName, StringComparison.OrdinalIgnoreCase);

    protected override ICustomDataProcessor CreateProcessorCore(IEnumerable<IDataSource> dataSources, IProcessorEnvironment processorEnvironment, ProcessorOptions options) =>
        new SessionProcessor(dataSources.OfType<FileDataSource>().First().FullPath, options, ApplicationEnvironment, processorEnvironment);

    public override ProcessingSourceInfo GetAboutInfo() => WpaFusionAbout.Info;
}

/// <summary>Loads one session manifest and supplies the sync-marker and metadata tables.</summary>
internal sealed class SessionProcessor : CustomDataProcessor
{
    private readonly string _path;
    private SessionView? _view;

    public SessionProcessor(string path, ProcessorOptions options, IApplicationEnvironment applicationEnvironment, IProcessorEnvironment processorEnvironment)
        : base(options, applicationEnvironment, processorEnvironment)
    {
        _path = path;
    }

    public override DataSourceInfo GetDataSourceInfo()
    {
        var v = _view ?? throw new InvalidOperationException("Processing has not run.");
        var times = v.Markers.Select(m => m.TimeNs).ToList();
        var first = times.Count == 0 ? 0 : times.Min();
        var last = times.Count == 0 ? 1 : Math.Max(first + 1, times.Max());
        return new DataSourceInfo(first, last, v.Timeline.NanosecondsToUtc(first));
    }

    protected override Task ProcessAsyncCore(IProgress<int> progress, CancellationToken cancellationToken)
    {
        _view = SessionView.Load(_path);
        progress.Report(100);
        return Task.CompletedTask;
    }

    protected override void BuildTableCore(TableDescriptor tableDescriptor, ITableBuilder tableBuilder)
    {
        var v = _view ?? throw new InvalidOperationException("Processing has not run.");
        if (tableDescriptor.Guid == SyncMarkerTable.TableDescriptor.Guid)
        {
            SyncMarkerTable.Build(tableBuilder, v);
        }
        else if (tableDescriptor.Guid == SessionInfoTable.TableDescriptor.Guid)
        {
            SessionInfoTable.Build(tableBuilder, v);
        }
    }
}

/// <summary>session.json projected onto the timeline.</summary>
public sealed class SessionView
{
    private SessionView(SessionManifest manifest, Timeline timeline, IReadOnlyList<MarkerRow> markers)
    {
        Manifest = manifest;
        Timeline = timeline;
        Markers = markers;
    }

    public SessionManifest Manifest { get; }

    public Timeline Timeline { get; }

    public IReadOnlyList<MarkerRow> Markers { get; }

    public static SessionView Load(string path)
    {
        var manifest = SessionManifest.Load(path);
        var timeline = Timeline.FromManifest(manifest);

        // With a TSC survey, compare in CPU-0 TSC so per-CPU offsets do not show up as residual.
        var offsets = TscOffsets.FromSurvey(manifest);
        var tscTimeline = offsets is null ? timeline : timeline.WithCpu0Tsc(offsets.Of);
        var rows = manifest.Markers.Select(m =>
        {
            var qpcNs = timeline.QpcToNanoseconds(m.Qpc);
            var tsc = offsets is null ? m.Tsc : (ulong)((long)m.Tsc - offsets.Of(m.Cpu ?? 0));
            var tscNs = tscTimeline.TscToNanoseconds(tsc);
            long? etwNs = m.EtlRawTimestamp is long raw ? timeline.QpcToNanoseconds(raw) : null;
            return new MarkerRow($"{m.Name}#{m.Index}", etwNs ?? qpcNs, qpcNs, tscNs, etwNs);
        }).ToList();
        return new SessionView(manifest, timeline, rows);
    }

    /// <summary>One marker: where ETW recorded it and where each clock read places it.</summary>
    /// <param name="Name">Marker name and index.</param>
    /// <param name="TimeNs">Row time: the ETW event time if known, else the QPC read.</param>
    /// <param name="QpcNs">Time of the QPC read taken just before the marker.</param>
    /// <param name="TscNs">Time of the rdtsc read, mapped through the clock map.</param>
    /// <param name="EtwNs">Time of the marker's own ETW timestamp, if read back from the ETL.</param>
    public sealed record MarkerRow(string Name, long TimeNs, long QpcNs, long TscNs, long? EtwNs);
}
