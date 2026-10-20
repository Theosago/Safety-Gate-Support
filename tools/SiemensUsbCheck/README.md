# Siemens USB Configuration — 0.2.1

Upload a `.signal` file to a Siemens **8WD4613-5JH37** over USB without opening
Siemens Configurator. The standalone `SiemensUsbCheck.exe` includes its .NET
runtime. It also retains the Read device and save report action.

USB identity and backup reads have succeeded on two columns. One column accepted
an upload and returned it in a later independent readback. Version 0.2.1 fixes report-saving failures that
could interrupt same-session reconnect verification; that recovery still needs
hardware retesting. Use your spare column disconnected from operating equipment.
Opening USB can restart the column; uploading replaces its settings and requests
a normal application restart. No firmware update or bootloader commands exist.

## Upload a configuration

1. Download `SiemensUsbCheck-Windows-x64` from the successful GitHub Actions run
   for version 0.2.1. Extract the complete ZIP into a new folder.
2. Close Siemens Configurator and other programs using the column. Connect the
   spare with a USB data cable, following the manufacturer's power instructions.
3. Run `SiemensUsbCheck.exe` and confirm **v0.2.1** in its title. Select the column;
   use Refresh if needed.
4. Click **Choose .signal file**, then select the actual configuration file.
   A report ZIP is not a configuration file. A backup named `column-backup.signal`
   extracted from an earlier report can also be selected to restore those settings.
5. Click **Upload selected file**. Choose a new report ZIP filename in a writable
   folder, then confirm the displayed target and file.
   Reports default to `%LOCALAPPDATA%\SiemensUsbCheck\Reports`. A local folder
   avoids shared-drive locks; you can copy the completed files to a share afterward.
6. Wait for verification. Keep the report and the separate `.before.signal` file
   beside it. Check the actual light and sound behavior on the spare.
7. Send back the upload report ZIP, whether it succeeds or fails.

The tool checks model, hardware variant 21, firmware v1.0.3, schema 5, nine
segments and sound support. The file limit is 1 MiB for this tool, not a claimed
firmware limit. Unknown formats and incompatible targets are rejected before
uploading. Detection selects normal USB VID 303A / PID 80F7; bootloaders are excluded.
Windows must already expose the serial COM port; the tool does not install drivers.

## Backup and verification

Before sending a PUT, the tool downloads and validates the existing configuration,
saves its exact bytes to **`<report-name>.before.signal`**, flushes that backup to
disk, and saves a report containing both the original and selected configuration.
Existing reports and backups are never overwritten. Failure to save either blocks
upload. If the selected settings already match, it saves the backup and skips
upload and restart.

Report snapshots retry transient file-access failures up to four times. Required
pre-write and pre-restart checkpoints still block device writes if they cannot
be saved. Once the restart has been sent, report-saving errors do not prevent the
remaining read-only verification. A permanently unwritable final report is shown
separately from whether the stored settings were verified. Reports record the
affected phase, operation and storage warnings.

For a changed configuration it sends the original selected ZIP bytes, waits for
a successful reply, then reads back all 65 JSON configuration files and compares
them with the selection. ZIP timestamps, compression and JSON property ordering
are ignored; actual settings are compared. It requests a normal application
restart, reconnects only to the original USB instance, checks the hardware address,
and verifies the settings again. A successful message means stored settings were
verified; it does not replace checking the actual physical outputs.

There is no automatic write retry or automatic rollback. Cancellation cannot undo
data already sent. A failed/lost reply, cancellation or verification mismatch means
the column may already have changed settings. Keep the backup and report. Restoring
is an explicit upload of the saved `.before.signal` file using the same checks;
first resolve any reported communication or compatibility failure.

Driver calls may take up to ten seconds to return; the session also uses a
120-second deadline. Upload reports contain selected/original/readback configurations,
device identification, response statuses and diagnostic bytes. Review before sharing.

The Read device and save report button remains read-only. This tool is separate
from SafetyGateSupport.exe and does not edit its shared data or settings. Windows
builds are unsigned; no publisher signature is supplied.
