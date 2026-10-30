namespace WpaFusion.Common;

public static class EmonMetricDefinitions
{
    public const string SnapshotFileName = "edp-metrics.xml";

    public static string? Find(string emonDataPath, string sepRoot)
    {
        var snapshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(emonDataPath))!, SnapshotFileName);
        if (File.Exists(snapshot))
        {
            return snapshot;
        }

        if (!File.Exists(emonDataPath))
        {
            return null;
        }

        const string prefix = "EDP metric file:";
        var header = File.ReadLines(emonDataPath).Take(4096)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var name = header?[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\'))
        {
            throw new InvalidDataException("Captured EDP metric filename must not contain a directory path.");
        }

        var path = Path.Combine(sepRoot, "config", "edp", name);
        return File.Exists(path) ? path : null;
    }

    public static string? Snapshot(string emonDataPath, string outputDirectory, string sepRoot)
    {
        var source = Find(emonDataPath, sepRoot);
        if (source is null)
        {
            return null;
        }

        var destination = Path.Combine(outputDirectory, SnapshotFileName);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: false);
        }

        return SnapshotFileName;
    }
}