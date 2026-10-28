using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WpaFusion.Plugin.Typeperf.Parsing;

/// <summary>One counter value at one sample time. Blank cells produce no sample.</summary>
public readonly record struct TypeperfSample(int SampleIndex, int CounterIndex, double Value);

/// <summary>
/// Parsed typeperf CSV (PDH-CSV 4.0): counter columns, sample times (UTC FILETIME ticks) and the
/// non-blank values. Values are stored row-major as they stream in.
/// </summary>
public sealed class TypePerfCsv
{
    private static readonly Regex HeaderBias = new(@"\((?<bias>-?\d+)\)\s*$", RegexOptions.Compiled);

    private TypePerfCsv(IReadOnlyList<TypeperfCounterPath> counters, long[] sampleTimesUtc, List<TypeperfSample> samples, int timeZoneBiasMinutes, int skippedRows)
    {
        Counters = counters;
        SampleTimesUtc = sampleTimesUtc;
        Samples = samples;
        TimeZoneBiasMinutes = timeZoneBiasMinutes;
        SkippedRows = skippedRows;
    }

    public IReadOnlyList<TypeperfCounterPath> Counters { get; }

    /// <summary>UTC FILETIME ticks of each sample row.</summary>
    public IReadOnlyList<long> SampleTimesUtc { get; }

    /// <summary>All non-blank values, in file order.</summary>
    public IReadOnlyList<TypeperfSample> Samples { get; }

    /// <summary>Minutes to add to local time to get UTC, from the header (e.g. 360 for UTC-6).</summary>
    public int TimeZoneBiasMinutes { get; }

    /// <summary>Rows that could not be parsed (e.g. a last row cut off when typeperf was stopped).</summary>
    public int SkippedRows { get; }

    /// <summary>Returns true if the first line of the file looks like typeperf output.</summary>
    public static bool LooksLikeTypePerf(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var first = reader.ReadLine();
        return first is not null && first.Contains("(PDH-CSV", StringComparison.Ordinal);
    }

    public static TypePerfCsv Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Load(reader, path);
    }

    public static TypePerfCsv Load(TextReader reader, string sourceName = "typeperf CSV")
    {
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException($"{sourceName} is empty.");
        var header = SplitCsv(headerLine);
        if (header.Count < 2 || !header[0].Contains("(PDH-CSV", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{sourceName} is not typeperf CSV output (missing PDH-CSV header).");
        }

        var biasMatch = HeaderBias.Match(header[0]);
        var bias = biasMatch.Success ? int.Parse(biasMatch.Groups["bias"].Value, CultureInfo.InvariantCulture) : 0;
        var counters = header.Skip(1).Select(TypeperfCounterPath.Parse).ToList();

        var times = new List<long>();
        var samples = new List<TypeperfSample>();
        var skipped = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var cells = SplitCsv(line);
            if (cells.Count != counters.Count + 1 || !TryParseLocalTime(cells[0], out var local))
            {
                skipped++;
                continue;
            }

            var utc = DateTime.SpecifyKind(local.AddMinutes(bias), DateTimeKind.Utc);
            var sampleIndex = times.Count;
            times.Add(utc.ToFileTimeUtc());
            for (var c = 1; c < cells.Count; c++)
            {
                var text = cells[c].Trim();
                if (text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    samples.Add(new TypeperfSample(sampleIndex, c - 1, v));
                }
            }
        }

        return new TypePerfCsv(counters, times.ToArray(), samples, bias, skipped);
    }

    private static bool TryParseLocalTime(string text, out DateTime local) =>
        DateTime.TryParseExact(text, new[] { "MM/dd/yyyy HH:mm:ss.fff", "MM/dd/yyyy HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);

    /// <summary>Splits one CSV line with double-quoted fields ("" escapes a quote).</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (quoted)
        {
            // Unterminated quote: the row was cut off mid-write.
            return new List<string>();
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
