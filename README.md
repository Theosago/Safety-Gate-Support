# Safety Gate Support

Windows WPF support console, version **6.4.2**, based on the supplied
`SafetyGateSupport_V6_4_1_TroubleshootPreview_FIXED.zip`.

The console provides gate/unit browsing, identification photos, shared notes,
drawings/configuration resources, manuals, parts, global search, and guided
troubleshooting. Version 6.4.2 improves startup, photo loading and search
responsiveness while retaining the existing shared-data format and workflow.

## Get the Windows app

1. Open [Build and validate Windows app](https://github.com/Theosago/Safety-Gate-Support/actions/workflows/validate.yml)
   on GitHub. Sign in with an account that can access this repository.
2. Select the latest successful run for the version you want. At the bottom of its
   summary, download **SafetyGateSupport-Windows-x64** under **Artifacts**.
3. Extract the ZIP into a folder on your Windows PC. Close any older running copy
   and run **SafetyGateSupport.exe** from the extracted folder.

The package includes the .NET runtime; users do not need to install the SDK.
The workflow uploads this artifact only after Linux data/search checks and native
Windows desktop checks succeed. Artifacts are retained for 30 days; use **Run
workflow** to create a fresh build when needed. The executable is not stored in Git.

An existing installation keeps using its local settings and configured shared
folder. For a first-time test, press **Ctrl+Shift+Alt+A**, open **Shared Data /
Software**, choose an empty local folder, and click **Initialize Starter Data**.
The default production path is `Q:\SafetyGateSupport`. Settings live in
`%LOCALAPPDATA%\SafetyGateSupport\settings.json`.

Production use requires access to the configured shared folder. Siemens/UAM
launch actions require their Windows applications and configured executable paths.
The storeroom lookup requires the configured plant-network service. Those live
integrations still need validation in the plant environment.

For the separate Siemens 8WD4613-5JH37 USB investigation, download
**SiemensUsbCheck-Windows-x64** from a successful workflow run. This diagnostic
reads identity and a configuration backup on a spare column; it does not upload
configuration. Follow [its instructions](tools/SiemensUsbCheck/README.md) and see
[protocol evidence and validation limits](docs/SIEMENS_USB.md).

## Develop and run on Windows

Install the .NET 10 SDK specified in [global.json](global.json). From the repository
root in PowerShell:

```powershell
dotnet build .\SafetyGateSupport\SafetyGateSupport.csproj -c Release
dotnet run --project .\SafetyGateSupport\SafetyGateSupport.csproj
```

Run native Windows regression checks with:

```powershell
dotnet run --project tests/SafetyGateSupport.WindowsValidation/SafetyGateSupport.WindowsValidation.csproj -c Release
```

They exercise actual WPF windows/codecs using isolated settings and shared data.
See [the runner documentation](tests/SafetyGateSupport.WindowsValidation/README.md)
and [remaining manual Windows checks](docs/WINDOWS_VALIDATION.md).

## Validate in the Linux cloud environment

```bash
bash scripts/bootstrap-dotnet.sh
bash scripts/validate.sh
```

The bootstrap verifies Microsoft's published SHA512 checksum and installs the
pinned SDK outside the checkout, at `/workspace/.tools/dotnet` in this environment.
CLI/NuGet caches also remain outside the checkout. Source `scripts/dotnet-env.sh`
to activate the SDK in a shell. Use the existing checkout; cloud tasks are already
isolated and do not need another Git worktree.

Validation cross-compiles both WPF projects, runs the Siemens protocol/archive
checks, and runs **26** data/search
checks. These cover seed initialization, migrations, repeat initialization,
assignments, notes, resource archives, photo storage/reselection, backups, audits,
troubleshooting persistence, cancellation, snapshots, and exact search equivalence
including 600 randomized comparisons. Tests use temporary settings/data. The
previously failing partial-migration regression now runs and passes by default.
The older `--include-known-issues` flag is accepted for compatibility.

For the optional CPU/allocation comparison against the original search logic:

```bash
bash scripts/validate.sh --benchmark-search
```

See [performance changes and measurement limits](docs/PERFORMANCE.md) and
[remaining known issues](docs/KNOWN_ISSUES.md). Desktop execution needs Windows;
Linux compilation alone does not validate WPF behavior.

## Build a self-contained Windows executable locally

On Linux:

```bash
bash scripts/publish-windows.sh
```

Or on Windows:

```powershell
dotnet publish .\SafetyGateSupport\SafetyGateSupport.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64
```

Output: `artifacts/win-x64/SafetyGateSupport.exe`. Initial data contains 60 units
in 28 gates across three areas, with identification photos. No data format change
or reinitialization is required to upgrade from version 6.4.1.
