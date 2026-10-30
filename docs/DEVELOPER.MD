# Developer setup

## Summary

WPA-Fusion is an experimental internal Intel tool that aligns SEP hardware
samples with Windows ETW traces in WPA. Its collector coordinates capture and
clock synchronization, offline attribution adds SEP module and stack sidecars,
and Intel MPP provides system-wide EMON/TMA metrics. Optional two-pass fusion
compares repeatable executions without changing their source ETLs or joining
thread identities across runs. Single-run capture remains the default.

This guide covers building and testing WPA-Fusion, changing plugin code, and
trying a capture on a supported development machine.

## Supported development environment

- Windows 11 x64 on Intel hardware.
- .NET 10 SDK (the repository pins `10.0.100` and rolls forward to the latest
  feature band); the .NET 6 runtime is required to run tests.
- WPA 11.7.383 / Windows Performance Toolkit for plugin validation.
- Intel SEP/EMON with a driver supporting the collection CPU. TBRW 3.52 is the
  original ABI baseline; other versions need validation, not just a version
  comparison. SEP 5.62/TBRW 3.62 completed the Sapphire Rapids render workflows
  described below, but that is not a general compatibility or accuracy claim.
  Override automatic SEP discovery with `WPA_FUSION_SEP_ROOT`.
- Intel MPP from the installed SEP `config\edp\pyedp` distribution for EMON
  processing and fusion. Use a Python version supported by that distribution;
  the development workflow was exercised with Python 3.14 and MPP 5.24.1.
- Administrator PowerShell for WPR/SEP collection.

The WPA SDK and test dependencies are versioned in
[`Directory.Packages.props`](../Directory.Packages.props). Plugin targets stay
on `net6.0` to match WPA. The collector also targets `net10.0` so it can be
published as a self-contained LTS application.

## Build and test

From the repository root, run the project check script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check.ps1
```

To run a focused test selection, pass the standard .NET test filter:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check.ps1 `
  -Filter "FullyQualifiedName~ClockMap"
```

The script builds the solution, then tests it without rebuilding. It prints a
truncated summary and returns a non-zero exit code on build or test failures.
Tests include pure unit tests and headless Microsoft Performance Toolkit
Engine tests; WPA itself is not launched by the check script.

## Stage and open the SEP plugin

Build and stage the plugin into the separate artifact folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stage-plugins.ps1
```

Staging protects build output from WPA file locks. Close WPA before restaging.
The staged assembly contains both SEP and EMON processing sources; their table
ownership must remain disjoint. Typeperf's processing source is not staged.
To verify plugin discovery with WPA:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe" `
  -listplugins -addsearchdir .\artifacts\plugins\WPA-Fusion
```

For an existing run, use the helper script to run attribution as needed, stage
the SEP plugin, and launch WPA:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\open-run.ps1 `
  -Run '.\output\<capture-timestamp>'
```

Optional function resolution uses a local symbol path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\open-run.ps1 `
  -Run '.\output\<capture-timestamp>' -SymbolPath C:\symbols
