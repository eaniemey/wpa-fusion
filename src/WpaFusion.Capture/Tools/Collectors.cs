using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using WpaFusion.Capture.Clocks;
using WpaFusion.Common;

namespace WpaFusion.Capture.Tools;

/// <summary>WPR: records the requested profiles plus the WPA-Fusion marker provider.</summary>
internal sealed class WprCollector
{
    public const string EtlFileName = "trace.etl";
    public const string MarkerProfileFileName = "WpaFusion.wprp";

    /// <summary>Built-in profile: context switches only (CPU Usage (Precise)), no sampled profile.</summary>
    public const string PreciseProfileName = "WpaFusionPrecise";
    public const string PreciseProfileFileName = "WpaFusionPrecise.wprp";
    private readonly ProcessRunner _runner;
    private readonly RunLog _log;
    private readonly string _runDir;
    private bool _started;

    public WprCollector(ProcessRunner runner, RunLog log, string runDir)
    {
        _runner = runner;
        _log = log;
        _runDir = runDir;
    }

    public static string WprExe => Path.Combine(Environment.SystemDirectory, "wpr.exe");

    public string EtlPath => Path.Combine(_runDir, EtlFileName);

    public void Start(IReadOnlyList<string> profiles)
    {
        var markerProfile = Path.Combine(_runDir, MarkerProfileFileName);
        File.WriteAllText(markerProfile, MarkerProfileXml(MarkerSource.ProviderGuid));

        var status = _runner.Run(WprExe, "-status", TimeSpan.FromSeconds(30), throwOnError: false);
        if (status.Contains("WPR is recording", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WPR is already recording. Stop it first with 'wpr -cancel'.");
        }

        // "none" records only the WPA-Fusion marker provider (tiny ETL; useful for alignment checks).
        string Profile(string p)
        {
            if (!p.Equals(PreciseProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }

            var path = Path.Combine(_runDir, PreciseProfileFileName);
            File.WriteAllText(path, PreciseProfileXml());
            return path + "!" + PreciseProfileName;
        }

        var args = string.Join(" ", profiles.Where(p => !p.Equals("none", StringComparison.OrdinalIgnoreCase)).Select(p => $"-start {Quote(Profile(p))}"))
            + $" -start {Quote(markerProfile + "!WpaFusionMarkers")} -filemode";
        _runner.Run(WprExe, args, TimeSpan.FromMinutes(2));
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _log.Info("Stopping WPR and merging the trace (this can take a minute)...");
        _runner.Run(WprExe, $"-stop {Quote(EtlPath)} \"WPA-Fusion capture\"", TimeSpan.FromMinutes(30));
    }

    public void Cancel()
    {
        if (_started)
        {
            _started = false;
            _runner.Run(WprExe, "-cancel", TimeSpan.FromMinutes(2), throwOnError: false);
        }
    }

    /// <summary>
    /// Kernel events for CPU Usage (Precise): process/thread and image lifetimes, context switches and ready-thread
    /// events, with stacks on both. Deliberately no SampledProfile, so ETW does not compete with SEP for CPU sampling.
    /// </summary>
    internal static string PreciseProfileXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <WindowsPerformanceRecorder Version="1.0" Author="WPA-Fusion">
          <Profiles>
            <SystemCollector Id="WpaFusionPreciseCollector" Name="NT Kernel Logger">
              <BufferSize Value="1024" />
              <Buffers Value="256" />
            </SystemCollector>
            <SystemProvider Id="WpaFusionPreciseProvider">
              <Keywords>
                <Keyword Value="ProcessThread" />
                <Keyword Value="Loader" />
                <Keyword Value="CSwitch" />
                <Keyword Value="ReadyThread" />
              </Keywords>
              <Stacks>
                <Stack Value="CSwitch" />
                <Stack Value="ReadyThread" />
              </Stacks>
            </SystemProvider>
            <Profile Id="WpaFusionPrecise.Verbose.File" Name="WpaFusionPrecise" Description="WPA-Fusion CPU Usage (Precise) only" LoggingMode="File" DetailLevel="Verbose">
              <Collectors>
                <SystemCollectorId Value="WpaFusionPreciseCollector">
                  <SystemProviderId Value="WpaFusionPreciseProvider" />
                </SystemCollectorId>
              </Collectors>
            </Profile>
          </Profiles>
        </WindowsPerformanceRecorder>
        """;

    internal static string MarkerProfileXml(Guid provider) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <WindowsPerformanceRecorder Version="1.0" Author="WPA-Fusion">
          <Profiles>
            <EventCollector Id="WpaFusionCollector" Name="WPA-Fusion Markers">
              <BufferSize Value="64" />
              <Buffers Value="8" />
            </EventCollector>
            <EventProvider Id="WpaFusionProvider" Name="{provider:D}" />
            <Profile Id="WpaFusionMarkers.Verbose.File" Name="WpaFusionMarkers" Description="WPA-Fusion clock sync markers" LoggingMode="File" DetailLevel="Verbose">
              <Collectors>
                <EventCollectorId Value="WpaFusionCollector">
                  <EventProviders>
                    <EventProviderId Value="WpaFusionProvider" />
                  </EventProviders>
                </EventCollectorId>
              </Collectors>
            </Profile>
          </Profiles>
        </WindowsPerformanceRecorder>
        """;

    internal static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

/// <summary>
/// SEP: collection into &lt;run&gt;/sep.tb7. "sep -start -nb" does not return until "sep -stop", and it
/// prints nothing when sampling begins, so it runs as a background process.
/// </summary>
internal sealed class SepCollector
{
    public const string OutputBaseName = "sep";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private readonly ProcessRunner _runner;
    private readonly RunLog _log;
    private readonly string _runDir;
    private Process? _process;

    public SepCollector(ProcessRunner runner, RunLog log, string runDir)
    {
        _runner = runner;
        _log = log;
        _runDir = runDir;
    }

    public string Tb7Path => Path.Combine(_runDir, OutputBaseName + ".tb7");

    public string CommandLine { get; private set; } = string.Empty;

    internal static string BuildCommandLine(CaptureOptions options, string runDir)
    {
        var events = options.SepEvents is not null ? $"-ec {WprCollector.Quote(options.SepEvents)}" : $"-atype {options.SepAtype}";
      var extraArgs = options.SepExtraArgs ?? string.Empty;
      var extraLbr = extraArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Any(arg => arg.Equals("-lbr", StringComparison.OrdinalIgnoreCase));
      var lbr = options.SepLbr is null || extraLbr ? string.Empty : $" -lbr {options.SepLbr}";
        var startPaused = options.SepStartPaused ? " -sp" : string.Empty;
        var samplingDelay = options.SepSamplingDelaySeconds > 0
            ? $" -sd {options.SepSamplingDelaySeconds.ToString("R", CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"-start -nb -d 0 {events}{startPaused}{samplingDelay} -out {WprCollector.Quote(Path.Combine(runDir, OutputBaseName))}{lbr} {options.SepExtraArgs}".Trim();
    }

    public void Start(CaptureOptions options)
    {
        if (!File.Exists(SepInstall.SepExe))
        {
            throw new FileNotFoundException($"SEP not found at '{SepInstall.SepExe}'. Set {SepInstall.EnvironmentVariable}.");
        }

        CommandLine = BuildCommandLine(options, _runDir);
        _process = _runner.StartBackground(SepInstall.SepExe, CommandLine, Path.Combine(_runDir, "sep.log"));
        Thread.Sleep(StartupDelay);
        if (_process.HasExited && _process.ExitCode != 0)
        {
            var code = _process.ExitCode;
          _process.Dispose();
            _process = null;
            throw new InvalidOperationException($"SEP exited immediately with code {code}; see sep.log in the run folder.");
        }
    }

    /// <summary>Stops sampling and waits for SEP to finish writing the .tb7.</summary>
    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        var p = _process;
        _process = null;
        _runner.Run(SepInstall.SepExe, "-stop", TimeSpan.FromMinutes(10), throwOnError: false);
        if (!p.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            _log.Info("SEP did not exit after -stop; terminating it.");
            p.Kill();
        }

        p.Dispose();
    }

      public void Resume()
      {
        if (_process is null)
        {
          throw new InvalidOperationException("SEP collection has not been started and cannot be resumed.");
        }

        _runner.Run(SepInstall.SepExe, "-resume", TimeSpan.FromSeconds(30));
      }
}

/// <summary>typeperf: samples performance counters into &lt;run&gt;/typeperf.csv.</summary>
internal sealed class TypePerfCollector
{
    public const string CsvFileName = "typeperf.csv";
    public const string CountersFileName = "typeperfinput.txt";
    private const string EmbeddedCounters = "WpaFusion.Capture.typeperfinput.txt";
    private readonly ProcessRunner _runner;
    private readonly RunLog _log;
    private readonly string _runDir;
    private Process? _process;

    public TypePerfCollector(ProcessRunner runner, RunLog log, string runDir)
    {
        _runner = runner;
        _log = log;
        _runDir = runDir;
    }

    public string CsvPath => Path.Combine(_runDir, CsvFileName);

    public string CountersPath => Path.Combine(_runDir, CountersFileName);

    public string CommandLine { get; private set; } = string.Empty;

    public void Start(CaptureOptions options)
    {
        if (options.CountersFile is not null)
        {
            File.Copy(options.CountersFile, CountersPath);
        }
        else
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedCounters)
                ?? throw new InvalidOperationException("Embedded counter list missing.");
            using var file = File.Create(CountersPath);
            resource.CopyTo(file);
        }

        var exe = Path.Combine(Environment.SystemDirectory, "typeperf.exe");
        CommandLine = string.Create(CultureInfo.InvariantCulture,
            $"-cf {WprCollector.Quote(CountersPath)} -si {options.IntervalSeconds} -f CSV -o {WprCollector.Quote(CsvPath)} -y");
        _process = _runner.StartBackground(exe, CommandLine, Path.Combine(_runDir, "typeperf.log"));
    }

