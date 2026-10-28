WPA-Fusion Capture - Windows x64
================================

Internal experimental Intel tool. For Intel internal use only; do not distribute
outside Intel. This package contains the collector, not the WPA analysis plugins.

Prerequisites
-------------
- Windows 11 x64 on an Intel processor. Other Windows versions and CPUs have
  not been validated.
- Administrator rights.
- Intel SEP installed separately, including its sampling driver.
  SEP binaries/drivers are NOT bundled. Follow Intel's installation/licensing
  requirements. Validated: SEP 5.52 with amplxe_tbrw_3.52.dll on a Cascade Lake
  Xeon. SEP 5.58 / TBRW 3.58 has completed a short capture (Core Ultra 9 285K) but
  is not validated; the collector logs a WARNING when TBRW is not 3.52. Confirm
  that your SEP release and driver support your CPU.
- Windows Performance Recorder at %SystemRoot%\System32\wpr.exe and typeperf at
  %SystemRoot%\System32\typeperf.exe. The default ETW profile is built into the
  collector (context switches and stacks for CPU Usage (Precise); no CPU
  sampling). --wpr-profile GeneralProfile adds CPU Usage (Sampled) if wanted.
- No .NET installation or SDK is required: the win-x64 .NET 10 (LTS) runtime is
  bundled. Use only in an approved test environment; this is not a hardened
  production release.

Run
---
1. Extract the ZIP into a writable directory on the target. The collector is a
   single self-contained wpafusion-capture.exe (the .NET runtime is inside it), so
   you can also copy just that file. On first run Windows may unpack native
   libraries from the exe to a temp folder; locations that block running code from
   temp need an exception.
2. Open a 64-bit PowerShell terminal as Administrator and change to that
   extracted directory.
3. Set the SEP installation directory if it is not the validated 5.52 path. If the
   variable is unset and that path is missing, the collector uses the newest
   C:\Program Files (x86)\IntelSWTools\sep* folder that has bin64\sep.exe.
   Setting it explicitly is clearer:

   $env:WPA_FUSION_SEP_ROOT = 'C:\Program Files (x86)\IntelSWTools\<your-sep-install>'

   The directory must contain bin64\sep.exe and bin64\amplxe_tbrw_*.dll.
4. Run:

   .\wpafusion-capture.exe --help
   .\wpafusion-capture.exe --duration 10 --output "$PWD\output"

   Standard collection of a workload (default TMA events):

   .\wpafusion-capture.exe --duration 10 --output "$PWD\output" -- C:\Tools\workload.exe

   To decide exactly when SEP begins sampling, start it paused while ETW and
   Typeperf are already recording, then press Enter when the workload reaches
   the desired point:

   .\wpafusion-capture.exe --output "$PWD\output" --sep-start-paused `
     --sep-sampling-delay 2 -- C:\Tools\workload.exe

   SEP resumes on Enter, then waits the configured sampling delay before its
   samples begin. If --duration is also supplied, its timer starts at resume.

   Do not pass --sep-events together with the default analysis type if you want
   TMA: --sep-events replaces it with only the events you list.

  LBR call stacks: user-mode SEP last-branch records are enabled by default with
  --sep-lbr call_stack:usr. Use --sep-lbr call_stack:os for kernel mode, or
  --sep-lbr off to disable LBR. The default uarch-exploration analysis includes
  CPU_CLK_UNHALTED.REF_TSC for estimated CPU usage; custom --sep-events lists
  must include it. The log's "SEP LBR call stacks" line shows what was used.
  LBR makes .tb7 files large (about 1 KB per sample): thin samples with
  --sep-args "-sam 4" (try -sam 2 for short runs, larger values for long ones).

   A run is reported successful only if its ETL, typeperf CSV (when enabled) and
   bound sep.tb7 exist, and session.json is written. On any failure the exit code is
   non-zero and the run folder contains CAPTURE-FAILED.txt; do not use such a folder.

   Always supply --output as shown for a predictable portable output location.
   Each run creates a NEW output\<yyyyMMdd-HHmmss> directory; existing run
   directories are never overwritten. Allow sufficient disk space for ETL files.

   Duration limits the central collection window, not total program execution.
   Startup, shutdown, SEP binding and ETL merging take additional time. The
   ETL and SEP coverage can extend beyond that central window. A workload still
   running when the duration expires is left running.

Troubleshooting
---------------
- Do not run alongside another WPR or SEP capture. Coordinate with its owner;
  do not cancel another user's recording.
- Inspect capture.log, sep.log and typeperf.log in the new run directory if
  collection fails.
- The default Typeperf list uses English counter names. On localized Windows, pass
  --counters with a counter-list file appropriate to that system.
- Analysis types/events depend on the Intel CPU and SEP support. The default
  is uarch-exploration; --sep-atype or --sep-events selects another collection.
- --no-sep allows an ETW/Typeperf capture without SEP; --no-typeperf skips Typeperf.
- The collector detects the platform with "sep -version", records its platform
  type, PMU, and CPU name in session.json, and checks the selected analysis type
  against "sep -atypelist" before collection. TMA analysis uses the SEP EDP
  metric file for that PMU from the analysis machine's SEP installation.

Return the results
------------------
Copy the ENTIRE timestamped run directory back to the analysis machine,
including trace.etl, typeperf.csv, sep.tb7, session.json and logs. Keep the
files together: session.json provides their clock alignment. Do not bind or
modify the original sample file with external tools. The collector binds it
after capture.

On the analysis machine, run wpafusion-attribute on the copied run directory
(from the WPA-Fusion build, not this package) to write the module/function and
LBR stack sidecars into a NEW output run, then open that new run in WPA with the
WPA-Fusion plugins. Opening the raw capture folder shows no stacks or module names.
WPA and the plugins are installed separately; this package is the collector only.
Native process/module symbol resolution is not added by packaging the collector.
Traces can contain sensitive system and process information. Transfer and
store them only through approved channels.
