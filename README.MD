# WPA-Fusion

WPA-Fusion is an experimental Windows Performance Analyzer (WPA) extension for
internal Intel use. It reviews System and Processor Event (SEP) samples
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
  SEP; detects the collection platform with `sep -version`; records clock
  synchronization points; and saves the run's files and `session.json` together.
- `wpafusion-attribute` resolves sample addresses against the matching ETL
  outside WPA and writes validated sidecars for module/function names and
  stacks.
- The WPA SEP plugin reads the bound `.tb7` file and displays event samples,
  optional Last Branch Record (LBR) stacks, and sampled TMA metrics.
- Every plugin data source uses the clock mapping in `session.json` to align
  with the ETL timeline.

**Current scope:** Typeperf CSV is captured, but its WPA processing source is
not staged by the plugin script. Typeperf counters therefore do not appear in
WPA yet. The code is retained for testing and future re-enablement. TMA and
per-function estimates are sampled estimates; they are experimental and have
not been validated against a trusted reference.

TMA uses only the SEP EDP metric XML for the detected platform's PMU. It is
loaded from the SEP installation on the analysis machine; metric files are not
bundled with the plugin or collector.

For the data flow, clock alignment, workflow, and accuracy limitations, see
[docs/ARCHITECTURE.MD](docs/ARCHITECTURE.MD). To build, test, or set up a
development machine, see [docs/DEVELOPER.MD](docs/DEVELOPER.MD).

## Get started

### Prerequisites

- Windows 11 x64 and a processor supported by the installed SEP release, with
  administrator access for capture. Current validation is on Intel hardware.
- Windows Performance Toolkit (WPA and WPR) and Windows `typeperf`.
- Intel SEP installed separately, including its sampling driver. SEP binaries
  and drivers are not included. SEP 5.52 with TBRW 3.52 (or newer) required; other versions require validation.
- For building from source, the .NET 10 SDK and the .NET 6 runtime for tests.
  See [developer setup](docs/DEVELOPER.MD).

### Capture a run

Use an elevated 64-bit PowerShell session. From the repository root, run the
built or packaged collector:

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --duration 10 --output .\output
```

The collector creates a new timestamped folder under `output`, containing the
capture artifacts and `session.json`. Do not reuse or overwrite run folders.
The default capture enables user-mode LBR stacks, which increase `.tb7` size;
use `--sep-lbr off` to disable them. Run `wpafusion-capture.exe --help` for all
options. To choose the point when SEP sampling begins, use
`--sep-start-paused`; press Enter after the workload starts. The optional
`--sep-sampling-delay <seconds>` delays sampling after resume. `--duration`
starts at the resume point. A failed capture is not usable; check its log files
and `CAPTURE-FAILED.txt`.

### Attribute a captured run

Run `wpafusion-attribute` after capture to resolve SEP instruction addresses
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
  -Run .\output\<timestamp>
```

`open-run.ps1` runs offline attribution when needed, stages the SEP plugin if
necessary, and launches WPA with the ETL and `.tb7`, using each table's default
configuration. No overview profile is shipped. To resolve function names,
pass a local symbol directory using `-SymbolPath <path>`; no symbol server is
selected automatically. You can pass an exported profile with `-Profile <file>`
for a saved layout. You can also stage plugins separately with
[`scripts/stage-plugins.ps1`](scripts/stage-plugins.ps1).

Paired collector and attribution packages can be built with
[`scripts/package-collector.ps1`](scripts/package-collector.ps1). The script
creates one single-file package for each tool; WPA plugins, SEP, WPR, and
Typeperf remain separate prerequisites. See the [developer guide](docs/DEVELOPER.MD) for setup and
verification details.

## Repository map

| Path | Purpose |
|---|---|
| `src/WpaFusion.Capture` | Coordinated collection, clock markers, and `session.json`. |
| `src/WpaFusion.Common` | Shared run-manifest and clock-mapping types. |
| `src/WpaFusion.Tbrw` | Safe wrapper around SEP's native TBRW reader. |
| `src/WpaFusion.Tma` | SEP EDP metric loading and formula evaluation. |
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
- TMA depends on the SEP EDP metric file for the collection platform and on event coverage. Multiplexing,
  sparse buckets, unsupported metrics, and the sample-based approach can bias
  results. TMA is not VTune-equivalent or reference-validated.

## Data handling

ETL and SEP files can contain sensitive system, process, and workload
information. Keep captures and symbol files in approved storage and transfer
them only through approved channels. The collector does not bundle Intel SEP or
send symbols to a server automatically.
