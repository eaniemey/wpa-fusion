namespace WpaFusion.Plugin.Typeperf.Parsing;

/// <summary>A Typeperf counter path split into its parts: \\machine\object(instance)\counter.</summary>
public sealed record TypeperfCounterPath(string FullPath, string Machine, string Object, string Instance, string Counter)
{
    /// <summary>
    /// Parses a typeperf column header. Instances may themselves contain parentheses
    /// (e.g. "svchost (1)"), so the instance runs from the first '(' to the last ')' of the object part.
    /// </summary>
    public static TypeperfCounterPath Parse(string path)
    {
        var p = path.Trim();
        var machine = string.Empty;
        var rest = p;
        if (rest.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var slash = rest.IndexOf('\\', 2);
            if (slash < 0)
            {
                throw new FormatException($"Counter path '{path}' has no object part.");
            }

            machine = rest[2..slash];
            rest = rest[(slash + 1)..];
        }
        else if (rest.StartsWith('\\'))
        {
            rest = rest[1..];
        }

        var counterSep = rest.LastIndexOf('\\');
        if (counterSep <= 0)
        {
            throw new FormatException($"Counter path '{path}' has no counter part.");
        }

        var objectPart = rest[..counterSep];
        var counter = rest[(counterSep + 1)..];
        var open = objectPart.IndexOf('(');
        var close = objectPart.LastIndexOf(')');
        string obj, instance;
        if (open >= 0 && close > open)
        {
            obj = objectPart[..open];
            instance = objectPart[(open + 1)..close];
        }
        else
        {
            obj = objectPart;
            instance = string.Empty;
        }

        return new TypeperfCounterPath(p, machine, obj, instance, counter);
    }
}
