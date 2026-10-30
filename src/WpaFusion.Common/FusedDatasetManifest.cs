using System.Text.Json;

namespace WpaFusion.Common;

public sealed record FusedDatasetSource(string Kind, string Directory, string RunId, CrossRunAlignment Alignment);

public sealed class FusedDatasetManifest
{
    public const string FileName = "fusion.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public int SchemaVersion { get; set; } = 1;
    public string BatchId { get; set; } = string.Empty;
    public string ReferenceDirectory { get; set; } = string.Empty;
    public string AlignmentMethod => "workload-start-offset";
    public bool SeparateExecutions => true;
    public List<string> Warnings { get; set; } = new();
    public List<FusedDatasetSource> Sources { get; set; } = new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static FusedDatasetManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<FusedDatasetManifest>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Empty fused dataset.");
        if (manifest.SchemaVersion != 1 || manifest.Sources.Count != 2 ||
            manifest.Sources.Count(source => source.Kind == "SEP") != 1 || manifest.Sources.Count(source => source.Kind == "EMON") != 1 ||
            !manifest.Sources.Any(source => source.Kind == "SEP" && source.Directory == manifest.ReferenceDirectory))
        {
            throw new InvalidDataException("Unsupported fused dataset or missing SEP reference run.");
        }

        foreach (var source in manifest.Sources)
        {
            _ = CaptureBatchManifest.ResolveRun(Path.GetDirectoryName(Path.GetFullPath(path))!,
                new CaptureBatchRun(source.Kind, source.Directory, source.RunId));
        }

        return manifest;
    }
}