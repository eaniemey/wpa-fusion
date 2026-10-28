using System.Diagnostics.Tracing;

namespace WpaFusion.Capture.Clocks;

/// <summary>
/// TraceLogging (self-describing) provider that writes sync markers into the ETL. WPR enables it
/// through WpaFusion.wprp. Provider name "WPA-Fusion-Capture"; its GUID is the EventSource name hash
/// (see <see cref="ProviderGuid"/>), which WpaFusion.wprp must match. Captures made before the rename
/// used "PerfScope-Capture"; this reader only reads ETLs this build just recorded.
/// </summary>
[EventSource(Name = ProviderName)]
internal sealed class MarkerSource : EventSource
{
    public const string ProviderName = "WPA-Fusion-Capture";

    public static readonly MarkerSource Log = new();

    private MarkerSource()
        : base(EventSourceSettings.EtwSelfDescribingEventFormat)
    {
    }

    public static Guid ProviderGuid => GetGuid(typeof(MarkerSource));

    /// <summary>Writes one marker carrying the clock values read immediately before it.</summary>
    public void Sync(string name, int index, ulong tsc, long qpc, long fileTimeUtc)
    {
        if (IsEnabled())
        {
            Write("Sync", new EventSourceOptions { Level = EventLevel.Informational }, new SyncPayload(name, index, tsc, qpc, fileTimeUtc));
        }
    }

    [EventData]
    private sealed class SyncPayload
    {
        public SyncPayload(string name, int index, ulong tsc, long qpc, long fileTimeUtc)
        {
            Name = name;
            Index = index;
            Tsc = tsc;
            Qpc = qpc;
            FileTimeUtc = fileTimeUtc;
        }

        public string Name { get; }

        public int Index { get; }

        public ulong Tsc { get; }

        public long Qpc { get; }

        public long FileTimeUtc { get; }
    }
}
