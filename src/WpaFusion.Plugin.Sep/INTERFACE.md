# WpaFusion.Plugin.Sep interface

Status: implemented; TMA accuracy remains experimental and requires reference validation.

## Responsibility

The assembly contains SEP and EMON processing sources. SEP reads `.tb7` for
sample/stack views. EMON reads `*.emon.json` plus Intel MPP's details/summary
CSVs for global TMA and other EDP metrics. No EMON/MPP binaries are bundled.

## Known constraints

- Read-only: rejects unbound `.tb7` files with a clear error instead of binding them.
- Samples are not time-ordered across CPUs.
- When a manifest supplies an ETL reference, the SEP source advertises the
  complete recorded capture bounds. Actual samples retain their native ETL
  timestamps, so a narrow SEP-only window appears at its original position,
  without cropping or rewriting the ETL. Fused reference ETLs remain byte-identical.
- References the SDK with `ExcludeAssets="runtime"`.
- Table ownership is explicit and disjoint: SEP registers its four sample/stack
  tables; EMON registers only the global summary and timeline. Do not use shared
  namespace discovery for both sources: WPA rejects duplicate table ownership
  even when one processor reports no data. Legacy attributed TMA is unregistered.
- SEP Samples and TMA Over Time presets keep timestamps left of the graph separator; only the numeric event count or metric belongs in the graphing area. Including the timestamp there makes WPA reject the arrangement.
- Optional `<tb7>.attribution.json` provides offline ETL module/function resolution.
  See `../WpaFusion.Attribution/INTERFACE.md`. No TraceProcessing or symbol-loading
  dependency is added to the plugin.
- Global TMA summary/timeline are supplied only by `EmonProcessingSource`.
  `TmaAnalysis.FromEmon` uses official MPP values without SEP estimation or
  formula re-evaluation. The summary defaults to arithmetic `Average Value`
  across valid interval values. `MPP Aggregate` preserves Intel's independently
  computed aggregate; `Valid Intervals` reports the number of contributing samples.
  Process/module/function TMA is not exposed: system-wide EMON results do not
  identify the process that caused a counter increment. The sampled evaluator
  and attributed table implementation remain legacy experimental code only.
- `Est. % CPU Usage` is a system-normalized estimate from group-0
  `CPU_CLK_UNHALTED.REF_TSC` sample-after counts: reference cycles divided by
  TSC frequency, window duration, and logical CPU count. Missing reference
  cycles produce NaN. SEP sample views attribute weight only to reference-cycle
  samples; raw LBR frame rows charge only the sampled instruction, not callers.
  This is sampled PMU accounting, not WPA's scheduler-derived CPU Usage (Precise).
- MPP selects metric definitions from the collected EMON platform metadata,
  not the WPA host's CPU. `scripts/process-emon.ps1` copies a raw run into a new
  processed run, executes MPP, and writes `metrics.emon.json`. The descriptor
  references local CSV filenames; `session.json` supplies collection time zone
  and native clock anchors, plus ETL zero when recorded. Invalid or
  DST-ambiguous timestamps are rejected.