```

No symbol server is configured automatically. No overview profile is shipped;
tables open using their built-in default configurations. Test-only profiles are
under [`tests/profiles/`](../tests/profiles/).

## Capture a development trace

Use a 64-bit elevated PowerShell. Set `WPA_FUSION_SEP_ROOT` if SEP is installed
outside the default location, then run the collector with a fresh output root:

```powershell
$env:WPA_FUSION_SEP_ROOT = 'C:\Program Files (x86)\IntelSWTools\sep' # Use your actual install.
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --duration 10 --output .\output
```

The collector creates a new `output\<yyyyMMdd-HHmmss>\` folder. Never reuse
one, and keep `trace.etl`, `typeperf.csv`, `sep.tb7`, `session.json`, and logs
together. `session.json` is required for clock alignment. Do not bind a fixture
or other `.tb7` with `sfdump`; binding changes the file in place. The capture
tool binds its own `.tb7` before marking a run successful.

### Automatic post-capture processing

Capture runs `wpafusion-attribute` by default after all collectors stop and the
raw manifest is finalized. Single SEP runs are attributed; standalone EMON is
processed by Intel MPP; batches are attributed/fused once after both passes.
Modules/LBR are processed without symbol servers by default. For custom local
symbols, use `--skip-attribute` and run attribution manually on the raw folder.

The resolver checks `WPA_FUSION_ATTRIBUTE_EXE`, a sibling attribution executable,
the newest source-tree Debug/Release build, then PATH. Build the whole solution
or keep both executables from the capture ZIP together. For a separate install:

```powershell
$env:WPA_FUSION_ATTRIBUTE_EXE = 'C:\Tools\WPA-Fusion\wpafusion-attribute.exe'
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe --duration 10 --output .\output
# Raw-only alternative; no attribution or MPP starts after collection.
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe --skip-attribute --duration 10 --output .\output
```

Use the final `Output:`/`Processed dataset:` path. A post-capture failure returns
a nonzero exit code but preserves the raw capture, with `ATTRIBUTION-FAILED.txt`
and available `post-capture.log`, not `CAPTURE-FAILED.txt`. Fix the dependency
and retry attribution on `Raw capture:` without recollecting. SEP `--no-etw`
requires `--skip-attribute` because attribution needs the matching ETL.
No-PMU captures skip processing because there is no attribution input.

Run `wpafusion-capture.exe --help` for capture options. The default SEP analysis
type includes `CPU_CLK_UNHALTED.REF_TSC` for estimated CPU usage; custom
`--sep-events` lists must include it. User-mode LBR capture is enabled by
default; `--sep-lbr off` reduces file size. The default WPR profile records
context switches, not periodic CPU samples. To choose the SEP sample start
point, use `--sep-start-paused`; after the workload launches, press Enter to
resume SEP. `--sep-sampling-delay <seconds>` adds SEP's native sampling delay
after resume, and `--duration` starts at resume. `--wpr-profile GeneralProfile`
adds sampled CPU events when that fallback view is being investigated.

For an automated delayed SEP window on the current render command, use
`--sep-start-paused --sep-resume-after 30 --sep-duration 5`: ETW starts alongside
paused SEP, the workload runs for 30 seconds, then SEP resumes for five seconds.
Only SEP stops at the window limit; ETW and Typeperf record the rest of the
workload. Sample timestamps remain relative to the original ETL zero, not
rebased to SEP's first sample. `--duration` remains a global cutoff and cannot
be combined with `--sep-duration`.

```powershell
$capture = '.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe'
$workload = 'C:\Tools\workload.exe' # Replace with the executable to measure.
& $capture --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --sep-args '-sam 4' --output .\output -- $workload
```

The warmup is measured from workload launch. Resume/stop calls and finalization
add overhead, so inspect `workload-start`, `sep-resume`, `sep-window-end`,
`sep-stopped`, and `workload-end` markers rather than assuming exact timestamps.
The SEP source keeps the complete recorded ETL range and native sample positions.

## EMON TMA workflow

Use `--emon` to collect `emon -collect-edp` instead of SEP samples. WPR and
Typeperf remain enabled by default; `--no-etw` allows standalone EMON, aligned
through the capture clock anchors. The capture records the
collection time-zone ID because MPP interval timestamps are local wall time.
SEP and EMON are mutually exclusive; the SEP pause options do not apply to EMON.
Use `--emon-events <path>` to select a custom EDP event file, for example after
changing its `-t` interval. The collector copies it into the run as
`emon-events.txt` and passes that snapshot to EMON. Without this option, EMON
auto-selects its event file, which may be a different private/nda/public variant.

Install Intel MPP from the local SEP distribution (not a similarly named public
package). An isolated development setup from the repository root is:

```powershell
python -m venv artifacts\mpp-env
.\artifacts\mpp-env\Scripts\python.exe -m pip install "$env:WPA_FUSION_SEP_ROOT\config\edp\pyedp"
$env:WPA_FUSION_MPP_EXE = (Resolve-Path .\artifacts\mpp-env\Scripts\mpp.exe).Path
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe --skip-attribute --emon --no-etw --duration 30 --output .\output
$raw = '.\output\<capture-timestamp>' # Replace with the printed raw run folder.
.\scripts\process-emon.ps1 -Run $raw
$processed = '.\output\<processed-timestamp>' # Replace with process-emon's Output path.
.\scripts\open-run.ps1 -Run $processed -NoAttribute
```

`process-emon.ps1` finds `mpp` on PATH, via `WPA_FUSION_MPP_EXE`/`-MppExe`, or
in `artifacts\mpp-env`. It creates a new run, preserves raw EMON/ETL inputs,
and uses Intel MPP for platform detection, normalization, and aggregation. The
plugin reads `metrics.emon.json` plus the system-view details/summary CSVs.
The selected metric XML is snapshotted as `edp-metrics.xml` and used for both
processing and hierarchy restoration. Once a descriptor references that
snapshot, viewing the dataset does not require the analysis host's CPU or SEP
installation to reconstruct its tree. Older runs without a snapshot require
the exact XML filename recorded in their raw EMON header to remain available.
Older EMON manifests need explicit `-TimeZoneId` for the collection system.
Blank MPP cells remain unavailable; DST-ambiguous timestamps are rejected.
Set `WPA_FUSION_DIAGNOSTIC_EMON` to a processed descriptor to run the real-file test.
This example skips automatic processing deliberately. Normally capture invokes
attribution, which now accepts native EMON folders and uses the same MPP path
as fusion. The separate script remains available for retries, explicit MPP
paths, and older captures needing a `-TimeZoneId` override.

Only global TMA summary/timeline are exposed. Process/module TMA and SEP-sample
TMA are disabled; system-wide EMON results cannot be assigned to processes.
The summary displays the arithmetic `Average Value` across each metric's valid
samples. `MPP Aggregate` retains the official aggregate, and `Valid Intervals`
shows the contributing count. The timeline displays the whole-run average
alongside raw interval values; gaps are not filled. Means exclude missing and
non-finite values and are not duration-weighted.
TMA summary/timeline default to the platform's original hierarchy at every
depth, ordered by the numeric `Definition Order` column rather than alphabetically
or by value. Omitted MPP nodes remain visible as unavailable branches; they do
not acquire invented values. `edp-metrics.xml` is copied into new capture and
processed folders and referenced by `metricDefinitions` in the descriptor.
Older runs fall back to the exact definition filename in their EMON header.
Only the flat all-EDP timeline filters unavailable values; TMA tree presets
retain the complete hierarchy. All-EDP metrics have different units, so select compatible metrics for
the graph rather than comparing slot counts and percentages on one scale.

## Opt-in cross-run fusion

The default remains one capture. To collect one concurrent SEP+ETL pass and
one standalone EMON pass of the same repeatable workload:

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --skip-attribute --runs 2 --output .\output -- C:\Tools\repeatable.exe -o "{run-directory}\result.out"
$batch = '.\output\<batch-timestamp>' # Use capture's final Output path, not a child pass.
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe $batch
$fused = '.\output\<timestamp-with-milliseconds>-fused' # Use attribution's Output path.
.\scripts\open-run.ps1 -Run $fused -NoAttribute
```

