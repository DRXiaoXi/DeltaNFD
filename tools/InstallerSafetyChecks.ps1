# Structural checks plus production PowerShell parser validation. No install/uninstall/system modifications.
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\DeltaNFD.iss'))
$app = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\App.xaml.cs'))
$handover = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\Services\DeploymentHandover.cs'))
$control = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'))
function Assert-Safety([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output "PASS: $Message"
}
$pre = [regex]::Match($source, 'if CurUninstallStep = usUninstall then[\s\S]+?if CurUninstallStep <> usPostUninstall').Value
Assert-Safety ($source.Contains('CloseApplications=no')) 'Restart Manager cannot force-kill restoration workers'
Assert-Safety ($source.Contains('VersionInfoVersion={#MyAppVersion}') -and $source.Contains('VersionInfoTextVersion={#MyAppDisplayVersion}')) 'Setup file version resources match the product version'
$runEntries = @([regex]::Match($source, '(?s)\[Run\](.*?)\[Code\]').Groups[1].Value.Split([char]10) | Where-Object { $_.TrimStart().StartsWith('Filename:') })
Assert-Safety (@($runEntries).Count -eq 1 -and $runEntries[0].Contains('postinstall nowait skipifsilent unchecked')) 'Application launch is only an unchecked opt-in after interactive setup completes'
Assert-Safety (-not $source.Contains('Check: WizardSilent')) 'Silent installation cannot automatically launch the GUI'
Assert-Safety ($control.Contains("if (`$Operation -ne 'uninstall')")) 'Standalone recovery cannot run the installed frontend during setup'
Assert-Safety ($source.Contains("PrepareDeployment('install', False)")) 'Setup completes the same safe handover before replacement'
Assert-Safety ($pre.Contains("PrepareDeployment('uninstall', True)") -and $pre.Contains('Abort;')) 'Uninstall verifies safe preparation before file deletion'
Assert-Safety ($pre.Contains('PreserveAllData()') -and $pre.Contains('完整备份复制或校验失败')) 'Copy failure aborts before program removal'
Assert-Safety ($source.Contains('CopyFile(Src, Dst, True)') -and $source.Contains('GetSHA256OfFile(Src) <> GetSHA256OfFile(Dst)')) 'Backup never overwrites and verifies bytes'
Assert-Safety ($source.Contains('PreserveBackupFiles(Src, Dst)') -and -not $source.Contains('PreserveFiles =')) 'Full data tree includes all recovery evidence rather than four filenames'
Assert-Safety ($source.Contains('if not PreserveDeploymentHistory then')) 'Historical records keep original data directories after uninstall'
Assert-Safety ($source.Contains('function DelTree(const Path, Root: String)') -and $source.Contains('if HasReparseAncestor(Path) then')) 'Recursive deletion keeps root and ancestor guards'
Assert-Safety ($source.Contains('FindRec.Attributes and ReparsePointAttribute')) 'Copy and cleanup refuse reparse traversal'
Assert-Safety (-not $control.Contains('Get-CimInstance') -and $control.Contains('QueryFullProcessImageName')) 'Process identity lookup works independently of WMI'
Assert-Safety (-not ($control -match 'Stop-Process|\.Kill\(')) 'Deployment cannot forcibly kill unknown writers'
Assert-Safety ($control.Contains('$child.WaitForExit(600000)') -and $control.Contains('$read.Wait(600000)')) 'Standalone and cooperative restore waits are bounded'
Assert-Safety ($control.Contains('GetNamedPipeServerProcessId') -and $control.Contains('StartTicks')) 'Pipe server matches retained PID and creation time'
Assert-Safety ($control.Contains('$request + ''|'' + $child.ExitCode')) 'Standalone result must match this request and exit code'
Assert-Safety ($app.IndexOf('"--prepare-deployment"') -lt $app.IndexOf('SingleInstanceGuard.TryBecomePrimary()')) 'Headless preparation is handled before GUI single-instance logic'
Assert-Safety ($app.Contains('DeploymentPipe.Start')) 'Running tray application implements cooperative deployment exit'
Assert-Safety ($app.Contains('DeploymentInProgress()') -and $source.Contains('ReleaseDeploymentMutex()')) 'Installer mutex blocks new GUI launches and releases on failure or completion'
$postInstall = [regex]::Match($source, 'procedure CurStepChanged\([\s\S]+?function InitializeUninstall').Value
Assert-Safety ($postInstall.Contains('ReleaseDeploymentMutex();')) 'Successful setup releases launch gate before postinstall Run entries'
Assert-Safety ($postInstall.Contains('UpdateChannelInstallationId') -and $postInstall.Contains('UpdateChannelAlphaInstallationId')) 'Installer records one-shot update-channel policy without launching the app'
Assert-Safety ($app.IndexOf('UpdateChannelInstallation.ApplyCurrentInstallation()') -lt $app.IndexOf('var mainWindow = new MainWindow()')) 'Install channel policy runs before UI and automatic update checks'
Assert-Safety ($app.Contains('if (ExitAlreadyPrepared) return;')) 'Completed or rejected launch cannot start new recovery during ProcessExit'
$update = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\Services\UpdateService.cs'))
$updateUi = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\ViewModels\UpdateViewModel.cs'))
Assert-Safety ($update.Contains('DeploymentHandover.PrepareAsync(uninstall: false)') -and -not $updateUi.Contains('RequestExitForUpdate()')) 'Automatic update uses shared preparation and cannot exit on a timer during installer recovery'
Assert-Safety ($handover.Contains('BackendOperationGate.BlockNewOperations()') -and $handover.Contains('BlockNewOperations()')) 'New calls are gated throughout preparation'
Assert-Safety ($handover.Contains('InvokeReconcileAsync') -and $handover.Contains('"guard"') -and -not $handover.Contains('Guard.DisableAsync')) 'Uninstall guard recovery bypasses old intent self-lock and verifies state'
Assert-Safety ($handover.Contains('BackendRecovery.RestoreAsync(cpu, "all")')) 'CPU unknown record uses full verified recovery'
Assert-Safety ($handover.Contains('restoreCpuSets: uninstall')) 'Offline helper is drained for setup and restored for uninstall'
Assert-Safety ($handover.Contains('!c.ExitConfirmed') -and $handover.Contains('!CanPreserveHistory(r)')) 'Active or unidentifiable evidence remains a blocker'
$tokens = $null; $errors = $null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'), [ref]$tokens, [ref]$errors)
Assert-Safety ($errors.Count -eq 0) ('Deployment script parses: ' + ($errors.Message -join '; '))
Write-Output 'Installer safety checks passed.'
