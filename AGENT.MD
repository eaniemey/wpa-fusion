# WPA-Fusion Agent Context

This is the canonical file for WPA-Fusion agent context and ongoing project
guidance. Put future agent-specific context and updates here; edit this file
instead of creating parallel context files.

WPA plugin that adds SEP sample and TMA data to Windows Performance Analyzer, aligned with ETW traces. The collector captures Typeperf CSV, but its WPA processing source is not staged. Windows x64, experimental, and validated only on limited hardware/software combinations. TMA formulas come only from the SEP EDP XML for the detected collection-platform PMU; perfmon JSON and CLX support have been removed.

## Architecture

1. `wpafusion-capture` starts WPR, typeperf and SEP together, records TSC/QPC/wall-clock sync points, and writes `session.json`.
2. WPA opens the ETL natively and the SEP plugin opens `.tb7`. Typeperf CSV is captured but not loaded in WPA for now.
3. Every plugin timestamp goes through `WpaFusion.Common`'s clock map built from `session.json`.
4. The SEP plugin reads samples through `WpaFusion.Tbrw` (P/Invoke over SEP's TBRW DLL).
5. Capture records SEP's platform from `sep -version` in `session.json`; attribution and plugin analysis fall back to matching the `.tb7` CPUID signature against local SEP `-pl` output for older runs.
6. `WpaFusion.Tma` turns event counts (samples x sample-after value) into metrics from the platform's SEP EDP XML, with no WPA dependency.
7. Plugin projects only map data to WPA tables. Logic lives in testable classes.

## Project map

| Project | Path | Depends on |
| --- | --- | --- |
| Common | `src/WpaFusion.Common` | none |
| Capture | `src/WpaFusion.Capture` | Common |
| Tbrw | `src/WpaFusion.Tbrw` | none |
| Tma | `src/WpaFusion.Tma` | Common |
| Plugin.Typeperf | `src/WpaFusion.Plugin.Typeperf` | Common, SDK |
| Plugin.Sep | `src/WpaFusion.Plugin.Sep` | Common, Tbrw, Tma, SDK |
| Tests | `tests/WpaFusion.Tests` | all, Toolkit.Engine |

Each `src` project has an `INTERFACE.md`. For a project you are not editing, read only that file.

## Commands

- Build and test: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1` (optional `-Filter "FullyQualifiedName~Name"`). Use this, not raw dotnet commands.
- List plugins WPA can see: `wpa.exe -listplugins -addsearchdir <plugin bin dir>`.

## Toolchain

- Target `net6.0`. WPA 11.7.383 runs plugins on its bundled .NET 6.0.27 with Microsoft.Performance.SDK `1.2.2-preview`. Versions live in `Directory.Packages.props`.
- Builds use the .NET 10 SDK (`global.json`). Tests run on the installed .NET 6 runtime. Only the collector (`src/WpaFusion.Capture`) also targets `net10.0` (LTS); the package script publishes that target self-contained.
- Capture options `--sep-start-paused` and `--sep-sampling-delay <seconds>` start SEP paused and resume it on user Enter after workload launch; the duration timer starts at resume and SEP's delay is passed as `-sd`.
- WPA: `C:/Program Files (x86)/Windows Kits/10/Windows Performance Toolkit/wpa.exe`.
- SEP root is configurable with `WPA_FUSION_SEP_ROOT`; otherwise code uses its pinned default or discovers an SEP install under `C:/Program Files (x86)/IntelSWTools`. The validated TBRW ABI is 3.52; other SEP/TBRW versions require validation.
- Collection (SEP, WPR) needs an elevated session.

## Run output and fixtures

- Every run writes its artifacts (ETL, CSV, `.tb7`, `session.json`, logs) to a new folder `output/<yyyyMMdd-HHmmss>/`. `output/` is git-ignored. Never write run artifacts anywhere else, and never reuse a run folder.
- `fixtures/smoke/` and `fixtures/lbr/` hold small committed test captures and reference data. Do not rely on machine-specific files under ignored `output/` as repository fixtures.
- sfdump binds a `.tb7` in place; never run it on a file that must stay unbound.

## Conventions

- Nullable enabled, nullable warnings are errors. No empty catch blocks.
- All P/Invoke lives in `WpaFusion.Tbrw`: SafeHandle for native resources, explicit marshaling, ownership documented on each import.
- Plugins never write to input files.
- Update the project's `INTERFACE.md` when a public signature changes. Keep repository guidance aligned with settled cross-project behavior.
- Targeted edits only. Never rewrite a whole file over 40 lines.
- In this shell, heredocs and inline scripts corrupt Windows backslash paths. Write such content with the editor tools, or use forward slashes.
