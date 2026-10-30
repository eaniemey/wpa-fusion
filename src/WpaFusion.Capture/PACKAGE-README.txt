WPA-Fusion Capture - Windows x64
================================

Internal experimental Intel tool. For Intel internal use only; do not distribute
outside Intel. This package contains the collector and its attribution companion,
not the WPA analysis plugins.

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
1. Extract the ZIP into a writable directory on the target. Keep the two
  self-contained executables together: wpafusion-capture.exe automatically
  invokes wpafusion-attribute.exe after capture. To copy only the collector,
  pass --skip-attribute and process the raw data later. On first run Windows may unpack native
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

  Automatic attribution/processing is the default. Use the final processed
  Output path, not the earlier Raw capture path, to open in WPA. The companion
  can also be found through WPA_FUSION_ATTRIBUTE_EXE or PATH. EMON processing
  and fusion additionally need Intel MPP, selected by WPA_FUSION_MPP_EXE.

  For a collection-only machine without processing dependencies:

  .\wpafusion-capture.exe --skip-attribute --duration 10 --output "$PWD\output"

  If processing fails, the exit code is non-zero but the successful raw capture
  is preserved. Inspect ATTRIBUTION-FAILED.txt/post-capture.log and retry the
  companion on that raw folder; CAPTURE-FAILED.txt means collection itself failed.

  Standard SEP sample/stack collection of a workload:

   .\wpafusion-capture.exe --duration 10 --output "$PWD\output" -- C:\Tools\workload.exe

   To decide exactly when SEP begins sampling, start it paused while ETW and
   Typeperf are already recording, then press Enter when the workload reaches
   the desired point:

   .\wpafusion-capture.exe --output "$PWD\output" --sep-start-paused `
     --sep-sampling-delay 2 -- C:\Tools\workload.exe

   SEP resumes on Enter, then waits the configured sampling delay before its
   samples begin. If --duration is also supplied, its timer starts at resume.

  For an automated 30-second workload warmup followed by five seconds of SEP,
  while retaining ETW/Typeperf through the complete workload:

  .\wpafusion-capture.exe --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
     --output "$PWD\output" -- C:\Tools\workload.exe

   Single-run mode remains the default. Opt into one repeated SEP+ETL/EMON pair:

   .\wpafusion-capture.exe --runs 2 --output "$PWD\output" -- C:\Tools\repeatable.exe

   This executes the workload twice to completion: SEP always has its own ETL;
  EMON runs separately without ETW. Do not combine --runs 2 with --duration,
  manual pause, --emon, --no-sep, or --no-etw. Timed SEP windows can use
  --sep-start-paused --sep-resume-after 30 --sep-duration 5; ETW remains active
  for the complete workload. Use {run-directory} in workload
   output arguments to avoid overwriting files between executions. On the
  analysis system, pass the raw batch folder to wpafusion-attribute if capture
  used --skip-attribute. Otherwise capture already creates the processed fused
  dataset automatically after both passes; do not process its output again.

  For counter-based TMA, use --emon instead of SEP sampling:

  .\wpafusion-capture.exe --emon --duration 30 --output "$PWD\output"

  This collects emon.dat with -collect-edp while WPR and Typeperf run. SEP and
  EMON do not run concurrently; SEP pause/delay flags do not apply to EMON.
  Add --no-etw for standalone EMON. To use an edited event list explicitly:

  .\wpafusion-capture.exe --emon --no-etw --duration 30 --output "$PWD\output" `
    --emon-events "$env:WPA_FUSION_SEP_ROOT\config\edp\sapphirerapids_server_events.txt"

  Select the event file for the collection platform. Public/private/nda lists
  may differ in both events and sampling interval. The chosen file is copied
  as emon-events.txt; the selected metric XML is retained as edp-metrics.xml
  when available. Do not edit an installation file and assume EMON chose it.
  Capture invokes its companion to process EMON with Intel MPP by default.
  scripts\process-emon.ps1 remains available for manually processing raw data
  collected with --skip-attribute or for time-zone overrides on older captures.
  MPP/Python are separate prerequisites, not bundled in this executable.

  LBR call stacks: user-mode SEP last-branch records are enabled by default with
  --sep-lbr call_stack:usr. Use --sep-lbr call_stack:os for kernel mode, or
  --sep-lbr off to disable LBR. The default uarch-exploration analysis includes
  CPU_CLK_UNHALTED.REF_TSC for estimated CPU usage; custom --sep-events lists
  must include it. The log's "SEP LBR call stacks" line shows what was used.
  LBR makes .tb7 files large (about 1 KB per sample): thin samples with
  --sep-args "-sam 4" (try -sam 2 for short runs, larger values for long ones).

  A run is successful only when enabled outputs exist and session.json is
  written: ETL and Typeperf CSV if enabled, a bound sep.tb7 for SEP, or emon.dat
  for EMON. A pair also requires both workload executions to exit successfully.
  On failure the exit code is
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
  against "sep -atypelist" before SEP collection. EMON -collect-edp writes its
  own platform metadata. Intel MPP processes those counters; WPA does not derive
  TMA from SEP samples. XML snapshots preserve the full original hierarchy.

Return the results
------------------
Copy the ENTIRE timestamped run directory back to the analysis machine,
including trace.etl, typeperf.csv, sep.tb7, session.json and logs. Keep the
files together: session.json provides their clock alignment. Do not bind or
modify the original sample file with external tools. The collector binds it
after capture.

After --skip-attribute, or to retry failed processing, select the raw-folder path:

  Native SEP: wpafusion-attribute.exe C:\captures\sep-run C:\symbols
  Raw EMON:  wpafusion-attribute.exe C:\captures\emon-run
  Pair:      wpafusion-attribute.exe C:\captures\batch C:\symbols

Use the printed Output path, then from a source checkout run:

  .\scripts\open-run.ps1 -Run 'C:\captures\processed-output' -NoAttribute

The attribution executable is included here and in the standalone attribution
package; the launcher scripts require a source checkout. Processing creates new output folders.
Fusion preserves each native SEP/ETL pair and compares separate executions
using workload markers, never cross-run thread ID matching. Keep the entire
batch/fused folder tree; copying only sep.tb7 or a metric CSV is insufficient.
WPA and the plugins are installed separately; no analysis plugin is bundled here.
Native process/module symbol resolution is not added by packaging the collector.
Traces can contain sensitive system and process information. Transfer and
store them only through approved channels.
