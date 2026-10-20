# Safety Gate Support 6.5.0 — Windows quick start

## Upgrade

1. Download **SafetyGateSupport-Windows-x64** from the successful GitHub Actions run for version 6.5.0. Extract the **whole ZIP** to a new folder on the PC.
2. Close the older app and run **SafetyGateSupport.exe**. No SDK, installer, or Siemens Configurator is required for the built-in light tool.
3. Confirm **v6.5.0** in the sidebar. Existing local settings, shared data, photos, and site troubleshooting edits remain in use. Keep the old executable folder until the new version has been checked at the site.

On a new PC, use **Set Up Shared Data** or **Ctrl+Shift+Alt+A → Shared Data / Software** to select the existing shared folder. Initialize starter data only when starting with an empty folder. The default shared folder is `Q:\SafetyGateSupport`; local settings live under `%LOCALAPPDATA%\SafetyGateSupport`.

## Import the supplied support files

1. Click **Support library → Import files / ZIPs**. Select the original manuals, parts PDFs, troubleshooting DOCX, **Schematics and Light Config - Copy.zip**, **Omni Sensors - Copy.zip**, and **BusLink 2 M2 - Copy.zip** from the PC. Multiple selections are supported.
2. Wait for the import summary. Exact duplicate sources are recognized by SHA-256, even if renamed. Different revisions are retained separately. The large schematics ZIP can be imported on the PC despite the cloud attachment transfer limit.
3. Search the imported filenames for the asset/unit. Select the intended `.signal`, `.hux`, or PDF drawing, select the **exact unit** below the list, and click **Assign to selected unit**. Check the source drawing/file label before confirming. No assignment is inferred from inconsistent filenames. Files for paired units require an explicit assignment to each unit.
4. Return to Systems and open that unit's imported resource. Existing resources remain available. Replacing an imported assignment archives its previous file.

Imports are stored under `<shared folder>\Library\Imports`, including the original source and a manifest; portable software folders retain DLLs and resources. The importer accepts up to 2 GiB per source ZIP, 30,000 entries, 512 MiB per expanded file and 4 GiB total expansion. It rejects unsafe paths, encrypted entries, symlinks, duplicate paths, and CRC errors. Failed/canceled imports are discarded; completed earlier imports remain.

Standalone PDF/DOCX imports become searchable manuals. ZIP documents are available from Support library; assign unit drawings explicitly. Search works on filenames/source names, not document contents. The provided Omni archive contains nine `.hux` scanner configurations and one report PDF, **not** UAM Project Designer. Vendor programs are never launched by importing or selecting a file.

## Check or configure a signal column

1. Select **Troubleshoot This System → Signal column**, then **Check stored settings / upload an approved file → Check / configure signal column**. The same action is available from a unit's Light Configuration resource.
2. Confirm the displayed asset/unit and isolate equipment for servicing before USB connection. Close Siemens Configurator. Connect the Siemens **8WD4613-5JH37** using a USB data cable, then select its detected port. Opening USB can restart it. Detection alone does not open it.
3. Explicitly select the unit's approved `.signal` file, or browse for an approved file. Multiple assigned files are not resolved automatically.
4. Click **Read device and save report**. The report contains `column-backup.signal`. A **MATCH** means the stored settings match the selected file; **DIFFERENT** means review the file assignment before deciding to change settings. Neither result measures physical function.
5. If needed, click **Upload selected file**, choose a **new local report filename**, and confirm the target/file. The tool saves a separate `.before.signal` backup before writing, checks the upload, restarts the column, reconnects to the same device, and verifies stored settings. Upload requires variant 21, firmware **v1.0.3**, and a validated schema-5, nine-segment sound configuration (file limit 1 MiB).
6. Keep the backup and report. Reports default to `%LOCALAPPDATA%\SiemensUsbCheck\Reports` to avoid share permissions/locks. If verification fails, settings may already have changed: read/compare before retrying. Restoration is an explicit upload of the saved `.before.signal`; there is no automatic rollback.
7. Expand **Expected output and physical observation**. Select the safely observed input state (input 1 = bit 0, input 6 = bit 5). Compare real light and sound against the stored segment/color/effect/sound values and record the observation. This preview does not force outputs or measure inputs. Check all applicable states using the approved site procedure before returning equipment to service.

USB read/upload has succeeded on the user's hardware, including a later readback and confirmed physical behavior. Automated regressions cover transport, backup, verification and desktop integration. The new integrated UI and local-library workflow still require an initial check on a disconnected spare at the site; cloud tests cannot inspect field wiring, supply, or physical outputs.

## Scanners, actuators, and parts

Guided troubleshooting includes separate **BEA LZR-S600**, **UAM scanner**, and **actuator** paths, with source-document titles/pages. Check the fitted model first. These guides do not transfer safety-field settings or certify a complete installation.

- **BusLink:** import the complete ZIP, search `BusLink2.exe`, then **Open selected** and confirm. Its working directory is the executable folder so adjacent dependencies can load. Follow **I.C. Actuator Programing Guide** with the dedicated programming cable and correct Gate/Arm program. **Launch BusLink** can also select an installed executable and remember it on this PC.
- **UAM:** use **Launch UAM** to select the installed Project Designer executable. Keep scanner configs assigned to the exact unit. Replacement/field changes use the actual model's documented transfer and functional validation procedure.
- **BEA:** the supplied `ScannerSafe2Cross.pdf` identifies the **LZR-S600**. Its LED diagnosis is separate from UAM guidance. Import/open the full manual for setup and limits.
- **Parts:** imported parts lists are available as manuals. Existing Parts records remain intact. The documents contain differing actuator stroke/part descriptions; confirm the fitted component and current stockroom record before ordering.

The original manuals, plant configurations, and vendor binaries are imported from your PC; they are not bundled into the public source or Windows artifact. Programs requiring a Windows driver or installer still need that vendor setup on each PC.
