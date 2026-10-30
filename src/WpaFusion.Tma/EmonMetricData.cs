using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using WpaFusion.Common;
using WpaFusion.Tma.Expressions;

namespace WpaFusion.Tma;

public sealed record EmonMetricWindow(long StartNs, long DurationNs, IReadOnlyList<TmaResult> Results);

public sealed class EmonMetricData
{
    public TmaMetricSet Metrics { get; private init; } = null!;
    public IReadOnlyList<EmonMetricWindow> Intervals { get; private init; } = Array.Empty<EmonMetricWindow>();
    public IReadOnlyList<TmaResult> Summary { get; private init; } = Array.Empty<TmaResult>();
    public IReadOnlyList<TmaResult> Averages { get; private init; } = Array.Empty<TmaResult>();
    public IReadOnlyList<long> ValidIntervalCounts { get; private init; } = Array.Empty<long>();
    public DateTime ZeroUtc { get; private init; }
    public string SourceRunId { get; private init; } = string.Empty;
    public string? DefinitionSource { get; private init; }
    public bool IsCrossRunComparison { get; private init; }
    public string Provenance => $"EMON {SourceRunId}" + (IsCrossRunComparison ? " (cross-run aligned)" : " (native)");
    public long FirstNs => Intervals.Min(interval => interval.StartNs);
    public long LastNs => Intervals.Max(interval => checked(interval.StartNs + interval.DurationNs));

