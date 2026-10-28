# Default typeperf counter set

`typeperfinput.txt` is the default Typeperf counter list for `wpafusion-capture`. It is passed to typeperf with `-cf`.

- Source: the Intel TypePerf counter set, copied unchanged on 2026-10-17. This
	list is retained for internal Intel use and is not for external redistribution.
- 59 counter paths. Wildcards (`Process(*)`, `Processor(*)`, `Processor Information(*,*)`, `Synchronization(*)`) expanded to 1,403 columns on the 2026-10-17 test machine.
- Keep CRLF line endings and plain ASCII; `.gitattributes` pins this file to CRLF.
- The file is also built into `wpafusion-capture` as an embedded resource, so the tool works without the source tree. Each run copies the counter list it used into its output folder.

The original package's `start_typeperf.bat` refers to `StandardTypePerfInput.txt`, which is not in that package, and stops typeperf with `taskkill /F`. The capture tool replaces both scripts.
