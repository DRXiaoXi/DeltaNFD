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
    [switch]$SkipFrontendBuild,
    [ValidateSet('true','false')][string]$RuntimeUiEnabled = 'true'
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
        -p:UseArtifactsOutput=true "-p:ArtifactsPath=$frontendArtifacts" -p:NuGetAudit=false "-p:RuntimeUiEnabled=$RuntimeUiEnabled"
    if ($LASTEXITCODE -ne 0) { throw "frontend build failed (exit=$LASTEXITCODE)" }
}

Write-Host "[2/3] Prepare isolated self-contained components (SDK per-project artifacts)..."
dotnet publish $frontendProject -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -m:1 -nr:false `
    -p:UseArtifactsOutput=true "-p:ArtifactsPath=$frontendArtifacts" `
    "-p:PublishComponentsArtifactsPath=$artifacts/components" `
    -p:PublishSelfContainedComponents=true -p:NuGetAudit=false "-p:RuntimeUiEnabled=$RuntimeUiEnabled" -o $componentLayout
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
    "Backends/DeltaNFD.FileUpdater.exe",
    "Backends/DeltaNFD.UpdateCore.dll",
    "Tools/DeploymentControl.ps1",
    "Assets/UpdateConfig.json",
    "Assets/Vc14Baseline14.42/vc_redist.x64.exe",
    "Assets/Vc14Baseline14.42/vc_redist.x86.exe",
    "Assets/RuntimeHighFps/Installer.cmd",
    "Assets/RuntimeHighFps/payload_manifest_sha256.csv",
    "Assets/TweakDb/RegistryTweaks.json"
)
$missing = @()
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $out $relative))) { $missing += $relative }
}
if ($missing.Count -gt 0) { throw ("publish layout missing: " + ($missing -join ", ")) }

$highFpsRoot = [IO.Path]::GetFullPath((Join-Path $out 'Assets/RuntimeHighFps'))
$highFpsPrefix = $highFpsRoot + [IO.Path]::DirectorySeparatorChar
$highFpsRows = @(Import-Csv -LiteralPath (Join-Path $highFpsRoot 'payload_manifest_sha256.csv'))
if ($highFpsRows.Count -ne 375) { throw 'Auxiliary payload manifest must contain 375 files' }
foreach ($year in @('2005', '2008', '2010', '2012', '2013', '2022', '2026')) {
    if (Test-Path -LiteralPath (Join-Path $highFpsRoot $year)) { throw 'Excluded AIO v14 payload found' }
}
foreach ($entry in $highFpsRows) {
    $file = [IO.Path]::GetFullPath((Join-Path $highFpsRoot $entry.relative_path))
    if (-not $file.StartsWith($highFpsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'High-FPS payload path escaped its root' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne [long]$entry.size_bytes) {
        throw ('High-FPS payload missing or incomplete: ' + $entry.relative_path)
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('High-FPS payload hash mismatch: ' + $entry.relative_path) }
}

foreach ($retired in @('Assets/DotNetRepair', 'Assets/VCRedistRepair', 'Assets/DirectXRepair', 'Assets/VCRedist2015-2022-x64.exe', 'Assets/VCRedist2015-2022-x86.exe', 'Assets/VCRedistRepair.exe')) {
    if (Test-Path -LiteralPath (Join-Path $out $retired)) { throw ('Retired repair payload found: ' + $retired) }
}
$officialCatalog = Get-Content -LiteralPath (Join-Path $repo 'src/DeltaNFD.Execution/Execution/AlphaRestore/RuntimeCppOfficial.catalog.json') -Raw | ConvertFrom-Json
if ($officialCatalog.Count -ne 10) { throw 'Expected ten Microsoft C++ originals' }
foreach ($officialPackage in $officialCatalog) {
    foreach ($officialFile in $officialPackage.Files) {
        $path = Join-Path $out ('Assets/RuntimeCppOfficial/' + $officialFile.Path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $officialFile.Bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $officialFile.Sha256) {
            throw ('Microsoft original or original source hash mismatch: ' + $officialFile.Path)
        }
    }
}
Write-Host "Validate actual .NET runtime files and dependency manifests in the final shared layout..."
dotnet build (Join-Path $repo 'tools/DotNetLayoutCheck/DotNetLayoutCheck.csproj') -c Release -m:1 -nr:false `
    -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifacts/layout-check"
if ($LASTEXITCODE -ne 0) { throw 'Build-only runtime layout checker failed to build.' }
& (Join-Path $artifacts 'layout-check/bin/DotNetLayoutCheck/release/DotNetLayoutCheck.exe') $out
if ($LASTEXITCODE -ne 0) { throw "Published .NET dependency check failed; installer generation is blocked." }

Write-Host "Publish layout complete: $out"
