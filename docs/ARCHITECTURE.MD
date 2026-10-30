# Performance Data Fusion and Architecture

WPA-Fusion coordinates independent performance sources and aligns them on one
timeline. It does not merge SEP or Typeperf records into ETW, rewrite the ETL,
or turn the sources into a new combined trace. WPA continues to process ETW
natively; the WPA-Fusion plugin contributes SEP sample/stack and EMON metric
tables. A fused dataset is a folder of distinct, preserved inputs, not a
rewritten ETL or a claim that separate executions occurred simultaneously.

The result is a shared timeline plus optional attribution, not proof of
causality: coincident changes across ETW and hardware counters help investigate
performance behavior but do not by themselves establish why it happened.
Typeperf CSV is captured, but its WPA processing source is not staged, so
Typeperf counters are not currently displayed in WPA.

## Data flow

```text
wpafusion-capture
  WPR       -> trace.etl
  SEP       -> sep.tb7 (bound before the run is considered complete)
  or EMON   -> emon.dat (-collect-edp; replaces SEP, not concurrent)
  Typeperf  -> typeperf.csv
  clocks    -> session.json (TSC/QPC/UTC sync points and ETL time origin)
  platform  -> session.json (SEP platform, PMU, and CPU name)

wpafusion-attribute (offline, optional)
  trace.etl + sep.tb7 + optional local symbols
    -> sep.tb7.attribution.json
    -> sep.tb7.lbrstacks.json (when LBR data is present)
    -> sep.tb7.sampledstacks.json (when requested and matching ETW samples exist)

scripts/process-emon.ps1 (offline, EMON runs only)
  emon.dat -> Intel MPP -> system-view details/summary CSVs + metrics.emon.json

wpafusion-capture --runs 2 (explicit opt-in)
  pass 1: workload + SEP + its concurrent ETL
  pass 2: repeated workload + standalone EMON (no ETL)
  -> capture-batch.json + two native run folders

wpafusion-attribute <batch folder>
  native SEP/ETL attribution + Intel MPP processing
  -> new fused folder: fusion.json + copied source runs + marker alignment metadata

WPA
  trace.etl -> native ETW tables
  sep.tb7   -> WPA-Fusion SEP sample and stack tables
  metrics.emon.json -> EMON/MPP global TMA summary and timeline
  session.json -> clock markers and shared timeline metadata
```

The capture tool writes all outputs to a newly-created timestamped run folder.
The manifest refers to files relative to that folder. Attribution creates a
new output run and copies the inputs rather than modifying the original. The
plugin opens the bound `.tb7` read-only and validates sidecars against the
`.tb7` hash and expected sample count.

## Clock alignment

SEP sample times are TSC ticks, ETW uses QPC-based time, and Typeperf records
local wall-clock timestamps. Capture writes sync points containing TSC, QPC,
and UTC FILETIME values near the start and end of collection. It selects the
tightest of repeated clock reads at each endpoint and records the ETL's time
origin.

`WpaFusion.Common` constructs a linear map from the start/end anchors:

1. Apply the per-CPU TSC correction measured by capture against QPC.
2. Convert TSC to QPC using the session clock map.
3. Express the QPC value as nanoseconds relative to the ETL's zero.

Typeperf parsing can convert CSV timestamps using the CSV's timezone header,
but a plugin must use the session clock map for cross-source alignment. SEP
hardware-section TSC offsets are not used; the capture survey is the source of
per-CPU corrections. If a correction is missing, it is treated as zero.

