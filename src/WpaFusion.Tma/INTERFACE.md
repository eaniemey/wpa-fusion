# WpaFusion.Tma interface

Status: implemented.

## Responsibility

Load TMA metrics from a SEP EDP metric XML (`SEP_ROOT/config/edp/<pmu>.xml`) and evaluate them over event counts. SEP EDP is the only formula source. No WPA dependency.

## Known constraints

- Event count = samples x sample-after value.
- Unknown events throw, never default to 0.
- Level 1 first, then drill-down levels.
- SEP EDP C ternaries and bracketed `.max`/`.min` formulas are parsed by `Formula`.

## Public API

`TmaMetricSet.LoadSepEdpXml` loads SEP EDP XML hierarchies. `TmaEvaluator` evaluates metrics against `EventCounts` and `TmaConstants`; `MultiplexEstimator` estimates per-event group scales. The formula parser supports the SEP EDP expression forms used by the installed definitions.
