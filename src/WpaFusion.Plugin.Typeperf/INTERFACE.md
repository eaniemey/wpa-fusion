# WpaFusion.Plugin.Typeperf interface

Status: implemented; the source is retained but currently not staged or opened by `scripts/open-run.ps1`.

## Responsibility

The Typeperf processing source and CSV parser are implemented. Capture continues to write `typeperf.csv`; the plugin is intentionally not staged and `scripts/open-run.ps1` does not pass the CSV to WPA.

When enabled, the view has one row per (timestamp, counter instance) with machine, object, counter and instance columns, plus default line-graph configurations.

## Known constraints

- Times map through the Common clock map, not the CSV's local timestamps alone.
- The CSV is wide: the default 59 counter paths expanded to 1,403 columns on the test machine (Processor 501, Process 418, Processor Information 228, Synchronization 226). Parse by streaming rows, not by materializing a column-major table.
- Rate counters (1,111 of 1,403 columns) are blank in the first row. Treat blank as "no value", never 0.
- Sample spacing drifts from the 1 s interval (observed 1.01 to 1.05 s). Use each row's own timestamp.
- Columns are fixed by the header, so processes and instances that appear after typeperf starts are never recorded.
- References the SDK with `ExcludeAssets="runtime"`.
- Processing-source metadata supplies an empty (not null) Owners array, required by WPA's launcher.
- `Typeperf Counters` presets keep Time and Duration left of the graph separator, with StartTime/Duration roles; only Value (Average) is graphed. Duration runs to the same counter's next sample (the median sample interval for its last sample), so each value holds until the next reading.
- `tests/profiles/TypeperfCounters.wpaProfile` is a test-only WPA profile for the table. Typeperf data is only loaded when `typeperf.csv` is passed to WPA with `-i`; keep `session.json` beside it for ETL alignment.

## Public API

`TypeperfData` (`Load`, `Merge`, `RowTimeNs`, `RowDurationNs`, `SampleIntervalNs`, `RowCounter`, `RowValue`) and `TypeperfCounterTable.TableDescriptor`.

## CSV parsing example

```csharp
using WpaFusion.Plugin.Typeperf.Parsing;

var csv = TypePerfCsv.Load(@".\fixtures\smoke\typeperf.csv");
Console.WriteLine($"{csv.Counters.Count} counters, {csv.SampleTimesUtc.Count} timestamps");
var firstUtc = DateTime.FromFileTimeUtc(csv.SampleTimesUtc[0]);
```

This parses the CSV; it does not enable a WPA table. Without `session.json`,
wall-clock parsing alone is not a verified ETL alignment. Capture options
`--counters <file>`, `--interval <seconds>`, and `--no-typeperf` do not change
the processing source's currently disabled staging status.
