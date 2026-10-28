namespace WpaFusion.Tma;

/// <summary>Event counts for one evaluation window, keyed by hardware identity.</summary>
public sealed class EventCounts
{
    private readonly Dictionary<EventKey, double> _counts = new();

    public EventCounts(EventCatalog catalog) => Catalog = catalog;

    public EventCatalog Catalog { get; }

    public IReadOnlyDictionary<EventKey, double> Values => _counts;

    /// <summary>Adds an estimated count for an event (any spelling). Repeated names accumulate.</summary>
    public void Add(string eventName, double count)
    {
        var key = Catalog.KeyOf(eventName);
        _counts[key] = _counts.TryGetValue(key, out var v) ? v + count : count;
    }

    /// <summary>Declares an event as collected with a zero count in this window.</summary>
    public void Declare(string eventName)
    {
        var key = Catalog.KeyOf(eventName);
        _counts.TryAdd(key, 0);
    }

    public bool TryGet(string eventName, out double count) => _counts.TryGetValue(Catalog.KeyOf(eventName), out count);
}

/// <summary>Values for the system constants used by TMA formulas.</summary>
public sealed class TmaConstants
{
    /// <summary>
    /// Hyper-threading flag fed to formulas (smt_on). WPA-Fusion evaluates per thread, as VTune does
    /// with the uarch-exploration event set, so this defaults to false (see DECISIONS.md).
    /// </summary>
    public bool HyperThreadingOn { get; init; }

    public int ThreadsPerCore { get; init; } = 1;

    public double SystemTscFrequencyHz { get; init; }

    public int SocketCount { get; init; } = 1;

    public int LogicalCpuCount { get; init; } = 1;

    /// <summary>Wall duration of the evaluation window.</summary>
    public double DurationMilliseconds { get; init; }

    internal bool TryResolve(string constantName, out double value)
    {
        switch (constantName.ToUpperInvariant())
        {
            case "HYPERTHREADING_ON":
                value = HyperThreadingOn ? 1 : 0;
                return true;
            case "THREADS_PER_CORE":
                value = ThreadsPerCore;
                return true;
            case "SYSTEM_TSC_FREQ":
            case "SYSTEM.TSC_FREQ":
                value = SystemTscFrequencyHz;
                return SystemTscFrequencyHz > 0;
            case "SYSTEM.SOCKETS[0][0].SIZE":
                value = ThreadsPerCore;
                return true;
            case "SOCKET_COUNT":
                value = SocketCount;
                return true;
            case "DURATIONTIMEINMILLISECONDS":
                value = DurationMilliseconds;
                return DurationMilliseconds > 0;
            case "SYSTEM.SOCKETS[0].CPUS.COUNT * SYSTEM.SOCKET_COUNT":
                value = LogicalCpuCount;
                return true;
            default:
                if (double.TryParse(constantName, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }

                value = double.NaN;
                return false;
        }
    }
}

/// <summary>The value of one TMA node, or why it could not be computed.</summary>
public sealed record TmaResult(TmaNode Node, double Value, IReadOnlyList<string> MissingInputs)
{
    public bool IsAvailable => MissingInputs.Count == 0;
}

/// <summary>A required event or constant has no value.</summary>
public sealed class MissingTmaInputException : Exception
{
    public MissingTmaInputException(string input)
        : base($"TMA input '{input}' was not collected.")
    {
        Input = input;
    }

    public string Input { get; }
}

/// <summary>Evaluates a <see cref="TmaMetricSet"/> over <see cref="EventCounts"/>.</summary>
public static class TmaEvaluator
{
    /// <summary>
    /// Evaluates one node. Throws <see cref="MissingTmaInputException"/> if the formula uses an
    /// event that was not collected; uncollected events are never treated as zero.
    /// </summary>
    public static double Evaluate(TmaNode node, EventCounts counts, TmaConstants constants)
    {
        return node.Formula.Evaluate(alias =>
        {
            if (node.EventsByAlias.TryGetValue(alias, out var eventName))
            {
                return counts.TryGet(eventName, out var c) ? c : throw new MissingTmaInputException(eventName);
            }

            if (node.ConstantsByAlias.TryGetValue(alias, out var constantName))
            {
                return constants.TryResolve(constantName, out var v) ? v : throw new MissingTmaInputException(constantName);
            }

            // Some formulas use constant names directly instead of aliases.
            return constants.TryResolve(alias, out var direct) ? direct : throw new MissingTmaInputException(alias);
        });
    }

    /// <summary>Evaluates every node, recording missing inputs instead of throwing.</summary>
    public static IReadOnlyList<TmaResult> EvaluateAll(TmaMetricSet set, EventCounts counts, TmaConstants constants)
    {
        var results = new List<TmaResult>(set.Nodes.Count);
        foreach (var node in set.Nodes)
        {
            try
            {
                results.Add(new TmaResult(node, Evaluate(node, counts, constants), Array.Empty<string>()));
            }
            catch (MissingTmaInputException ex)
            {
                results.Add(new TmaResult(node, double.NaN, new[] { ex.Input }));
            }
        }

        return results;
    }
}
