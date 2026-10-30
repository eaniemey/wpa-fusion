WPA-Fusion Attribute - Windows x64
==================================

Internal experimental Intel tool; do not distribute outside Intel. This is a
separate, single-file, self-contained analysis executable paired by release
timestamp with the WPA-Fusion collector.
It does not include WPA or the WPA-Fusion plugin.

Prerequisites
-------------
- Windows x64 and Intel SEP installed locally, including the TBRW native DLL.
- A collected run directory containing session.json, trace.etl, and a bound
  sep.tb7. Run wpafusion-capture on the collection system, then transfer the
  entire timestamped run directory to the analysis system.
- Optional local PDBs for function names. No symbol server is selected or
  contacted automatically. Use a directory containing approved symbols.
- The self-contained .NET 6 runtime is bundled; no separate .NET installation
  is required.
- Batch fusion also requires Intel MPP/Python installed separately and the
  platform's EDP definition files or captured XML snapshot. The WPA plugin and
  launcher scripts are separate; they are not bundled in this executable.

Run
---
From a 64-bit PowerShell session, if SEP is not installed at the
auto-detected location, set its root first:

  $env:WPA_FUSION_SEP_ROOT = 'C:\Program Files (x86)\IntelSWTools\sep'

Generate a new attributed run without modifying the source capture:

  .\wpafusion-attribute.exe C:\captures\run C:\approved-symbols --stacks lbr

For an opt-in capture batch (wpafusion-capture --runs 2), pass its parent folder
instead. Intel MPP must be available on PATH or through WPA_FUSION_MPP_EXE.
The tool attributes SEP against its own concurrently recorded ETL, processes
the independent EMON run, and creates a new *-fused dataset. Workload-start
markers provide offset-only alignment; separate run identities and duration
differences remain recorded. No thread IDs or counter causality are joined
across separate executions.

  $env:WPA_FUSION_MPP_EXE = 'C:\Tools\mpp\mpp.exe'
  .\wpafusion-attribute.exe C:\captures\batch C:\approved-symbols --stacks lbr

The batch must contain exactly one completed SEP+ETL pass and one independent
EMON pass with matching workload command templates on the same machine. Native
SEP attribution uses its own ETL before display alignment. Global duration caps
cannot produce a complete batch; use a SEP-only window during full-workload
capture instead. The reference ETL is copied unchanged, not trimmed to samples.

The tool preserves the platform recorded during capture. For older runs, it
uses the local SEP's `sep -pl` output to identify the platform from the `.tb7`
CPU signature. TMA values come from official MPP CSVs in processed EMON runs,
not from SEP samples. Metric XML snapshots retain original hierarchy and order;
unsupported branches remain unavailable, not zero. Supported stack modes are
`lbr` (default), `etw`, and `both`. ETW stacks require
CPU sampling in the collected ETL; LBR stacks require LBR data in the SEP file.
The tool writes sidecars into a fresh output directory under the current
WPA-Fusion checkout's output folder.

Open the processed output with WPA and the WPA-Fusion plugins. Keep source
captures unchanged; sidecars are SHA256-bound to the SEP file. Treat ETL, SEP,
and symbol data as sensitive diagnostics.

From a source checkout, close WPA, then refresh and open the exact Output folder:

  .\scripts\stage-plugins.ps1
  .\scripts\open-run.ps1 -Run 'C:\captures\processed-output' -NoAttribute

TMA displays Average Value (unweighted finite-sample mean), Valid Intervals,
and the independent MPP Aggregate. TMA presets retain original XML hierarchy
and Definition Order; the all-EDP view remains flat. No recollection is needed
for display-only changes when the original data and required XML are available.