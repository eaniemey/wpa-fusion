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

With SEP enabled, capture detects the collection platform from `sep -version` (platform type, PMU, CPU
name) and records it as `platform` in `session.json`, logs which SEP EDP metric file TMA will use (or that
none exists), and fails before collecting if `--sep-atype` is not in `sep -atypelist` for this platform.

## Portable package

Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-collector.ps1`
from the repository. It publishes an untrimmed, self-contained Release `win-x64`
folder and ZIP under `artifacts\packages`, with a timestamped name and SHA256
printed to the console. No installed .NET runtime is needed on the target.
Intel SEP (including its driver), WPR and typeperf remain external prerequisites.
See `PACKAGE-README.txt` for target-system setup, capture and result-transfer steps.