Use the printed `Output:` path; the fused name includes milliseconds. MPP must
be installed as above. The launcher can also process a raw batch when
`-NoAttribute` is omitted. Each SEP pass is attributed against its own native
ETL, preserving process/thread identity. EMON is a separate execution and does
not acquire matching thread IDs. Alignment is a workload-start offset only,
with no duration normalization. `Capture Run` and source labels identify the
separate executions; `fusion.json` records duration mismatch warnings.
Only the reference SEP ETL is opened natively; all original files are retained.
This shows the manual alternative. Omit `--skip-attribute` to get the processed
fused dataset directly from capture, after the complete pair finishes.

Version 1 supports exactly one pair (`--runs 2`), requires workload completion
with exit code zero, and rejects global duration caps and manual SEP pause.
Timed SEP sampling is supported with `--sep-start-paused --sep-resume-after 30
--sep-duration 5`; the native ETL still covers the full workload. It must
not be used for non-repeatable workloads without assessing run variability.
Set `WPA_FUSION_DIAGNOSTIC_FUSION` to a fused folder for the integration test.

### Arnold bathroom example

Adapt the executable, scene, textures, and event-file paths to your installation.
This explicitly selects the edited public event list; the default private list
may have different event coverage and a different `-t` rate. The render uses
1024x1024, AA6, diffuse samples 3, and automatic threading (`-t 0`), with no
global duration cap. LBR files can exceed 1 GB even for five seconds of sampling.

```powershell
$capture = '.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe'
$kick = 'C:\Tools\Arnold\bin\kick.exe'
$scene = 'C:\Scenes\bathroom\test-5.0.ass'
$textures = 'C:\Scenes\bathroom\textures'
$events = Join-Path $env:WPA_FUSION_SEP_ROOT 'config\edp\sapphirerapids_server_events.txt'
$render = @(
  $scene, '-r', '1024', '1024', '-as', '6', '-ds', '3', '-dp', '-dw', '-v', '3',
  '-set', 'options.texture_searchpath', $textures,
  '-o', '{run-directory}\bathroom.tif', '-logfile', '{run-directory}\arnold.log',
  '-ostatsfile', '{run-directory}\arnold-stats.json', '-t', '0'
)
& $capture --runs 2 --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --sep-args '-sam 4' --emon-events $events --output .\output -- $kick @render
$fused = '.\output\<timestamp-with-milliseconds>-fused' # Use capture's final processed Output path.
.\scripts\stage-plugins.ps1 # Close WPA first.
.\scripts\open-run.ps1 -Run $fused -NoAttribute
```