- Global summary and over-time views include MPP metrics and the complete
  percentage-TMA hierarchy from the captured platform's XML. Missing MPP nodes
  are retained as unavailable placeholders, never recalculated or filled with zero.
  Default TMA presets group by every original hierarchy level, including separate
  levels beyond three. Numeric `Definition Order` (ascending, Min on collapsed
  groups) preserves the XML's original sibling order. The `All EDP Metrics Over
  Time` preset remains flat and grouped by Metric. The Level-1-only preset is
  removed. Values retain
  the EDP formula's units (non-TMA metric names retain unit annotations), not
  uniformly percent of slots. Different units should not be summed or compared
  on one axis without selecting compatible metrics. Definitions whose events,
  constants, or formulas cannot be evaluated by MPP remain visible with NaN
  and Status. A blank interval cell may mean that metric was not scheduled in
  that multiplexed interval. The summary can still have an aggregated value.
- The timeline adds the same whole-run `Average Value` and `Valid Intervals`
  alongside each metric, without replacing raw interval values or filling gaps.
  These averages exclude missing/NaN/infinite values and are not time-weighted.
- TMA hierarchy presets filter only Category=TMA, retaining unavailable
  ancestors and children so the breakdown remains complete. The flat all-EDP
  timeline filters `[Status]:="ok"`; clear that filter to inspect missing readings.
  The default TMA preset avoids the
  enormous scale differences between slot counts, rates, and percentages;
  select compatible units when using the all-EDP preset.
- New captures/processed outputs retain `edp-metrics.xml` and reference it as
  `metricDefinitions` in the EMON descriptor. Older runs can resolve the exact
  XML filename recorded in their `emon.dat` header from the SEP installation;
  the analysis host's CPU is never used to choose the hierarchy. If no XML is
  available, the importer logs that only the surviving MPP CSV names are known.

- `CPU Usage (SEP LBR Stack Frames)` has one row per SEP sample with a user-mode call stack from LBR
  call-stack mode (`sep -lbr call_stack:usr`), from `<tb7>.lbrstacks.json` written by
  `wpafusion-attribute`. It needs no ETW CPU sampling. Frame 1..16 are the outermost recorded
  callers first (hardware depth is 32); a sample with an empty LBR shows only its own frame.
  Stacks are the pending call chain at the sample, with skid for non-precise events.
  Kernel stacks need a separate `:os` run. Client CPUs (Raptor Lake) are untested.
- `SEP Event Counts by ETW Sample` (fallback view) has one row per ETW CPU sample (stack, process, thread,
  CPU, weight) with a Sum column per SEP event. It needs `<tb7>.sampledstacks.json`
  from `wpafusion-attribute` and otherwise reports no data. Event counts are those
  accumulated in the ETW sample's interval on the same CPU and thread, not at the
  exact instruction; unmatched SEP samples sit in `[no ETW sample]` rows. Stacks are
  Frame 1..8 columns, root first (deeper frames only in the Stack column), not WPA's
  native stack view.

## Public API

- `SampledStackAttribution` (sidecar contract, validated loader), `SampledStackJoin.Join`
  (pure ETW/SEP join; `EtwCpuSample`, `SepSampleSet`, `SampledStackJoinStats`) and
  `SepData.SampledStacks`. `SepCpuUsageTable` is the table.
- `LbrStackBuilder` (sample IP + LBR FROM chain to a deduped `AddressStack`), `LbrStackAttribution`
  (sidecar contract, validated loader), `SepData.LbrStacks` and `SepLbrCallStackTable`.

- `SampleLocation` / `SampleAttribution`: versioned sidecar contract, SHA256 helper
  and validated loader. `SepData.Attribution` is null when no sidecar exists.
- `TmaAttributionKey`, `AttributedTmaWindow`, `AttributedTma.Compute`: grouped
  sampled evaluation at process/module/function scope and per time slice.
- `TmaAnalysis.FromEmon`: MPP intervals, per-metric means, and retained official
  aggregates for the global views, with source-run provenance.
  `TmaAnalysis.Attributed` and the explicit sampled evaluator overload are legacy
  experimental APIs, not exposed by the active processing sources.

## Refresh and open

```powershell
# Close WPA first; restaging does not replace a plugin already loaded in memory.
.\scripts\stage-plugins.ps1
$processed = '.\output\<processed-run-or-fused-folder>' # Use the processing Output path.
.\scripts\open-run.ps1 -Run $processed -NoAttribute
```

TMA presets keep the XML's original hierarchy at every depth; restore ascending
`Definition Order` if a saved workspace sorts by another column. `Value` is
the graphed interval metric; `Average Value` is a whole-run table value, not
an interpolated series. Use the flat EDP preset for compatible non-TMA units.
No recollection is needed for a view update or XML-based hierarchy restoration
when the raw data and corresponding definition are still available.
