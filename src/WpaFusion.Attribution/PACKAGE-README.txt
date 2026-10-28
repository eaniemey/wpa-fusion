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

Run
---
From a 64-bit PowerShell session, if SEP is not installed at the
auto-detected location, set its root first:

  $env:WPA_FUSION_SEP_ROOT = 'C:\Program Files (x86)\IntelSWTools\sep'

Generate a new attributed run without modifying the source capture:

  .\wpafusion-attribute.exe C:\captures\run C:\approved-symbols --stacks lbr

The tool preserves the platform recorded during capture. For older runs, it
uses the local SEP's `sep -pl` output to identify the platform from the `.tb7`
CPU signature; TMA metrics are loaded by WPA from that SEP installation's EDP
directory. Supported stack modes are `lbr` (default), `etw`, and `both`. ETW stacks require
CPU sampling in the collected ETL; LBR stacks require LBR data in the SEP file.
The tool writes sidecars into a fresh output directory under the current
WPA-Fusion checkout's output folder.

Open the processed output with WPA and the WPA-Fusion plugins. Keep source
captures unchanged; sidecars are SHA256-bound to the SEP file. Treat ETL, SEP,
and symbol data as sensitive diagnostics.