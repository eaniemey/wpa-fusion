using System.Globalization;

namespace WpaFusion.Tma;

/// <summary>
/// Hardware identity of a core event: the encoding the PMU is programmed with. Two names with the
/// same key count the same thing (e.g. ICACHE_TAG.STALLS and ICACHE_64B.IFTAG_STALL).
/// </summary>
public readonly record struct EventKey(string Identity)
{
    public override string ToString() => Identity;
}

/// <summary>
/// Resolves SEP and EDP event spellings ("X:cmask=1:e=yes", "X_PS") to an <see cref="EventKey"/>.
/// </summary>
public sealed class EventCatalog
{
    private readonly Dictionary<string, Encoding> _byName;

    private EventCatalog(Dictionary<string, Encoding> byName) => _byName = byName;

    public int Count => _byName.Count;

    /// <summary>An empty catalog: keys fall back to normalized names.</summary>
    public static EventCatalog Empty { get; } = new(new Dictionary<string, Encoding>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Resolves an event name to its key.</summary>
    public EventKey KeyOf(string eventName)
    {
        var parsed = ParsedName.Parse(eventName);
        var baseName = parsed.Base;
        if (!_byName.TryGetValue(baseName, out var enc) && baseName.EndsWith("_PS", StringComparison.OrdinalIgnoreCase))
        {
            // SEP marks precise (PEBS) sampling with a _PS suffix; the count is the same event.
            baseName = baseName[..^3];
            _byName.TryGetValue(baseName, out enc);
        }

        if (enc is null)
        {
            return new EventKey("name:" + baseName.ToUpperInvariant() + parsed.ModifierSuffix);
        }

        var cmask = parsed.CMask ?? enc.CounterMask;
        var inv = parsed.Invert ?? enc.Invert;
        var edge = parsed.Edge ?? enc.Edge;
        var any = parsed.AnyThread ?? enc.AnyThread;
        var umask = parsed.UMask ?? enc.UMask;
        return new EventKey(string.Create(CultureInfo.InvariantCulture,
            $"ev={enc.Code}/u={umask}/c={cmask}/i={(inv ? 1 : 0)}/e={(edge ? 1 : 0)}/any={(any ? 1 : 0)}/msr={enc.Msr}{parsed.ModifierSuffix}"));
    }

    private sealed record Encoding(string Code, string UMask, int CounterMask, bool Invert, bool Edge, bool AnyThread, string Msr);

    /// <summary>An event name split into base and modifiers.</summary>
    private sealed class ParsedName
    {
        public string Base { get; private init; } = string.Empty;

        public int? CMask { get; private init; }

        public bool? Invert { get; private init; }

        public bool? Edge { get; private init; }

        public bool? AnyThread { get; private init; }

        public string? UMask { get; private init; }

        /// <summary>Modifiers that do not change the encoding we compare (e.g. privilege filters), kept so they never collide.</summary>
        public string ModifierSuffix { get; private init; } = string.Empty;

        public static ParsedName Parse(string name)
        {
            var parts = name.Split(':');
            int? cmask = null;
            bool? inv = null, edge = null, any = null;
            string? umask = null;
            var other = new List<string>();
            foreach (var raw in parts.Skip(1))
            {
                var m = raw.Trim().ToLowerInvariant();
                if (TryValue(m, "cmask=", out var v) || TryShort(m, 'c', out v))
                {
                    cmask = ParseInt(v);
                }
                else if (m is "e=yes" or "e1" or "edge" or "e=1")
                {
                    edge = true;
                }
                else if (m is "e=no" or "e0" or "e=0")
                {
                    edge = false;
                }
                else if (m is "inv=yes" or "i1" or "inv" or "i=1")
                {
                    inv = true;
                }
                else if (m is "inv=no" or "i0" or "i=0")
                {
                    inv = false;
                }
                else if (m is "anythr=yes" or "any=yes" or "t1")
                {
                    any = true;
                }
                else if (TryValue(m, "umask=", out v) || (m.StartsWith('u') && m.Length > 1 && m[1] == '0'))
                {
                    umask = "0X" + ParseInt(v.Length > 0 ? v : m[1..]).ToString("X2", CultureInfo.InvariantCulture);
                }
                else if (m.Length > 0)
                {
                    other.Add(m);
                }
            }

            other.Sort(StringComparer.Ordinal);
            return new ParsedName
            {
                Base = parts[0].Trim(),
                CMask = cmask,
                Invert = inv,
                Edge = edge,
                AnyThread = any,
                UMask = umask,
                ModifierSuffix = other.Count == 0 ? string.Empty : ":" + string.Join(":", other),
            };
        }

        private static bool TryValue(string m, string prefix, out string value)
        {
            value = m.StartsWith(prefix, StringComparison.Ordinal) ? m[prefix.Length..] : string.Empty;
            return value.Length > 0;
        }

        private static bool TryShort(string m, char letter, out string value)
        {
            value = m.Length > 1 && m[0] == letter && char.IsDigit(m[1]) ? m[1..] : string.Empty;
            return value.Length > 0;
        }

        private static int ParseInt(string v) =>
            v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(v, CultureInfo.InvariantCulture);
    }
}
