#Requires -Version 5.1
<#
  Delta NFD - pack an extension plugin project into a .dnfdplugin package.

  The host importer (spec section 2) requires a non-encrypted ZIP whose ROOT contains
  manifest.json, ui.json, README.md and LICENSE.txt, plus the declared backend entry
  under backend/. This script publishes the backend self-contained and assembles that
  layout, so plugin authors do not have to build the archive by hand.

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\pack-plugin.ps1 `
      -ProjectPath src\Plugin.WeakNetBoost

  Optional:
    -Configuration     default Release
    -OutputDirectory   default <repo root>\dist
    -SkipPublish       reuse the existing publish output instead of publishing again

  The plugin project must look like:
    <ProjectPath>\<name>.csproj
    <ProjectPath>\package\manifest.json     (backend.entry decides the exe name)
    <ProjectPath>\package\ui.json
    <ProjectPath>\package\README.md
    <ProjectPath>\package\LICENSE.txt

  IMPORTANT (project rule, see HANDOFF section 11.2): keep this script body pure ASCII.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoRoot {
    $root = Split-Path -Parent $PSScriptRoot
    if (-not (Test-Path -LiteralPath (Join-Path $root 'DeltaNFD.sln'))) {
        throw "Cannot locate the repository root from $PSScriptRoot (DeltaNFD.sln not found)."
    }
    return $root
}

$repoRoot = Resolve-RepoRoot
$projectDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $ProjectPath))
if (-not (Test-Path -LiteralPath $projectDir -PathType Container)) {
    $projectDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
}
if (-not (Test-Path -LiteralPath $projectDir -PathType Container)) {
    throw "Plugin project directory not found: $ProjectPath"
}

$packageDir = Join-Path $projectDir 'package'
if (-not (Test-Path -LiteralPath $packageDir -PathType Container)) {
    throw "Plugin package folder not found: $packageDir"
}

$projectFile = Get-ChildItem -LiteralPath $projectDir -Filter '*.csproj' -File | Select-Object -First 1
if ($null -eq $projectFile) { throw "No .csproj found in $projectDir" }

# ---- read the manifest (backend.entry gives the expected exe name and the package identity) ----
$manifestPath = Join-Path $packageDir 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "manifest.json not found: $manifestPath" }
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json

$pluginId = [string]$manifest.id
$pluginVersion = [string]$manifest.version
$entry = [string]$manifest.backend.entry
if ([string]::IsNullOrWhiteSpace($pluginId) -or [string]::IsNullOrWhiteSpace($pluginVersion)) {
    throw 'manifest.json must declare id and version.'
}
if ([string]::IsNullOrWhiteSpace($entry) -or -not $entry.StartsWith('backend/') -or -not $entry.ToLowerInvariant().EndsWith('.exe')) {
    throw "manifest.json backend.entry must be a backend/*.exe path (got: $entry)"
}
$entryFileName = Split-Path -Leaf $entry

foreach ($required in @('ui.json', 'README.md', 'LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDir $required) -PathType Leaf)) {
        throw "Package is missing the required root file: $required"
    }
}

# ---- publish the backend self-contained (spec: must not require the user to install a runtime) ----
$publishDir = Join-Path $projectDir "artifacts\publish-$Configuration"
if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    Write-Host "Publishing $($projectFile.Name) ($Configuration, win-x64, self-contained) ..."
    & dotnet publish $projectFile.FullName -c $Configuration -r win-x64 --self-contained true `
        -p:Platform=x64 -p:PublishSingleFile=false --nologo -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$publishedExe = Join-Path $publishDir $entryFileName
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "Published backend entry not found: $publishedExe (check AssemblyName vs manifest backend.entry)"
}

# ---- stage the package layout ----
$staging = Join-Path $projectDir "artifacts\package-$Configuration"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# root files declared by the spec (never copy the whole package folder blindly: it may hold extras)
foreach ($required in @('manifest.json', 'ui.json', 'README.md', 'LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $packageDir $required) -Destination (Join-Path $staging $required) -Force
}
$assets = Join-Path $packageDir 'assets'
if (Test-Path -LiteralPath $assets -PathType Container) {
    Copy-Item -LiteralPath $assets -Destination (Join-Path $staging 'assets') -Recurse -Force
}

$backendDir = Join-Path $staging 'backend'
New-Item -ItemType Directory -Path $backendDir -Force | Out-Null
Get-ChildItem -LiteralPath $publishDir -File | Where-Object { $_.Extension -notin @('.pdb', '.xml') } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $backendDir $_.Name) -Force }

# ---- zip ----
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot 'dist' }
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $outputFull)) { New-Item -ItemType Directory -Path $outputFull -Force | Out-Null }
$packagePath = Join-Path $outputFull "$pluginId-$pluginVersion.dnfdplugin"
if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Write entries by hand instead of ZipFile::CreateFromDirectory: on Windows PowerShell 5.1
# (.NET Framework) that helper writes entry names with BACKSLASHES, which is not the ZIP
# convention and breaks tools that only understand '/'. The spec also fixes the in-package
# layout as POSIX-style, so every entry name is normalized here.
$prefixLength = $staging.TrimEnd('\', '/').Length + 1
$zip = [System.IO.Compression.ZipFile]::Open($packagePath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
        $relativeName = $_.FullName.Substring($prefixLength) -replace '\\', '/'
        $zipEntry = $zip.CreateEntry($relativeName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $zipEntry.Open()
        try {
            $sourceStream = [System.IO.File]::OpenRead($_.FullName)
            try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose() }
        }
        finally { $entryStream.Dispose() }
    }
}
finally { $zip.Dispose() }

# ---- report ----
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = $archive.Entries
    $entryCount = $entries.Count
    $totalBytes = ($entries | Measure-Object -Property Length -Sum).Sum
    $rootNames = @($entries | Where-Object { $_.FullName -notmatch '/' } | ForEach-Object { $_.Name })
    $hasEntry = @($entries | Where-Object { $_.FullName -eq $entry })
}
finally { $archive.Dispose() }

foreach ($required in @('manifest.json', 'ui.json', 'README.md', 'LICENSE.txt')) {
    if ($rootNames -notcontains $required) { throw "Packed archive is missing root file $required" }
}
if ($hasEntry.Count -ne 1) { throw "Packed archive does not contain exactly one backend entry: $entry" }

$sizeMb = [math]::Round((Get-Item -LiteralPath $packagePath).Length / 1MB, 2)
$totalMb = [math]::Round($totalBytes / 1MB, 2)

Write-Host ''
Write-Host "Plugin package written: $packagePath"
Write-Host "  plugin         : $pluginId $pluginVersion"
Write-Host "  backend entry  : $entry"
Write-Host "  files in zip   : $entryCount"
Write-Host "  archive size   : $sizeMb MiB (uncompressed $totalMb MiB)"
Write-Host ''
Write-Host 'Import checklist (spec section 2):'
Write-Host '  * Importer limits: <= 100 MiB archive, <= 300 MiB expanded, <= 2048 entries, <= 100 MiB per file.'
Write-Host '  * Import only parses and validates; it never runs the backend or any script.'
Write-Host '  * Import lands disabled; enabling requires confirming the admin risk and permissions.'
