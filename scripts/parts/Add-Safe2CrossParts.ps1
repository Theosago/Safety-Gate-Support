param([string]$SharedRoot)
$ErrorActionPreference = 'Stop'
$lease = $null
$temp = $null
$backup = $null
try {
    if ([string]::IsNullOrWhiteSpace($SharedRoot)) {
        $settingsFile = Join-Path $env:LOCALAPPDATA 'SafetyGateSupport\settings.json'
        if (Test-Path -LiteralPath $settingsFile) {
            $settings = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $SharedRoot = $settings.sharedRoot
        }
    }
    if ([string]::IsNullOrWhiteSpace($SharedRoot)) {
        throw 'The app shared-folder setting was not found. Open Safety Gate Support and configure its shared folder first, or run this script with -SharedRoot followed by that folder.'
    }
    $SharedRoot = [IO.Path]::GetFullPath($SharedRoot)
    $partsFile = Join-Path $SharedRoot 'Data\parts.json'
    if (-not (Test-Path -LiteralPath $partsFile -PathType Leaf)) { throw "Parts file not found: $partsFile. No files were changed." }
    Write-Host "Parts file: $partsFile"
    $lockFolder = Join-Path $SharedRoot '_AppData'
    [void][IO.Directory]::CreateDirectory($lockFolder)
    try { $lease = [IO.File]::Open((Join-Path $lockFolder 'asset-files.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another update is running, or the shared folder is not writable. Close the app and retry after checking drive access.' }
    $before = [IO.File]::ReadAllBytes($partsFile)
    $text = [Text.Encoding]::UTF8.GetString($before).TrimStart([char]0xFEFF)
    if (-not $text.TrimStart().StartsWith('[')) { throw 'parts.json is not a JSON list. It has not been overwritten.' }
    $existing = @($text | ConvertFrom-Json)
    $catalog = @'
[
  {
    "id": "site-safe2cross-0115639",
    "partNumber": "0115639",
    "description": "TL70 yellow light module, 12–30 VDC — SG-TL70-Y",
    "manufacturer": "Banner",
    "keywords": "Safe2Cross safety gate SG-TL70-Y TOP K-12",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: SG-TL70-Y\nBin location: TOP K-12\nReported quantity: 5.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0115638",
    "partNumber": "0115638",
    "description": "TL70 red light module, 12–30 VDC — SG-TL70-R",
    "manufacturer": "Banner",
    "keywords": "Safe2Cross safety gate SG-TL70-R TOP K-12",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: SG-TL70-R\nBin location: TOP K-12\nReported quantity: 4.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0115641",
    "partNumber": "0115641",
    "description": "TL70 black audible alarm module, 12–30 VDC — SG-TL70-A",
    "manufacturer": "Banner",
    "keywords": "Safe2Cross safety gate SG-TL70-A TOP K-12",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: SG-TL70-A\nBin location: TOP K-12\nReported quantity: 4.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0115640",
    "partNumber": "0115640",
    "description": "TL70 green light module, 12–30 VDC — SG-TL70-G",
    "manufacturer": "Banner",
    "keywords": "Safe2Cross safety gate SG-TL70-G TOP K-12",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: SG-TL70-G\nBin location: TOP K-12\nReported quantity: 3.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0115642",
    "partNumber": "0115642",
    "description": "TL70 black light-stack base module, 12–30 VDC — B-TL70-8",
    "manufacturer": "Banner",
    "keywords": "Safe2Cross safety gate B-TL70-8 TOP K-12",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: B-TL70-8\nBin location: TOP K-12\nReported quantity: 3.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117369",
    "partNumber": "0117369",
    "description": "Proximity switch, 24 V, 12 mm — AMI-AP-4A",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate AMI-AP-4A TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: AMI-AP-4A\nBin location: TOP T-05\nReported quantity: 2.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117488",
    "partNumber": "0117488",
    "description": "Hydraulic gate closer — CRL7072",
    "manufacturer": "CRL",
    "keywords": "Safe2Cross safety gate CRL7072 TOP T-04",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: CRL7072\nBin location: TOP T-04\nReported quantity: 2.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117489",
    "partNumber": "0117489",
    "description": "Linear arm actuator — 361610-03",
    "manufacturer": "Linak",
    "keywords": "Safe2Cross safety gate 361610-03 TOP T-04",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: 361610-03\nBin location: TOP T-04\nReported quantity: 2.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117490",
    "partNumber": "0117490",
    "description": "Linear arm actuator — 361609-02",
    "manufacturer": "Linak",
    "keywords": "Safe2Cross safety gate 361609-02 TOP T-04",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: 361609-02\nBin location: TOP T-04\nReported quantity: 2.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0116820",
    "partNumber": "0116820",
    "description": "Electronically configurable signal column, M12 8-pin — 8WD46135JH37",
    "manufacturer": "Siemens",
    "keywords": "Safe2Cross safety gate 8WD46135JH37 TOP U-05 8WD4613-5JH37",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: 8WD46135JH37\nBin location: TOP U-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM\nSupplied reference: Safe2Cross gates, 903 Scrapfeed."
  },
  {
    "id": "site-safe2cross-0117711",
    "partNumber": "0117711",
    "description": "Logic push switch — S2C-LOGIC SWITCH",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate S2C-LOGIC SWITCH TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: S2C-LOGIC SWITCH\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117712",
    "partNumber": "0117712",
    "description": "USB programming interface cable with converter — USB2LIN06",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate USB2LIN06 TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: USB2LIN06\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117713",
    "partNumber": "0117713",
    "description": "Electromagnetic interlock — E-942FC-1300",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate E-942FC-1300 TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: E-942FC-1300\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117714",
    "partNumber": "0117714",
    "description": "Anvil mount assembly — ANVIL MOUNT ASSEMBLY",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate ANVIL MOUNT ASSEMBLY TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: ANVIL MOUNT ASSEMBLY\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117715",
    "partNumber": "0117715",
    "description": "5-degree pivot plate — 22-MAG-1003",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate 22-MAG-1003 TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: 22-MAG-1003\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: HRM"
  },
  {
    "id": "site-safe2cross-0117716",
    "partNumber": "0117716",
    "description": "Interlock mount — 20-MAG-0009",
    "manufacturer": "",
    "keywords": "Safe2Cross safety gate 20-MAG-0009 TOP T-05",
    "relatedAssets": "Safe2Cross gates",
    "notes": "Manufacturer/reference P/N: 20-MAG-0009\nBin location: TOP T-05\nReported quantity: 1.00 (supplied stock snapshot; check storeroom for current availability).\nSite: Not supplied"
  }
]
'@ | ConvertFrom-Json
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $stocks = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($part in $existing) {
        if ($null -eq $part) { throw 'parts.json contains an empty entry. It has not been overwritten.' }
        [void]$ids.Add([string]$part.id)
        [void]$stocks.Add(([string]$part.partNumber).Trim().TrimStart('0'))
    }
    $added = @($catalog | Where-Object { -not $ids.Contains([string]$_.id) -and $stocks.Add(([string]$_.partNumber).Trim().TrimStart('0')) })
    if ($added.Count -eq 0) { Write-Host 'All supplied stock numbers are already present. No changes needed.' -ForegroundColor Green; exit 0 }
    $backups = Join-Path $SharedRoot 'Data\Backups'
    [void][IO.Directory]::CreateDirectory($backups)
    $backup = Join-Path $backups ('parts_before_storeroom_' + (Get-Date -Format 'yyyyMMdd_HHmmssfff') + '_' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.json')
    [IO.File]::WriteAllBytes($backup, $before)
    $temp = $partsFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $merged = @($existing) + @($added)
    $json = ConvertTo-Json -InputObject $merged -Depth 100
    [IO.File]::WriteAllText($temp, $json, (New-Object Text.UTF8Encoding($false)))
    # Check an older app has not changed the catalog since we read it.
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $originalHash = [Convert]::ToBase64String($sha.ComputeHash($before))
        $currentHash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($partsFile)))
    } finally { $sha.Dispose() }
    if ($originalHash -ne $currentHash) { throw 'The parts file changed during this update. Close other app copies and retry. The newer file has not been replaced.' }
    # Atomically publish on the same volume; never delete the original as a fallback.
    [IO.File]::Replace($temp, $partsFile, $null, $true)
    $temp = $null
    Write-Host ("Added {0} missing parts; existing records were retained." -f $added.Count) -ForegroundColor Green
    Write-Host "Backup: $backup"
    Write-Host 'Reopen Safety Gate Support to see the parts. No app upgrade is required.'
    exit 0
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if ($backup) { Write-Host "Original backup: $backup" }
    exit 1
} finally {
    if ($temp -and [IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    if ($lease) { $lease.Dispose() }
}
