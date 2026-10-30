# WPA-Fusion

WPA-Fusion is an experimental Windows Performance Analyzer (WPA) extension for
internal Intel use. It reviews SEP samples/stacks and EMON counter metrics
alongside the native Windows Event Tracing for Windows (ETW) timeline, maps
their clocks to a shared timeline, and adds SEP sample and Top-down
Microarchitecture Analysis (TMA) tables to WPA. It is not for public
distribution.

The project is an experimental tool for performance investigations on Windows
x64 systems supported by the installed SEP release. Validation is limited to
the hardware and software combinations described in the component guides; this
is not a general-purpose profiler or a hardened production tool.

## What it does

- `wpafusion-capture` starts Windows Performance Recorder (WPR), Typeperf, and
  SEP, or EMON instead of SEP with `--emon`; records clock
  synchronization points; and saves the run's files and `session.json` together.
- `wpafusion-attribute` resolves sample addresses against the matching ETL
  outside WPA and writes validated sidecars for module/function names and
  stacks.
- The WPA SEP plugin reads the bound `.tb7` file and displays event samples,
  and optional Last Branch Record (LBR) stacks. A separate EMON processing
  source in the same plugin assembly provides TMA from Intel MPP results.
- Native SEP timestamps use the clock mapping in `session.json`. EMON uses its
  recorded collection time zone and capture clock anchors. Optional fusion
  aligns separate workload executions to the reference SEP ETL by markers.

**Current scope:** Typeperf CSV is captured, but its WPA processing source is
not staged by the plugin script. Typeperf counters therefore do not appear in
WPA yet. The code is retained for testing and future re-enablement. TMA uses
official Intel MPP output from EMON counters, not estimates from SEP samples.
Process/module/function TMA is not exposed: the imported EMON views are system-wide.

Intel MPP selects the metric configuration from the platform metadata in an
EMON `-collect-edp` capture, normalizes counters, and calculates metrics. The
WPA plugin imports MPP's interval values and retains its official aggregate.
`Average Value` is a separate, unweighted arithmetic mean of valid samples;
`Valid Intervals` shows its support. Missing values are excluded, never zeroed.
TMA views preserve the platform XML's original order and full hierarchy,
including unavailable branches omitted by MPP; EDP also has a flat metric view.
SEP and EMON never collect concurrently; old SEP-only runs do not provide TMA.

For the data flow, clock alignment, workflow, and accuracy limitations, see
[docs/ARCHITECTURE.MD](docs/ARCHITECTURE.MD). To build, test, or set up a
development machine, see [docs/DEVELOPER.MD](docs/DEVELOPER.MD).

## Get started

### Prerequisites

- Windows 11 x64 and a processor supported by the installed SEP release, with
  administrator access for capture. Current validation is on Intel hardware.
- Windows Performance Toolkit (WPA and WPR) and Windows `typeperf`.
- Intel SEP installed separately, including its sampling driver. SEP binaries
  and drivers are not included. The pinned development configuration is SEP
  5.52 with TBRW 3.52; other versions require validation.
- For building from source, the .NET 10 SDK and the .NET 6 runtime for tests.
  See [developer setup](docs/DEVELOPER.MD).
- For EMON processing/fusion, Intel MPP from the installed SEP `config/edp/pyedp`
  distribution and its Python dependencies. MPP, SEP, and drivers are not bundled.

Build before running the source-tree examples below:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
```

### Capture a run

Use an elevated 64-bit PowerShell session. From the repository root, run the
built or packaged collector:

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --duration 10 --output .\output
```

The collector creates a new timestamped folder under `output`, containing the
capture artifacts and `session.json`. Do not reuse or overwrite run folders.
After successful PMU collection it automatically runs `wpafusion-attribute`:
SEP gets module/LBR attribution, standalone EMON gets MPP processing, and an
opt-in pair gets attribution plus fusion after both passes finish. Open the
final `Output:`/`Processed dataset:` path; raw files remain unchanged.

```powershell
# Opt out on a collection-only machine or when processing manually later.
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --skip-attribute --duration 10 --output .\output
```

The companion is found via `WPA_FUSION_ATTRIBUTE_EXE`, beside capture, in the
newest local Debug/Release build, or on PATH. The capture ZIP includes it.
Post-processing failure returns a nonzero exit code but preserves the raw
capture, with `ATTRIBUTION-FAILED.txt` and available `post-capture.log` rather
than a misleading `CAPTURE-FAILED.txt`.
The default capture enables user-mode LBR stacks, which increase `.tb7` size;
use `--sep-lbr off` to disable them. Run `wpafusion-capture.exe --help` for all
options. To choose the point when SEP sampling begins, use
`--sep-start-paused`; press Enter after the workload starts. The optional
`--sep-sampling-delay <seconds>` delays sampling after resume. `--duration`
starts at the resume point. A failed capture is not usable; check its log files
and `CAPTURE-FAILED.txt`.