    public static EmonMetricData Load(string descriptorPath)
    {
        using var descriptor = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        var root = descriptor.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported EMON metrics descriptor version.");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(descriptorPath))!;
        string Input(string property)
        {
            var name = root.GetProperty(property).GetString();
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/'))
            {
                throw new InvalidDataException("EMON metric inputs must be filenames in the run folder.");
            }

            return Path.Combine(folder, name);
        }

        var manifest = SessionManifest.Load(Path.Combine(folder, SessionManifest.FileName));
        if (string.IsNullOrWhiteSpace(manifest.CaptureTimeZoneId))
        {
            throw new InvalidDataException("EMON alignment requires the collection time zone in session.json; capture clock anchors establish the native timeline.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(manifest.CaptureTimeZoneId);
        var timeline = Timeline.FromManifest(manifest);
        var definitionPath = root.TryGetProperty("metricDefinitions", out var definitionProperty) && definitionProperty.ValueKind == JsonValueKind.String
            ? Input("metricDefinitions")
            : manifest.Files.EmonDat is { } dataName && Path.GetFileName(dataName) == dataName && !dataName.Contains('/') && !dataName.Contains('\\')
                ? EmonMetricDefinitions.Find(Path.Combine(folder, dataName), SepInstall.Root)
                : null;
        var definitions = definitionPath is null ? Array.Empty<TmaNode>() : TmaMetricSet.LoadSepEdpXml(definitionPath, includeAllMetrics: true).Nodes
            .Where(node => node.IsTopDown && node.OriginalName.EndsWith("(%)", StringComparison.Ordinal)).ToArray();
        var definitionsByName = definitions.ToDictionary(node => node.OriginalName, StringComparer.Ordinal);
        var nodes = new List<TmaNode>();
        var summary = new List<TmaResult>();
        var names = new List<string>();
        var hierarchy = new List<TmaNode>();
        using (var csv = OpenCsv(Input("summary")))
        {
            var header = csv.ReadFields() ?? throw new InvalidDataException("MPP summary is empty.");
            var aggregate = Array.IndexOf(header, "aggregated");
            var description = Array.IndexOf(header, "description");
            if (aggregate < 0)
            {
                throw new InvalidDataException("MPP summary has no aggregated column.");
            }

            while (!csv.EndOfData)
            {
                var fields = csv.ReadFields()!;
                if (!fields[0].StartsWith("metric_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (fields.Length != header.Length || names.Contains(fields[0], StringComparer.Ordinal))
                {
                    throw new InvalidDataException("MPP summary contains malformed or duplicate metrics.");
                }

                var rawName = fields[0];
                var topDown = rawName.StartsWith("metric_TMA_", StringComparison.Ordinal) && rawName.EndsWith("(%)", StringComparison.Ordinal);
                var name = topDown ? rawName[11..^3] : rawName[7..];
                var dots = topDown ? name.TakeWhile(character => character == '.').Count() : 0;
                var level = dots / 2 + 1;
                while (topDown && hierarchy.Count >= level)
                {
                    hierarchy.RemoveAt(hierarchy.Count - 1);
                }

                level = topDown ? Math.Min(level, hierarchy.Count + 1) : 1;
                var parent = topDown && level > 1 ? hierarchy[^1] : null;
                var node = new TmaNode(name[dots..], level, parent?.Name,
                    description < 0 ? string.Empty : fields[description], Formula.Parse("0"),
                    new Dictionary<string, string>(), new Dictionary<string, string>(), topDown ? "TMA" : "EDP")
                {
                    Parent = parent,
                    IsTopDown = topDown,
                    OriginalName = rawName,
                    DefinitionOrder = nodes.Count,
                };
                if (definitionsByName.TryGetValue(rawName, out var definition))
                {
                    node = definition;
                }
                if (topDown)
                {
                    hierarchy.Add(node);
                }

                names.Add(rawName);
                nodes.Add(node);
                summary.Add(Result(node, fields[aggregate]));
            }
        }

        if (nodes.Count == 0)
        {
            throw new InvalidDataException("MPP summary contains no metrics.");
        }

        var mppMetricNames = names.ToHashSet(StringComparer.Ordinal);
        foreach (var definition in definitions.Where(definition => !mppMetricNames.Contains(definition.OriginalName)))
        {
            names.Add(definition.OriginalName);
            nodes.Add(definition);
            summary.Add(MissingDefinition(definition));
        }

        var order = Enumerable.Range(0, nodes.Count)
            .OrderBy(index => nodes[index].IsTopDown ? 1 : 0)
            .ThenBy(index => nodes[index].IsTopDown ? nodes[index].DefinitionOrder : index).ToArray();
        names = order.Select(index => names[index]).ToList();
        summary = order.Select(index => summary[index]).ToList();
        nodes = order.Select(index => nodes[index]).ToList();

        var intervals = new List<EmonMetricWindow>();
        var means = new double[nodes.Count];
        var validCounts = new long[nodes.Count];
        using (var csv = OpenCsv(Input("details")))
        {
            var header = csv.ReadFields() ?? throw new InvalidDataException("MPP details are empty.");
            var timestamp = Array.IndexOf(header, "timestamp");
            var duration = Array.IndexOf(header, "$samplingTime");
            var indices = names.Select(name => Array.IndexOf(header, name)).ToArray();
            if (timestamp < 0 || duration < 0 || indices.Where((index, metric) => index < 0 && mppMetricNames.Contains(names[metric])).Any())
            {
                throw new InvalidDataException("MPP details must contain timestamp, $samplingTime, and every summary metric.");
            }

            long previousEnd = long.MinValue;
            while (!csv.EndOfData)
            {
                var fields = csv.ReadFields()!;
                if (fields.Length != header.Length)
                {
                    throw new InvalidDataException("MPP details row length does not match its header.");
                }

                var local = DateTime.SpecifyKind(DateTime.Parse(fields[timestamp], CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                {
                    throw new InvalidDataException("EMON timestamp is ambiguous or invalid in the collection time zone.");
                }

                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                var end = checked((utc.Ticks - timeline.ZeroUtc.Ticks) * 100 + (manifest.Alignment?.OffsetNs ?? 0));
                var seconds = double.Parse(fields[duration], CultureInfo.InvariantCulture);
                if (!double.IsFinite(seconds) || seconds < 0 || seconds > long.MaxValue / 1e9 || end <= previousEnd)
                {
                    throw new InvalidDataException("MPP interval has invalid duration or non-advancing timestamps.");
                }

                var length = checked((long)(seconds * 1e9));
                var results = nodes.Select((node, index) => indices[index] < 0
                    ? MissingDefinition(node)
                    : Result(node, fields[indices[index]])).ToArray();
                for (var index = 0; index < results.Length; index++)
                {
                    if (!results[index].IsAvailable)
                    {
                        continue;
                    }

                    var count = ++validCounts[index];
                    means[index] = means[index] * ((count - 1d) / count) + results[index].Value / count;
                }

                intervals.Add(new EmonMetricWindow(checked(end - length), length, results));
                previousEnd = end;
            }
        }

        if (intervals.Count == 0)
        {
            throw new InvalidDataException("MPP details contain no intervals.");
        }

        return new EmonMetricData
        {
            Metrics = TmaMetricSet.FromProcessedMetrics(nodes, descriptorPath),
            Summary = summary,
            Averages = nodes.Select((node, index) => validCounts[index] > 0
                ? new TmaResult(node, means[index], Array.Empty<string>())
                : !mppMetricNames.Contains(node.OriginalName) ? MissingDefinition(node)
                : new TmaResult(node, double.NaN, new[] { "no valid EMON intervals for this metric" })).ToArray(),
            ValidIntervalCounts = validCounts,
            Intervals = intervals,
            ZeroUtc = manifest.Alignment is { } alignment ? DateTime.FromFileTimeUtc(alignment.ReferenceZeroFileTimeUtc) : timeline.ZeroUtc,
            SourceRunId = manifest.RunId,
            DefinitionSource = definitionPath,
            IsCrossRunComparison = manifest.Alignment is not null,
        };
    }

    private static TextFieldParser OpenCsv(string path)
    {
        var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        return parser;
    }

    private static TmaResult Result(TmaNode node, string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? new TmaResult(node, value, Array.Empty<string>())
            : new TmaResult(node, double.NaN, new[] { "unavailable in Intel MPP output (not scheduled, unsupported, or missing inputs)" });

    private static TmaResult MissingDefinition(TmaNode node) =>
        new(node, double.NaN, new[] { "defined in platform EDP XML but omitted by Intel MPP; required inputs unavailable" });
}