# Safety Gate Support

Windows WPF support console for plant safety gates, version **8.7.0**.

Engineers and electricians pick a unit from a searchable photo gallery and get
its identification photo, current electrical drawing, shared notes, replacement
parts, manuals, configuration files and guided troubleshooting in one place. The
engineering library lives on a shared folder (normally the plant's Q: drive).

Main features:

- **One search bar** — at the top of every page. Typing filters the unit photos
  and lists matching drawings, manuals, parts, notes and help topics; click a
  match to open it. Ctrl+F focuses search from anywhere and Esc clears it.
- **Drawings** — upload a PDF/DWG pair once, choose the units it covers, and
  replace it from the unit page. Prior revisions are archived on the shared drive.
- **Publish & print QR** — publishes unit guides and electrical PDFs to Google
  Drive folders, keeps them updated after drawing changes, optionally syncs
  shared notes with phones, and prints every unit's QR code in one PDF. See the
  [Drive publishing guide](docs/GOOGLE_DRIVE_PILOT.md).
- **Parts and stockroom** — replacement parts per unit with live stockroom
  availability, Item ID import, and dated spare-parts PDFs for the QR folders.
- **MaintainX** — read-only view of open work orders for a unit's linked
  MaintainX asset. See [MaintainX setup](docs/WINDOWS_QUICK_START.md#view-maintainx-work-orders).
- **Equipment tools** — built-in Siemens signal-column USB backup, comparison
  and verified upload; read-only Hokuyo UAM USB diagnostics; PLC files.

Start with the [Windows quick start](docs/WINDOWS_QUICK_START.md). Release history
is in the [changelog](docs/CHANGELOG.md).

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
folder. Choose **Plant setup** in the sidebar or press **Ctrl+Alt+Shift+A**.
The **Start here** page connects an existing library or creates a blank plant in
an empty folder, then guides one pilot unit through photo, PDF and Common Problem
setup. A local folder works for the pilot. Huntingdon sample data is an explicit
training option under Library & software.
Local settings live in `%LOCALAPPDATA%\SafetyGateSupport\settings.json`.

Production use requires access to the configured shared folder. Built-in Siemens
USB configuration needs no vendor configurator. Optional Siemens/UAM/BusLink
launch actions require their Windows applications; each PC saves its own
executable paths. The stockroom lookup requires the configured plant-network
service.

For a standalone Siemens 8WD4613-5JH37 USB utility, download
**SiemensUsbCheck-Windows-x64** from a successful workflow run. It backs up a
spare column and uploads compatible `.signal` files with a saved original and
readback verification. Follow [its instructions](tools/SiemensUsbCheck/README.md)
and see [protocol evidence and validation limits](docs/SIEMENS_USB.md).

Hokuyo UAM-05LP-T301 diagnostics are read-only; see
[USB diagnostics and current limits](docs/HOKUYO_USB_DIAGNOSTICS.md). No scanner
programming controls are provided.

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

## Validate on Linux

```bash
bash scripts/bootstrap-dotnet.sh
bash scripts/validate.sh
```

The bootstrap verifies Microsoft's published SHA512 checksum and installs the
pinned SDK outside the checkout (`../.tools/dotnet`). CLI/NuGet caches also stay
outside the checkout. Source `scripts/dotnet-env.sh` to activate the SDK in a shell.

Validation cross-compiles the WPF projects, runs the Siemens protocol/archive
checks, the data/search/publishing checks, and the Hokuyo checks. Tests use
temporary settings and data and never touch the configured shared library.
Linux compilation alone does not validate WPF behavior; desktop execution needs
Windows. For the optional CPU/allocation comparison against the original search
logic, run `bash scripts/validate.sh --benchmark-search`.

See [remaining known issues](docs/KNOWN_ISSUES.md).

## Build a self-contained Windows executable locally

On Linux:

```bash
bash scripts/publish-windows.sh
```

Or on Windows:

```powershell
dotnet publish .\SafetyGateSupport\SafetyGateSupport.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64
```

Output: `artifacts/win-x64/SafetyGateSupport.exe`.
