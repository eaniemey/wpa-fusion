# WpaFusion.Plugin.Sep interface

Status: implemented; TMA accuracy remains experimental and requires reference validation.

## Responsibility

WPA processing source for `.tb7`. Sample table grouped by process, thread and event. Top-down table with L1/L2/L3 hierarchy columns and percentages per time slice.

## Known constraints

- Read-only: rejects unbound `.tb7` files with a clear error instead of binding them.
- Samples are not time-ordered across CPUs.
- References the SDK with `ExcludeAssets="runtime"`.
- SEP Samples and TMA Over Time presets keep timestamps left of the graph separator; only the numeric event count or metric belongs in the graphing area. Including the timestamp there makes WPA reject the arrangement.
- Optional `<tb7>.attribution.json` provides offline ETL module/function resolution.
  See `../WpaFusion.Attribution/INTERFACE.md`. No TraceProcessing or symbol-loading
  dependency is added to the plugin.
- `Top-Down by Process, Module, Function (TMA)` has six presets: each scope over
  time or over the whole run, initially filtered to Level 1. Remove the Depth
  filter for deeper metrics. Each entity/window is evaluated from its event
  counts, not an average of child percentages. Percentages are non-additive.
- Percentage cells use Max for display, never Sum. Collapsed display rollups
  are maxima, not recomputed parent TMA:
  use the appropriate Process/Module/Function scope preset for a true scoped ratio.
- Supporting Samples and explicit status identify sparse/missing attribution,
  zero denominators, and unsupported system/uncore metrics. Global multiplex
  scales can bias entity estimates; percentages are not reference-validated.
- Buckets with fewer than 100 supporting samples or percentages outside 0..100
  are unavailable. The 100-sample cutoff is a conservative display heuristic,
  not a confidence bound or an accuracy guarantee.
- `Est. % CPU Usage` is a system-normalized estimate from group-0
  `CPU_CLK_UNHALTED.REF_TSC` sample-after counts: reference cycles divided by
  TSC frequency, window duration, and logical CPU count. Missing reference
  cycles produce NaN. SEP sample views attribute weight only to reference-cycle
  samples; raw LBR frame rows charge only the sampled instruction, not callers.
  This is sampled PMU accounting, not WPA's scheduler-derived CPU Usage (Precise).
- TMA metrics come only from SEP EDP under `SEP_ROOT/config/edp` for the
  collection platform's PMU (public file, then `_nda`, then `_private`). The
  platform is `session.json` `platform` (from `sep -version` at capture), or the
  local SEP's `sep -pl` entry for the `.tb7` CPUID signature in older runs.
  Without a platform or EDP file, TMA tables report the reason and stay empty.

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
- `TmaAnalysis.Attributed`: grouped results alongside the unchanged global views.
