Run on Windows with the pinned .NET SDK:

```powershell
dotnet run --project tests/SafetyGateSupport.WindowsValidation/SafetyGateSupport.WindowsValidation.csproj --configuration Release
```

This executable runs eleven checks using the production WPF windows and Windows image
codecs. It checks asynchronous inventory startup, asset search, rapid photo selection
and clearing in all three photo views, guided troubleshooting navigation, bounded
image dimensions, frozen cross-thread image access, file release, replacement pixels,
missing/corrupt images, cancellation, and preserving searchable notes when a malformed
managed-resource folder interrupts file enumeration.

All settings and shared data live in newly created temporary directories. The runner
never loads the user's local settings or the default shared drive. Windows are shown
offscreen without activation. Each check has a 25-second deadline and the process
has a three-minute watchdog for unexpected dispatcher or modal-dialog hangs.

Success requires `Windows validation: 11/11 passed; 0 failed.` and exit code 0.
Failures exit nonzero and retain the isolated data directory named in the console
output. A Linux cross-build verifies compilation; execution requires Windows.
