using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpaFusion.Common;

/// <summary>
/// Contents of session.json, written by wpafusion-capture into each run folder. Plugins read it
/// to map every timestamp onto one timeline. File paths are relative to the run folder.
/// </summary>
public sealed class SessionManifest
{
    public const string FileName = "session.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Run folder name, e.g. 20261017-110058.</summary>
    public string RunId { get; set; } = string.Empty;

    public string Machine { get; set; } = string.Empty;

    public string? CaptureTimeZoneId { get; set; }

    public List<string> WorkloadCommand { get; set; } = new();
    public List<string> ExecutedWorkloadCommand { get; set; } = new();
    public int? WorkloadExitCode { get; set; }
    public CrossRunAlignment? Alignment { get; set; }

    /// <summary>SEP platform of the collection machine (from sep -version). Null in older manifests or without SEP.</summary>
    public SepPlatform? Platform { get; set; }

    /// <summary>QueryPerformanceFrequency, ticks per second.</summary>
    public long QpcFrequency { get; set; }

    /// <summary>Tightest sync point taken before collection started.</summary>
    public SyncPoint? SyncStart { get; set; }

    /// <summary>Tightest sync point taken after collection stopped.</summary>
    public SyncPoint? SyncEnd { get; set; }

    /// <summary>
    /// One QPC-edge-aligned sync point per logical CPU, taken at start. Used to measure per-CPU TSC
    /// offsets directly (see <see cref="TscOffsets"/>). Empty in older manifests.
    /// </summary>
    public List<SyncPoint> TscSurvey { get; set; } = new();

    /// <summary>
    /// The ETL's own clock reference as read back from the trace after collection: the QPC value
    /// WPA treats as the ETL's time zero and its UTC wall clock. Null if no ETL was captured.
    /// </summary>
    public EtlClockReference? Etl { get; set; }

    public SessionFiles Files { get; set; } = new();

    /// <summary>Marker events emitted into the ETL (TraceLogging) during the run.</summary>
    public List<SessionMarker> Markers { get; set; } = new();

    /// <summary>Collector command lines and versions, for the record.</summary>
    public Dictionary<string, string> Tools { get; set; } = new();

    /// <summary>
    /// Full path of the capture folder this processed run was made from. Null for a raw capture.
    /// Attribution sets it so the analysis side can find the processed run for a capture.
    /// </summary>
    public string? ProcessedFrom { get; set; }

    public static SessionManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<SessionManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' is empty.");
        manifest.Validate(path);
        return manifest;
    }

    /// <summary>Finds session.json in the folder of a data file (the run folder layout).</summary>
    public static string? FindFor(string dataFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataFilePath));
        if (dir is null)
        {
            return null;
        }

        var candidate = Path.Combine(dir, FileName);
        return File.Exists(candidate) ? candidate : null;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    private void Validate(string path)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"'{path}' has schema version {SchemaVersion}; this build reads {CurrentSchemaVersion}.");
        }

        if (QpcFrequency <= 0 || SyncStart is null || SyncEnd is null)
        {
            throw new InvalidDataException($"'{path}' is missing the QPC frequency or sync points.");
        }

        if (SyncEnd.Tsc <= SyncStart.Tsc || SyncEnd.Qpc <= SyncStart.Qpc)
        {
            throw new InvalidDataException($"'{path}' has sync points that do not advance.");
        }

        if (Alignment is { } alignment &&
            (alignment.SourceRunId != RunId || string.IsNullOrWhiteSpace(alignment.ReferenceRunId) ||
             alignment.SourceWorkloadDurationNs <= 0 || alignment.ReferenceWorkloadDurationNs <= 0 ||
             alignment.ReferenceZeroFileTimeUtc <= 0 || alignment.ReferenceZeroFileTimeUtc > DateTime.MaxValue.ToFileTimeUtc()))
        {
            throw new InvalidDataException($"'{path}' has invalid cross-run alignment metadata.");
        }
    }
}

/// <summary>The ETL's clock reference as WPA sees it.</summary>
/// <param name="ZeroQpc">QPC value of the ETL's time zero in WPA.</param>
/// <param name="ZeroFileTimeUtc">UTC wall clock (FILETIME ticks) WPA assigns to that zero.</param>
public sealed record EtlClockReference(long ZeroQpc, long ZeroFileTimeUtc);

/// <summary>Paths of the run's artifacts, relative to the run folder. Null if not captured.</summary>
public sealed class SessionFiles
{
    public string? Etl { get; set; }

    public string? TypeperfCsv { get; set; }

    public string? TypeperfCounters { get; set; }

    // Read-only aliases for manifests written before the Typeperf rename.
    [JsonPropertyName("pdhCsv")]
    public string? LegacyCsv { get => null; set => TypeperfCsv ??= value; }

    [JsonPropertyName("pdhCounters")]
    public string? LegacyCounters { get => null; set => TypeperfCounters ??= value; }

    public string? Tb7 { get; set; }

    public string? EmonDat { get; set; }
    public string? EmonEvents { get; set; }
    public string? EmonDefinition { get; set; }

    public string? EmonMetrics { get; set; }
}

/// <summary>A marker event written into the ETL, with the clock values it carries.</summary>
/// <param name="Name">"start" or "end".</param>
/// <param name="Index">Marker number within its burst.</param>
/// <param name="Tsc">rdtsc read just before the marker was written.</param>
/// <param name="Qpc">QPC read just before the marker was written.</param>
/// <param name="FileTimeUtc">Precise UTC wall clock read just before the marker was written.</param>
public sealed record SessionMarker(string Name, int Index, ulong Tsc, long Qpc, long FileTimeUtc)
{
    /// <summary>The marker event's own ETW timestamp (raw QPC) as read back from the ETL; null if not read.</summary>
    public long? EtlRawTimestamp { get; init; }

    /// <summary>Logical CPU the clock reads ran on; null in older manifests.</summary>
    public int? Cpu { get; init; }
}
