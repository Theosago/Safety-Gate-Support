# Safety Gate Support — Windows quick start

## Upgrade

1. Download **SafetyGateSupport-Windows-x64** from the successful GitHub Actions run for the version you want. Extract the **whole ZIP** to a new folder on the PC.
2. Close the older app and run **SafetyGateSupport.exe**. No SDK, installer, or Siemens Configurator is required for the built-in light tool.
3. Check the version in the sidebar. The app reuses the existing local settings and shared library.

On a new PC, choose **Set up this plant** or press **Ctrl+Shift+Alt+A**. Setup opens
**Library & software**. Choose **Connect** for an existing library, or select an
empty folder and create a blank plant with its first area. Then add assets and
units under **Units & assets**. A new asset starts as a single **whole asset**;
add numbered units only where the asset has more than one. A local folder works for a pilot; other PCs need a shared location
for the same library. Huntingdon sample data is an optional training choice, not
the new-plant default. See [the guided setup walkthrough](CHANGELOG.md#841--a-guided-first-plant-pilot).
Local settings live at `%LOCALAPPDATA%\SafetyGateSupport\settings.json`.

## View MaintainX work orders

Open a unit and choose **MaintainX work orders**. In **MaintainX settings**,
enter the ID of the corresponding asset in MaintainX and a plant API key
that can read assets and work orders. Choose **Connect and load**, then verify
the asset name. Ask your MaintainX administrator for the key and asset ID.

The shared library remembers the unit’s asset link. Every Windows user needs
their own approved key, which Windows encrypts on that PC. Use **Refresh** to
update the list. If two units use the same asset record in MaintainX, link both
units to that asset ID. The list includes open, in-progress, and on-hold work
orders for the linked asset. Create and manage work orders in MaintainX.

## Import the supplied support files

From **Manuals → Import documents…**, select the PDFs and Word files together.
The app copies the originals to the connected shared library. Coworkers can
find them in **Manuals** or the search bar at the top of the app by their titles.

1. Open **Manuals & files** and choose **Software & configuration files → Import files / ZIPs**. Select the original manuals, parts PDFs, troubleshooting DOCX, **Schematics and Light Config - Copy.zip**, **Omni Sensors - Copy.zip**, and **BusLink 2 M2 - Copy.zip** from the PC. Multiple selections are supported.
2. Wait for the import summary. Exact duplicate sources are recognized by SHA-256, even if renamed. Different revisions are retained separately. The large schematics ZIP can be imported on the PC despite the cloud attachment transfer limit.
3. Search the imported filenames for the asset/unit. Select the intended `.signal`, `.hux`, or PDF drawing, select the **exact unit** below the list, and click **Assign to selected unit**. Check the source drawing/file label before confirming. No assignment is inferred from inconsistent filenames. Files for paired units require an explicit assignment to each unit.
4. Return to Units & files and open that unit's imported resource. Existing resources remain available. Replacing an imported assignment archives its previous file.

Imports are stored under `<shared folder>\Library\Imports`, including the original source and a manifest; portable software folders retain DLLs and resources. The importer accepts up to 2 GiB per source ZIP, 30,000 entries, 512 MiB per expanded file and 4 GiB total expansion. It rejects unsafe paths, encrypted entries, symlinks, duplicate paths, and CRC errors. Failed/canceled imports are discarded; completed earlier imports remain.

Standalone PDF/DOCX imports become searchable manuals. ZIP documents are available from Software & configuration files; assign unit drawings explicitly. Search works on filenames/source names, not document contents. The provided Omni archive contains nine `.hux` scanner configurations and one report PDF, **not** UAM Project Designer. Vendor programs are never launched by importing or selecting a file.

## Check or configure a signal column

1. Open the unit and choose **Light column**. The tab shows three steps.
2. **Connect the column.** Isolate the equipment for servicing, close Siemens Configurator, and connect the Siemens **8WD4613-5JH37** with a USB data cable. The tab shows **✓ Column found**; choose **Look again** after plugging in. A column picker appears only when more than one column is connected. Finding a column does not open USB.
3. **Choose the configuration.** If one `.signal` file is saved for the unit it is chosen for you; with several, pick the right one. **Compare with the column** reads the column (saving a backup) and shows **✓ Stored settings match the selected file** or that the column has different settings.
4. **Upload to column.** Confirm, and the tool backs up the current settings, uploads, restarts the column and verifies the stored settings. Upload requires variant 21, firmware **v1.0.3**, and a validated schema-5, nine-segment sound configuration (file limit 1 MiB). Reports and backups are saved automatically to `%LOCALAPPDATA%\SiemensUsbCheck\Reports`.
5. **All states** lists every configured input combination with its segment colors, blink/flash/rotate effects and sound. Click a row, or use **Try inputs**, to see one state in detail and record whether the real column matches it.
6. **Configuration sheet** shows the PDF saved with the configuration. To save a new configuration, choose **Upload configuration pair…** and select the `.signal` file and its PDF together. If a configuration is already shown, the app asks whether this is a new revision of it (the old `.signal` and PDF are archived and every unit using it gets the new one) or a different configuration for this unit. **Manage saved files…** covers renaming, archiving and which units use each file.

USB read/upload has succeeded on the user's hardware, including a later readback and confirmed physical behavior. Automated regressions cover transport, backup, verification and desktop integration. The new integrated UI and local-library workflow still require an initial check on a disconnected spare at the site; cloud tests cannot inspect field wiring, supply, or physical outputs.

## Scanners, actuators, and parts

Guided troubleshooting includes separate **BEA LZR-S600**, **UAM scanner**, and **actuator** paths, with source-document titles/pages. Check the fitted model first. These guides do not transfer safety-field settings or certify a complete installation.

- **BusLink:** import the complete ZIP, search `BusLink2.exe`, then **Open selected** and confirm. Its working directory is the executable folder so adjacent dependencies can load. Follow **I.C. Actuator Programing Guide** with the dedicated programming cable and correct Gate/Arm program. **Launch BusLink** can also select an installed executable and remember it on this PC.
- **UAM:** use **Launch UAM** to select the installed Project Designer executable. Keep scanner configs assigned to the exact unit. Replacement/field changes use the actual model's documented transfer and functional validation procedure.
- **BEA:** the supplied `ScannerSafe2Cross.pdf` identifies the **LZR-S600**. Its LED diagnosis is separate from UAM guidance. Import/open the full manual for setup and limits.
- **Parts:** imported parts lists are available as manuals. Existing Parts records remain intact. The documents contain differing actuator stroke/part descriptions; confirm the fitted component and current stockroom record before ordering.

Selecting a unit automatically displays its assigned current electrical drawing. PDFs render inline with page controls; image drawings also display. If several drawings are assigned, choose another from the drawing selector. **Enlarge drawing** opens the current page larger; **Open original** uses the installed file viewer. DWG/DXF require their installed CAD viewer. Shared notes are always open beside the drawing and continue to auto-save.

The original manuals, plant configurations, and vendor binaries are imported from your PC; they are not bundled into the public source or Windows artifact. Programs requiring a Windows driver or installer still need that vendor setup on each PC.

## Hokuyo scanner diagnostics

Open a unit's UAM scanner resource and choose **Check UAM scanner…**. Version 8.4.1 includes a read-only USB diagnostic prototype and offline front-display error-code lookup. The first USB report must be compared on the spare scanner before the live interpretation can be considered validated. See [Hokuyo USB diagnostics](HOKUYO_USB_DIAGNOSTICS.md). No scanner programming controls are included.

## PLC information and files

Each unit page has a **PLC** button for its asset. Model, address/IP and notes are shared across that asset’s units. Enter them in **Plant setup → Units & assets → Asset details → Manufacturer, search terms and PLC details**, then **Save asset**. **Open PLC files** shows that asset’s current files and supports archived same-name replacements. This page records connection details; it does not connect to or program the PLC.

## Scanner shortcut and Setup photos

**Hokuyo scanner · USB status** now opens diagnostics directly from the selected unit. The read-only USB function and display-code lookup are retained. **Troubleshoot** includes the model-specific scanner guides, including BEA.

In Setup, select a gate and unit to see the saved photo at the top of the editor. Photo selection, paste and drag/drop remain available. Missing/unreadable files show an explanation rather than an empty preview. Asset/PLC settings expand below the photo.

Normal builds publish the standard Windows app. Home-preview packages and screenshot artifacts are not generated automatically.
