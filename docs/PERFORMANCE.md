# Performance changes in 6.4.2

The changes target avoidable UI-thread work and shared-drive traffic. Shared-data
formats, the default data location, note-save behavior, and troubleshooting content
remain compatible with 6.4.1.

- **Startup:** construct the window without probing the shared drive. Read
  inventory and run existing migrations on a worker after Loaded. The shell can
  render while reads finish; controls become enabled after the snapshot is applied.
- **Search:** use the startup connection state instead of checking two shared
  files on every query. Split the query once. A bounded queue retains the best 40
  matches, sorting only those results. Preserve scores, title ordering, stable
  ties, shared-note snippets and result payloads.
- **Photos:** check/open/decode off the UI thread. Decode to at most 1280 pixels
  on the long edge while preserving aspect ratio and avoiding upscaling. Freeze
  the image and close streams before handing it to WPF. Read fresh file contents;
  no persistent photo cache is introduced. Cancel older selections and guard
  against late completions after selection changes or window closure.
- **Index refresh:** cancel scans after reload/setup/closure, keep newer saved
  notes when an older scan completes, and cache each folder listing only for the
  duration of one scan. Keep per-unit links for resources shared by multiple
  units. Read fresh file metadata in subsequent scans so other-PC changes remain
  visible. An unreadable resource does not discard inventory/notes search results.
- **Setup/navigation:** reuse an already loaded gate list, probe readiness once
  per setup reload, and avoid loading the same selected unit details twice.

## Measurement

`bash scripts/validate.sh --benchmark-search` compares the original query logic
with the new service, first asserting identical ordered results. The synthetic
fixture has 16,000 entries and six queries per sweep; it reports the median of
seven measured sweeps after warmup.

One Linux Release run measured **103.14 ms → 26.49 ms per sweep** and
**21,201,080 → 386,120 allocated bytes**: about 3.9 times faster and 98% fewer
allocated bytes. Timings vary with CPU load, JIT and dataset. This is a
search-engine benchmark, not measured Windows UI latency or a plant-network
benchmark. No fragile timing threshold is used in correctness tests.

The normal Linux suite covers 26 data/search checks. The Windows workflow runs
native codec/window checks before publishing its downloadable executable. Manual
clipboard/drag-drop, real shared-drive behavior, and plant integrations remain in
[WINDOWS_VALIDATION.md](WINDOWS_VALIDATION.md).
