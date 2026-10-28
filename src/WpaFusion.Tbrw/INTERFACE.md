# WpaFusion.Tbrw interface

Status: implemented.

## Responsibility

Read-only P/Invoke wrapper over `SEP_ROOT/bin64/amplxe_tbrw_3.52.dll`. Exposes a streaming sample iterator with raw TSC timestamps.

## Native functions in scope

Lifecycle: `TBRW_open`, `TBRW_close`, `TBRW_error_string`, `TBRW_convert_uniqueid_to_string`.
Binding check: `TBRW_is_bound` (and `TBRW_dobind`, used only by Capture).
Sections: `TBRW_reading_section`, `TBRW_done_section`, `TBRW_reading_stream`, `TBRW_done_stream`, `TBRW_reading_stream_section`, `TBRW_done_stream_section`, `TBRW_get_number_data_streams`.
Metadata: `TBRW_get_system`, `TBRW_enumerate_processes`, `TBRW_enumerate_threads`, `TBRW_enumerate_events`, `TBRW_enumerate_data_descriptor_entries`.
Samples: `TBRW_enumerate_data`.

Out of scope for now: module and bind enumeration, OS/platform queries, writes, `smrk.dll`.

## Known constraints

- Strings are UTF-16 (`wchar_t`). Buffer sizes are in bytes.
- Enumerate functions take a pointer to a callback pointer (`TBRW_DATA_CALLBACK *`). Keep delegates alive for the call.
- Data callbacks deliver chunks that can split a record; report leftover bytes through the adjustment out-parameter.
- Sample layout per event comes from data descriptors. Timestamp is the `ST_TIME_STAMP` field; PID, TID, CPU and IP are in `ST_LEGACY_SAMPLE_RECORD`.
- The sample record's raw SEP `EventId` can be sparse; `PmuSample.EventIndex` is normalized through the event catalog and is safe to index into `TbrwFile.Events`.
- The TBRW layout and declarations are based on SEP's `tbrw.h` and `tbrw_types.h` headers; do not substitute another wrapper's struct definitions.

## LBR call stacks

`TbrwFile.HasLbr` is true when the sample layouts carry LBR fields (`sep -lbr call_stack:usr`).
`TbrwFile.ReadLbrSamples(LbrSampleHandler)` walks the samples in the same file order as `ReadSamples`
(the index aligns) and passes each sample's LBR FROM/TO addresses, newest branch first. The record
stores the 32-entry ring in hardware order; `LBR_TOS` indexes the newest entry and older ones sit at
TOS-1, TOS-2 (wrapping), matching sfdump. Valid depth ends at the first all-zero FROM/TO entry. The
spans are valid only during the callback, and samples without LBR data get empty spans.
Verified on Cascade Lake only (`fixtures/lbr`).

## Public API

`TbrwFile` (open, metadata, `ReadSamples`, `ReadLbrSamples`, `HasLbr`), `TbrwEventInfo` (dense `Index` and raw `EventId`),
`PmuSample`, `PmuSampleHandler`, `LbrSampleHandler`, `TbrwDescriptor`. See the source for the rest.
