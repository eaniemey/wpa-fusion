# Developer setup

This guide covers building and testing WPA-Fusion, changing plugin code, and
trying a capture on a supported development machine.

## Supported development environment

- Windows 11 x64 on Intel hardware.
- .NET 10 SDK (the repository pins `10.0.100` and rolls forward to the latest
  feature band); the .NET 6 runtime is required to run tests.
- WPA 11.7.383 / Windows Performance Toolkit for plugin validation.
- Intel SEP 5.52 with the TBRW 3.52 DLL and sampling driver for capture and
  `.tb7` tests. Override automatic SEP discovery with `WPA_FUSION_SEP_ROOT`.
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

Staging protects build output from WPA file locks. It currently stages the SEP
plugin only. To verify plugin discovery with WPA:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe" `
  -listplugins -addsearchdir .\artifacts\plugins\WPA-Fusion
```

For an existing run, use the helper script to run attribution as needed, stage
the SEP plugin, and launch WPA:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\open-run.ps1 `
  -Run .\output\<timestamp>
```

Optional function resolution uses a local symbol path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\open-run.ps1 `
  -Run .\output\<timestamp> -SymbolPath C:\symbols
```

No symbol server is configured automatically. No overview profile is shipped;
tables open using their built-in default configurations. Test-only profiles are
under [`tests/profiles/`](../tests/profiles/).

## Capture a development trace

Use a 64-bit elevated PowerShell. Set `WPA_FUSION_SEP_ROOT` if SEP is installed
outside the default location, then run the collector with a fresh output root:

```powershell
$env:WPA_FUSION_SEP_ROOT = 'C:\Program Files (x86)\IntelSWTools\<your-sep-install>'
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --duration 10 --output .\output
```

The collector creates a new `output\<yyyyMMdd-HHmmss>\` folder. Never reuse
one, and keep `trace.etl`, `typeperf.csv`, `sep.tb7`, `session.json`, and logs
together. `session.json` is required for clock alignment. Do not bind a fixture
or other `.tb7` with `sfdump`; binding changes the file in place. The capture
tool binds its own `.tb7` before marking a run successful.

Run `wpafusion-capture.exe --help` for capture options. The default SEP analysis
type includes `CPU_CLK_UNHALTED.REF_TSC` for estimated CPU usage; custom
`--sep-events` lists must include it. User-mode LBR capture is enabled by
default; `--sep-lbr off` reduces file size. The default WPR profile records
context switches, not periodic CPU samples. To choose the SEP sample start
point, use `--sep-start-paused`; after the workload launches, press Enter to
resume SEP. `--sep-sampling-delay <seconds>` adds SEP's native sampling delay
after resume, and `--duration` starts at resume. `--wpr-profile GeneralProfile`
adds sampled CPU events when that fallback view is being investigated.

## Offline attribution

`wpafusion-attribute` operates on a completed run and creates a new output run;
it does not modify the source capture. From a repository build:

```powershell
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe `
  .\output\<timestamp> C:\symbols --stacks lbr
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
in testable classes. P/Invoke belongs only in `WpaFusion.Tbrw`, with
`SafeHandle`, explicit marshaling, and ownership documented at each native
import.

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
package requires local SEP/TBRW; the plugin reads metrics from that SEP
installation's EDP directory. Each package includes a `README.txt` with
target-machine prerequisites and usage.

## Troubleshooting

- **SEP/TBRW not found:** verify `WPA_FUSION_SEP_ROOT` points to the SEP
  installation containing `bin64\sep.exe` and the TBRW DLL.
- **Collection fails:** run elevated, avoid concurrent WPR/SEP sessions, and
  inspect the logs and `CAPTURE-FAILED.txt` in the new run folder.
- **Plugin not listed:** rerun `stage-plugins.ps1`, verify the stage directory,
  and check WPA's search directory. Close WPA before replacing files it has
  loaded.
- **No module/function names:** use the matching ETL; provide suitable local
  symbols for function names. Unresolved locations are expected for missing
  images, symbols, and some JIT code.
- **No ETW-joined SEP rows:** the ETL needs CPU sampled events/stacks during
  SEP collection. This is separate from LBR call stacks, which come from SEP.
- **Unavailable TMA cells:** check that the SEP install has an EDP metric file for the
  collection platform's PMU (see the TMA status in the WPA log), event
  availability, sample support, and denominator/status columns. Results are
  estimates and not currently reference-validated.

Capture files and symbols may contain sensitive system and process data. Use
approved storage and transfer channels.
