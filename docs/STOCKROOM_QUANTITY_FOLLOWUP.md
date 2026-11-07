# Stockroom quantity follow-up after v8.2.3

These changes continue quantity retrieval work; they do not implement automatic part import by Item ID or establish live Huntingdon operation.

## Correctness

A failed interactive lookup previously retained initial HTML for the report, but an incidental matching Item ID in that unfiltered page could still appear as current stock. Snapshots now carry `QuantitiesConfirmed`; failed browser searches retain rows and table samples with that flag false. All quantity matching rejects those rows, including the parts list, selected-part details, replacement-parts panel and report match counts. Reports retain raw matches as evidence and serialize the confirmation flag.

The Huntingdon inventory structure (`inventory-container` with `input.search-input`) always requires interactive server-confirmed search, even if every requested Item ID appears in its initial HTML. The v8.2.3 WebView2 acknowledgement and highlighted-result checks remain in place. Confirmed zero quantities remain valid.

## User and initial admin setup

Both stock panels disable refresh during a lookup and offer **Cancel check**. Canceling restores refresh and gives retry guidance without retaining live quantities. Switching plants still cancels the previous request and rejects its late results.

Setup → Shared Data / Software now has a stockroom first-use checklist: WebView2 Evergreen Runtime, plant-network inventory access, per-PC plant choice, a manual Available-quantity comparison, and report collection. No shared-data migration or reinitialization is needed.

## Validation and remaining site check

Linux validation passed: 62 data/search checks, 172 Siemens checks and 37 Hokuyo checks. The WPF application and Windows regression runner cross-compile with zero warnings/errors. New checks cover a complete-looking but unconfirmed initial inventory, browser-search failure evidence, confirmed zero after retry, cancellation, synchronized refresh controls and unknown quantities after a failed check. The UI check requires native Windows execution; compiling it on Linux does not establish a pass.

On a plant-network Windows PC:

1. Open Parts and select Huntingdon. Open stockroom and confirm browser access to `http://usdevapps01:3454/inventory/02`.
2. Refresh stock and compare a known Item ID's Available quantity and bin with the site's Safe2Cross search. If possible, include a known zero-stock item.
3. Confirm cancel and retry work from either stock panel. If the lookup fails, save the stockroom report from the right panel. `QuantitiesConfirmed: false` means its retained table is diagnostic evidence only.
4. Confirm an unsuccessful search never shows quantities from initial unfiltered inventory. Missing or ambiguous Item IDs must remain unknown.

Automatic Item ID import remains deferred until that live comparison succeeds. No home previews or screenshots are needed for these checks.
