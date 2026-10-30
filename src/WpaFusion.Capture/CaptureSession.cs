using System.Diagnostics;
using System.Globalization;
using WpaFusion.Capture.Clocks;
using WpaFusion.Capture.Tools;
using WpaFusion.Common;
using WpaFusion.Tbrw;

namespace WpaFusion.Capture;

/// <summary>
/// One capture run: start collectors, take sync points and markers, wait, stop in reverse order,
/// post-process (bind .tb7, read ETL clock reference) and write session.json.
/// </summary>
internal sealed class CaptureSession
{
    private readonly CaptureOptions _options;

    public CaptureSession(CaptureOptions options) => _options = options;

    public string? RunDirectory { get; private set; }

    public int Run()
    {
        var outputRoot = _options.OutputRoot ?? RunFolder.DefaultOutputRoot();
        var runDir = RunFolder.Create(outputRoot, DateTime.Now);
        RunDirectory = runDir;
        using var log = new RunLog(Path.Combine(runDir, "capture.log"));
        var runner = new ProcessRunner(log);
        log.Info($"Run folder: {runDir}");
        try
        {
            return RunCore(runDir, log, runner);
        }
        catch (Exception ex)
        {
            // Leave an unmistakable marker so a partial run folder is never mistaken for a usable session.
            log.Info($"Capture FAILED: {ex.Message}");
            WriteFailureMarker(runDir, ex);
            throw;
        }
        finally
        {
            runner.CloseLogs();
        }
    }

