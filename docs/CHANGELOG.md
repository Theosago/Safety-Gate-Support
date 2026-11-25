# Changelog

Release history for Safety Gate Support, newest first. This file consolidates the per-version notes that previously lived in separate files under `docs/`. Older entries describe behavior at the time of that release; the current behavior is in the [README](../README.md) and the [Windows quick start](WINDOWS_QUICK_START.md).

## 8.8.2 — App icon

- Safety Gate Support has its own icon (a boom barrier with a striped arm, in Gränges red and white) on `SafetyGateSupport.exe`, the taskbar, the title bar and Alt+Tab, replacing the generic Windows icon. The Siemens USB check tool uses the same icon. `tools/branding/make_icon.py` regenerates it.

## 8.8.1 — Signal column names and true colours

- The signal column tab reads the names the Siemens configurator saves inside a `.signal` file. The input switches and the All states table show each input's wire colour (for example **Input 2 (brown)**), any renamed inputs and their descriptions, and the state's name when it was given one.
- Colours now look the way they do in the Siemens configurator's simulator. The column stores mixed colours at reduced power (amber is stored as 134, 120, 0), so the app scales them back for display instead of showing a dark olive. Stored values in the file are unchanged.

## 8.8.0 — Simpler unit page, assets without units, and an easy signal column tab

- **Spare parts:** the stockroom is read once when the app opens and again only when a part is added, so new parts show an accurate count. The parts panel keeps **Choose spare parts…** and **Browse catalog**; the stockroom picker and refresh buttons moved to the Parts catalog (**Check stock again**). Part cards are compact.
- **Unit page:** shared notes sit under the photo to use the open space. A unit with no electrical drawing shows its spare parts where the drawing would be.
- **Drawings:** one **Upload drawing…** button replaces Add drawing pair and Replace selected drawing. It asks whether the files are a new revision of the drawing shown or a different drawing.
- **Assets without units:** a new asset starts as a single **whole asset** and needs no unit number. Adding the first numbered unit turns that record into Unit 1, keeping its files, photo and links.
- **Assigning manuals and parts:** one searchable checklist picks a unit's manuals or spare parts, with **Only this unit** or **Every unit on this asset**. In Setup, a part's or manual's units are chosen from an asset → unit tree.
- **Plant setup:** Start here and Resource buttons are removed. Setup opens on Library & software until a library is connected, then on Units & assets with the current unit selected.
- **Manuals & files:** the separate Support library tab is now **Software & configuration files** inside Manuals & files. The Troubleshoot tab is hidden for now.
- **Signal column:** three short steps (connect, choose the configuration, upload) with instructions in place of warnings. The only saved configuration is chosen automatically, the comparison says **Stored settings match the selected file**, and an **All states** table shows every input combination. **Upload configuration pair…** saves a `.signal` file together with the PDF that shows it; the PDF appears beside the configuration and both are archived together on revision. Reports save automatically without a file dialog.
- Existing drawings, files, links, archives, QR folders and Drive IDs are unchanged.

## 8.7.0 — A redesigned, consistent interface

- **Every control restyled:** buttons have clear roles (blue primary, white secondary, quiet text actions, red delete), and rounded text fields highlight when focused. Dropdowns open as shadowed lists, checkboxes and radio buttons are custom, and lists highlight hover and selection. Scrollbars are slim, collapsible sections use arrows, and sections sit on cards. Tooltips, links and progress bars match.
- **Dark mode reworked:** cards sit above a darker background, text is readable everywhere, and the theme picker lives at the foot of the sidebar.
- **Main window:** wider sidebar with icons and Plant setup as a menu item, card-style unit and Common Problems tiles, and card sections on the unit page. Common Problems starts collapsed until problems exist.
- **Plant setup rebuilt:** a left-hand section menu with icons (Start here, Units & assets, Common problems, Parts, Library & software), with advanced tools under a toggle at the bottom. Each page has a title and one-line explanation. Forms are laid out in columns with labelled fields, Start here is a numbered 1–5 checklist with plain wording, and task guidance shows in a banner. The unit editor puts the identification photo first and large.
- Help, MaintainX, photo, support library and folder organisation windows share the same look.
- No data, file or workflow changes. The build now saves screenshots of the main screens for design review.

## 8.6.1 — Remove guide PDFs

