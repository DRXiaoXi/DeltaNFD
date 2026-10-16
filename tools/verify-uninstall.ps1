# Delta NFD uninstaller verification script (OpenAlphaV0.83)
#
# Purpose: prove that the NEW installer completely cleans up after itself.
# This script is intentionally self-contained and prints a PASS/FAIL matrix.
#
# HOW TO RUN (must be an ADMIN PowerShell - the installer and uninstaller both
# require elevation because they touch HKLM and scheduled tasks):
#
#   0. If an older version is still installed (check "已安装的应用" for
#      "三角帧不掉洲（Delta NFD）"), uninstall it FIRST. The new installer refuses to
#      proceed while a previous install is registered in a different directory
#      (see PrepareToInstall in installer\DeltaNFD.iss), and your %APPDATA% data
#      is preserved anyway.
#   1. Right-click PowerShell -> "Run as administrator"
#   2. cd to the repo root
#   3. powershell -ExecutionPolicy Bypass -File tools\verify-uninstall.ps1 `
#        -InstallerPath "installer\Output\三角帧不掉洲_DeltaNFD_安装包_0.83.0_x64.exe"
#
# WHAT IT DOES
#   Phase 0  snapshot the current state (and back up %APPDATA% data)
#   Phase 1  install the package silently (/VERYSILENT)
#   Phase 2  assert every artifact exists (exe, pri, xbf, background, tasks, registry)
#   Phase 3  uninstall silently (/VERYSILENT) - this path keeps backups
#   Phase 4  assert everything is gone; report what remains
#
# NOTE ON SILENT UNINSTALL: a silent uninstall cannot show the "完全清除 / 保留备份"
# page, so the uninstaller takes the SAFE default and copies the restore backups to
# the Desktop before wiping %APPDATA%. To exercise the interactive "完全清除" path,
# run the setup without /VERYSILENT and pick that option by hand.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [string]$InstallDir = "$env:ProgramFiles\Delta NFD",
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

function Write-Phase([string]$Text) {
    Write-Host ''
    Write-Host ('=' * 72)
    Write-Host "  $Text"
    Write-Host ('=' * 72)
}

function Test-Artifact([string]$Label, [scriptblock]$Probe, [bool]$ShouldExist) {
    $actual = $false
    try { $actual = [bool](& $Probe) } catch { $actual = $false }
    $ok = ($actual -eq $ShouldExist)
    $expect = if ($ShouldExist) { 'present' } else { 'absent' }
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1,-42} expect {2,-8} actual {3}" -f $mark, $Label, $expect, $(if ($actual) { 'present' } else { 'absent' }))
    return $ok
}

function Get-UninstallKey {
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$appData   = Join-Path $env:APPDATA 'Delta NFD'
$legacyApp = Join-Path $env:APPDATA 'DeltaOptimizer'
$localData = Join-Path $env:LOCALAPPDATA 'Delta NFD'
$localLeg  = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer'
$desktopBk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Delta NFD 备份'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '[ERROR] Not elevated. Right-click PowerShell -> Run as administrator, then re-run.' -ForegroundColor Red
    exit 1
}

