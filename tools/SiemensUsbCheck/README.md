# Siemens USB Check — 0.1.0

This standalone Windows test tool reads a Siemens **8WD4613-5JH37** light column
over USB and saves a diagnostic ZIP, including the original configuration backup
when the read succeeds. It sends only identity/configuration GET requests. It has
no upload, reset, or firmware commands. It does not require the Siemens
Configurator application or a separately installed .NET runtime.

The independent USB client needs validation on real hardware. Use your spare
column disconnected from operating equipment: opening the COM port can toggle
DTR/RTS and restart the column even though the requests only read data.

1. Extract the entire downloaded ZIP to a folder on your Windows PC.
2. Close Siemens Configurator and any other program using the column's COM port.
3. Connect the spare column using a USB data cable. Follow the manufacturer's
   normal power/connection instructions.
4. Run **SiemensUsbCheck.exe**. Click **Refresh** if needed and select the column.
5. Click **Read device and save report**, then choose where to save the report ZIP.
6. Send that report ZIP back for analysis, whether the check succeeds or fails.

The tool identifies the normal USB interface (VID 303A, PID 80F7) and verifies the
reported article number and hardware variant before requesting configuration.
Other models and the bootloader interface are excluded from backup. If Windows
does not enumerate a COM port, check Device Manager and use the manufacturer's
driver instructions; the tool does not install drivers or try arbitrary ports.

The report contains device identification, firmware/version fields, serial-port
details, HTTP response headers/statuses, and the configuration or diagnostic
response bytes. It does not collect your Safety Gate Support shared data or PC
credentials. Review it before sharing. A successful backup is stored as
`column-backup.signal` inside the report; unsuccessful replies use
`config-response.bin` and/or a bounded `received-prefix.bin`.

Cancel is cooperative: a serial driver write can take up to ten seconds to return.
Reads time out after ten seconds without incoming bytes; the complete USB session
also has a sixty-second deadline. There are no automatic retries.

The tool is distributed separately from SafetyGateSupport.exe. It does not edit
the main application's settings or shared folder. Windows builds are currently
unsigned; no publisher signature is supplied.
