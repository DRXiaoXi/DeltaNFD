# ============================================================
#  T5 self-contained publish layout (isolated output dir only; no installer, no upload)
#
#  Output (default <repo>/_buildcheck/t5/publish):
#    DeltaNFD.exe                                frontend (WinUI + self-contained .NET/WindowsAppSDK)
#    Backends/                                  six backends, one self-contained runtime
#    Assets/...                                  TweakDb / UpdateConfig / VC++ repair payload, etc.
#
#  Components use one SDK ArtifactsPath with per-project subdirectories,
#  so shared dependencies are byte-identical and project intermediates stay isolated.
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File installer\publish-selfcontained.ps1
# ============================================================
[CmdletBinding()]
param(
    [string]$OutDir = "_buildcheck/t5/publish",
    [string]$Configuration = "Release",
    [string]$ArtifactsRoot = "_buildcheck/t5/publish-artifacts",
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$frontendProject = Join-Path $repo "src/DeltaNFD/DeltaNFD.csproj"
$out = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }))
if (Test-Path -LiteralPath $out) { throw "Output must be a new directory: $out" }
$componentLayout = $out + '.components-' + [Guid]::NewGuid().ToString('N')
$artifacts = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ArtifactsRoot)) { $ArtifactsRoot } else { Join-Path $repo $ArtifactsRoot }))
$frontendArtifacts = Join-Path $artifacts "frontend"

Write-Host "[1/3] Build frontend (produces xbf/pri and backend build output)..."
if (-not $SkipFrontendBuild) {
    dotnet build $frontendProject -c $Configuration -p:Platform=x64 -m:1 -nr:false `
        -p:UseArtifactsOutput=true "-p:ArtifactsPath=$frontendArtifacts" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "frontend build failed (exit=$LASTEXITCODE)" }
}

Write-Host "[2/3] Prepare isolated self-contained components (SDK per-project artifacts)..."
dotnet publish $frontendProject -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -m:1 -nr:false `
    -p:UseArtifactsOutput=true "-p:ArtifactsPath=$frontendArtifacts" `
    "-p:PublishComponentsArtifactsPath=$artifacts/components" `
    -p:PublishSelfContainedComponents=true -p:NuGetAudit=false -o $componentLayout
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit=$LASTEXITCODE)" }

# WinUI3 unpackaged publish is known to drop .xbf and pri; copy them back from build output.
Write-Host "[3/3] Copy WinUI resources (xbf + pri)..."
$buildOut = Join-Path $frontendArtifacts ("bin/DeltaNFD/" + $Configuration.ToLower() + "_win-x64")
if (-not (Test-Path $buildOut)) { throw "frontend build output not found: $buildOut" }
robocopy $buildOut $componentLayout *.xbf DeltaNFD.pri /S /NFL /NDL /NJH /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy WinUI resources failed (robocopy exit=$LASTEXITCODE)" }

# Components share project-specific SDK artifact directories and the same dependency binaries.
# Keep the original staged layouts for diagnostics; they are not part of the delivered directory.
& (Join-Path $PSScriptRoot 'merge-shared-backends.ps1') -SourceLayout $componentLayout -Destination (Join-Path $out 'Backends')
robocopy $componentLayout $out /E /NFL /NDL /NJH /NP /XD (Join-Path $componentLayout 'Backends') (Join-Path $componentLayout 'OfflineHelper') | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy frontend payload failed (robocopy exit=$LASTEXITCODE)" }

$required = @(
    "DeltaNFD.exe",
    "DeltaNFD.pri",
    "Backends/DeltaNFD.Backend.Ace.exe",
    "Backends/DeltaNFD.Backend.Gpu.exe",
    "Backends/DeltaNFD.Backend.Runtime.exe",
    "Backends/DeltaNFD.Backend.SystemMaintenance.exe",
    "Backends/DeltaNFD.Backend.CpuFrame.exe",
    "Backends/DeltaNFD.Backend.Procman.exe",
    "Assets/UpdateConfig.json",
    "Assets/TweakDb/RegistryTweaks.json"
    "Assets/VCRedistRepair/Installer.cmd"
    "Assets/DirectXRepair/DXSETUP.exe"
    "Assets/DirectXRepair/payload-manifest.json"
)
$missing = @()
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $out $relative))) { $missing += $relative }
}
if ($missing.Count -gt 0) { throw ("publish layout missing: " + ($missing -join ", ")) }

if (Test-Path -LiteralPath (Join-Path $out 'Assets/DotNetRepair')) { throw 'Retired .NET repair payload found; rebuild from current source.' }
Write-Host "Validate actual .NET runtime files and dependency manifests in the final shared layout..."
dotnet run --project (Join-Path $repo 'tools/DotNetLayoutCheck/DotNetLayoutCheck.csproj') -c Release `
    -p:Platform=x64 -p:NuGetAudit=false -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifacts/layout-check" -- $out
if ($LASTEXITCODE -ne 0) { throw "Published .NET dependency check failed; installer generation is blocked." }

Write-Host "Publish layout complete: $out"
