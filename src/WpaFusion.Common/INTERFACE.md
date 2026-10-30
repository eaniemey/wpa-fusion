# WpaFusion.Common interface

Status: implemented.

## Responsibility

Shared types, the clock map (TSC to QPC to wall clock), and the `session.json` reader. No dependencies.

## Known constraints

- The clock map is built only from `session.json` sync points (tightest of 16 rdtsc/QPC/precise-filetime triples at start and end).
- Per-CPU TSC offsets are applied before TSC to QPC conversion.
- `SepPlatform` parses `sep -version`, resolves older runs by matching the `.tb7` CPUID signature against `sep -pl`, and locates the platform's EDP metric file under `SEP_ROOT/config/edp`.
- EMON captures record `captureTimeZoneId` for local MPP timestamp conversion, `files.emonDat` for raw data, and `files.emonMetrics` for the processed `.emon.json` descriptor. MPP selects the captured platform from the EMON header, not the analysis host.
- `files.emonEvents` names an explicit event-list snapshot and `files.emonDefinition` names the selected XML snapshot when available. `WorkloadCommand` retains the repeatable template; `ExecutedWorkloadCommand` records `{run-directory}` expansion per pass. Workload and SEP-window markers remain in native time.
- `CaptureBatchManifest` records the opt-in SEP+ETL/EMON pair; `FusedDatasetManifest` records copied source provenance and warnings. `CrossRunAlignment` validates workload markers and successful matching commands, then computes translation to the SEP reference timeline. Clock maps remain native for attribution; display loaders apply the offset afterward.
- Timestamp conversion is allocation-free per sample; end-to-end 100M-sample handling is a performance target, not a validated capacity claim.

## Public API

`SessionManifest`, `Timeline`, `ClockMap`, `SepPlatform`, `SyncPoint`, `TscOffsets`,
`CaptureBatchManifest`, `FusedDatasetManifest`, `CrossRunAlignment`, and
`EmonMetricDefinitions`. The definition helper resolves the filename recorded
in the capture header and preserves an XML snapshot, not an analysis-host CPU map.

## Artifact contracts

| Artifact | Purpose |
|---|---|
| `session.json` | Schema 1; native clock anchors, optional ETL origin, collection time zone, workload command/markers, files, optional display alignment. |
| `capture-batch.json` | Complete opt-in pair; successful matching workload executions and relative source-run paths. Incomplete batches cannot be fused. |
| `fusion.json` | Two copied sources, native reference ETL, offset-only alignment, source identities and repeatability warnings. |
| `metrics.emon.json` | Schema 1; local details/summary CSV filenames and optional `metricDefinitions` XML snapshot. |

Load the manifest beside the selected data file to construct its native timeline:

```csharp
using WpaFusion.Common;

var manifest = SessionManifest.Load(@".\output\run\session.json");
var nativeTimeline = Timeline.FromManifest(manifest);
var workloadStart = manifest.Markers.Single(marker => marker.Name == "workload-start");
var nativeStartNs = nativeTimeline.QpcToNanoseconds(workloadStart.Qpc);
```

Apply `Alignment.OffsetNs` only in display loaders, after native attribution;
do not modify the source ETL or use aligned time to resolve native process lifetimes.
