using System.Xml.Linq;
using WpaFusion.Tma.Expressions;

namespace WpaFusion.Tma;

/// <summary>One TMA node from a SEP EDP metrics file.</summary>
public sealed class TmaNode
{
    internal TmaNode(string name, int level, string? parentName, string description, Formula formula,
        IReadOnlyDictionary<string, string> eventsByAlias, IReadOnlyDictionary<string, string> constantsByAlias, string groups)
    {
        Name = name;
        Level = level;
        ParentName = parentName;
        Description = description;
        Formula = formula;
        EventsByAlias = eventsByAlias;
        ConstantsByAlias = constantsByAlias;
        Groups = groups;
    }

    public string Name { get; }

    /// <summary>Depth in the TMA tree, 1 = Frontend_Bound / Bad_Speculation / Backend_Bound / Retiring.</summary>
    public int Level { get; }

    public string? ParentName { get; }

    public TmaNode? Parent { get; internal set; }

    public string Description { get; }

    public Formula Formula { get; }

    public IReadOnlyDictionary<string, string> EventsByAlias { get; }

    public IReadOnlyDictionary<string, string> ConstantsByAlias { get; }

    public string Groups { get; }

    /// <summary>Names from the root down to this node, e.g. [Backend_Bound, Memory_Bound, L1_Bound].</summary>
    public IReadOnlyList<string> Path
    {
        get
        {
            var names = new List<string>();
            for (var n = this; n is not null; n = n.Parent)
            {
                names.Add(n.Name);
            }

            names.Reverse();
            return names;
        }
    }
}

/// <summary>The TMA hierarchy loaded from a SEP EDP metrics XML file.</summary>
public sealed class TmaMetricSet
{
    private TmaMetricSet(IReadOnlyList<TmaNode> nodes, string source)
    {
        Nodes = nodes;
        Source = source;
    }

    /// <summary>Tree nodes in file order (parents before children).</summary>
    public IReadOnlyList<TmaNode> Nodes { get; }

    public string Source { get; }

    public int MaxLevel => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Level);

    /// <summary>Loads SEP EDP XML metrics, whose TMA depth is encoded by paired leading dots in each metric name.</summary>
    public static TmaMetricSet LoadSepEdpXml(string metricsXmlPath)
    {
        var doc = XDocument.Load(metricsXmlPath);
        var nodes = new List<TmaNode>();
        var hierarchy = new List<TmaNode>();
        foreach (var metric in doc.Root?.Elements("metric") ?? Enumerable.Empty<XElement>())
        {
            if (!string.Equals((string?)metric.Element("category"), "TMA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawName = (string?)metric.Attribute("name") ?? string.Empty;
            const string prefix = "metric_TMA_";
            if (!rawName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var pathName = rawName[prefix.Length..];
            if (pathName.EndsWith("(%)", StringComparison.Ordinal))
            {
                pathName = pathName[..^3];
            }

            var dotCount = pathName.TakeWhile(c => c == '.').Count();
            if (dotCount % 2 != 0)
            {
                throw new InvalidDataException($"Metric '{rawName}' in '{metricsXmlPath}' has an invalid hierarchy depth.");
            }

            var level = (dotCount / 2) + 1;
            var name = pathName[dotCount..];
            if (name.Length == 0)
            {
                throw new InvalidDataException($"Metric '{rawName}' in '{metricsXmlPath}' has no name.");
            }

            while (hierarchy.Count >= level)
            {
                hierarchy.RemoveAt(hierarchy.Count - 1);
            }

            // Some EDP files omit an intermediate node; hang the child on its nearest ancestor.
            if (level > 1 && hierarchy.Count == 0)
            {
                throw new InvalidDataException($"Metric '{rawName}' in '{metricsXmlPath}' has no ancestor.");
            }

            level = Math.Min(level, hierarchy.Count + 1);
            var parent = level == 1 ? null : hierarchy[^1];
            var events = metric.Elements("event").ToDictionary(
                element => (string?)element.Attribute("alias") ?? string.Empty,
                element => element.Value.Trim(), StringComparer.Ordinal);
            var constants = metric.Elements("constant").ToDictionary(
                element => (string?)element.Attribute("alias") ?? string.Empty,
                element => element.Value.Trim(), StringComparer.Ordinal);
            var formulaText = metric.Element("formula")?.Value.Trim() ?? string.Empty;
            Formula formula;
            try
            {
                formula = Formula.Parse(formulaText);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"Metric '{rawName}' in '{metricsXmlPath}' has an unparsable formula: {ex.Message}", ex);
            }

            var node = new TmaNode(name, level, parent?.Name, metric.Element("description")?.Value.Trim() ?? string.Empty,
                formula, events, constants, "TMA") { Parent = parent };
            nodes.Add(node);
            hierarchy.Add(node);
        }

        if (nodes.Count == 0)
        {
            throw new InvalidDataException($"No TMA metrics found in SEP EDP file '{metricsXmlPath}'.");
        }

        return new TmaMetricSet(nodes, metricsXmlPath);
    }
}