Use a properly licensed renderer. For a single SEP+ETL render, omit `--runs 2`
and `--emon-events $events`, then attribute the raw run normally. No private
dataset path in this guide is required; all generated inputs stay under `output`.

## SEP attribution commands

`wpafusion-attribute` operates on a completed run and creates a new output run;
it does not modify the source capture. From a repository build:

```powershell
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe `
  '.\output\<capture-timestamp>' C:\symbols --stacks lbr
```

Use `--stacks etw` or `--stacks both` to request the ETW CPU-sample join
sidecar. This requires ETW CPU samples during the SEP collection window; a run
without them will not produce that sidecar. Without local symbols, module
names may resolve but function names remain unavailable. For complete CLI and
sidecar details, see
[`WpaFusion.Attribution/INTERFACE.md`](../src/WpaFusion.Attribution/INTERFACE.md).

## Project map and code conventions

See [architecture](ARCHITECTURE.MD) for the runtime flow and dependency
boundaries. Each project has an `INTERFACE.md` describing its contract. Keep
plugin table classes focused on WPA column/table mapping; keep business logic
in testable classes. TBRW P/Invoke belongs in `WpaFusion.Tbrw`, with `SafeHandle`,
explicit marshaling, and documented ownership. Capture's Windows clock/affinity
helpers are separate native calls, not TBRW APIs.

Nullable analysis is enabled and nullable warnings are errors. Preserve
read-only input handling, do not add empty catches, and update the edited
project's interface document when changing a public signature.

## Packaging

To create a timestamped self-contained collector package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-collector.ps1
```

The script writes paired Capture and Attribute ZIPs and publish directories
under `artifacts\packages\` and prints each ZIP's SHA-256. These packages do
not include SEP or its driver, WPR, WPA, or the WPA plugin. The Attribute
package requires local SEP/TBRW and MPP for batch fusion. Processed metrics are
CSV values, with the selected XML snapshot supplying hierarchy metadata.
Each package includes a `README.txt` with
target-machine prerequisites and usage.

## Troubleshooting

- **SEP/TBRW not found:** verify `WPA_FUSION_SEP_ROOT` points to the SEP
  installation containing `bin64\sep.exe` and the TBRW DLL.
- **Collection fails:** run elevated, avoid concurrent WPR/SEP sessions, and
  inspect the logs and `CAPTURE-FAILED.txt` in the new run folder.
- **Automatic attribution fails:** inspect `ATTRIBUTION-FAILED.txt` and
  `post-capture.log`. The raw capture is still usable. Fix companion/MPP
  discovery and retry attribution, or collect with `--skip-attribute` when those
  dependencies are intentionally absent from a remote collection machine.
- **Plugin not listed:** rerun `stage-plugins.ps1`, verify the stage directory,
  and check WPA's search directory. Close WPA before replacing files it has
  loaded.
- **No module/function names:** use the matching ETL; provide suitable local
  symbols for function names. Unresolved locations are expected for missing
  images, symbols, and some JIT code.
- **No ETW-joined SEP rows:** the ETL needs CPU sampled events/stacks during
  SEP collection. This is separate from LBR call stacks, which come from SEP.
- **Unavailable TMA cells:** inspect the original MPP CSV and Status. A node
  restored from XML can be present without a computed value. Compare `Average
  Value`, `Valid Intervals`, and `MPP Aggregate`; they have different semantics.
- **Missing TMA hierarchy/order:** check the `edp-metrics.xml` snapshot or the
  captured `EDP metric file:` fallback, select the TMA hierarchy preset, and
  restore ascending `Definition Order`. The all-EDP preset is intentionally flat.
- **Only a narrow ETL window:** `--duration` stops all collectors. Recollect
  using `--sep-duration` for a narrow SEP window with a complete workload ETL;
  missing tail events cannot be reconstructed from a truncated capture.
- **Duplicate table ownership:** restage the current build. SEP owns four
  sample/stack tables and EMON owns two global metric tables. Both sources must
  not discover the same namespace's table set.

Capture files and symbols may contain sensitive system and process data. Use
approved storage and transfer channels.
