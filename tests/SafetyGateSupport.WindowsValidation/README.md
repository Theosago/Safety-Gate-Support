Run on Windows with the pinned .NET SDK:

```powershell
dotnet run --project tests/SafetyGateSupport.WindowsValidation/SafetyGateSupport.WindowsValidation.csproj --configuration Release
```

This executable runs seventeen checks using the production WPF windows and Windows image
codecs. It checks asynchronous inventory startup, asset search, rapid photo selection
and clearing in all three photo views, guided troubleshooting navigation, bounded
image dimensions, frozen cross-thread image access, file release, replacement pixels,
missing/corrupt images, cancellation, and preserving searchable notes when a malformed
managed-resource folder interrupts file enumeration.

Six checks cover the separate Siemens USB tool: rendering its idle window with
upload disabled until a device and validated file are selected,
exact backup bytes and GET sequence, reading regardless of `firstConfig`, rejecting
a different model before requesting configuration, preserving HTTP errors without
retry, and cancellation before sending a request. They use scripted fragmented
streams and synthetic archives. No physical USB device is accessed; passive device
discovery during the window check only enumerates Windows registry/PnP data.
The dependency-free Siemens validation runner separately exercises the complete
upload/backup/reconnect session with strict simulated columns and disk failures.

All settings and shared data live in newly created temporary directories. The runner
never loads the user's local settings or the default shared drive. Windows are shown
offscreen without activation. Each check has a 25-second deadline and the process
has a three-minute watchdog for unexpected dispatcher or modal-dialog hangs.

Success requires `Windows validation: 17/17 passed; 0 failed.` and exit code 0.
Failures exit nonzero and retain the isolated data directory named in the console
output. A Linux cross-build verifies compilation; execution requires Windows.