Write-Phase 'Phase 0 - snapshot + data backup'
$backupRoot = Join-Path $env:USERPROFILE ("nfd-verify-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($d in @($appData, $legacyApp)) {
    if (Test-Path $d) { Copy-Item $d $backupRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "  backup of %APPDATA% data -> $backupRoot"
Write-Host "  installer               -> $installer"

if (-not $SkipInstall) {
    Write-Phase 'Phase 1 - silent install'
    $proc = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES' -PassThru -Wait
    Write-Host "  setup exit code: $($proc.ExitCode)"
    if ($proc.ExitCode -ne 0) { Write-Host '  [FAIL] installer did not exit 0' -ForegroundColor Red; exit 1 }
}

Write-Phase 'Phase 2 - artifacts must EXIST after install'
$results = @()
$results += Test-Artifact 'install dir'            { Test-Path $InstallDir } $true
$results += Test-Artifact 'DeltaNFD.exe'           { Test-Path (Join-Path $InstallDir 'DeltaNFD.exe') } $true
$results += Test-Artifact 'DeltaNFD.pri (WinUI)'   { Test-Path (Join-Path $InstallDir 'DeltaNFD.pri') } $true
$results += Test-Artifact 'Views\SettingsPage.xbf' { Test-Path (Join-Path $InstallDir 'Views\SettingsPage.xbf') } $true
$results += Test-Artifact 'bundled background.png' { Test-Path (Join-Path $InstallDir 'Assets\background.png') } $true
$results += Test-Artifact 'task DeltaNFD_AutoStart' { schtasks /Query /TN 'DeltaNFD_AutoStart' *> $null; $LASTEXITCODE -eq 0 } $true
$results += Test-Artifact 'quoted EXE product version' {
    (Get-Item (Join-Path $InstallDir 'DeltaNFD.exe')).VersionInfo.ProductVersion -like 'OpenAlphaV0.83*' } $true
$results += Test-Artifact 'registry uninstall entry' { Test-Path (Get-UninstallKey) } $true

Write-Phase 'Phase 3 - silent uninstall (safe default: keep backups)'
$unins = Join-Path $InstallDir 'unins000.exe'
if (-not (Test-Path $unins)) {
    Write-Host '  [FAIL] unins000.exe missing - cannot test uninstall' -ForegroundColor Red
    exit 1
}
$guardRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$guardNames = @('vc_redist.x64.exe','vc_redist.x86.exe','vc_redist.arm64.exe','vcredist_x64.exe','vcredist_x86.exe','vcredist_ia64.exe','vcredist.exe','DXSETUP.exe','dxwebsetup.exe','UE4PrereqSetup_x64.exe','UE4PrereqSetup_x86.exe')
$guardStatePath = Join-Path $env:ProgramData 'Delta NFD\RuntimeGuard\state.json'
$aclBefore = @()
if (Test-Path -LiteralPath $guardStatePath) {
    $guardBefore = [IO.File]::ReadAllText($guardStatePath) | ConvertFrom-Json
    $aclBefore = @($guardBefore.Acls)
}
$legacyAbsent = @()
$legacyPath = Join-Path $appData 'backups.json'
if (Test-Path -LiteralPath $legacyPath) {
    $legacyAbsent = @([IO.File]::ReadAllText($legacyPath) | ConvertFrom-Json | Where-Object {
        $_.ValueName -eq 'Debugger' -and $_.ValueKind -eq 'None' -and $_.KeyPath -like '*Image File Execution Options*'
    } | ForEach-Object { Split-Path $_.KeyPath -Leaf })
}
$expectedAbsent = @($guardNames | Where-Object {
    $key = Get-Item -LiteralPath (Join-Path $guardRoot $_) -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $false }
    try {
        $owner = $key.GetValue('DeltaNFD_RuntimeGuard_Owner')
        $debugger = $key.GetValue('Debugger', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        ($owner -like 'DeltaNFD.RuntimeGuard/1/*/absent') -or
            ($legacyAbsent -contains $_ -and $null -eq $owner -and $null -eq $key.GetValue('RuntimeGuardHotPatch_Owner') -and $debugger -eq '%windir%\System32\taskkill.exe')
    } finally { $key.Dispose() }
})
$proc = Start-Process -FilePath $unins -ArgumentList '/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES' -PassThru -Wait
Write-Host "  uninstaller exit code: $($proc.ExitCode)"
# 延迟自删链（cmd ping ≈5s）+ 空目录清理需要几秒收尾
Write-Host '  waiting 12s for the delayed self-delete chain (cmd ping delay ~5s)...'
Start-Sleep -Seconds 12

Write-Phase 'Phase 4 - everything must be GONE after uninstall'
$results += Test-Artifact 'owned runtime IFEO marker' {
    foreach ($name in $guardNames) {
        $key = Get-Item -LiteralPath (Join-Path $guardRoot $name) -ErrorAction SilentlyContinue
        if ($null -ne $key) { try { if ($key.GetValue('DeltaNFD_RuntimeGuard_Owner') -like 'DeltaNFD.RuntimeGuard/1/*') { return $true } } finally { $key.Dispose() } }
    }
    $false
} $false
$results += Test-Artifact 'legacy owned runtime IFEO residue' {
    foreach ($name in $expectedAbsent) {
        $key = Get-Item -LiteralPath (Join-Path $guardRoot $name) -ErrorAction SilentlyContinue
        if ($null -ne $key) { try { if ($null -ne $key.GetValue('Debugger')) { return $true } } finally { $key.Dispose() } }
    }
    $false
} $false
foreach ($acl in $aclBefore) {
    $results += Test-Artifact ('original UE4 ACL: ' + (Split-Path $acl.Path -Leaf)) {
        $current = (Get-Acl -LiteralPath $acl.Path).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
        $expected = [Security.AccessControl.RawSecurityDescriptor]::new($acl.Original).GetSddlForm([Security.AccessControl.AccessControlSections]::Access)
        $current -eq $expected
    } $true
}
$results += Test-Artifact 'install dir'                { Test-Path $InstallDir } $false
$results += Test-Artifact 'unins000.exe leftover'      { Test-Path (Join-Path $InstallDir 'unins000.exe') } $false
$results += Test-Artifact 'unins000.dat leftover'      { Test-Path (Join-Path $InstallDir 'unins000.dat') } $false
$results += Test-Artifact '%APPDATA%\Delta NFD'        { Test-Path $appData } $false
$results += Test-Artifact '%APPDATA%\DeltaOptimizer'   { Test-Path $legacyApp } $false
$results += Test-Artifact '%LOCALAPPDATA%\Delta NFD'   { Test-Path $localData } $false
$results += Test-Artifact '%LOCALAPPDATA%\DeltaOptimizer' { Test-Path $localLeg } $false
$results += Test-Artifact 'task DeltaNFD_AutoStart'    { schtasks /Query /TN 'DeltaNFD_AutoStart' *> $null; $LASTEXITCODE -eq 0 } $false
$results += Test-Artifact 'task DeltaNFD_FrameMode'    { schtasks /Query /TN 'DeltaNFD_FrameMode' *> $null; $LASTEXITCODE -eq 0 } $false
$results += Test-Artifact 'registry uninstall entry'   { Test-Path (Get-UninstallKey) } $false
$results += Test-Artifact 'desktop shortcut'           { Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) '三角帧不掉洲（Delta NFD）.lnk') } $false

Write-Phase 'Result'
$failed = ($results | Where-Object { -not $_ }).Count
Write-Host "  checks run : $($results.Count)"
Write-Host "  passed     : $($results.Count - $failed)"
Write-Host "  failed     : $failed"
if (Test-Path $desktopBk) {
    Write-Host ''
    Write-Host "  restore backups were saved to: $desktopBk" -ForegroundColor Yellow
}
Write-Host "  original %APPDATA% data backup: $backupRoot"
if ($failed -eq 0) {
    Write-Host ''
    Write-Host '  ALL CHECKS PASSED - the uninstaller leaves nothing behind.' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host '  SOME CHECKS FAILED - see FAIL lines above.' -ForegroundColor Red
    exit 2
}