For a timed SEP window with the complete workload ETL, add
`--sep-start-paused --sep-resume-after 30 --sep-duration 5`: SEP resumes after
a 30-second workload warmup and records a five-second target window, while
ETW and Typeperf continue until the render exits. `--duration` is different:
it caps all collectors and may omit the remainder of a still-running workload.

Multi-run fusion is **opt-in**, not the default: `--runs 2 -- <workload>`
executes the workload once with SEP+ETL, then once with standalone EMON.
Pass the resulting batch folder to `wpafusion-attribute` to create a fused
dataset. It preserves native source files, labels separate executions, and
aligns workload starts without stretching time or joining thread IDs across
runs. See [docs/DEVELOPER.MD](docs/DEVELOPER.MD) for constraints and commands.

```powershell
$capture = '.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe'
$workload = 'C:\Tools\workload.exe' # Replace with your workload executable.
& $capture --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --output .\output -- $workload
```

This waits for workload exit before stopping ETW. The ETL is not cropped or
rebased to SEP's five-second window. LBR files can still be large; use
`--sep-lbr off` or deliberate sample thinning via `--sep-args '-sam 4'`.

### Process an EMON run

For TMA, collect an EMON run instead (no `.tb7` or stack attribution). ETW is
enabled by default in single-run mode; add `--no-etw` for standalone EMON:

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --skip-attribute --emon --no-etw --duration 30 --output .\output
$rawRun = '.\output\<capture-timestamp>' # Replace with the printed run folder.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\process-emon.ps1 -Run $rawRun
```

Install Intel MPP first and set `WPA_FUSION_MPP_EXE` to its executable, or use
the isolated development environment described in [docs/DEVELOPER.MD](docs/DEVELOPER.MD).
Open the processed `Output:` folder with `scripts\open-run.ps1`. The launcher
also processes a raw EMON run automatically when MPP is available.
This example deliberately skips the automatic step to show manual processing.
Normally omit `--skip-attribute` and open capture's final processed path.
Use `--emon-events '<path-to-event-list>'` to select an edited EMON event file;
otherwise EMON may choose a different public/private/nda variant. The selected
event list is copied into the capture, and the matching metric XML is retained
as `edp-metrics.xml` when available. This preserves sampling settings and TMA
hierarchy independently of the analysis host.

### Fuse repeatable workload runs

Fusion is explicit; the default is still one run. This example preserves a
complete SEP ETL with a delayed sampling window, then runs EMON independently:

```powershell
$capture = '.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe'
$workload = 'C:\Tools\repeatable.exe' # Replace with your repeatable workload.
& $capture --skip-attribute --runs 2 --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --output .\output -- $workload
$batch = '.\output\<batch-timestamp>' # Replace with the capture's Output path.
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe $batch
$fused = '.\output\<timestamp-with-milliseconds>-fused' # Use attribution's Output path.
.\scripts\stage-plugins.ps1 # Close WPA before replacing a loaded plugin.
.\scripts\open-run.ps1 -Run $fused -NoAttribute
```

Only `--runs 1` and `--runs 2` are supported. A pair requires a workload that
exits successfully, not a global `--duration` cap or manual pause. SEP thread
IDs are resolved against that same execution's ETL before display alignment.
EMON data remains system-wide and comes from a different execution: timestamps
are translated at workload start without time stretching, cross-run thread
matching, or causal attribution. Duration mismatches are reported in `fusion.json`.
Use `{run-directory}` in workload output arguments to avoid overwriting outputs.
The example shows the manual alternative. Without `--skip-attribute`, capture
runs attribution/fusion once after the complete pair and prints its final
fused path; do not attribute that processed dataset again.

### Attribute SEP samples

Usually capture already runs attribution. Use `--skip-attribute` when collecting
for this manual workflow or a custom local symbol path, then run attribution on
the raw folder to resolve SEP instruction addresses
against the matching ETL and create the sidecars used by the WPA plugin. The
input run must contain `session.json`, its bound `.tb7`, and the matching ETL
with image metadata. For example, after building the solution:

```powershell
$run = ".\output\<timestamp>" # Replace with the completed capture folder.
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe $run
```

By default, the utility resolves SEP LBR stacks when the capture includes LBR
data. To resolve function names, provide a local symbol directory (or an
explicitly approved symbol path):

```powershell
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe `
  $run C:\symbols
```

