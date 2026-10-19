# SafetyGateSupport V6.0 Foundation

V6 normalizes the shared data into gate-level records plus unit records and introduces shared resource buttons. One drawing/config resource can be assigned to multiple units, which is intended for paired gates that share one electrical print.

Shared data remains under `Q:\SafetyGateSupport` by default. Existing V5 shared data is migrated non-destructively on first V6 startup: old system/unit folders are preserved and the app adds `gates.json`, `resources.json`, `manuals.json`, and `troubleshooting.json` indexes.

Hidden Setup Mode: `Ctrl + Shift + Alt + A`.

V6 Setup Mode can edit gate/unit hierarchy, create arbitrary resource buttons, link one resource to multiple units, build guided troubleshooting trees, assign manuals and parts to gates, and view audit history.

Normal content changes do not require rebuilding the EXE. A new EXE is only required when application code/UI changes.


## Distribution note
This package intentionally omits self-updating code. Use a single master EXE on the shared Q: drive (or a company-approved signed deployment method) rather than having the application replace its own executable.


## Clipboard photo paste
In Setup Mode, identification photos can be selected from a file, pasted from the Windows clipboard with the Paste Image button or Ctrl+V while the preview is focused, or dragged onto the preview. The candidate image is only copied to the shared Q-drive data after Save Unit is clicked.
