# WpaFusion.Tma interface

Status: implemented.

## Responsibility

Import official Intel MPP interval and aggregated metrics from EMON EDP captures
for WPA TMA views. No WPA dependency. The older sampled formula evaluator is
retained for tests and experiments, not used as a WPA TMA source.

## Known constraints

- `EmonMetricData.Load` consumes a schema-version-1 `.emon.json` descriptor with
	local `details` and `summary` CSV filenames. `session.json` must supply ETL
	time zero (or capture sync anchors without ETL) and `captureTimeZoneId`. No clocks or platform constants are taken
	from the analysis host.
- Metric interval values and `Summary` come directly from Intel MPP; `Summary`
	preserves its official `aggregated` results. `Averages` is the arithmetic mean
	of finite values for each metric across the whole run, with `ValidIntervalCounts`
	reporting its support. Missing, NaN, and infinite values are excluded; no-valid
	metrics remain unavailable. Means are not time-weighted or formula-recomputed.
- Details use interval-end `timestamp` and `$samplingTime`. Zero-duration MPP
	samples are preserved as points and their finite values participate in means;
	negative durations and non-advancing timestamps are rejected.
- Local timestamps ambiguous at DST transitions are rejected rather than guessed.
- The loader does not assign system-wide metrics to processes or modules.
- `metricDefinitions` optionally names the captured XML snapshot. Otherwise,
  the loader uses `edp-metrics.xml` beside the raw capture or resolves the exact
  `EDP metric file:` filename recorded in the EMON header. Percentage-TMA nodes
  retain XML parent links, declared depth, `OriginalName`, and `DefinitionOrder`.
  Definitions omitted by MPP are added as unavailable nodes with no numeric
  value. Interval CSV columns are matched by exact name after ordering.

## Public API

`EmonMetricData.Load` imports the processed descriptor, official summary, and
interval windows, exposing `Metrics`, `Summary`, `Intervals`, and ETL-relative
time bounds, plus `Averages`, `ValidIntervalCounts`, `Provenance`, and
`DefinitionSource`. `Summary` is MPP's aggregate; the plugin's displayed
summary uses `Averages` and retains those aggregates separately.

```csharp
using WpaFusion.Tma;

var data = EmonMetricData.Load(@".\output\processed-run\metrics.emon.json");
for (var index = 0; index < data.Metrics.Nodes.Count; index++)
{
	Console.WriteLine($"{data.Metrics.Nodes[index].Name}: " +
		$"mean={data.Averages[index].Value}, aggregate={data.Summary[index].Value}, " +
		$"valid intervals={data.ValidIntervalCounts[index]}");
}
```

Replace the example path with a processed run. `IsAvailable` and `MissingInputs`
must be checked before interpreting a value; a visible hierarchy node can lack
an MPP result. The following formula APIs are legacy experimental utilities:

`TmaMetricSet.LoadSepEdpXml(path, includeAllMetrics: true)` loads all SEP EDP definitions; the default retains only TMA hierarchy nodes for callers needing top-down percentages. `TmaNode.IsTopDown` distinguishes these nodes; `FormulaError` describes unsupported expressions. `TmaEvaluator` evaluates metrics against `EventCounts` and `TmaConstants`; `MultiplexEstimator` estimates per-event group scales. Unsupported formulas and non-finite results are unavailable, never silently zeroed.
