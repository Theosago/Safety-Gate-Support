# Remaining Windows desktop validation

Linux checks cover compilation, packaging and data/search workflows. The GitHub
workflow also runs native automated WPF/codec checks before uploading a package.
The manual checks below are still useful on the target Windows PC, especially
clipboard, shared-drive and plant-specific integrations. Use a disposable local
data folder for a first-time test.

1. Start `SafetyGateSupport.exe` or run the project. Open Setup Mode with
   **Ctrl+Shift+Alt+A**, then select **Shared Data / Software**, choose an empty local folder,
   and click **Initialize Starter Data**. Confirm the connection status updates
   and the three seeded areas can be browsed.
2. Select an area, gate, and unit in Systems. Confirm its identification photo
   and unit details appear. Search for a known asset from Home and open a result.
3. Open **Troubleshoot a System** from Home. Confirm Start Troubleshooting is
   disabled until a unit is selected. Check its preview photo/details. Change
   area or gate and confirm the previous unit/photo clears. Double-clicking a
   unit should not launch troubleshooting; the Start button should.
4. In Setup Mode, add a temporary unit without a photo and confirm the picker
   shows the missing-photo message. Assign a photo, replace it, and confirm the
   picker displays the replacement. Test clipboard paste and image drag/drop,
   saving the unit each time. Check image files are not left locked.
5. Edit a unit's shared notes, switch units, close/reopen the app, and confirm the
   saved text remains attached to the correct unit.
6. Add a temporary troubleshooting flow with two choices, a terminal step, and a
   resource link. Confirm selection, forward navigation, Back, and Restart work.
7. Upload a drawing to a temporary resource and replace it. Confirm the new
   document opens and the previous version exists in Archive. Link multiple
   manuals and parts and check their normal browsing pages.
8. If the required plant services and vendor applications are available, test
   storeroom lookup and Siemens/UAM launch actions separately. Record unavailable
   integrations as untested. Cloud validation did not contact these services.

Record Windows version, build configuration, and outcomes. See
[KNOWN_ISSUES.md](KNOWN_ISSUES.md) for remaining source-review findings before interpreting failures. The previously
identified partial-migration defect is fixed and covered by standard tests.
