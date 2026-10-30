# WpaFusion.Attribution interface

## Responsibility

Offline executable `wpafusion-attribute`: resolves SEP instruction pointers against
the matching ETL's process/image lifetimes and optional symbols. TraceProcessing
runs outside WPA; the SEP plugin reads only the resulting data sidecar.

## Command line

`wpafusion-attribute <run-directory> [symbol-path] [--stacks etw|lbr|both]`

Capture invokes this automatically by default; `--skip-attribute` opts out.
The input can be a native SEP+ETL run, native EMON run, or completed two-pass
batch. Manual invocation remains useful for retries and custom symbol paths.

Requires SEP/TBRW, Windows Performance Toolkit, and a run containing `session.json`,
a bound `.tb7`, and its concurrent ETL with process/image metadata (the default
WpaFusionPrecise profile suffices; ETW CPU sampling is needed only for the ETW-join pass).
Without a symbol path, modules resolve but functions are explicitly unavailable.
Use local PDBs or an explicitly approved symbol path. No symbol server is selected
automatically. This tool never modifies inputs or injects events into the ETL.

Passing a folder containing `capture-batch.json` invokes opt-in fusion: SEP
samples are resolved against that pass's native ETL before any time shift;
standalone EMON is processed by Intel MPP (`WPA_FUSION_MPP_EXE`, PATH, or the
development MPP environment). A new `*-fused` folder contains copied sources,
sidecars, metrics, and `fusion.json`. Source inputs and ETLs are unchanged.

Alignment translates each timeline to the first SEP workload-start marker,
without stretching time. It validates successful executions, machine identity,
and identical command templates, and reports duration differences. Source run
IDs and a separate-execution warning remain visible. Duration differences over
10% produce an additional repeatability warning. No process/thread join or
causal attribution is inferred between runs. Already aligned source copies
must not be re-attributed; use the original native capture instead.

The processed `session.json` keeps the captured `platform`; for older captures
without one it is resolved from the `.tb7` CPUID signature via the local SEP's
`sep -pl`. The tool prints the platform and the EDP metric file available from
that local SEP installation. TMA values themselves come from separately
processed EMON/MPP data; the XML snapshot supplies the original TMA hierarchy.

Every invocation creates a fresh `output/<yyyyMMdd-HHmmss>` directory and copies
the manifest and referenced capture files there. Symbol caches also stay inside
that directory. The manifest retains the original capture run ID and timestamps.
Open the copied `.tb7` with the updated SEP plugin.

Fusion uses a separate `output/<yyyyMMdd-HHmmss-fff>-fused` folder. Examples:

```powershell
$attribute = '.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe'
& $attribute 'C:\captures\native-sep-run' C:\symbols --stacks lbr
& $attribute 'C:\captures\two-pass-batch' C:\symbols --stacks lbr
$processed = 'C:\captures\processed-output' # Use the tool's Output path.
.\scripts\open-run.ps1 -Run $processed -NoAttribute
```

Passing a standalone native EMON folder now processes it with Intel MPP into a
new `output/<yyyyMMdd-HHmmss-fff>-emon` folder. `scripts/process-emon.ps1` remains
an alternative for explicit MPP/time-zone overrides. The fused launcher loads only the reference
SEP ETL natively, its attributed `.tb7`, and the aligned EMON descriptor.

## Sidecar

`<tb7-filename>.attribution.json`, schema version 1:

- `Tb7Sha256`: exact bound input file hash.
- `Locations`: unique module path, function name, and process-start timestamp
  (ETL-relative nanoseconds; `long.MinValue` when unknown), plus the ETL process name.
- `SampleLocations`: indices into Locations, in original TBRW sample order.

The plugin validates version, file hash, sample count, location fields and indices.
It rejects malformed/mismatched maps instead of applying incorrect attribution.
Unknown and ambiguous images/functions remain explicit unresolved buckets.
Address ranges are half-open and image/process lifetimes are checked. Kernel
addresses may resolve through the System/Idle image sets. JIT code and missing PDBs
can remain unresolved. No call stacks are synthesized.

`<tb7-filename>.sampledstacks.json`, schema version 1, is written when the ETL has
ETW CPU samples with stacks (otherwise the tool says so and writes none). It holds
the tb7 hash, ETL length and zero time, unique frames and stacks (leaf first), one
row per ETW sample and sparse per-event SEP sample counts joined by CPU, time and
thread (see `SampledStackJoin`). Stacks come only from ETW samples. Read by the
`SEP Event Counts by ETW Sample` table; validation also requires counts to sum to the SEP
sample total.

## Analysis limits

Attribution supplies SEP sample/stack names only. WPA TMA views now use
system-wide EMON/MPP data; process/module/function TMA presets are not exposed.
The sampled TMA implementation described below is legacy experimental code.

Attributed TMA is a sampled estimate, not a validated VTune-equivalent result.
Global event multiplex scales are reused and may bias short-lived functions.
Out-of-range percentages are marked unavailable, not clamped into plausible values.
Buckets below 100 total samples are unavailable (a heuristic, not a confidence bound).
Process instances are separated where ETL creation timestamps are available.
Currently the resolver scans candidate image ranges per sample and materializes
the map; 100M-sample scalability has not been established.

## Build and run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check.ps1
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe C:\captures\run C:\symbols
```

Stage updated plugins with `scripts\stage-plugins.ps1`. Open the NEW output run
in WPA to inspect SEP sample and stack views. For TMA, collect an EMON run with
`--emon`, process it using `scripts\process-emon.ps1`, and open that processed
run. The older `tests\profiles\TmaAttribution.wpaProfile` is a legacy test asset.

`<tb7-filename>.lbrstacks.json`, schema version 1, is written when the `.tb7` carries LBR data
(`sep -lbr call_stack:usr`, or `wpafusion-capture --sep-lbr call_stack:usr`). It holds the tb7
hash, unique frames (module, function), unique stacks (frame indices, leaf first: the sample IP,
then each LBR call site newest first) and one stack id per SEP sample in TBRW order. Only the
ETL's process/image lifetimes (and optional symbols) are used, not its CPU samples.
`--stacks` picks the sources: `lbr` (default), `etw` (the ETW-join fallback, which needs an ETL with CPU
sampling taken during the SEP run; the default capture profile records none) or `both`. The ETW-join pass writes no sidecar when the ETL has no
CPU samples during the SEP window. Rerunning on a capture creates a new output run each time.
