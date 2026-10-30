using System.Text.Json;

namespace WpaFusion.Common;

public sealed record CaptureBatchRun(string Kind, string Directory, string RunId);

public sealed class CaptureBatchManifest
{
    public const string FileName = "capture-batch.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public int SchemaVersion { get; set; } = 1;
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
    public bool Complete { get; set; }
    public List<string> WorkloadCommand { get; set; } = new();
    public List<CaptureBatchRun> Runs { get; set; } = new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static CaptureBatchManifest Load(string path)
    {
        var batch = JsonSerializer.Deserialize<CaptureBatchManifest>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Empty capture batch.");
        if (batch.SchemaVersion != 1 || !batch.Complete || batch.Runs.Count != 2 ||
            batch.WorkloadCommand.Count == 0 || batch.Runs.Select(run => run.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Runs.Count)
        {
            throw new InvalidDataException("Capture batch is incomplete, duplicated, or unsupported.");
        }

        for (var index = 0; index < batch.Runs.Count; index++)
        {
            if (batch.Runs[index].Kind != (index % 2 == 0 ? "SEP" : "EMON"))
            {
                throw new InvalidDataException("Capture batch must alternate SEP and EMON runs.");
            }

            _ = ResolveRun(Path.GetDirectoryName(Path.GetFullPath(path))!, batch.Runs[index]);
        }

        return batch;
    }

    public static string ResolveRun(string batchDirectory, CaptureBatchRun run)
    {
        var root = Path.GetFullPath(batchDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, run.Directory));
        if (Path.IsPathRooted(run.Directory) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Batch run paths must remain inside the capture batch folder.");
        }

        return path;
    }
}