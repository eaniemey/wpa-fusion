using System.Globalization;

namespace WpaFusion.Capture;

/// <summary>
/// Parsed command-line configuration for one coordinated capture.
/// Defaults mirror the collector's normal workflow: WPR, Typeperf, SEP, reference-cycle TMA events,
/// and user-mode LBR call stacks are enabled unless explicitly disabled.
/// </summary>
internal sealed class CaptureOptions
{
    /// <summary>Help text printed by the capture executable.</summary>
    public const string Usage = """
        wpafusion-capture: collect ETW (WPR), Typeperf and SEP together with clock sync points.

        Usage:
          wpafusion-capture [options] [-- <workload command> [args...]]

        Collection stops when the workload exits, after --duration, or when you press Enter.

        Options:
          --duration <seconds>   Stop after this many seconds.
          --output <dir>         Output root; each run gets <dir>/<yyyyMMdd-HHmmss>/ (default: <repo>/output).
          --wpr-profile <name>   WPR profile to record, repeatable (default: WpaFusionPrecise, context
                                 switches only for CPU Usage (Precise); GeneralProfile adds CPU Usage (Sampled)).
                                 "none" records only the WPA-Fusion sync markers.
          --counters <file>      typeperf counter list (default: built-in Intel TypePerf set).
          --interval <seconds>   typeperf sample interval (default: 1).
          --sep-atype <name>     SEP analysis type (default: uarch-exploration; includes reference cycles).
          --sep-events <list>    SEP event list passed to -ec instead of an analysis type; include
                                 CPU_CLK_UNHALTED.REF_TSC for estimated CPU usage.
          --sep-start-paused     Start SEP paused; press Enter when the workload reaches the desired point to resume.
          --sep-sampling-delay <seconds> Delay sampling after SEP starts or is resumed (default: 0).
          --sep-lbr <mode|off>   SEP last-branch records for call stacks (default: call_stack:usr).
                                 Use "off" to disable. LBR makes .tb7 files large; use -sam to thin samples.
          --sep-args <args>      Extra arguments appended to the SEP start command.
          --markers <n>          Sync markers written at start and at end (default: 3).
          --no-etw               Skip WPR.
          --no-typeperf          Skip typeperf.
          --no-sep               Skip SEP.
          -h, --help             Show this help.
        """;

    /// <summary>Optional maximum capture duration; null means wait for workload completion or user input.</summary>
    public double? DurationSeconds { get; private set; }

    /// <summary>Optional output root. A fresh timestamped run folder is created beneath it.</summary>
    public string? OutputRoot { get; private set; }

    /// <summary>WPR profile names to start; an empty command-line list receives the precise default profile.</summary>
    public List<string> WprProfiles { get; } = new();

    /// <summary>Optional Typeperf counter-list file, resolved to an absolute path.</summary>
    public string? CountersFile { get; private set; }

    /// <summary>Typeperf sampling interval in seconds.</summary>
    public double IntervalSeconds { get; private set; } = 1;

    /// <summary>SEP analysis type used unless <see cref="SepEvents"/> supplies a custom event list.</summary>
    public string SepAtype { get; private set; } = "uarch-exploration";

    /// <summary>Optional SEP event list; when set, replaces the selected analysis type.</summary>
    public string? SepEvents { get; private set; }

    /// <summary>Starts SEP paused and waits for the user to resume it after the workload begins.</summary>
    public bool SepStartPaused { get; private set; }

    /// <summary>SEP sampling delay in seconds; SEP begins collecting after this delay.</summary>
    public double SepSamplingDelaySeconds { get; private set; }

    /// <summary>Additional arguments appended to the SEP start command.</summary>
    public string? SepExtraArgs { get; private set; }

    /// <summary>LBR capture mode passed to SEP as -lbr; null disables LBR.</summary>
    public string? SepLbr { get; private set; } = "call_stack:usr";

    /// <summary>Number of clock markers captured in each start/end burst.</summary>
    public int Markers { get; private set; } = 3;

    /// <summary>Whether WPR/ETW collection is enabled.</summary>
    public bool Etw { get; private set; } = true;

    /// <summary>Whether Typeperf counter collection is enabled.</summary>
    public bool Typeperf { get; private set; } = true;

    /// <summary>Whether Intel SEP collection is enabled.</summary>
    public bool Sep { get; private set; } = true;

    /// <summary>Whether help was requested.</summary>
    public bool Help { get; private set; }

    /// <summary>Workload executable and arguments following the <c>--</c> separator.</summary>
    public List<string> Workload { get; } = new();

    /// <summary>
    /// Parses collector options, normalizes path arguments, and validates positive numeric values.
    /// Arguments after <c>--</c> are preserved as the workload command.
    /// </summary>
    public static CaptureOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CaptureOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Value()
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"Option {a} needs a value.");
                }

                return args[++i];
            }

            switch (a)
            {
                case "--":
                    o.Workload.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "-h":
                case "--help":
                case "/?":
                    o.Help = true;
                    break;
                case "--duration":
                    o.DurationSeconds = PositiveDouble(a, Value());
                    break;
                case "--output":
                    o.OutputRoot = Path.GetFullPath(Value());
                    break;
                case "--wpr-profile":
                    o.WprProfiles.Add(Value());
                    break;
                case "--counters":
                    o.CountersFile = Path.GetFullPath(Value());
                    break;
                case "--interval":
                    o.IntervalSeconds = PositiveDouble(a, Value());
                    break;
                case "--sep-atype":
                    o.SepAtype = Value();
                    break;
                case "--sep-events":
                    o.SepEvents = Value();
                    break;
                case "--sep-start-paused":
                    o.SepStartPaused = true;
                    break;
                case "--sep-sampling-delay":
                    o.SepSamplingDelaySeconds = PositiveDouble(a, Value());
                    break;
                case "--sep-args":
                    o.SepExtraArgs = string.Join(' ', new[] { o.SepExtraArgs, Value() }.Where(x => !string.IsNullOrEmpty(x)));
                    break;
                case "--sep-lbr":
                    var mode = Value();
                    o.SepLbr = mode.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : mode;
                    break;
                case "--markers":
                    o.Markers = (int)PositiveDouble(a, Value());
                    break;
                case "--no-etw":
                    o.Etw = false;
                    break;
                case "--no-typeperf":
                    o.Typeperf = false;
                    break;
                case "--no-sep":
                    o.Sep = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{a}'. Use --help.");
            }
        }

        if (o.WprProfiles.Count == 0)
        {
            o.WprProfiles.Add(Tools.WprCollector.PreciseProfileName);
        }

        if (o.CountersFile is not null && !File.Exists(o.CountersFile))
        {
            throw new ArgumentException($"Counter file not found: '{o.CountersFile}'.");
        }

        if (!o.Sep && (o.SepStartPaused || o.SepSamplingDelaySeconds > 0))
        {
            throw new ArgumentException("SEP pause and sampling-delay options require SEP collection; remove --no-sep.");
        }

        return o;
    }

    /// <summary>Parses an invariant-culture numeric option and rejects non-positive values.</summary>
    private static double PositiveDouble(string option, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0)
        {
            throw new ArgumentException($"Option {option} needs a positive number, got '{value}'.");
        }

        return d;
    }
}