    private int RunCore(string runDir, RunLog log, ProcessRunner runner)
    {
        var manifest = new SessionManifest
        {
            RunId = Path.GetFileName(runDir),
            Machine = Environment.MachineName,
            CaptureTimeZoneId = TimeZoneInfo.Local.Id,
            WorkloadCommand = _options.Workload.ToList(),
            ExecutedWorkloadCommand = _options.Workload.Select(argument => argument.Replace("{run-directory}", runDir, StringComparison.Ordinal)).ToList(),
            QpcFrequency = NativeClock.QpcFrequency,
        };

        manifest.Tools["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        log.Info($"CPU: {manifest.Tools["cpu"]}{(CpuFeatures.IsHybrid() ? " (hybrid)" : string.Empty)}");
        if (_options.Sep)
        {
            manifest.Platform = DetectSepPlatform(log);
        }

        if (_options.Sep)
        {
            log.Info(_options.SepLbr is null
                ? "SEP LBR call stacks: off."
                : $"SEP LBR call stacks: -lbr {_options.SepLbr} (large .tb7; use --sep-args \"-sam N\" to thin samples).");
        }
        if (_options.SepStartPaused)
        {
            log.Info(_options.SepResumeAfterSeconds is { } delay
                ? $"SEP will start paused and resume {delay.ToString(CultureInfo.InvariantCulture)} seconds after workload launch."
                : "SEP will start paused; press Enter after the workload starts to resume it.");
        }

        if (_options.SepSamplingDelaySeconds > 0)
        {
            log.Info($"SEP sampling delay: {_options.SepSamplingDelaySeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }

        var wpr = _options.Etw ? new WprCollector(runner, log, runDir) : null;
        var sep = _options.Sep ? new SepCollector(runner, log, runDir) : null;
        var emon = _options.Emon ? new EmonCollector(runner, runDir, _options) : null;
        var typeperf = _options.Typeperf ? new TypePerfCollector(runner, log, runDir) : null;

        try
        {
            // Start order: ETW first (it records the markers), then SEP, then typeperf.
            if (wpr is not null)
            {
                wpr.Start(_options.WprProfiles);
                manifest.Tools["wpr"] = "-start " + string.Join(" -start ", _options.WprProfiles) + " -start WpaFusion.wprp!WpaFusionMarkers -filemode";
            }

            if (sep is not null)
            {
                sep.Start(_options);
                manifest.Tools["sep"] = sep.CommandLine;
            }

            if (typeperf is not null)
            {
                typeperf.Start(_options);
                manifest.Tools["typeperf"] = typeperf.CommandLine;
            }

            if (emon is not null)
            {
                emon.Start();
                manifest.Tools["emon"] = emon.CommandLine;
            }

            Thread.Sleep(500);
            manifest.SyncStart = SyncSampler.Capture();
            manifest.TscSurvey = SyncSampler.SurveyAllCpus();
            WriteMarkers(manifest, "start");
            log.Info($"Collecting. Sync start spread: {manifest.SyncStart.TscSpread} TSC ticks.");

            WaitForWorkload(log, manifest, sep is not null && _options.SepStartPaused
                ? () =>
                {
                    sep.Resume();
                    WriteMarker(manifest, "sep-resume", 0);
                    log.Info("SEP resumed; sampling delay, if configured, now applies.");
                }
                : null, sep is not null && _options.SepDurationSeconds is not null
                ? () =>
                {
                    WriteMarker(manifest, "sep-window-end", 0);
                    sep.Stop();
                    WriteMarker(manifest, "sep-stopped", 0);
                    log.Info("SEP sampling window complete; ETW and other enabled collectors continue until workload exit.");
                }
                : null);

            WriteMarkers(manifest, "end");
            manifest.SyncEnd = SyncSampler.Capture();
            log.Info($"Sync end spread: {manifest.SyncEnd.TscSpread} TSC ticks.");
        }
        catch
        {
            typeperf?.Stop();
            TryRun(log, () => emon?.Stop());
            TryRun(log, () => sep?.Stop());
            wpr?.Cancel();
            throw;
        }

        // Stop in reverse order.
        typeperf?.Stop();
        emon?.Stop();
        sep?.Stop();
        wpr?.Stop();

        if (typeperf is not null)
        {
            manifest.Files.TypeperfCsv = TypePerfCollector.CsvFileName;
            manifest.Files.TypeperfCounters = TypePerfCollector.CountersFileName;
        }

        if (sep is not null)
        {
            BindTb7(log, sep.Tb7Path);
            manifest.Files.Tb7 = Path.GetFileName(sep.Tb7Path);
        }

        if (emon is not null)
        {
            manifest.Files.EmonDat = EmonCollector.DataFileName;
            manifest.Files.EmonEvents = _options.EmonEventsFile is null ? null : EmonCollector.EventsFileName;
            manifest.Files.EmonDefinition = File.Exists(Path.Combine(runDir, EmonMetricDefinitions.SnapshotFileName))
                ? EmonMetricDefinitions.SnapshotFileName : null;
        }

        VerifyArtifacts(runDir, wpr, typeperf);

        if (wpr is not null)
        {
            manifest.Files.Etl = WprCollector.EtlFileName;
            manifest.Etl = ReadEtlReference(log, wpr.EtlPath, manifest);
        }

        var manifestPath = Path.Combine(runDir, SessionManifest.FileName);
        manifest.Save(manifestPath);
        log.Info($"Wrote {manifestPath}");
        PrintOpenHint(log, runDir, manifest);
        return 0;
    }

    /// <summary>
    /// Asks the local SEP which platform this machine is, reports whether SEP has TMA metrics for it, and
    /// checks the requested analysis type exists on it before anything starts collecting.
    /// </summary>
    private SepPlatform DetectSepPlatform(RunLog log)
    {
        if (!File.Exists(SepInstall.SepExe))
        {
            throw new FileNotFoundException($"SEP not found at '{SepInstall.SepExe}'. Set {SepInstall.EnvironmentVariable}.");
        }

        var platform = SepPlatform.ParseVersion(SepInstall.Query("-version"))
            ?? throw new InvalidOperationException($"'{SepInstall.SepExe} -version' reported no PMU; check the SEP driver is installed and loaded.");
        log.Info($"SEP platform: {platform}");
        var edp = platform.EdpMetricsPath(SepInstall.Root);
        log.Info(edp is null
            ? $"TMA metrics: SEP has no EDP metric file for PMU '{platform.Pmu}'; TMA tables will be unavailable for this run."
            : $"TMA metrics: {edp}");

        if (_options.SepEvents is null)
        {
            var atypes = SepAtypes(SepInstall.Query("-atypelist"));
            if (atypes.Count > 0 && !atypes.Contains(_options.SepAtype, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"SEP analysis type '{_options.SepAtype}' is not available on {platform.Pmu}. Available: {string.Join(", ", atypes)}.");
            }
        }

        return platform;
    }

    /// <summary>Analysis type names from "sep -atypelist" ("atype: name" lines).</summary>
    internal static IReadOnlyList<string> SepAtypes(string? output) =>
        (output ?? string.Empty).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("atype:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["atype:".Length..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

    private void WriteMarkers(SessionManifest manifest, string name)
    {
        for (var i = 0; i < _options.Markers; i++)
        {
            WriteMarker(manifest, name, i);
            Thread.Sleep(10);
        }
    }

    private static void WriteMarker(SessionManifest manifest, string name, int index)
    {
        int cpu;
        SyncPoint point;
        using (var pin = ThreadAffinity.PinToCurrentProcessor())
        {
            cpu = pin.Cpu;
            point = SyncSampler.OnPinnedCpu();
            MarkerSource.Log.Sync(name, index, point.Tsc, point.Qpc, point.FileTimeUtc);
        }

        manifest.Markers.Add(new SessionMarker(name, index, point.Tsc, point.Qpc, point.FileTimeUtc) { Cpu = cpu });
    }

    private void WaitForWorkload(RunLog log, SessionManifest manifest, Action? resumeSep, Action? stopSep)
    {
        if (_options.Workload.Count > 0)
        {
            var command = manifest.ExecutedWorkloadCommand;
            var psi = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            foreach (var a in command.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            log.Info($"Running workload: {string.Join(" ", command)}");
            WriteMarker(manifest, "workload-start", 0);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Workload did not start.");
            ResumeSepWhenRequested(log, resumeSep, p);
            if (_options.SepDurationSeconds is { } window && stopSep is not null)
            {
                log.Info($"Recording a {window.ToString(CultureInfo.InvariantCulture)} second SEP-only window; ETW will retain the complete workload.");
                p.WaitForExit((int)Math.Ceiling(window * 1000));
                stopSep();
            }

            if (_options.DurationSeconds is double limit)
            {
                if (!p.WaitForExit((int)(limit * 1000)))
                {
                    log.Info("Duration reached; workload left running.");
                }
            }
            else
            {
                p.WaitForExit();
            }

            if (p.HasExited)
            {
                WriteMarker(manifest, "workload-end", 0);
                manifest.WorkloadExitCode = p.ExitCode;
            }

            log.Info(p.HasExited ? $"Workload exited with code {p.ExitCode}." : "Stopping collection.");
            return;
        }

        ResumeSepWhenRequested(log, resumeSep);

        if (_options.DurationSeconds is double seconds)
        {
            log.Info($"Collecting for {seconds.ToString(CultureInfo.InvariantCulture)} s...");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return;
        }

        log.Info("Press Enter to stop collection.");
        Console.ReadLine();
    }

    private void ResumeSepWhenRequested(RunLog log, Action? resumeSep, Process? workload = null)
    {
        if (resumeSep is null)
        {
            return;
        }

        if (_options.SepResumeAfterSeconds is { } seconds)
        {
            log.Info($"SEP remains paused for {seconds.ToString(CultureInfo.InvariantCulture)} seconds; other enabled collectors continue.");
            if (workload is not null)
            {
                if (workload.WaitForExit((int)Math.Ceiling(seconds * 1000)))
                {
                    throw new InvalidOperationException("The workload exited before the timed SEP resume point.");
                }
            }
            else
            {
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
            }
        }
        else
        {
            log.Info("Other enabled collectors are collecting; SEP is started but paused.");
            Console.WriteLine("Press Enter when the workload reaches the desired point to resume SEP sampling.");
            Console.ReadLine();
        }

        resumeSep();
    }

    private static void BindTb7(RunLog log, string tb7)
    {
        if (!File.Exists(tb7))
        {
            var partial = tb7 + "_";
            throw new FileNotFoundException(File.Exists(partial)
                ? $"SEP did not finish writing '{tb7}' (only the incomplete '{Path.GetFileName(partial)}' exists); see sep.log in the run folder."
                : $"SEP did not produce '{tb7}'; see sep.log in the run folder.");
        }

        TbrwLibrary.Initialize(SepInstall.Bin64);
        var tbrw = Path.GetFileName(TbrwLibrary.LoadedPath ?? string.Empty);
        log.Info($"SEP root: {SepInstall.Root}; TBRW: {tbrw}.");
        if (!tbrw.EndsWith(SepInstall.ValidatedTbrwVersion + ".dll", StringComparison.OrdinalIgnoreCase))
        {
            log.Info($"WARNING: TBRW {SepInstall.ValidatedTbrwVersion} is the validated version; {tbrw} has not been validated with WPA-Fusion.");
        }

        log.Info(TbrwFile.BindInPlace(tb7) ? $"Bound {Path.GetFileName(tb7)}." : $"{Path.GetFileName(tb7)} was already bound.");
    }

    private static EtlClockReference ReadEtlReference(RunLog log, string etl, SessionManifest manifest)
    {
        log.Info("Reading sync markers back from the ETL...");
        var markers = EtlClockReader.ReadMarkers(etl);
        var reference = EtlClockReader.Derive(markers, manifest.QpcFrequency);
        foreach (var m in markers)
        {
            // How far ETW's own event timestamp lags the QPC read just before the marker.
            var lagUs = (m.EventRawTimestamp - m.PayloadQpc) * 1e6 / manifest.QpcFrequency;
            log.Info(string.Create(CultureInfo.InvariantCulture, $"  marker {m.Name}#{m.Index}: ETL time {m.RelativeNanoseconds / 1e6:F3} ms, event-vs-payload QPC lag {lagUs:F2} us"));
        }

        // Each marker implies a time zero; they must agree exactly if WPA's ETL time is linear in QPC.
        var zeros = markers.Select(m => m.EventRawTimestamp - (long)Math.Round(m.RelativeNanoseconds * (double)manifest.QpcFrequency / 1e9)).ToList();
        log.Info($"ETL time zero: QPC {reference.ZeroQpc} (spread across markers: {zeros.Max() - zeros.Min()} QPC ticks), UTC {DateTime.FromFileTimeUtc(reference.ZeroFileTimeUtc):O}");

        // Record each marker's ETW timestamp so plugins can show where ETW placed it.
        for (var i = 0; i < manifest.Markers.Count; i++)
        {
            var sm = manifest.Markers[i];
            var match = markers.FirstOrDefault(m => m.Name == sm.Name && m.Index == sm.Index && m.PayloadQpc == sm.Qpc);
            if (match is not null)
            {
                manifest.Markers[i] = sm with { EtlRawTimestamp = match.EventRawTimestamp };
            }
        }

        return reference;
    }

    /// <summary>A run is only reported successful when every artifact it promises exists and is non-empty.</summary>
    private static void VerifyArtifacts(string runDir, WprCollector? wpr, TypePerfCollector? typeperf)
    {
        var problems = new List<string>();
        void Require(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                problems.Add($"{Path.GetFileName(path)} is missing or empty");
            }
        }

        if (wpr is not null)
        {
            Require(wpr.EtlPath);
        }

        if (typeperf is not null)
        {
            Require(typeperf.CsvPath);
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Capture incomplete in {runDir}: {string.Join("; ", problems)}.");
        }
    }

    private static void WriteFailureMarker(string runDir, Exception ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(runDir, "CAPTURE-FAILED.txt"),
                $"This run did not complete and is not a usable session (no session.json was written).{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (IOException io)
        {
            Console.Error.WriteLine($"Could not write the failure marker: {io.Message}");
        }
    }

    private void PrintOpenHint(RunLog log, string runDir, SessionManifest manifest)
    {
        if (!_options.SkipAttribute && (manifest.Files.Tb7 is not null || manifest.Files.EmonDat is not null))
        {
            log.Info("Raw capture is finalized; automatic attribution will report the processed dataset after collection completes.");
            return;
        }

        if (manifest.Files.EmonDat is not null)
        {
            log.Info($"EMON counters captured. Process with Intel MPP via scripts\\process-emon.ps1 -Run \"{runDir}\"; scripts\\open-run.ps1 can process and open the result.");
            return;
        }

        var files = new[] { manifest.Files.Etl, manifest.Files.TypeperfCsv, manifest.Files.Tb7 }.Where(f => f is not null).Select(f => Path.Combine(runDir, f!));
        log.Info("Open in WPA (one session): wpa.exe " + string.Join(" ", files.Select(f => $"-i \"{f}\"")));
        log.Info($"On the analysis machine: scripts\\open-run.ps1 -Run \"{runDir}\"");
    }

    private static void TryRun(RunLog log, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            log.Info($"Cleanup step failed: {ex.Message}");
        }
    }
}
