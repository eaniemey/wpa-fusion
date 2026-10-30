# WpaFusion.Capture interface

Status: implemented.

## Responsibility

Console app `wpafusion-capture`. Starts WPR, typeperf and `SEP_ROOT/bin64/sep.exe`, stops them in reverse order, captures sync points, emits TraceLogging markers carrying TSC, binds the `.tb7`, and writes `session.json`.

## Known constraints

- Runs elevated (`requireAdministrator` manifest).
- Each run creates a new folder `output/<yyyyMMdd-HHmmss>/` under the repo root (or the root given on the command line) and writes every artifact there: ETL, typeperf CSV, `.tb7`, `session.json`, tool logs. If the folder already exists, fail rather than overwrite.
- typeperf CSV times are local with millisecond resolution; the time zone offset is only in the header. The first sample of `%` counters is blank.
- Default counters: embedded resource `WpaFusion.Capture.typeperfinput.txt` (source `Counters/typeperfinput.txt`, 59 paths). Write it into the run folder, then run `typeperf -cf <run>/typeperfinput.txt -si 1 -f CSV -o <run>/typeperf.csv -y`. A user-supplied counter file replaces the default and is also copied into the run folder.
- Stop typeperf by its own PID and let it flush; never `taskkill /IM typeperf.exe /F`, which kills unrelated instances and can truncate the last row.

## Command line

`--emon` replaces SEP collection with `emon -collect-edp`, retaining WPR and
Typeperf. It records `emon.dat`, `emon.log`, and `captureTimeZoneId` in the run.
SEP and EMON are mutually exclusive; SEP pause/delay flags are rejected in
EMON mode. `--emon --no-etw` is supported using capture clock anchors. Run `scripts/process-emon.ps1` with Intel MPP after
collection for TMA; the capture executable does not bundle MPP or Python.
`--emon-events <file>` explicitly selects an EDP event file. Its contents are
copied unchanged into the run as `emon-events.txt`; the command line uses that
snapshot, and the manifest records it as `files.emonEvents`.
When the selected metric XML is available, capture also preserves it as
`edp-metrics.xml` (`files.emonDefinition`) so later views can restore the full
original TMA hierarchy even when MPP omits unsupported definitions.

Run `wpafusion-capture --help` for capture options. For portable use, specify
`--output <directory>` explicitly; each run gets a fresh timestamped subdirectory.
The default SEP analysis type is `uarch-exploration`, which includes
`CPU_CLK_UNHALTED.REF_TSC` for estimated CPU usage. User-mode LBR call stacks
(`--sep-lbr call_stack:usr`) are enabled by default; use `--sep-lbr off` to
reduce `.tb7` size. Custom `--sep-events` replaces the analysis type and must
include `CPU_CLK_UNHALTED.REF_TSC` for CPU usage estimates. Without LBR data,
LBR stack attribution retains only the sample IP, not a captured call stack.

`--sep-start-paused` starts WPR, SEP, and Typeperf, with SEP paused. The
collector starts the workload (if supplied), then waits for Enter before
issuing `sep -resume`. The resume moment is recorded in ETW and `session.json`
as a `sep-resume` marker. `--sep-sampling-delay <seconds>` passes SEP's
sampling delay (`-sd`); when combined with start-paused, sampling is delayed
after the user resumes SEP. `--duration` is measured from the resume point.
`--sep-start-paused --sep-resume-after 30 --sep-duration 5` starts the workload,
waits 30 seconds, resumes SEP, and stops only SEP after a five-second target
window. ETW/Typeperf continue through workload exit; workload-end and final
clock markers remain in the full ETL. `sep-window-end` and `sep-stopped` record
the stop request and completion. The global `--duration` option instead caps
all collectors and leaves an unfinished workload running. The two duration
options cannot be combined.

## Opt-in repeated capture

Single-run capture remains the default. `--runs 2 -- <workload>` explicitly
collects one SEP+ETL execution followed by one standalone EMON execution.
Each SEP file is attributed against the ETL collected concurrently in that
same pass; thread IDs are never matched across separate executions. Typeperf
follows its existing enabled/disabled setting in both passes.

The pair requires a workload that exits successfully, with no global `--duration`,
`--emon`, `--no-sep`, or `--no-etw` overrides. A paused SEP pass requires both
`--sep-resume-after` and `--sep-duration`, preserving its full ETL while EMON
collects the other execution independently. `{run-directory}` in
arguments expands to the execution's folder for separate workload outputs.
`capture-batch.json` records both native runs, their workload command template,
and completion state. Failed/incomplete batches cannot be fused.

With SEP enabled, capture detects the collection platform from `sep -version` (platform type, PMU, CPU
name) and records it as `platform` in `session.json`, logs which SEP EDP metric file TMA will use (or that
none exists), and fails before collecting if `--sep-atype` is not in `sep -atypelist` for this platform.

## Usage examples

From a built checkout, run one SEP+ETL capture with a full workload trace and
a short sampling window, or explicitly opt into the repeated pair:

```powershell
$capture = '.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe'
$workload = 'C:\Tools\repeatable.exe' # Replace with an existing executable.
& $capture --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
	--output .\output -- $workload
& $capture --runs 2 --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
	--output .\output -- $workload
```

These are separate invocations, not a required sequence. Use `--duration 10`
without a workload for a ten-second global capture instead. For EMON-only
processing, see [the developer workflow](../../docs/DEVELOPER.MD).

## Portable packaging

Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-collector.ps1`
from the repository. It publishes an untrimmed, self-contained Release `win-x64`
folder and ZIP under `artifacts\packages`, with a timestamped name and SHA256
printed to the console. No installed .NET runtime is needed on the target.
Intel SEP (including its driver), WPR and typeperf remain external prerequisites.
See `PACKAGE-README.txt` for target-system setup, capture and result-transfer steps.