- Guide PDFs (extra PDFs added to a unit's QR guide) show as **Guide PDF • name** in a unit's **Drawings and guide PDFs** list, so they are not mistaken for electrical drawings.
- Select one and choose **Remove from this unit…** to take it off that unit. Other units on the asset keep it. When no unit uses it, the file moves to `_AppData/Operator Publishing/Removed Guide PDFs` on the shared library rather than being deleted. The unit guide on Google Drive is rebuilt in place at the next publish, so QR links are unchanged.
- PDFs left by the original one-unit QR pilot can be removed the same way.
- The Windows build tests allow the embedded stockroom browser more start-up time, which stops intermittent build failures.

## 8.6.0 — One search bar and a cleaner layout

- A single search bar at the top of every page replaces the separate unit search, **Search everything**, Manuals and Parts search boxes. Typing filters the unit photo gallery and shows a **Matches** list of units, drawings, manuals, parts, notes and help topics. One click (or Enter) opens a match; searching from another page returns to the results.
- Keyboard: **Ctrl+F** focuses search from any page, **↓** moves into the matches, **Esc** clears.
- The search bar has a magnifier icon, hint text and a clear button. Each match shows its type (Unit, Manual, Part, Files, Help).
- Sidebar sections have icons. **MaintainX work orders** sits beside **Replacement parts** in the unit header.
- The duplicate search panel below the gallery is removed. Shared data, drawings, publishing and settings are unchanged.

## 8.5.7

Shows current MaintainX work orders from a unit’s page. See [MaintainX setup](WINDOWS_QUICK_START.md#view-maintainx-work-orders).

## 8.5.6

Scans stockroom counts for QR spare-parts PDFs daily and registers the optional background task directly with Windows Task Scheduler. See [QR publishing and stockroom refresh](GOOGLE_DRIVE_PILOT.md).

## 8.5.5

Adds unit spare-parts PDFs with individually dated stockroom counts.

## 8.5.4

Adds batch PDF and Word imports directly in **Manuals**. Shows drawing coverage on the unit page and lets one PDF attachment appear in several unit guides.

## 8.5.3

Lets you include additional units as part of replacing a drawing pair.

## 8.5.2

Can sync the app's shared notes with the Operator notes in each Drive QR folder. See [the shared notes setup](GOOGLE_DRIVE_PILOT.md#sync-shared-notes-with-phones).

## 8.5.1

Lets you upload a drawing pair once, choose the units it covers, and update every linked unit from one replacement.

## 8.5.0

Adds **Publish & print QR** to the main sidebar. The app creates Drive folders and illustrated unit guides, updates the standalone electrical PDFs after paired drawing uploads, and prints all unit QR codes in one batch. See the [Drive publishing guide](GOOGLE_DRIVE_PILOT.md).

## 8.4.6 — Item ID import and bulk lookup

In Plant setup → Parts, choose the stockroom plant, type an Item ID and click
**Import from stockroom** (or press Enter). The app saves the returned item name,
full source description, Available quantity, bin/location and plant. Manufacturer
and photo are imported when the stockroom supplies them; absent fields are not
invented. No manual description is needed. Saving an ID-only new part triggers
lookup rather than creating an empty record.

Expand **Bulk import by Item ID** to paste up to 100 unique IDs separated by lines,
commas, semicolons or spaces. Each result is reported independently. Cancel stops
remaining lookups; records already saved remain. Imports update the catalog;
choose the units that use each part afterward. Existing documents, photos,
assignments and custom notes survive reimport. Stockroom descriptions remain
separate from custom notes.

IDs can contain up to 64 ASCII letters/digits and - _ . /. Long IDs and NSOP are
supported. IDs remain strings. Numeric zero-padding compatibility is retained;
matching never uses prefixes. Conflicting multiple rows remain ambiguous.

Direct ID search requires server acknowledgement and changed, stable results,
without requiring highlighted ID badges or global network silence. Initial
unfiltered inventory is rejected. Catalog stock refresh additionally searches IDs
missing from the Safe2Cross results, under a two-minute overall limit. Individual
lookup failures do not discard successful results. Live operation still requires
access to the private plant network and must be checked there.

### Phone QR trial

The CI **QR-offline-test** download contains a PNG and SVG with harmless test text.
Display the PNG on a computer or print it, then scan with the intended phone camera.
It needs no hosting, employee login or Q: access. Text-QR support varies by camera.

For real units, the existing Phone support setup exports printable QR labels and
self-contained photo guides. Offline QR labels carry brief saved text, not photos
or a whole website. An HTTPS QR needs an approved phone-accessible destination;
it does not create hosting. Exported HTML guides include their photos and can be
printed to PDF. Do not use a personal account to publish company material without
company authorization.

Validation covers exact long/alphanumeric IDs, decimal quantities, metadata,
bulk failures/cancellation, delayed unhighlighted browser results, rejection of
unchanged inventory, QR decoding and packaged Windows browser operation. Fixture
checks cannot confirm the private stockroom's live availability or a physical scan.

## 8.4.4

Adds PLC, light column and scanner examples to the operator demo and puts PLC files directly on the unit page.

## 8.4.3 — Set up the first useful unit

Open **Plant setup → Start here → Try the operator demo** to see the result
before entering plant data. Open the DEMO-01 photo, find its reference PDF
under **Manuals / documents**, then open the missing-label card in
**Common Problems**. Close the demo to return to setup. The example uses a
separate temporary library and settings.

The demo also includes **PLC files**, **Light / signal column**, and **Safety
scanner** under the unit photo. PLC files contain a plain-text code listing.
The column example has a synthetic `.signal` file for previewing display
states. The scanner example has a fictional configuration record and a
display-code lookup. The demo disables USB reads and writes.

Use **Replacement parts (1)** in the unit header to see the demo label kit,
or **Parts catalog** in the sidebar to search the full library. **Troubleshoot**
in the sidebar opens Common Problems and guided checks for any unit. Use
**Close demo** to return to setup. In the main app, **Plant setup** and
**Ctrl+Alt+Shift+A** both open the setup guide.

1. Choose an existing library, or create an empty folder and name your first
   area. A local folder is enough for the first trial.
2. Add the physical asset / box and save it. Check the selected asset, then
   add its first unit and identification photo. Match the number to the
   physical label when one exists.
3. Select the saved unit for the pilot. The checklist shows whether that unit
   has a photo and a PDF assigned. Attach one approved PDF and publish one
   common problem with a photo and clear instructions. The PDF is copied to
   the library, and published problems appear across the plant.
4. Open the pilot in operator view. Ask someone unfamiliar with the app to
   identify the unit, open its PDF and find the problem instructions. Use
   **Plant setup** in the sidebar to correct whatever slowed them down.
5. Try a [phone guide or QR label](#842--try-one-unit-on-a-phone) if the crew needs it.

Check the actual photo and PDF in operator view. The checklist reports saved
assignments. Once a coworker can complete the trial, repeat it with the next
asset. Add replacement parts and configuration files as needed.

For a real unit, enable the light column and scanner that actually exist in
**Gates & Units → Equipment on this unit**. Save the unit, then use its
equipment actions to manage the assigned files. Use **PLC files** for
controller backups and code shared across the asset's units. Keep those
files in the controlled plant library.

## 8.4.2 — Try one unit on a phone

In Setup → Start here, select the saved pilot in step 2. Step 5 shows that
unit's saved QR mode. Choose **Edit pilot QR settings**, then **Save Unit**
and **Return to phone pilot**. Export this pilot, extract the ZIP and open
`index.html` to find its printable guide and label. The site-wide export
under Shared Data / Software still covers all units.

- With neither field saved, the package contains the printable guide only.
- With offline instructions and no link, the label encodes plain text. Camera
  support varies. The label also prints the text for anyone who cannot scan it.
- With an approved HTTPS link, the QR opens that exact URL and requires internet.
  Offline instructions stay in the printable guide. Clear the URL and
  re-export to switch back to an offline label.

The pilot exports one unit and its selected parts, plus the published Common
Problems shared by the plant. It omits shared notes, PDF attachments, live
inventory and machine configuration files. Review the guide before distributing
it. PDFs attached in Setup open in desktop operator view.

### Test document sharing without a server

Start with a fictional PDF. If your organization approves anonymous viewing,
use a company-owned document location and share an individual approved PDF as
[Anyone with the link → Viewer](https://support.google.com/drive/answer/7166529).
Anyone with the link can read that file, and administrators can restrict the
setting. Keep machine configuration files in controlled storage.

Paste the file's share link into the pilot QR settings and export again.
Test the printed QR on a signed-out phone using mobile data. The PDF should
open directly. A sign-in or access request means the link failed the trial.
After replacing an approved PDF, verify that the original share URL still works
before reusing printed labels.

## 8.4.1 — A guided first-plant pilot

Setup now opens **Start here** instead of the full equipment editor. The goal is one useful pilot unit, not a complete plant migration. Operators should be able to identify that unit, find its approved PDF, and recognize a common failure without needing the engineer who configured it.

### A new plant engineer’s path

1. Choose **Set up this plant** on the opening screen, or press **Ctrl+Shift+Alt+A**. This opens the guide; it no longer automatically installs Huntingdon sample inventory.
2. In step 1, connect a readable existing library, or choose an **empty folder**, name the first area and create a **blank plant library**. A local folder works for a pilot. Move to a shared location when several PCs need the same content.
3. Add an **asset / box**: its number, area and location are the primary fields. Manufacturer, search keywords and PLC details are optional and collapsed. No manufacturer is assumed for a new asset.
4. Use **Next: add a unit to this asset**. Enter the physical unit number and choose its identification photo. For a box with one assembly, Unit 1 is a practical starting convention if it matches the plant’s labeling. Only enable equipment that actually exists; new unit forms do not assume a signal column or scanner. Save Unit.
5. Use **Next: pilot checklist**. Choose the saved pilot and **Attach a PDF**. The file is copied into managed storage, titled from its filename and assigned to that unit. Other units’ assignments remain intact; no resource rules need to be configured for this first PDF.
6. Add one **Common Problem** with its photos and clear fix instructions. Parts are optional for the first pilot. Item ID lookup currently depends on the three configured stockroom plants and their network access; a different inventory service still needs its own integration. Reviewed parts can be entered manually.
7. Choose **Open the saved pilot in operator view**. Setup closes, saved data reloads, and the operator page selects that pilot. Check the real photo, open **Manuals / documents**, try the problem photo → fix page → Back flow, and check that the unit remains correct.
8. For workers without a PC/account, export a printed photo guide and offline text QR label. Document links can be added when approved sharing is available; see [phone support](#84--common-problems-pages-and-practical-phone-support).

The guide reports saved record/assignment counts. Those counts do not establish that external files, network services or physical equipment work; the operator trial is the check of actual usefulness.

### Keep optional tools out of the first visit

**Show advanced tools** reveals resource-button rules, troubleshooting-flow editing, manual-index editing, areas and audit history. The guide can open the specific advanced editor when requested, such as Manage areas. Common Problems places unrelated home-page wording inside an optional expander. Vendor executable paths are optional. Huntingdon sample data is clearly labeled training data and separate from blank-plant creation.

Blank library creation refuses nonempty folders and never resets an existing plant. Its catalog marker prevents the bundled Huntingdon parts from appearing after defaults refresh or app restart. Existing installations keep their records and current paths. This is an initial onboarding design, not a claim that every plant’s inventory or documents can be configured automatically.

### Uncoached pilot experiment

Ask one engineer from another plant to start with an empty test folder and approved example files. Give them the tasks below without explaining which tab to choose. Have a maintenance colleague use the result.

| Task | Evidence of value | Record for the next iteration |
| --- | --- | --- |
| Create one asset and unit with a photo | Correct item is recognizable from the operator gallery | Time, terms they misunderstood, questions asked |
| Attach an approved PDF | Operator opens the correct unit’s document | Misassignments, missing-file confusion |
| Add a problem picture and fix | Operator finds the right instructions and returns to the unit | Wrong clicks, unclear instructions |
| Prepare a phone/box guide | Crew can read the printed guide and scan the text QR | Actual camera behavior, readability at label size |

Do not expand to dozens of units until the pilot reduces real lookup effort. Fix the point where the engineer or operator gets stuck before adding more setup options. No external usability session has been claimed or performed automatically.

### Validation

Regression checks cover blank creation, refusal to overwrite existing content, reopen without bundled parts, copied PDF persistence and unit-specific assignment, and rejected invalid PDF files. Windows CI walks through the actual Start here → blank library → asset → unit → Common Problem → operator-preview selection flow, including hidden advanced tabs and neutral equipment defaults. Existing photo/layout, Common Problems, stockroom and hardware protocol checks remain in the workflow.

## 8.4 — Common Problems pages and practical phone support

Common Problems is available on the right of both the home and selected-unit pages. Its header button collapses it to a narrow edge button and restores it without losing the current unit. Opening replacement parts collapses Common Problems; expanding Common Problems closes replacement parts to preserve workspace width.

Unit actions (Troubleshoot, Electrical Schematics, signal column, scanner, manuals and Parts) are below the identification photo and above shared notes. Opening a Common Problem now navigates to a full page inside the app, with all of its photos and instructions. Back restores the originating home or selected-unit view. Global search uses the same page for Common Problems.

### Multiple problem photos

Setup → Home / Help → choose a Common Problem. **Add photos** accepts multiple files. Select a photo to preview or remove it; **Use selected as cover** moves it to the first position. Save Topic copies new photos into shared storage. Existing single-photo entries continue to work. Up to 20 photos are supported, each below 64 MB. Removed/replaced shared files remain available to backed-up records. A failed save removes newly copied files and retains the prior record.

### Phones without Q:, a server, or employee accounts

A QR code cannot grant access to Q: or bypass a document provider’s login. A practical first step is a printed photo guide in each box plus a text-only QR label. Neither requires an employee email or a server. Text QR behavior varies between camera apps; test on the crew’s actual phones. Keep the printed instructions readable as a fallback.

1. In Setup → Gates / Units, select the unit and expand **Phone guide / QR label**.
2. Enter a short **offline instruction** summary (maximum 700 characters) and save the unit. The QR includes the unit identity, location, instructions and a snapshot date.
3. In **Shared Data / Software**, select **Export phone guides and QR labels**. Shared notes are excluded unless explicitly selected.
4. Extract the ZIP and open `index.html`. Each unit guide embeds its photos; no live server is needed to read the files. Open the guide in a desktop browser and use Print → Save as PDF, or print it directly for the box.
5. Open `qr-labels.html`, print at the intended label size and test scanning before attaching it. Offline labels contain text only. Replace labels when their instructions change.

The package contains unit identification, published Common Problems and their photos, and that unit’s replacement parts with reported stock snapshots. It has no live inventory, USB controls, drawings or manual attachments. Desktop exports resize photos for manageable guides. Warnings about missing/unusable photos are in `READ-ME.txt`. Nothing is uploaded or published automatically.

#### If approved document sharing becomes available

Use a company-owned document library or account so ownership and updates continue after the internship. A company-owned Google Drive folder with **Anyone with the link → Viewer** could serve PDFs without requiring employee accounts, if the company permits that sharing. Anyone receiving the QR can read such a document. Check that the guidance is approved for that access; a personal account should not become the maintenance system’s owner.

Pilot one unit first. Upload its photo guide PDF to the approved location, paste the exact HTTPS sharing link into the unit’s **Approved document link** field, save and export labels again. That link takes priority over offline text. Test it on a phone signed out of employee accounts. The exporter never invents a hosted URL or creates an account/server. Keeping the same document and link lets its guide be updated without replacing link-based labels.

### Validation

Linux checks exercise multiple-photo storage, rollback, reordering and backward compatibility; exported images/content, HTML escaping, hidden-entry/notes exclusion, unchanged outputs on failed exports; and independent QR decoding for both offline text and HTTPS links. A headless browser checked an isolated guide at 390 px width, embedded-image loading, and PDF printing. Managed-browser policy blocked opening a `file:` URL in this cloud runtime, so that rendering test loaded the generated HTML through the browser automation API; opening exported files on users’ Windows browsers and scanning on physical phones remain site checks.

Windows CI checks the collapsible panel across unit selection, action-button placement, full-page navigation and Back, two problem photos, and photo-preserving Setup edits. Existing hardware validation limits still apply. No screenshots or home previews are generated.

## 8.3.1 — Recognize a common problem by its photo

The home page has a **Common Problems** tab on the right. Each published entry shows an uncropped problem photo and title. Clicking the card opens the full photo and the configured recognition/fix instructions. The panel scrolls independently of the unit gallery; unit search stays fixed at the top. Entries without a usable photo remain clickable, with an explicit photo-unavailable message.

### Configure shared entries

1. Open Setup with **Ctrl+Shift+Alt+A**, then **Home / Help**.
2. Click **+ Common Problem**.
3. Enter a short recognizable title and the instructions for identifying and fixing the problem.
4. Use **Choose / replace photo** for a picture of the damaged part or other visible symptom. The photo previews before saving.
5. Set **Sort order** (lower numbers first) and leave **Show this entry to users** checked. Click **Save Topic**.

Select an existing entry to change its instructions, replace/remove its photo, hide it temporarily, or delete it. Unpublished entries also stay out of global search. Published entries appear in global search as well as the home panel. Changes appear after closing Setup on that computer and when other computers next reload shared data.

Photos are copied to `HelpPhotos` under the configured shared folder. Users do not need access to the editor's original photo file. Replacing/removing a photo retains the previous shared file so backed-up help records can still use it. Existing Quick Help and Troubleshooting entries remain compatible and visible by default; no reinitialization is needed.

Validation covers shared photo copying, edits, visibility, retained backup photos, rejection of missing fix instructions, and preservation of unrelated topics. The Windows workflow also checks home card photos/order, hidden entries, clicking into the photo and correct instructions, and editing through Setup without losing the photo. No home previews or screenshots are generated.

## 8.3 — Item ID setup and portrait unit browsing

Setup → Parts now starts with a plant selector and Item ID. Enter the ID and press Enter or **Import from stockroom**. The application searches that plant using WebView2, waits for the server search acknowledgement and highlighted results, requires a unique exact Item ID, and saves the returned name/description, bin, available-quantity snapshot, plant and manufacturer to shared data. Optional manual edits are collapsed; unit/gate assignments remain available.

Existing Item IDs update in place while retaining their identity, photo, document, custom notes and assignments. Missing manufacturer data does not erase an existing manufacturer. Missing, ambiguous and unconfirmed results are rejected. Imported quantities are labeled snapshots; they do not become live availability without a fresh quantity check. Item ID import is now implemented at the user's request; the earlier deferral in stockroom follow-up notes describes the previous build.

If an inventory row supplies a photo, setup downloads it from the same stockroom origin, validates its format and size, and copies it into the shared photo library. Photo errors are reported separately without discarding a valid imported item. If the stockroom provides no photo, setup says so; no image or metadata is invented. The private stockroom may expose additional fields or photos only on a detail page; those layouts are not established by the available saved inventory pages and are not guessed.

Unit replacement parts now show photos. Their panel starts collapsed, opens with **Parts**, closes with another click, and collapses when changing units. The opening gallery no longer reserves room for a parts panel. Search and area filters stay above the scrollable content. Compact wrapping cards use uncropped portrait photos and less repetitive location text.

No shared-data reinitialization is needed. WebView2 Evergreen Runtime and plant-network inventory access are required for import. Cloud regression tests cover field mapping, repeated imports and preservation of existing assignments/files. Windows checks additionally exercise numeric interactive search, automatic setup import, fixed search position and panel opening. Live arbitrary-Item-ID searches and stockroom photos still need a plant-network Windows check. No home previews or screenshots were generated.

The signal-column tool has one primary action: **Reupload signal column configuration**. USB connection controls and read/compare or expected-output tools are collapsed into connection details and Advanced diagnostics. The upload still requires explicit device/file selection, confirmation, a backup and stored-settings verification. Cancel appears only during an operation. No USB protocol or hardware verification behavior was changed.

## Stockroom quantity follow-up after 8.2.3

These changes continue quantity retrieval work; they do not implement automatic part import by Item ID or establish live Huntingdon operation.

### Correctness

A failed interactive lookup previously retained initial HTML for the report, but an incidental matching Item ID in that unfiltered page could still appear as current stock. Snapshots now carry `QuantitiesConfirmed`; failed browser searches retain rows and table samples with that flag false. All quantity matching rejects those rows, including the parts list, selected-part details, replacement-parts panel and report match counts. Reports retain raw matches as evidence and serialize the confirmation flag.

The Huntingdon inventory structure (`inventory-container` with `input.search-input`) always requires interactive server-confirmed search, even if every requested Item ID appears in its initial HTML. The v8.2.3 WebView2 acknowledgement and highlighted-result checks remain in place. Confirmed zero quantities remain valid.

### User and initial admin setup

Both stock panels disable refresh during a lookup and offer **Cancel check**. Canceling restores refresh and gives retry guidance without retaining live quantities. Switching plants still cancels the previous request and rejects its late results.

Setup → Shared Data / Software now has a stockroom first-use checklist: WebView2 Evergreen Runtime, plant-network inventory access, per-PC plant choice, a manual Available-quantity comparison, and report collection. No shared-data migration or reinitialization is needed.

### Validation and remaining site check

Linux validation passed: 62 data/search checks, 172 Siemens checks and 37 Hokuyo checks. The WPF application and Windows regression runner cross-compile with zero warnings/errors. New checks cover a complete-looking but unconfirmed initial inventory, browser-search failure evidence, confirmed zero after retry, cancellation, synchronized refresh controls and unknown quantities after a failed check. The UI check requires native Windows execution; compiling it on Linux does not establish a pass.

On a plant-network Windows PC:

1. Open Parts and select Huntingdon. Open stockroom and confirm browser access to `http://usdevapps01:3454/inventory/02`.
2. Refresh stock and compare a known Item ID's Available quantity and bin with the site's Safe2Cross search. If possible, include a known zero-stock item.
3. Confirm cancel and retry work from either stock panel. If the lookup fails, save the stockroom report from the right panel. `QuantitiesConfirmed: false` means its retained table is diagnostic evidence only.
4. Confirm an unsuccessful search never shows quantities from initial unfiltered inventory. Missing or ambiguous Item IDs must remain unknown.

Automatic Item ID import remains deferred until that live comparison succeeds. No home previews or screenshots are needed for these checks.

## 8.2.3 — Confirm the inventory server applied its search

The before/after saved Huntingdon inventory pages establish the actual DOM: the search is a text input with class `search-input` inside `inventory-container`. The successful filtered page has `.search-info .search-terms` containing `Safe2Cross` and exactly 19 replacement-part rows. The initial page has unrelated rows and pagination (804 pages). These are server-rendered Blazor pages, so DOM readiness alone does not establish that the server's interactive handlers are connected.

For this inventory layout, the embedded reader now commits input/change/blur and waits for the server-rendered Searching: Safe2Cross indicator before accepting the table. Nonempty result tables must also contain the site’s highlighted search term; the caption can arrive before the result rows. If handlers were not ready, it retries at 1.5-second intervals within the existing 35-second bound. Merely inserting text is not a successful search. Missing acknowledgement fails with a specific message and retains unfiltered HTML only as diagnostic evidence. Generic interactive tables must change after the input, rather than returning unchanged initial inventory. Exact matching, zero quantities and Available-column preference remain unchanged.

Windows regression tests include the site's class structure, unrelated initial inventory, delayed server-like event registration and filtering driven by change events. The packaged executable's search fixture is still tested independently. The actual .NET server-side source is not included in saved pages, so change-vs-input binding and connection timing are handled defensively rather than claimed as proven server implementation details. Live refresh still needs confirmation on the plant network.

## 8.2.2 — Embedded stockroom search replaces the Edge remote-debugging launch

The Huntingdon report from 2026-11-05 proves that the HTTP reader reached HRM's inventory but received the first 50 unfiltered rows. Eight had numeric IDs accepted by the parser, all unrelated to the requested 19 items. Its diagnostic reader string was: "The background stockroom reader exited before connecting." There was no completed Safe2Cross search.

The application now uses Microsoft's embedded WebView2 API for the interactive search rather than launching Edge/Chrome with a remote-debugging port. It runs the existing input/search/table-reading logic in a hidden app-owned Windows view. It types the search using the supported DevTools input API within WebView2; no externally listening debugging port is needed. HTTP-only inventories keep the lightweight reader. Selected-plant boundaries, exact Item ID matching, explicit Available-column preference and truthful match counts remain. The page and WebView control are disposed on completion/cancellation. The Windows user profile stores WebView session data under LocalAppData/SafetyGateSupport/StockroomWebView. Reports still omit browser cookies and credentials.

Microsoft's Evergreen WebView2 Runtime must be installed; if missing, the diagnostic reports that requirement explicitly. There is no silent runtime installer. Windows tests exercise a page with unrelated initial inventory and a JavaScript-only search returning 19 parts, including zero stock, without changing the page URL. The published executable is tested separately with an isolated HTML fixture to verify the WebView loader and JavaScript search survive packaging. No home preview or screenshots are generated. Actual plant-network quantities still require confirmation after installing this build.

## 8.2.1 — Stockroom lookup corrections and evidence

The stockroom status reports matched Item IDs against the catalog count, not success merely because some inventory rows were parsed. Zero matches is explicitly a failed quantity lookup. Duplicate desktop/mobile tables with identical complete cell text and column mapping are deduplicated. Conflicting quantities and different locations/sites remain ambiguous. When both available and on-hand columns exist, the explicit available column takes precedence.

A recognizable search form is submitted even if the landing page already contains a table. The application supplies its catalog Item IDs to the reader, so an incomplete initial HTML table can trigger the JavaScript browser search rather than be accepted as complete. This remains the current Safe2Cross search, not a general Item ID import implementation.

Save stockroom report on the right panel exports a ZIP containing selected plant URL, lookup status, parsed rows, per-Item-ID matching results and a bounded text-only sample of table cells (up to 250 rows / 16 cells / 500 characters each). It excludes browser cookies, sign-in fields, scripts and hidden form values. Reports are saved only where the user chooses, and are not transmitted automatically. They provide the evidence needed to adapt the reader to the private site's actual response. They do not prove that returned data is up to date on the database server.

The live plant site cannot be reached from this cloud environment. These corrections address reproducible reader defects; a returned report or a successful plant-network comparison is still needed to establish the cause of the user's all-unknown results. Automatic import of descriptions, bins, photos and quantities from an arbitrary Item ID is deferred until quantity retrieval is confirmed.

## 8.2 — Replacement parts beside the unit

The app opens maximized, with ordinary Windows resize/restore controls retained. Unit selection uses the left side of the workspace; a right panel shows common replacement parts. Selecting a unit shows its parts in that same panel. The unit's Parts button stays in the unit workspace. Choose unit parts opens a searchable checkbox list saved to shared unit data, with backups and a fresh read under the shared write lock. An empty selection is retained. Existing units initially inherit their applicable parts until explicitly configured.

Available appears above location and Item ID. Positive live quantities are green, zero is red and unknown is neutral. Original reported quantities never populate Available. Bin values are bold while the Reported bin label remains regular. A returned live bin is used in the parts list and right panel; otherwise the original location is labeled Reported bin. The separate reported snapshot remains available in part details.

Plant selection is visible in the right panel as well as Parts and is remembered on each computer. Live stock refreshes at startup after plant selection, on demand, and every two minutes while open. Failed reads clear availability rather than presenting a stale quantity as current. Results identify the plant and last check time. This is polling, not a real-time subscription: changes appear on the next successful read. Private stockroom connectivity and the production site's table format still need confirmation on a plant-network PC. The cloud tests use isolated fixtures, including an interactive JavaScript stockroom search; they cannot prove live plant connectivity.

No home preview or screenshots are generated. Automated checks cover maximized startup, the unit parts panel and persistence, changing live quantities, zero/unknown colors, snapshot separation and existing drawing/device workflows.

The unit workspace now emphasizes the larger uncropped identification photo on the left, with the drawing alongside it. A prominent Back to units button replaces the upper-right Change Unit button, and the redundant asset/unit browser is hidden. Area labels are centered. A global Light/Dark selector in the navigation saves the preference on this computer; app surfaces and text use shared theme brushes while photographs, PDFs and signal colors retain their original content.

Electrical drawing collections, saved signal files and scanner configurations open inside the selected unit workspace. The existing USB column compare/upload tool and scanner diagnostics can be hosted inline with their original framing, backup, cancellation and report logic. Switching away during an active operation is blocked until it finishes or is canceled. The column workflow compares an approved saved file or uploads that saved file; editing the column's configuration is not required. Physical function remains a separate observation and USB matching alone is not a functional test. Managed electrical/column/scanner collections allow regular-user uploads by default, including existing collections.

Part Manuals opens the manuals library page for the selected unit. Choose this unit's manuals saves an explicit unit list independently of part selections, including an empty list. The Support library remains a searchable source of imported documentation, vendor utilities and other reference files.

## 8.1.3 — Select the stockroom plant

Parts now has a visible Stockroom plant selector. Choose Huntingdon, Salisbury or Newport once on each computer. The choice is saved in the local settings file, not in the shared asset folder. A fresh installation does not silently assume a plant. Saved choices refresh automatically on startup; changing plants immediately clears previous current quantities and cancels the previous request. Late replies from that request cannot replace the selected plant's results.

The actual inventory routes supplied in the portal screenshots are:

| Plant | Inventory URL |
| --- | --- |
| Huntingdon | http://usdevapps01:3454/inventory/02 |
| Salisbury | http://usdevapps01:3454/inventory/04 |
| Newport | http://usdevapps01:3454/inventory/05 |

The reader goes directly to the selected inventory page, runs the Safe2Cross search when needed, and matches exact stock numbers. Open stockroom opens the same selected inventory page. Live quantity details show the source plant, returned bin and check time. Ambiguous duplicate stock rows remain unavailable. Original supplied bin locations and quantities remain clearly labeled snapshots; the legacy HRM site field no longer filters another plant's inventory. The old shared landing-page URL is retained for compatibility but is not used for these plant inventory checks.

The previous build used the shared landing URL and legacy HRM site matching; it did not establish which plant inventory had been selected. These routes are based on the user's portal screenshots. The private stockroom server cannot be reached from the cloud; live search must still be confirmed on the plant network. Automated checks cover plant routing, local selection persistence, no request before selection, switching during a pending request, site/bin differences, and offline behavior. No home preview is generated.

## 8.1.2 — Complete stock list, interactive stockroom search and print pagination

The catalog now contains all 19 supplied stock numbers. It adds terminal housing **0117717 / SG-TB-4PT-GS**, proximity switch **0117718 / AM1-AP-1A**, and Hokuyo scanner **0117719 / UAM-05LP-T301**. The scanner's reported quantity is zero, not missing. Stock **0117716** now has site HRM; no site was supplied for 0117719. The v2 catalog import runs once on existing installations, retaining matching records, photos and site edits while adding missing parts. An unknown site on 0117716 is filled with the supplied HRM value. No repeat of the data-only updater is required.

The stockroom reader no longer assumes the landing page contains results. It submits a recognizable search form with **Safe2Cross** and retains hidden form fields and session cookies. When results require JavaScript and the address does not change, it uses installed Microsoft Edge or Chrome in an isolated background session, types **Safe2Cross** into the page search, and reads the resulting inventory table. It supports the common DataTables search/pagination API so results beyond the initial visible rows are included. The browser session is closed after reading; your normal browser profile and tabs are untouched. It does not install a browser or expose a debugging listener to the network.

Plain HTML inventory pages use the lightweight HTTP reader. Interactive pages need Edge or Chrome installed. Search is bounded and runs off the UI thread; a failed search leaves current quantity unavailable and retains the reported snapshot. Actual zero-stock rows remain zero. The plant's private site cannot be reached from this cloud environment: automated Windows tests exercise a local page that exposes all 19 rows only after its JavaScript search is used, plus GET/POST forms and normal read failures. The live plant result still needs confirmation on a PC with access.

Single-page PDFs and image drawings show no page counter or page arrows. Multiple-page prints keep their page indicator and navigation. Automatic text/OCR orientation, click-to-zoom and PDF/DWG download links remain available. No home preview is generated.

## 8.1.1 — Automatic reading direction and click-to-zoom drawings

Electrical prints no longer ask the technician to flip a page. The reader first examines embedded PDF text baselines, including the PDF page's rotation metadata, to distinguish upright, upside-down and sideways text. Image-only PDFs and drawing photos use bundled local OCR to compare both landscape reading directions. All recognition runs on this computer; there is no cloud upload, account or separate OCR install.

The initial landscape preview appears immediately while recognition runs in the background. The selected page updates automatically when its reading direction is detected. Results are cached locally by document contents, page and detector version, so reopening the same print avoids repeating OCR. Previous manual-flip settings are superseded by automatic detection. Blank or unreadable pages use a landscape fallback; reading direction cannot be determined reliably without directional information. Original PDF/DWG/image bytes are unchanged.

Click the drawing itself to open its zoom viewer. Use the mouse wheel to zoom at the pointer, drag to pan, double-click to fit the whole drawing, and Esc to close. The viewer follows the automatically corrected preview even if recognition finishes after it opens. The manual-flip and enlarge buttons are removed.

Small **Download PDF** and **Download DWG** links replace the Open original/Open DWG buttons. They save an unchanged copy to the location you choose; they do not launch another application or modify the shared original. The DWG link is available only when the companion is assigned. The existing PDF/DWG revision warnings remain.

Extract the whole Windows download and keep its `Ocr` folder with the executable. The package includes the recognition engine, language model and app-local runtime DLLs. There is no additional installation step. CI tests text-bearing PDFs at all quarter-turns, scanned text, cached results, click-to-zoom and the OCR engine inside the published package. No home preview is generated.

## 8.1 — Parts photos and stockroom checks

The Parts page now lists description, bin location and then stock number, with a thumbnail beside each entry. The selected part has a large uncropped photo. Text wraps; the window can be resized and the divider between the list and details can be dragged. Details scroll vertically rather than hiding long descriptions.

Choose **Upload / replace photo…** on the selected part. JPG, PNG, BMP, GIF and TIFF are supported (up to 64 MB). Camera orientation metadata is applied automatically on display. The app verifies that the selected file can be decoded, copies the original bytes into `Library/Parts/Photos/<stock number>/`, and stores the shared relative path in that part's record. Uploaded originals on your PC are retained. Previous managed photos are archived; externally referenced images are not moved. **Larger photo** shows the whole image in a maximized viewer. Only visible list thumbnails are decoded, at a bounded size; stale photo requests are canceled when selections change.

The 16 supplied stockroom parts are included automatically for new and existing installations; existing matching records are retained. Parts imported by the data-only updater do not need re-entry or a second import. Their bin, site and reported quantity are read from the existing notes. Whole quantities such as `5.00` display as `5`, including stockroom results. A reported quantity is labeled as a snapshot. Setup has editable bin, site and reported-quantity fields; photo uploads are available directly on Parts. Keep using the existing shared folder; no folder organizer is required. Update PCs that edit parts to this version so an older executable does not discard the new photo metadata when saving its older record format.

### Automatic quantity check

On each startup with parts present, the app makes one background GET to the stockroom URL configured in Setup, using Windows default credentials. The configured plant URL is `http://172.16.59.7/mne/storeroom.html`. The startup check does not block the main window and does not make a separate request per part. **Refresh stock levels** checks again manually.

The reader requires an HTML table with recognizable stock-number and quantity headers. It matches full stock numbers, tolerating omitted leading zeros, and uses site/bin when available to disambiguate repeated rows. It does not guess quantities from arbitrary nearby text, ambiguous rows, login pages or JavaScript-only pages. A successful match shows the stockroom quantity and check time. A genuine zero is displayed as zero. An unavailable page, missing/ambiguous row or unsupported layout displays **Current quantity unavailable**, while retaining the reported snapshot. Live results do not overwrite reported quantities in the shared catalog.

Requests have an eight-second overall timeout, a 4 MB response limit and cancellation on close. Redirects are not followed automatically. This cloud environment cannot validate the plant's private web page; automated checks use representative HTML and simulated HTTP failures. The first plant check must confirm that the actual page exposes the expected stock and quantity headers. If it does not, the app will explain that the table could not be read rather than show an invented stock level.

No home preview is generated by this release. The existing drawing, notes, Siemens, scanner and PLC tools remain available.

### Unit pages and automatic electrical-print orientation

Electrical PDF pages and drawing images automatically display in landscape. Existing landscape pages retain their orientation; portrait pages are rotated on display without an upload prompt or any change to the original PDF/image. PDF page rotation metadata is honored by the renderer before this landscape adjustment. If a page's text is upside down, **Flip 180°** remembers the correction for that unit, file revision and page. Preferences are shared under `_AppData/DrawingViews.json`. Replacing a drawing revision restores automatic landscape for the new revision, so a stale correction cannot rotate a revised print incorrectly. No PDF edits are made, and view rotation does not invalidate PDF/DWG companion hashes.

The unit page exposes **Electrical Schematics**, **Signal Column configuration**, **Safety Scanner configuration**, **Part Manuals** and **Parts**, alongside the Troubleshoot action. Electrical Schematics opens only drawing files. Safety Scanner configuration shows the configuration files and retains **Read scanner USB status…**; scanner programming remains in the vendor software. The redundant resource-button list and Photos action are removed. The identification photo remains visible and can be clicked to enlarge. PLC files/connection details remain inside Unit details.

In Setup → Gates & Units, select a unit and use **Equipment on this unit** to check or uncheck **Signal column** and **Safety scanner**, then save the unit. This shows/hides that unit's corresponding action and preserves its files and assignments. Different units of the same asset can have different equipment choices.

## 8.0 — Readable asset files and paired drawing revisions

Each asset has one active file location. Technicians can browse it in File Explorer without the app:

```text
Assets/
  Asset 1223/
    Electrical Drawings/
    Light Configurations/
    Scanner Configurations/
    PLC/
    Photos/Unit 1/
    Notes/Unit 1/
    Archive/
    File index.csv
    READ ME.txt
```

`File index.csv` identifies which units each current file covers. A print shared by Units 1 and 2 is one PDF/DWG pair linked to those units. A different unit can keep its own print. Light and scanner configuration assignments remain specific to their recorded units. PLC files and connection details remain at asset level.

### Organize an existing shared folder once

Run this version on a PC that can access the existing shared data (including Q: if that is its location). Open Setup → Shared Data / Software → **Organize asset folders**.

1. Review the preview, including each file's unit scope and planned destination. Preview does not move files or change assignments.
2. Identical copies consolidate automatically. Different contents targeting the same filename require a choice: archive the older copy, or retain it as a separate sheet. The app does not decide which drawing revision is approved.
3. Close other copies of the app, then apply the reviewed layout. Files are copied and checked with SHA-256 before metadata switches. Source bytes and records are rechecked against the preview.
4. Reopen an asset and check its drawing, identification photo, notes and configuration assignments. Use **Applies to units…** if a print's recorded scope needs correction; changing either companion updates both.

Previous metadata and original managed files are retained under `_AppData/Storage Backups/<dated backup>/`, with a report. Old revisions stay under the asset's `Archive` folder. Original library import packages stay under `Library/Imports` as source/recovery material; they are not the active drawing location. Unknown files are left in place. Running the organizer again preserves current filenames, scopes and recorded pair baselines.

Older app versions do not understand the new file assignments. Update the PCs using this shared data before making further edits. To recover from an interrupted conversion, retain the backup and refresh the preview; do not delete recovery material. Restoring an earlier metadata snapshot also requires its corresponding original files, with all app copies closed.

### Add or revise a print

The selected-unit page continues to show its drawing immediately, with shared notes visible. The PDF is selected for inline display, and **Open DWG** opens its companion with the PC's associated CAD viewer.

- **Add PDF + DWG…**: select exactly one PDF and its corresponding DWG together. A missing companion blocks publication and explains what is required. Additional sheets stay alongside existing prints.
- **Replace selected print pair…**: select either current companion, then select both replacement files. Both prior companions are archived. The exact existing unit scope is retained, including shared prints; unrelated sheets remain current. Filenames may change and do not have to share a stem.
- **Applies to units…**: explicitly choose the units covered by this print. Both companions receive the same assignments without copying the physical files.

Support-library assignments use the same collection. The previous “Electrical Drawings (imported)” entry came from creating a separate resource for every import. The organizer consolidates those entries, updates saved troubleshooting links and retains the original files in recovery storage. New assignments reuse the asset collection.

### Companion warnings

The app records the pair's hashes and upload time. It warns beside the drawing when either companion is missing, when only the PDF or DWG changes, when both change outside a recorded upload, or when a replacement submits one changed file and one unchanged companion. A lightweight check runs every 15 seconds while the drawing is visible; content hashes are reread after a file's size or modification time changes. Reopening the unit also checks content.

Legacy files are retained even if they lack a companion. Existing same-stem PDF/DWG files may be recognized as companions during organization, but remain unconfirmed until uploaded together. A PDF-only legacy print can be replaced with a complete pair.

These checks establish that files were submitted together and detect later changes. They cannot prove that the PDF was plotted from that DWG or that either is the approved electrical revision. There is no automatic CAD conversion. Confirm the export and revision before publishing; an unchanged companion remains visibly flagged for review.

The normal Windows build includes the PDF renderer. No CAD application is needed for inline PDF viewing. Scanner diagnostics, Siemens USB programming, photos, troubleshooting, PLC tools and notes remain available. This change does not generate a home preview.

### 8.0.1: area-save recovery and Setup responsiveness

A Windows regression reproduces the previous area-rename failure when a shared record permits reads but blocks replacement: the old handler throws an access-denied exception after saving the category name. Area changes now run on a worker thread, read current records under the shared write lease, validate names, and retain recovery snapshots of the affected records. If a later save fails, already-written records are restored. Setup displays an error and permits retry instead of letting the save exception terminate the app. File paths, asset IDs and explicit unit/file assignments do not change when an area is renamed.

Area changes replace the bound lists after a successful save instead of mutating a list while WPF is displaying it. They refresh the inventory selectors without reloading manuals, resources, guides and the entire audit view. Setup's initial shared-data reads and manual audit refresh also run off the UI thread. Recent audit entries are read backwards in blocks rather than loading the complete shared-drive history. These changes address the identified code paths; they are not a claim that all shared-drive operations are asynchronous.

Recovery snapshots for area edits are under `Data/Backups/Area change <timestamp-id>/`. Normal JSON backups remain available as before. No folder migration is required for this patch, and it does not create a home preview.

## 7.3.0

Selecting an asset/unit now automatically displays the current assigned electrical drawing directly on the unit page. The drawing is the main pane; its identification photo and permanently open shared notes sit alongside. The five existing action buttons remain in a compact toolbar above. No file-picker or extra resource window is required to view an assigned PDF or image.

The app bundles PDFtoImage/PDFium and SkiaSharp to display pages locally, without a browser or separately installed PDF runtime. Page controls, an enlarged current-page view, and Open original are available on the drawing pane. Multiple assigned current drawings appear in a selector, with the first PDF shown automatically. Image drawings preserve their full aspect ratio. DWG/DXF retain an Open original action for the installed CAD viewer; assign a PDF for an inline view.

Only visible drawing/schematic resources explicitly applicable to the selected gate/unit are considered. The app enumerates their Current folders, not archives, other units, manuals or similarly named assets. Missing assignments, inaccessible folders, unsupported formats and damaged/encrypted PDFs produce an inline explanation. A failed preview never substitutes another asset’s drawing.

Reads and rendering run asynchronously with cancellation and stale-selection guards. Each PDF is read into a bounded in-memory snapshot (64 MiB maximum); original files are not rewritten or held open while displayed. Only the requested page is rendered, up to 2000 pixels on its long edge. Shared-note loading, saving and conflict behavior are retained. Returning from Files & drawings reloads the inline drawing so updates appear on the unit page.

Native Windows validation renders actual two-page landscape/portrait PDFs, checks aspect ratios and source-byte preservation, exercises automatic display, paging, rapid unit switching, assignment/archive isolation, deselection clearing and visible notes. Normal-app packaging and launch validation remain enabled, including rendering a real PDF through the published executable and checking its clean exit. Third-party engine notices are included in the download. No home-preview package or screenshot artifact is generated.

## 7.2.0

The selected-unit page is simplified to five primary actions beside the large uncropped photo: Troubleshoot, Files & drawings, Light column, Hokuyo scanner USB status, and PLC. The header identifies the actual asset/unit and location. Notes and secondary resources remain under disclosures. Troubleshoot opens the problem chooser; the main page no longer presents several overlapping sets of symptom/file buttons.

Hokuyo USB diagnostics were not removed. A direct scanner button now opens the existing read-only UAM identity/status check and manual display-code lookup. The guide still covers BEA separately. Opening the window does not start a USB read. Device protocol and upload/recovery behavior are unchanged.

Setup puts the selected unit and its saved photo before the asset-information form, with the image above photo-selection controls. Selecting another unit returns the editor to the top. Photo paths wrap within the editor, and missing/unreadable photos show a visible explanation. Asset settings expand below; paste, file selection, drag/drop and original-file preservation remain supported.

Every asset has a PLC page, accessible from each of its units. It records model, address/IP and notes through Setup’s asset-information section. PLC files are stored under `PLC/<stable gate ID>/Current` with same-name replacements archived through the existing resource manager. All units of the asset share those files. Renaming an asset does not change the folder. No PLC software is launched and no PLC connection/programming is performed by this page.

The build workflow now tests and smoke-launches the normal published executable using a temporary shared-data folder on the Windows runner, restoring runner settings afterwards. Home-preview packages, home-preview profile tests and screenshot artifacts are not part of normal builds. Existing preview compatibility code is retained for an explicit future request.

## 7.1 — Choose the unit first

The app opens directly to **Which unit are you working on?**, a searchable photo gallery with All areas selected. Asset number, unit, model, location and description are searchable. The area filters and search are on the opening screen. Every card identifies the asset and unit, with its own assigned identification photograph. Selecting a card opens that unit’s workspace.

The workspace keeps a large uncropped photo beside current-file shortcuts and troubleshooting choices. These actions use the selected unit directly; they do not ask the technician to choose it again. **Change unit** returns to the gallery with its search/page preserved. **Selected unit** returns to the current workspace without clearing its identity. Shared notes and resource assignments continue to use the selected unit.

Click the photograph or **Enlarge / browse photos** for a maximized viewer. It shows the assigned identification photo first, followed by image files directly in the unit’s assigned photo folder. Archive subfolders are excluded. Both landscape and portrait images fit the available space with `Stretch.Uniform`; no cropping, stretching, editing or rewriting of originals occurs. Missing/unreadable photos explain the problem and do not substitute a different unit’s image.

Gallery pages contain at most 12 units. Photo decoding runs off the UI thread with at most four loads in flight and a 640-pixel long edge. Search is debounced and cancels obsolete photo requests. The workspace uses a 1280-pixel preview and the large viewer allows up to 4096 pixels, preserving aspect ratio without upscaling the decoded source. Only the current gallery page and selected view retain decoded photos.

The two Gränges logo treatments are scalable vector transcriptions of the references supplied in this conversation: horizontal on the opening view and stacked in the navigation rail. They remain secondary to equipment identification. There is no runtime dependency on an external logo service.

Existing shared data, settings, photographs, site-edited guides and device protocols are retained. The Siemens and Hokuyo tools keep their existing backup/upload verification and read-only scanner behavior. Home Preview still uses its isolated local profile and disables USB operations.

Native Windows validation exercises the opening gallery, unit selection and context retention, file/guide actions without a second picker, bounded/aspect-preserving image decoding, stale-request cancellation, photo browsing and original-file preservation. The build’s **SafetyGateSupport-V7-Screenshots** artifact includes the gallery and selected-unit workspace as well as the device/guide views. Production equipment and shared-drive behavior still require site checks.

## 7.1.0 Home Preview build

1. Extract the entire ZIP into a folder on your Windows PC.
2. Double-click **SafetyGateSupportPreview.exe**.
3. The opening screen is **Which unit are you working on?** Browse the large photo cards or search an asset number/location. Click a unit’s card to open its workspace; click its photo to enlarge it. Photos retain their complete proportions.
4. To explore the sample light file, search **DEMO**, select **Asset DEMO / Unit 1**, choose **Light configurations**, then **Check / configure light**. The demo unit has no invented equipment photograph; use a real starter unit to try the photo viewer.

Choose **DEMO ONLY - not for equipment.signal** in the assigned-files list. Use the **Expected lights & sound** tab. Turn on only **Input 1** (red), **Input 2** (amber), **Input 3** (green), or **Input 4** (sound); turn the others off. The preview displays stored values; it does not animate or play physical outputs. Device discovery, USB reads and uploads are disabled in the home preview. The sample configuration is also blocked from upload by the shared upload engine.

Use the troubleshooting choices beside a selected unit’s photograph to explore the new signal-column, BEA LZR-S600, UAM scanner and actuator guides. Browse the starter inventory/photos, search, try notes, and open **Support library** to explore importing local documents or ZIPs.

No Q drive, VPN, Siemens software, SDK, or installer is required. This is a self-contained local preview. It uses its own settings and data under:

```
%LOCALAPPDATA%\SafetyGateSupport\HomePreview
```

Your normal app settings and shared data are separate. Local preview edits remain when reopening it. To reset the preview, close it and delete only the `HomePreview` folder above, then reopen the preview executable. The normal app can also be launched with `--preview` to select this local profile.

The inventory/photos are from the bundled starter data, not a live copy of Q. Asset DEMO and its configuration are illustrative. The large plant schematics, manuals, actual signal configurations and vendor programs are not bundled; those still need their original local files for import. Plant stockroom services and real equipment checks are unavailable from this preview.

## 7.0.0

The opening screen starts with six symptoms: gate will not open, gate will not close, actuator/arm issue, wrong/dark light, scanner stop, or an uncertain problem. The symptom survives the asset/unit confirmation step and opens the relevant guide. The picker searches across all areas initially and retains explicit unit/photo confirmation.

**Just need a file?** opens light configurations, electrical drawings or all current assigned unit files. The file browser filters/searches only the chosen unit’s current assignments and linked manuals. Archives and configurations assigned exclusively to another unit stay out of these results. Missing/inaccessible folders remain reported; missing files are never substituted with a similar unit’s configuration. Use Support library for imports and assignment changes.

The shared visual theme adds a dark navigation rail, rounded cards, a clearer type hierarchy, selected navigation state, wrapped guide titles/choices and visible keyboard focus. Unit details put the file and troubleshooting shortcuts beside the unit identity. Search, shared notes, parts, manuals and existing resource actions remain available.

### Light checks

The Siemens tool replaces the 64-state dropdown with six ON/OFF input switches. Any combination from 0 through 63 can be selected; **All OFF** clears the switches. The **Expected lights & sound** tab shows each stored segment’s color and effect, its ordinal number, and a separate sound indication. Expand stored values for RGB/brightness and sound codes. Color swatches illustrate stored RGB and do not simulate actual brightness, animation or sound. Segment orientation must be checked against the drawing.

These switches only read the chosen file in memory. They do not send USB commands, activate outputs, measure inputs or edit the configuration. Changing the combination clears the previous physical observation. Explicit assigned-file selection, supported model/firmware checks, backup-before-write, restart/readback verification and uncertain-outcome recovery are preserved. Read/compare results appear directly beneath the action buttons.

### What follows a scanner check

Choose the scanner type from its physical label before opening its guide. UAM USB diagnostics retain the fixed read-only requests and separate active error flags from stored display codes. The UAM guide continues after a snapshot with paths for no active error, close-area/window error 85, internal error 75, and connection/other-code issues. A clear power-only bench reading does not establish that a production-floor issue is repaired. The guides do not program the scanner or certify the complete gate.

Saved site troubleshooting flows take precedence over bundled defaults with the same ID. No shared data is reset or automatically reassigned by upgrading. A new triage guide is added by stable ID. A site-edited UAM guide remains intact and may consequently retain its previous steps.

### Try it on Windows

Download the successful build’s **SafetyGateSupport-Home-Preview-Windows-x64** artifact, extract it, and run **SafetyGateSupportPreview.exe**. It needs no Q drive and disables USB. Try **Light configurations → DEMO → Unit 1 → Continue → Check / configure light**, choose the sample file and toggle one input at a time. Normal production use is **SafetyGateSupport-Windows-x64 / SafetyGateSupport.exe** and retains the existing shared-root settings. See [Windows quick start](WINDOWS_QUICK_START.md).

Automated verification covers pure protocol/upload recovery, import/data/search, and native Windows WPF navigation, unit assignment isolation, all 64 input combinations, observation reset and preview USB restrictions. The build also provides **SafetyGateSupport-V7-Screenshots** with renders of the real Home, light preview and guided check windows. Physical equipment behavior and plant-network integrations require site checks.

## 6.5.0

Integrates Siemens USB backup, configuration comparison and upload, adds expected-output previews and documented troubleshooting, and imports local support libraries.

## 6.4.2 — Responsiveness improvements

- Load shared inventory/migrations after the main window opens, on a worker thread.
- Remove shared-drive readiness probes from each search keystroke.
- Tokenize each search once and retain/sort only the best results, preserving prior ranking and stable ties.
- Load identification photos on worker threads and limit decoded previews to 1280 pixels on the long edge.
- Cancel obsolete previews and index scans so old work cannot overwrite a new selection or reload.
- Keep newly saved notes in the search index if a background scan began before the save.
- Reuse each shared resource folder listing within a scan; reload file metadata for subsequent scans.
- Reduce redundant setup reads and repeated photo/note loads during navigation.
- Fix partial legacy migration so newly discovered gates are saved before their units reference them.
- Add isolated data/search tests, native Windows regression checks, and downloadable GitHub Actions builds.
- Retain shared-data formats, note saving, troubleshooting content, and vendor launch behavior.

### Performance details

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

#### Measurement

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

## 6.4.1 — Troubleshooting unit preview

- Adds a Selected Unit Preview to the Home > Troubleshoot a System picker.
- After choosing an asset and unit, the picker shows that unit's identification photo before Start Troubleshooting is clicked.
- Shows the selected asset/unit plus available manufacturer, model, location, and equipment-type details for confirmation.
- Shows a clear message when a unit has no saved identification photo or the photo cannot be found.
- Removes the unit-list double-click shortcut so troubleshooting starts only from the explicit Start Troubleshooting button after the preview is visible.
- Loads preview images from a file stream with BitmapCacheOption.OnLoad to avoid stale WPF image caching.
- Retains all V6.3 photo-refresh, V6.2 clipboard/drag-drop, and V6.0 Foundation behavior.

## 6.0 — Foundation

V6 normalizes the shared data into gate-level records plus unit records and introduces shared resource buttons. One drawing/config resource can be assigned to multiple units, which is intended for paired gates that share one electrical print.

Shared data remains under `Q:\SafetyGateSupport` by default. Existing V5 shared data is migrated non-destructively on first V6 startup: old system/unit folders are preserved and the app adds `gates.json`, `resources.json`, `manuals.json`, and `troubleshooting.json` indexes.

Hidden Setup Mode: `Ctrl + Shift + Alt + A`.

V6 Setup Mode can edit gate/unit hierarchy, create arbitrary resource buttons, link one resource to multiple units, build guided troubleshooting trees, assign manuals and parts to gates, and view audit history.

Normal content changes do not require rebuilding the EXE. A new EXE is only required when application code/UI changes.


### Distribution note
This package intentionally omits self-updating code. Use a single master EXE on the shared Q: drive (or a company-approved signed deployment method) rather than having the application replace its own executable.


### Clipboard photo paste
In Setup Mode, identification photos can be selected from a file, pasted from the Windows clipboard with the Paste Image button or Ctrl+V while the preview is focused, or dragged onto the preview. The candidate image is only copied to the shared Q-drive data after Save Unit is clicked.
