# WPA-Fusion Agent Context

This is the canonical file for WPA-Fusion agent context and ongoing project
guidance. Put future agent-specific context and updates here; edit this file
instead of creating parallel context files.

WPA plugin that adds SEP samples/stacks and EMON/MPP TMA data to Windows Performance Analyzer, aligned with ETW traces. The collector captures Typeperf CSV, but its WPA processing source is not staged. Windows x64, experimental, and validated only on limited hardware/software combinations. TMA is exclusively official Intel MPP output from EMON EDP captures; SEP sample-derived TMA is disabled. Perfmon JSON has been removed.

## Architecture

1. `wpafusion-capture` starts WPR, typeperf and SEP together, records TSC/QPC/wall-clock sync points, and writes `session.json`.
2. WPA opens the ETL natively and the SEP plugin opens `.tb7`. Typeperf CSV is captured but not loaded in WPA for now.
3. SEP timestamps use Common's native TSC/QPC clock map. EMON uses its collection time zone and capture clock anchors. Optional display alignment translates separate executions at workload-start markers; native attribution always precedes that shift.
4. The SEP plugin reads samples through `WpaFusion.Tbrw` (P/Invoke over SEP's TBRW DLL).
5. Capture records SEP's platform from `sep -version`; attribution can resolve older SEP runs through `sep -pl`. EMON/MPP selects its captured platform from the EMON header. Preserve `edp-metrics.xml`, exact metric-name mappings, missing TMA nodes, declared levels, and numeric Definition Order; never infer a complete tree from filtered MPP rows or the analysis host CPU.
6. WPA TMA views use only official Intel MPP results from EMON `-collect-edp` captures. `--emon` replaces SEP capture, and `scripts/process-emon.ps1` generates aligned system-view data. SEP retains sample/stack views; process/module TMA is not exposed. The old sampled evaluator is experimental/test code only.
7. Plugin projects only map data to WPA tables. Logic lives in testable classes.
8. Cross-run fusion is opt-in (`--runs 2`), never default: one concurrent SEP+ETL execution, then standalone EMON. Attribute SEP against its own native ETL before display alignment; preserve raw IDs and label source runs. Match workload-start markers with an offset only, never infer cross-run causality or process-attributed EMON values.
9. SEP and EMON own explicit disjoint table sets (four sample/stack and two global metric tables). Shared namespace discovery caused a WPA registration failure; a no-data response does not unregister a table. TMA views preserve hierarchy/order; flat EDP metrics are separate. Average Value excludes missing/non-finite samples; MPP Aggregate remains independent.

## Project map

| Project | Path | Depends on |
| --- | --- | --- |
| Common | `src/WpaFusion.Common` | none |
| Capture | `src/WpaFusion.Capture` | Common, Tbrw |
| Attribution | `src/WpaFusion.Attribution` | Common, Tbrw, Plugin.Sep, TraceProcessing |
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
- Capture defaults to one run. `--sep-start-paused` resumes on Enter unless `--sep-resume-after` is supplied. `--sep-duration` stops only SEP and keeps ETW through workload exit; global `--duration` stops all collectors and cannot be combined with it. `--sep-sampling-delay` passes SEP `-sd`. Explicit `--emon-events` snapshots an edited event list instead of relying on automatic variant selection.
- WPA: `C:/Program Files (x86)/Windows Kits/10/Windows Performance Toolkit/wpa.exe`.
- SEP root is configurable with `WPA_FUSION_SEP_ROOT`; otherwise code uses its pinned default or discovers an SEP install under `C:/Program Files (x86)/IntelSWTools`. The validated TBRW ABI is 3.52; other SEP/TBRW versions require validation.
- Collection (SEP, WPR) needs an elevated session.

## Run output and fixtures

- Every run writes its artifacts (ETL, CSV, `.tb7`, `session.json`, logs) to a new folder `output/<yyyyMMdd-HHmmss>/`. `output/` is git-ignored. Never write run artifacts anywhere else, and never reuse a run folder.
- `fixtures/smoke/` and `fixtures/lbr/` hold small committed test captures and reference data. Do not rely on machine-specific files under ignored `output/` as repository fixtures.
- sfdump binds a `.tb7` in place; never run it on a file that must stay unbound.

## Conventions

- Nullable enabled, nullable warnings are errors. No empty catch blocks.
- TBRW P/Invoke lives in `WpaFusion.Tbrw`: SafeHandle, explicit marshaling, documented ownership. Existing native Windows clock/affinity helpers belong to Capture.
- Plugins never write to input files.
- Update the project's `INTERFACE.md` when a public signature changes. Keep repository guidance aligned with settled cross-project behavior.
- Targeted edits only. Never rewrite a whole file over 40 lines.
- In this shell, heredocs and inline scripts corrupt Windows backslash paths. Write such content with the editor tools, or use forward slashes.
