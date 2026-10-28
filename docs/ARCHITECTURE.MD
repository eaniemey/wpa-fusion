# Performance Data Fusion and Architecture

WPA-Fusion coordinates independent performance sources and aligns them on one
timeline. It does not merge SEP or Typeperf records into ETW, rewrite the ETL,
or turn the sources into a new combined trace. WPA continues to process ETW
natively; the WPA-Fusion plugin contributes SEP tables.

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
  Typeperf  -> typeperf.csv
  clocks    -> session.json (TSC/QPC/UTC sync points and ETL time origin)
  platform  -> session.json (SEP platform, PMU, and CPU name)

wpafusion-attribute (offline, optional)
  trace.etl + sep.tb7 + optional local symbols
    -> sep.tb7.attribution.json
    -> sep.tb7.lbrstacks.json (when LBR data is present)
    -> sep.tb7.sampledstacks.json (when requested and matching ETW samples exist)

WPA
  trace.etl -> native ETW tables
  sep.tb7   -> WPA-Fusion SEP sample, stack, and TMA tables
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
| `WpaFusion.Capture` | Starts/stops WPR, Typeperf, and SEP; surveys clocks; binds `.tb7`; writes manifest. | References Common and Tbrw; runs elevated for collection. |
| `WpaFusion.Tbrw` | Read-only managed API over SEP's native TBRW DLL; marshaling, native handles, sample/layout streaming. | No project dependencies; all P/Invoke stays here. |
| `WpaFusion.Tma` | SEP EDP metric loading, formula evaluation, multiplex estimates. | References Common; no WPA SDK dependency. |
| `WpaFusion.Plugin.Sep` | Loads `.tb7`, maps processed values to WPA tables and views. | References Common, Tbrw, Tma, and WPA SDK. No TraceProcessor/symbol engine. |
| `WpaFusion.Plugin.Typeperf` | CSV processing source and counter table implementation. | References Common and WPA SDK; retained, not staged currently. |
| `WpaFusion.Attribution` | Resolves sample addresses/stacks using ETL process/image lifetimes and optional symbols. | Runs outside WPA; uses TraceProcessing and the SEP plugin's sidecar contracts. |
| `WpaFusion.Tests` | Unit tests and headless Toolkit Engine plugin tests. | Exercises project logic without launching WPA. |

Plugin classes should primarily describe columns and projections. Parsing,
clock conversion, stack joining, and metric evaluation stay in reusable classes
that can be tested independently of WPA.

Project contracts and implementation notes:

- [Capture](../src/WpaFusion.Capture/INTERFACE.md) coordinates WPR, Typeperf,
  SEP, clock markers, and the run manifest.
- [Common](../src/WpaFusion.Common/INTERFACE.md) owns the shared clock map and
  `session.json` contract.
- [Tbrw](../src/WpaFusion.Tbrw/INTERFACE.md) wraps SEP's native sample reader.
- [Tma](../src/WpaFusion.Tma/INTERFACE.md) loads SEP EDP metrics and evaluates
  formulas without a WPA dependency.
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

The TMA evaluator uses the SEP EDP metric file for the collection platform's PMU
(`SEP_ROOT/config/edp/<pmu>.xml`, trying public, `_nda`, then `_private` files).
Capture records the platform from `sep -version`; for older runs, local SEP
resolves it from the `.tb7` CPUID signature using `sep -pl`. If no platform or
matching metric file is available, TMA reports unavailable rather than falling
back to another metric database.
Unknown events are errors rather than implicit zero counts. Global event
counts are based on sample-after values and may be adjusted by multiplex
scales.

Attributed TMA groups samples by process, module, or function and re-evaluates
each metric from that group's sampled event counts. Percentages are not
additive, so a displayed parent rollup is not a recomputed parent metric.
Buckets below 100 supporting samples, unsupported metrics, missing inputs,
zero denominators, and out-of-range percentages are marked unavailable rather
than presented as plausible values. The 100-sample rule is a display heuristic,
not a statistical confidence guarantee.

Global multiplex scales can bias short-lived functions, and the current TMA
results are experimental estimates that require reference validation. Estimated
CPU usage from reference cycles is a sampled PMU estimate; it is not WPA's
scheduler-derived CPU Usage (Precise).

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
  .\output\<timestamp> C:\symbols --stacks lbr
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
ETW through its native tables and adds SEP samples, TMA, and available stack
views. The Typeperf CSV stays in the run folder but is not passed to WPA until
its processing source is re-enabled.

For attributed TMA, choose the matching process/module/function scope preset.
Metrics are recalculated from each scope's event counts, not averaged from
percentages. Percentages are non-additive; collapsed percentage display
rollups use Max and are not recomputed parent ratios.

## Accuracy and operational tradeoffs

| Area | Limitation |
|---|---|
| Correlation | A shared timeline enables comparison, not causal inference. Typeperf instances are not a universal process-instance join key. |
| Clock accuracy | Endpoint calibration and per-CPU survey reduce alignment error but do not provide instruction-level synchronization. Typeperf's millisecond timestamps, sample spacing, and possible wall-clock changes require care. |
| Counter semantics | A blank first rate sample means missing data, not zero. Counter sampling intervals drift; use each row's timestamp. A held-value graph is a display convention, not a claim about exact counter timing. |
| Typeperf coverage | The counter set is fixed when collection starts, so later process/instance names may be absent. The default paths use English names and may need replacement on localized Windows. |
| SEP sampling | Sample-derived event counts are estimates, not an exact instruction execution log. Non-precise events may skid; per-sample source-line attribution is deferred. |
| TMA accuracy | CPU-specific event and metric definitions are required. Multiplexing can bias short intervals and individual functions; results are experimental and not validated as VTune-equivalent. The 100-sample cutoff is not a confidence bound. |
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
stages `WpaFusion.Plugin.Sep` and its shared dependencies. SEP EDP files are
read from the local SEP installation at analysis time; no metric data or
overview profile is bundled. Typeperf is implemented but not staged.

The collector is separately packaged as a self-contained `win-x64` application
for .NET 10. The package does not include Intel SEP, WPR, Typeperf, or the WPA
analysis plugin.