    /// <summary>Stops this typeperf instance only (by PID). A partial last row is tolerated by the Typeperf plugin.</summary>
    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        var p = _process;
        _process = null;
        if (!p.HasExited)
        {
            p.Kill();
            p.WaitForExit(10_000);
        }

        _log.Info($"typeperf stopped (exit code {(p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "n/a")}).");
        p.Dispose();
    }
}

internal sealed class EmonCollector
{
  public const string DataFileName = "emon.dat";
  public const string EventsFileName = "emon-events.txt";
  private readonly ProcessRunner _runner;
  private readonly string _runDir;
  private readonly CaptureOptions _options;
  private Process? _process;

  public EmonCollector(ProcessRunner runner, string runDir, CaptureOptions options)
  {
    _runner = runner;
    _runDir = runDir;
    _options = options;
  }

  public static string Exe => Path.Combine(SepInstall.Bin64, "emon.exe");

  public string CommandLine => BuildCommandLine(_options, _runDir);

  internal static string BuildCommandLine(CaptureOptions options, string runDir)
  {
    var events = options.EmonEventsFile is null ? string.Empty :
      $" {WprCollector.Quote("edp_file=" + Path.Combine(runDir, EventsFileName))}";
    return $"-collect-edp{events} -l 0 -f {WprCollector.Quote(Path.Combine(runDir, DataFileName))}";
  }

