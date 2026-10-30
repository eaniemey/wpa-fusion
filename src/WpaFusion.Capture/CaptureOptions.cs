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
          --duration <seconds>   Stop all collectors after this many seconds; use --sep-duration for a SEP-only window.
          --output <dir>         Output root; each run gets <dir>/<yyyyMMdd-HHmmss>/ (default: <repo>/output).
          --skip-attribute       Keep raw capture only; do not automatically run wpafusion-attribute afterward.
          --wpr-profile <name>   WPR profile to record, repeatable (default: WpaFusionPrecise, context
                                 switches only for CPU Usage (Precise); GeneralProfile adds CPU Usage (Sampled)).
                                 "none" records only the WPA-Fusion sync markers.
          --counters <file>      typeperf counter list (default: built-in Intel TypePerf set).
          --interval <seconds>   typeperf sample interval (default: 1).
          --sep-atype <name>     SEP analysis type (default: uarch-exploration; includes reference cycles).
          --sep-events <list>    SEP event list passed to -ec instead of an analysis type; include
                                 CPU_CLK_UNHALTED.REF_TSC for estimated CPU usage.
          --sep-start-paused     Start SEP paused; press Enter when the workload reaches the desired point to resume.
          --sep-resume-after <seconds> Automatically resume paused SEP after this workload delay instead of Enter.
          --sep-duration <seconds> Stop only SEP after this resumed window; ETW/Typeperf continue until workload exit.
          --sep-sampling-delay <seconds> Delay sampling after SEP starts or is resumed (default: 0).
          --sep-lbr <mode|off>   SEP last-branch records for call stacks (default: call_stack:usr).
                                 Use "off" to disable. LBR makes .tb7 files large; use -sam to thin samples.
          --sep-args <args>      Extra arguments appended to the SEP start command.
          --markers <n>          Sync markers written at start and at end (default: 3).
          --no-etw               Skip WPR.
          --no-typeperf          Skip typeperf.
          --no-sep               Skip SEP.
          --emon                 Collect EMON EDP counters instead of SEP samples (mutually exclusive).
          --emon-events <file>   EMON EDP event file override; copied into the run for reproducibility.
          --runs <1|2>           Opt-in pair: SEP+ETL followed by standalone EMON (default: 1 run).
                                 Requires a workload and ETW; no global --duration. Timed SEP windows are supported. {run-directory}
                                 in workload arguments expands to each execution's output folder.
          -h, --help             Show this help.
        """;

    /// <summary>Optional maximum capture duration; null means wait for workload completion or user input.</summary>
    public double? DurationSeconds { get; private set; }

    /// <summary>Optional output root. A fresh timestamped run folder is created beneath it.</summary>
    public string? OutputRoot { get; private set; }
    public bool SkipAttribute { get; private set; }

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
    public double? SepResumeAfterSeconds { get; private set; }
    public double? SepDurationSeconds { get; private set; }

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

    public bool Emon { get; private set; }
    public string? EmonEventsFile { get; private set; }
    public int RunCount { get; private set; } = 1;

    internal CaptureOptions ForBatchRun(bool emon, string outputRoot)
    {
        var copy = (CaptureOptions)MemberwiseClone();
        copy.RunCount = 1;
        copy.SkipAttribute = true;
        copy.OutputRoot = outputRoot;
        copy.Emon = emon;
        copy.Sep = !emon;
        copy.Etw = !emon;
        copy.EmonEventsFile = emon ? EmonEventsFile : null;
        copy.SepStartPaused = !emon && SepStartPaused;
        copy.SepResumeAfterSeconds = emon ? null : SepResumeAfterSeconds;
        copy.SepDurationSeconds = emon ? null : SepDurationSeconds;
        copy.SepSamplingDelaySeconds = emon ? 0 : SepSamplingDelaySeconds;
        return copy;
    }

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
                case "--skip-attribute":
                    o.SkipAttribute = true;
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
                case "--sep-resume-after":
                    o.SepResumeAfterSeconds = PositiveDouble(a, Value());
                    if (!double.IsFinite(o.SepResumeAfterSeconds.Value) || o.SepResumeAfterSeconds.Value > int.MaxValue / 1000d)
                    {
                        throw new ArgumentException("--sep-resume-after must be finite and fit the process wait timeout.");
                    }

                    break;
                case "--sep-duration":
                    o.SepDurationSeconds = PositiveDouble(a, Value());
                    if (!double.IsFinite(o.SepDurationSeconds.Value) || o.SepDurationSeconds.Value > int.MaxValue / 1000d)
                    {
                        throw new ArgumentException("--sep-duration must be finite and fit the process wait timeout.");
                    }

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
                case "--emon":
                    o.Emon = true;
                    o.Sep = false;
                    break;
                case "--emon-events":
                    o.EmonEventsFile = Path.GetFullPath(Value());
                    break;
                case "--runs":
                    if (!int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs) ||
                        runs is not (1 or 2))
                    {
                        throw new ArgumentException("--runs must be 1 (default single run) or 2 (one SEP/ETL and EMON pair).");
                    }

                    o.RunCount = runs;
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

        if (o.SepResumeAfterSeconds is not null && (!o.Sep || !o.SepStartPaused))
        {
            throw new ArgumentException("--sep-resume-after requires --sep-start-paused and SEP collection.");
        }

        if (o.SepDurationSeconds is not null && (!o.Sep || !o.Etw || o.Workload.Count == 0 || o.DurationSeconds is not null))
        {
            throw new ArgumentException("--sep-duration requires SEP, ETW, and a workload; use it instead of the global --duration cap.");
        }

        if (o.Sep && !o.Etw && !o.SkipAttribute)
        {
            throw new ArgumentException("Automatic SEP attribution requires the matching ETL; remove --no-etw or add --skip-attribute.");
        }

        if (o.EmonEventsFile is not null && (!(o.Emon || o.RunCount > 1) || !File.Exists(o.EmonEventsFile)))
        {
            throw new ArgumentException("--emon-events requires --emon and an existing event file.");
        }

        if (o.RunCount > 1 && (o.Workload.Count == 0 || !o.Etw || !o.Sep || o.Emon ||
            o.DurationSeconds is not null || o.SepStartPaused && (o.SepResumeAfterSeconds is null || o.SepDurationSeconds is null)))
        {
            throw new ArgumentException("Multi-run capture requires a repeatable workload and ETW, without global duration/collector overrides. A paused SEP pass requires both --sep-resume-after and --sep-duration.");
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
