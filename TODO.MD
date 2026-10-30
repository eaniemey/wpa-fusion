# WPA-Fusion TODO

Prioritized follow-up work. As of 2026-10-30, the documented check
(`scripts/check.ps1`) passes 106 tests; run it for current totals. Implemented
features include opt-in SEP+ETL/EMON fusion, SEP-only windows with complete ETL
coverage, official MPP metrics, original-order TMA hierarchy restoration, and
explicit disjoint table ownership. The items below remain outstanding.

## P1 - Reliability and result confidence

- [ ] **Guarantee collector cleanup after every failure.** In
  `CaptureSession`, attempt to stop Typeperf, SEP, and WPR even if one stop
  fails, on both normal completion and error paths. Preserve the original
  failure and report cleanup failures in the run log and failure marker.
  Add tests for each stop operation failing independently.
- [ ] **Validate clock calibration before publishing results.** Extend
  `SessionManifest`/`ClockMap` validation to reject non-advancing or invalid
  UTC anchors, and surface marker residuals, missing per-CPU TSC corrections,
  and suspicious clock discontinuities to users. Do not silently present an
  unqualified alignment when calibration is incomplete.
- [ ] **Expand EMON/MPP and fusion reference validation.** Verify imported
  interval values, original hierarchy, arithmetic means versus MPP aggregates,
  and clock alignment across PMUs, event sets, SMT configurations, multiplexing,
  DST, and MPP versions. Quantify repeatability before using cross-run phase
  comparisons. Process/module TMA remains intentionally unexposed; the sampled
  evaluator is legacy experimental code, not the WPA metric source.

## P2 - Usability and correctness

- [ ] **Reject invalid numeric CLI values before starting collectors.** Require
  finite, representable values for duration, Typeperf interval, SEP delay, and
  marker count; validate integer/range constraints and guard conversions and
  timeout calculations against overflow. Add tests for NaN, infinity, very
  large values, and fractional marker counts. Timed resume/SEP-only duration
  already have finite-value and timeout bounds; other numeric paths need review.
- [ ] **Complete the Typeperf WPA workflow.** Stage the Typeperf processing
  source and pass `typeperf.csv` from `open-run.ps1`, or clearly expose an
  opt-in workflow if it must remain disabled by default. Verify CSV clock
  alignment and missing-value behavior in WPA. Make the default counter set
  configurable or provide localized counter paths.
- [ ] **Provide a first-run WPA analysis layout.** Ship and document a
  maintained overview profile with useful SEP, TMA, and ETW tables; explain
  when the ETW-sample join needs a CPU-sampling WPR profile and how to enable
  it. Keep estimates, unavailable metrics, and non-additive rollups clearly
  labeled in the layout.

## P3 - Scale and ongoing validation

- [ ] **Measure and improve large-run memory use.** Profile attribution,
  sidecar loading, and TMA analysis on large captures (including the documented
  100M-sample target). Avoid allocations proportional to every sample where
  possible, bound slice/table materialization, and document supported limits.
- [ ] **Expand the hardware/software validation matrix.** Add repeatable
  end-to-end checks for supported SEP/TBRW, WPA, and CPU combinations,
  including capture, clock alignment, plugin discovery, attribution, and
  representative TMA results. Record unsupported combinations and expected
  diagnostics.