  public void Start()
  {
    if (!File.Exists(Exe))
    {
      throw new FileNotFoundException($"EMON not found at '{Exe}'. Set {SepInstall.EnvironmentVariable}.");
    }

    if (_options.EmonEventsFile is not null)
    {
      File.Copy(_options.EmonEventsFile, Path.Combine(_runDir, EventsFileName));
    }

    _process = _runner.StartBackground(Exe, CommandLine, Path.Combine(_runDir, "emon.log"));
    if (_process.WaitForExit(2000))
    {
      var code = _process.ExitCode;
      _process.Dispose();
      _process = null;
      throw new InvalidOperationException($"EMON exited during startup with code {code}; see emon.log.");
    }
  }

  public void Stop()
  {
    if (_process is not { } process)
    {
      return;
    }

    _process = null;
    using (process)
    {
      if (!process.HasExited)
      {
        _runner.Run(Exe, "-stop", TimeSpan.FromSeconds(30));
      }

      if (!process.WaitForExit(60_000))
      {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("EMON did not finish writing its data after -stop.");
      }

            var data = new FileInfo(Path.Combine(_runDir, DataFileName));
            if (process.ExitCode != 0 || !data.Exists || data.Length == 0)
      {
        throw new InvalidOperationException("EMON collection failed; see emon.log.");
      }

            EmonMetricDefinitions.Snapshot(data.FullName, _runDir, SepInstall.Root);
    }
  }
}