Available stack options:

- `--stacks lbr` (default): write SEP LBR call-stack data; requires LBR data in
  the `.tb7`, collected with `--sep-lbr`.
- `--stacks etw`: write the ETW CPU-sample join sidecar. The ETL must contain
  CPU samples during the SEP collection window; the default WPR profile does
  not collect these.
- `--stacks both`: request both stack sidecars.

For example, to request both sources and use local symbols:

```powershell
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe `
  $run C:\symbols --stacks both
```

The utility also accepts `--help` or `-h` to print its usage. Every invocation
creates a **new output run** under `output`, copies the capture files and
manifest, and writes attribution sidecars there; it does not modify the input
run. Use the `Output:` path printed by the tool when opening the processed run
in WPA. See the [attribution interface guide](src/WpaFusion.Attribution/INTERFACE.md)
for sidecar details and analysis limitations.

### Open the run in WPA

Build and stage the plugin, then open a completed run with the helper script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\open-run.ps1 `
  -Run '.\output\<capture-timestamp>'
```

`open-run.ps1` runs offline attribution when needed, stages the SEP plugin if
necessary, and launches WPA with the available ETL, `.tb7`, and/or EMON metrics,
using each table's default
configuration. No overview profile is shipped. To resolve function names,
pass a local symbol directory using `-SymbolPath <path>`; no symbol server is
selected automatically. You can pass an exported profile with `-Profile <file>`
for a saved layout. You can also stage plugins separately with
[`scripts/stage-plugins.ps1`](scripts/stage-plugins.ps1).

Paired collector and attribution packages can be built with
[`scripts/package-collector.ps1`](scripts/package-collector.ps1). The script
creates one executable for each tool; the capture ZIP includes the attribution
companion beside the collector for automatic processing. WPA plugins, SEP,
MPP, WPR, and Typeperf remain separate prerequisites. See the [developer guide](docs/DEVELOPER.MD) for setup and
verification details.

## Repository map

| Path | Purpose |
|---|---|
| `src/WpaFusion.Capture` | Coordinated collection, clock markers, and `session.json`. |
| `src/WpaFusion.Common` | Shared run-manifest and clock-mapping types. |
| `src/WpaFusion.Tbrw` | Safe wrapper around SEP's native TBRW reader. |
| `src/WpaFusion.Tma` | EMON/MPP metric import and legacy experimental formula utilities. |
| `src/WpaFusion.Plugin.Sep` | WPA processing source and SEP/TMA tables. |
| `src/WpaFusion.Plugin.Typeperf` | Typeperf CSV parser and WPA source, currently not staged. |
| `src/WpaFusion.Attribution` | Offline ETL-backed address and stack attribution executable. |
| `tests/WpaFusion.Tests` | Unit and headless WPA Toolkit Engine tests. |
| `fixtures` | Small committed test captures and reference data. |
| `docs` | Project architecture and development setup guides. |

## Important limitations

- Capture and analysis have been validated on limited hardware/software
  combinations; consult the component interface docs before relying on a view.
- The default WPR profile records context switches, not periodic CPU samples.
  The ETW-sample join view needs ETW CPU sampling during the SEP run and may
  therefore contain no data.
- SEP samples are not necessarily ordered by time across CPUs.
- Module/function attribution depends on the matching ETL; function names also
  require suitable local symbols. Unknown or ambiguous locations remain
  unresolved.
- LBR stacks are captured by SEP and resolved offline. They are a pending call
  chain at a sampled instruction, and non-precise events may have skid.
- TMA values depend on Intel MPP and actual EMON event coverage. Multiplexing,
  unsupported counters, absent hardware, and zero denominators can leave values
  unavailable. Restoring a missing TMA branch does not manufacture its value.
- The arithmetic mean is not MPP's independently normalized aggregate and need
  not produce additive top-down rollups. Compare compatible units; EDP counts
  and percentages should not share one scale indiscriminately.
- Cross-run fusion is comparison, not simultaneous measurement. Workload
  variability and collection overhead can shift phases despite aligned starts.

## Data handling

ETL and SEP files can contain sensitive system, process, and workload
information. Keep captures and symbol files in approved storage and transfer
them only through approved channels. The collector does not bundle Intel SEP or
send symbols to a server automatically.
