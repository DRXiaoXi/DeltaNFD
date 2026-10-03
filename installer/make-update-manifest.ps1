#Requires -Version 5.1
<#
  Delta NFD - generate the auto-update manifest (update.json).

  Usage (from anywhere; the script only needs an absolute or relative installer path):

    powershell -ExecutionPolicy Bypass -File installer\make-update-manifest.ps1 `
      -InstallerPath installer\Output\三角帧不掉洲_DeltaNFD_安装包_0.90.0_x64.exe `
      -Version 0.90.0 `
      -DisplayVersion "Beta0.9" `
      -InstallerUrl "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.90.0/<asset>.exe" `
      -NotesFile notes\0.90.0.txt

  Optional:
    -DisplayVersion       display text; pass "Beta0.9" for version 0.90.0
    -ReleaseTag           default "v<Version>"
    -ReleasePageUrl       default the GitHub release tag page
    -MinimumSupportedVersion  clients below this version are forced to update
    -Mandatory            force the update even if the user skipped that version
    -MirrorUrl            one or more extra https download urls (same bytes, same hash)
    -OutFile              default: <repo root>\update.json

  IMPORTANT (project rule, see HANDOFF section 11.2): keep this script body pure ASCII.
  Chinese changelog text must be passed through -NotesFile, never inlined here.

  Mirror hosts are rejected by the client at run time unless they are listed in
  src\DeltaNFD\Assets\UpdateConfig.json -> allowedHosts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$InstallerUrl,
    [string]$DisplayVersion = '',
    [string]$ReleaseTag = '',
    [string]$ReleasePageUrl = '',
    [string]$MinimumSupportedVersion = '',
    [string[]]$MirrorUrl = @(),
    [string]$NotesFile = '',
    [switch]$Mandatory,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer not found: $InstallerPath"
}

if (-not $InstallerUrl.StartsWith('https://')) {
    throw "InstallerUrl must be https: $InstallerUrl"
}

foreach ($mirror in $MirrorUrl) {
    if (-not $mirror.StartsWith('https://')) {
        throw "MirrorUrl must be https: $mirror"
    }
}

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "Version must be numeric, for example 0.90.0 (got: $Version)"
}

$installer = Get-Item -LiteralPath $InstallerPath
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$size = $installer.Length

if ([string]::IsNullOrWhiteSpace($DisplayVersion)) {
    # Project naming convention: 0.82.0 -> OpenAlphaV0.82, 0.80.3 -> OpenAlphaV0.803, 0.79.10 -> OpenAlphaV0.7910
    $parts = $Version.Split('.')
    $displayNumber = $parts[1]
    if ($parts.Length -ge 3 -and [int]$parts[2] -gt 0) { $displayNumber += $parts[2] }
    $DisplayVersion = "OpenAlphaV0.$displayNumber"
}
if ([string]::IsNullOrWhiteSpace($ReleaseTag)) { $ReleaseTag = "v$Version" }
if ([string]::IsNullOrWhiteSpace($ReleasePageUrl)) {
    $ReleasePageUrl = "https://github.com/DRXiaoXi/DeltaNFD/releases/tag/$ReleaseTag"
}

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutFile = Join-Path $repoRoot 'update.json'
}

$notes = ''
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    $resolvedNotes = Resolve-Path -LiteralPath $NotesFile
    $notes = [System.IO.File]::ReadAllText($resolvedNotes.Path, [System.Text.Encoding]::UTF8)
    $notes = $notes.TrimEnd("`r", "`n")
}

$installers = New-Object System.Collections.Generic.List[object]
$installers.Add([ordered]@{ url = $InstallerUrl; sizeBytes = $size; sha256 = $hash })
foreach ($mirror in $MirrorUrl) {
    $installers.Add([ordered]@{ url = $mirror; sizeBytes = $size; sha256 = $hash })
}

$manifest = [ordered]@{
    schemaVersion           = 1
    version                 = $Version
    displayVersion          = $DisplayVersion
    channel                 = $(if ($DisplayVersion -match '^Beta') { 'beta' } else { 'alpha' })
    mandatory               = [bool]$Mandatory
    minimumSupportedVersion = $MinimumSupportedVersion
    publishedAtUtc          = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    releasePageUrl          = $ReleasePageUrl
    notes                   = $notes
    installers              = $installers
}

$json = $manifest | ConvertTo-Json -Depth 6

$fullOut = [System.IO.Path]::GetFullPath($OutFile)
$outDir = Split-Path -Parent $fullOut
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# UTF-8 without BOM: the client parses it with System.Text.Json after File.ReadAllText
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($fullOut, $json + "`n", $utf8NoBom)

# Guard against the classic PowerShell single-element-array unwrapping surprise
# Read with explicit UTF-8: PowerShell 5.1 Get-Content defaults to ANSI for BOM-less files,
# which corrupts the Chinese notes and makes ConvertFrom-Json fail on the escaped sequence.
$verify = [System.IO.File]::ReadAllText($fullOut, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
if ($verify.installers -isnot [array]) {
    throw "Generated manifest has a non-array 'installers' field - check the urls passed in."
}
if ($verify.installers.Count -lt 1) {
    throw "Generated manifest has no installer entries."
}
foreach ($entry in $verify.installers) {
    if ($entry.sha256.Length -ne 64) {
        throw "Generated manifest has an invalid sha256 for $($entry.url)"
    }
    if ($entry.sizeBytes -ne $size) {
        throw "Generated manifest has an unexpected sizeBytes for $($entry.url)"
    }
}

Write-Host ""
Write-Host "update.json written: $fullOut"
Write-Host "  version        : $Version ($DisplayVersion)"
Write-Host "  mandatory      : $([bool]$Mandatory)"
Write-Host "  min supported  : $(if ([string]::IsNullOrWhiteSpace($MinimumSupportedVersion)) { '(none)' } else { $MinimumSupportedVersion })"
Write-Host "  installer url  : $InstallerUrl"
Write-Host "  size / sha256  : $size / $hash"
foreach ($mirror in $MirrorUrl) {
    Write-Host "  mirror url     : $mirror"
}
if ($notes.Length -eq 0) {
    Write-Host "  notes          : (empty - pass -NotesFile to ship a changelog)"
}
Write-Host ""
Write-Host "Publish checklist:"
Write-Host "  1. Upload the installer to the GitHub Release for tag $ReleaseTag"
Write-Host "  2. Commit and push update.json to the main branch (this is the url clients read)"
Write-Host "     https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/update.json"
Write-Host "  3. Optionally attach the same update.json to the release as a fixed asset name"
Write-Host "  4. Verify the live path: dotnet run --project tools\BackendSmokeTest -- --update-live"
Write-Host ""
Write-Host "Notes:"
Write-Host "  * Clients only update when the manifest version is HIGHER than their own version."
Write-Host "  * Clients on a build without the updater (0.82.0 or older) need one manual install first."
Write-Host "  * Every mirror host must be added to src\DeltaNFD\Assets\UpdateConfig.json -> allowedHosts,"
Write-Host "    otherwise the client refuses to download from it."
Write-Host "  * The client always verifies sha256 + size before running the installer."