See [WpaFusion.Common's interface](../src/WpaFusion.Common/INTERFACE.md) for
the manifest and timeline contract.

## Projects and boundaries

| Project | Responsibility | Dependencies / boundary |
|---|---|---|
| `WpaFusion.Common` | `session.json`, run folders, clock map, shared time values. | No project dependencies. |
| `WpaFusion.Capture` | Starts/stops WPR, Typeperf, and SEP or EMON; surveys clocks; records workload markers; supports optional two-pass capture. | References Common and Tbrw; runs elevated; includes native Windows clock/affinity helpers. |
| `WpaFusion.Tbrw` | Read-only managed API over SEP's native TBRW DLL; marshaling, native handles, sample/layout streaming. | No project dependencies; TBRW P/Invoke stays here. |
| `WpaFusion.Tma` | EMON/MPP metric import and legacy experimental formula utilities. | References Common; no WPA SDK dependency. |
| `WpaFusion.Plugin.Sep` | Two sources: `.tb7` samples/stacks and `.emon.json` metric views, with disjoint table ownership. | References Common, Tbrw, Tma, and WPA SDK. No TraceProcessor/symbol engine. |
| `WpaFusion.Plugin.Typeperf` | CSV processing source and counter table implementation. | References Common and WPA SDK; retained, not staged currently. |
| `WpaFusion.Attribution` | Resolves sample addresses/stacks using ETL process/image lifetimes and optional symbols. | Runs outside WPA; uses TraceProcessing and the SEP plugin's sidecar contracts. |
| `WpaFusion.Tests` | Unit tests and headless Toolkit Engine plugin tests. | Exercises project logic without launching WPA. |

Plugin classes should primarily describe columns and projections. Parsing,
clock conversion, stack joining, and metric evaluation stay in reusable classes
that can be tested independently of WPA.

SEP owns four sample/stack tables and EMON owns two global metric tables.
Returning no data does not remove a registration: sources must not discover
the same table GUIDs, or WPA rejects the plugin when both are loaded.

Project contracts and implementation notes:

- [Capture](../src/WpaFusion.Capture/INTERFACE.md) coordinates WPR, Typeperf,
  SEP, clock markers, and the run manifest.
- [Common](../src/WpaFusion.Common/INTERFACE.md) owns the shared clock map and
  `session.json` contract.
- [Tbrw](../src/WpaFusion.Tbrw/INTERFACE.md) wraps SEP's native sample reader.
- [Tma](../src/WpaFusion.Tma/INTERFACE.md) imports Intel MPP interval and
  summary metrics without a WPA dependency.
- [SEP plugin](../src/WpaFusion.Plugin.Sep/INTERFACE.md) exposes the `.tb7`
  data through WPA tables.
- [Typeperf source](../src/WpaFusion.Plugin.Typeperf/INTERFACE.md) retains the
  CSV parser and processing source, but is not staged.
- [Attribution](../src/WpaFusion.Attribution/INTERFACE.md) resolves addresses
  and stacks outside WPA.

## SEP samples, stacks, and attribution

TBRW sample layouts are obtained from per-event descriptors rather than a fixed
record shape. Samples may arrive out of chronological order across CPUs. Each
sample has an event index; its estimated event count is the number of samples
times that event's sample-after value.

Offline attribution resolves a sample IP against ETL process and image
lifetimes. Optional local symbols resolve function names. The attribution
sidecar records unique locations and a per-sample index in original TBRW
sample order. Unknown, ambiguous, or unavailable addresses remain explicit
unresolved entries.

LBR stack attribution uses SEP's own last-branch records and does not require
ETW CPU sampling. An optional ETW join associates SEP samples with ETW CPU
samples on CPU, time interval, and thread. It is a statistical interval
association, not exact instruction attribution; unmatched SEP event counts are
retained in synthetic rows. The default capture profile records context
switches, not SampledProfile CPU samples, so this fallback table may have no
data. User-mode SEP LBR stacks are enabled by default; `--sep-lbr off` disables
them.

## TMA behavior and limitations

WPA TMA views use only official Intel MPP output from EMON `-collect-edp`
captures. MPP reads the collected platform metadata, selects the corresponding
EDP definitions, and performs counter normalization and formula evaluation.
The plugin imports MPP's system-view details and aggregated summary CSVs; it
does not estimate these values from SEP samples or recalculate the formulas.

The global summary and timeline retain all MPP metrics, including non-TMA
categories. TMA defaults to its original hierarchy and definition order from
the captured platform's XML, with an expandable column for every depth. Nodes
omitted by MPP remain visible as unavailable placeholders, preserving their
original parents and sibling order. No values are inferred for these nodes.
`All EDP Metrics Over Time` remains a flat list of available metric series;
select compatible units to avoid scale distortion. Only this flat preset
filters unavailable readings; TMA trees retain the full breakdown. Value is
in each formula's native units; Status explains missing
inputs instead of hiding definitions. Blank MPP cells remain unavailable;
multiplexed intervals may not have a value for every metric. The displayed
`Average Value` is the arithmetic mean of valid samples for that metric across
the whole run, with its valid-interval count shown. Intel's independent
aggregate is retained separately as `MPP Aggregate`; neither value fills the
raw timeline's gaps. The arithmetic mean is not time-weighted.

Process/module/function TMA is not exposed because system-wide EMON results
cannot identify the process responsible for a counter increment. SEP continues
to provide independently collected sample and stack attribution.

MPP local interval-end timestamps are converted with the collection time-zone
ID and aligned to the manifest's ETL UTC origin. DST-ambiguous times are
rejected. EMON and SEP capture modes are mutually exclusive to avoid unverified
PMU sharing. The older sampled evaluator remains experimental/test code only.

## End-to-end workflow

### 1. Capture a coordinated run

From an elevated PowerShell session, run the built or packaged collector:

```powershell
.\wpafusion-capture.exe --duration 10 --output .\output

# Reduce the .tb7 size by disabling user-mode LBR call stacks.
.\wpafusion-capture.exe --duration 10 --output .\output `
  --sep-lbr off
```

Each invocation creates a new timestamped run directory. Keep the ETL, CSV,
bound `.tb7`, `session.json`, and logs together; never reuse an existing run
folder. The default SEP analysis type, `uarch-exploration`, includes
`CPU_CLK_UNHALTED.REF_TSC` for estimated CPU usage. Custom `--sep-events` lists
must include that reference-cycle event to retain the estimate.

To start ETW and Typeperf before SEP samples, add `--sep-start-paused`. The
collector launches the workload and waits for Enter, then resumes SEP and
records a `sep-resume` marker. Add `--sep-sampling-delay <seconds>` to delay
actual SEP sampling after resume; `--duration` begins at resume and includes
that delay.

For automatic warmup and a short SEP window within the full workload ETL:

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --output .\output -- C:\Tools\workload.exe
```

Only SEP stops at the window limit. ETW/Typeperf continue to workload exit,
and SEP timestamps remain in the native ETL coordinate system. `--duration`
instead caps every collector and may omit the rest of the workload. Native
ETLs are copied byte-for-byte during attribution and fusion, never cropped.

The default WPR profile records context switches and their stacks, not periodic
CPU samples. `--wpr-profile GeneralProfile` adds CPU sampling if investigating
the optional ETW-sample join. Collector startup and shutdown are sequential;
the sources need not have identical start and end coverage.

### 2. Understand the mapped timeline

SEP sample timestamps are TSC ticks, ETW uses QPC-based time, and Typeperf CSV
timestamps are local wall-clock values. The shared timeline is calculated from
`session.json`; file-header times alone do not establish cross-source
alignment:

```text
SEP:
  sample TSC -> capture's per-CPU correction -> session TSC-to-QPC map
             -> nanoseconds relative to ETL time zero

Typeperf:
  local CSV time + header time-zone bias -> UTC FILETIME
             -> session clock anchors -> nanoseconds relative to ETL zero

ETW:
  native trace timestamps -> ETL-relative timeline
```

Capture selects the tightest of repeated clock-read triples at the start and
end and surveys logical CPUs against QPC for per-CPU TSC corrections. SEP
hardware-section TSC offsets are not used; a missing survey offset is treated
as zero. Typeperf timestamps have millisecond resolution and typical sampling
spacing near one second, so they are not instruction-level timing.

For example, the committed smoke CSV can be parsed and its first sample
converted from FILETIME to UTC as follows:

```csharp
using WpaFusion.Plugin.Typeperf.Parsing;

var csv = TypePerfCsv.Load(@".\fixtures\smoke\typeperf.csv");
var firstUtc = DateTime.FromFileTimeUtc(csv.SampleTimesUtc[0]);
```

This conversion only interprets the CSV timestamp. The session clock map is
still required to align the resulting value to ETW.

### 3. Resolve SEP samples offline

Run the attribution tool against a completed capture before opening it in WPA:

```powershell
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe `
  '.\output\<capture-timestamp>' C:\symbols --stacks lbr
```

Attribution checks each sample's PID, timestamp, and instruction pointer
against process and image lifetimes in the matching ETL. A local symbol path
can add function names; no symbol server is selected automatically. Without
symbols, module attribution may still succeed while function names remain
unavailable.

The tool creates a new output run and copies the capture files; open that
processed run rather than the original. Sidecars are bound to the `.tb7` SHA256
and validated for schema, sample count, and indices. Entries preserve TBRW
sample order, which is not necessarily chronological across CPUs.

LBR stacks come from SEP and need no ETW CPU sampling. The optional ETW join
requires ETW CPU samples with stacks during the SEP interval. It associates by
CPU, interval, and thread; it is statistical interval attribution rather than
an exact instruction-level join. Unmatched counts remain represented in
synthetic rows.

### 4. Open the processed run in WPA

Stage the plugin and open the ETL and SEP file together:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stage-plugins.ps1
$run = ".\output\<processed-timestamp>"
& "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe" `
  -addsearchdir .\artifacts\plugins\WPA-Fusion `
  -i "$run\trace.etl" -i "$run\sep.tb7"
```

The SEP plugin loads the adjacent manifest and sidecars. WPA continues to show
ETW through its native tables and adds SEP samples and available stack
views. The Typeperf CSV stays in the run folder but is not passed to WPA until
its processing source is re-enabled.

For TMA, collect with `--emon`, process using `scripts/process-emon.ps1`, and
open the processed run with `scripts/open-run.ps1`. The launcher passes the
EMON descriptor and optional ETL to WPA. Standalone EMON uses its recorded
clock anchors. Old SEP-only captures do not contain the EMON
counter data needed for these views.

### 5. Compare repeatable executions

```powershell
.\src\WpaFusion.Capture\bin\Debug\net10.0\wpafusion-capture.exe `
  --runs 2 --sep-start-paused --sep-resume-after 30 --sep-duration 5 `
  --output .\output -- C:\Tools\repeatable.exe
$batch = '.\output\<batch-timestamp>' # Replace with the printed batch Output path.
.\src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe $batch
$fused = '.\output\<timestamp-with-milliseconds>-fused' # Use attribution's Output path.
.\scripts\open-run.ps1 -Run $fused -NoAttribute
```

Fusion is never the default. Version 1 accepts one successful SEP+ETL/EMON pair
with identical workload command templates on the same machine. Native SEP
attribution happens before alignment, using that pass's own ETL and IDs.
Display-time offsets align workload-start markers to the reference SEP ETL;
no stretching, cross-run ID matching, or process attribution of EMON is performed.
The reference ETL is opened intact; source labels and `fusion.json` retain
provenance and duration differences, with an extra warning above 10% mismatch.
See the developer guide for `{run-directory}` output arguments and the full
Arnold example. Non-repeatable phase behavior can invalidate a comparison.

## Accuracy and operational tradeoffs

| Area | Limitation |
|---|---|
| Correlation | A shared timeline enables comparison, not causal inference. Typeperf instances are not a universal process-instance join key. |
| Clock accuracy | Endpoint calibration and per-CPU survey reduce alignment error but do not provide instruction-level synchronization. Typeperf's millisecond timestamps, sample spacing, and possible wall-clock changes require care. |
| Counter semantics | A blank first rate sample means missing data, not zero. Counter sampling intervals drift; use each row's timestamp. A held-value graph is a display convention, not a claim about exact counter timing. |
| Typeperf coverage | The counter set is fixed when collection starts, so later process/instance names may be absent. The default paths use English names and may need replacement on localized Windows. |
| SEP sampling | Sample-derived event counts are estimates, not an exact instruction execution log. Non-precise events may skid; per-sample source-line attribution is deferred. |
| TMA accuracy | Intel MPP supplies normalized global metrics and aggregated summaries. Unscheduled/unsupported interval metrics stay unavailable. No process attribution is inferred from system-wide counters. |
| Symbol coverage | Missing PDBs, JIT code, and ambiguous or missing image metadata can remain unresolved. Unknown samples are retained instead of silently discarded. |
| Stack collection | LBR uses finite hardware branch history, not an unlimited software stack. User and kernel capture are separate modes. Samples without LBR data may show only the sample IP. |
| ETW stack fallback | The optional join needs ETW CPU samples with stacks. The default WPR profile collects none, and SEP suppressed ETW CPU sampling on the tested machine. Matching is interval-based, not instruction-exact. |
| Scale and storage | Sidecars deduplicate locations and stacks but still map individual samples. Large runs incur loading, hashing, copying, and memory costs; 100M-sample attribution scalability is unestablished. |
| WPA integration | Plugin tables share the workspace but do not become native ETW tables or inherit every native stack capability. Symbol-aware `wpaexporter` export has hung in this environment; export reliability should not be assumed. |

## Reuse outside the WPA UI

The Microsoft Performance Toolkit Engine test harness can load the plugin and
build tables headlessly. This provides a foundation for reuse, not an
implemented query service. Automated native ETW scheduling analysis or access
to WPA's current UI selection would require additional integration.

## Deployment

The WPA plugin targets `net6.0` and Microsoft Performance SDK
`1.2.2-preview`, matching WPA 11.7.383's bundled runtime/SDK. Its SDK reference
excludes runtime assets so WPA supplies the only SDK copy. `stage-plugins.ps1`
stages `WpaFusion.Plugin.Sep` and its shared dependencies, including the EMON
processing source. MPP and SEP/EMON must be installed separately on the
capture/processing systems. Once processed, the EMON source reads only the
copied CSVs, descriptor, and manifest. No overview profile is bundled.

The collector is separately packaged as a self-contained `win-x64` application
for .NET 10. The package does not include Intel SEP, WPR, Typeperf, or the WPA
analysis plugin.
