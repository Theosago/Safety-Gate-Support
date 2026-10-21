# UAM software analysis pack collector — 0.1.0

This prepares a smaller upload from **UAM Project Designer.zip** so the USB diagnostics for **Hokuyo UAM-05LP-T301** can be investigated. It is a file collection utility, not a scanner status checker.

1. Extract the entire downloaded ZIP and open **UamSoftwarePack.exe**.
2. Click **Choose UAM Project Designer ZIP** and select the original large software ZIP on your PC.
3. Save the suggested **UAM-analysis-....zip** (Desktop is selected by default).
4. Wait for **DONE**, then upload that **UAM-analysis** ZIP in the existing chat. **Open output folder** selects it in Explorer.

The output stays below **30 MiB**. You do not need to connect the scanner, install anything, run Project Designer, or use an administrator account. The .NET runtime is included in the Windows x64 download.

The collector preserves the original ZIP. It copies selected main executables, vendor/USB libraries, INF files, diagnostic resources and eligible manuals into the output and inventories every filename/size. It checks copied bytes against ZIP CRCs and records SHA-256 hashes. Managed executable identification reads PE/.NET metadata without loading or executing the assemblies.

Framework components, large files and installers can be inventory-only to fit the budget. `.hux` projects, logs, databases, and files named as licenses/credentials are not selected. Review `inventory.json` and `pack.json` inside the output if desired; the inventory includes filenames for omitted files. No absolute source path is recorded.

If it reports **0 selected files**, send the resulting analysis ZIP anyway: the inventory identifies the installer/large file needed next. A software archive alone cannot establish the scanner's actual health or active error state.

This utility does not access USB, query or reset scanners, modify safety fields, run vendor binaries, or upload anything automatically.
