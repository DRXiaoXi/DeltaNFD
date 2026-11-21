# Structural checks plus production PowerShell parser validation. No install/uninstall/system modifications.
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\DeltaNFD.iss'))
$app = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\App.xaml.cs'))
$handover = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\Services\DeploymentHandover.cs'))
$control = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'))
$pipe = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\Native\DeploymentPipe.cs'))
$window = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\MainWindow.xaml.cs'))
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
Assert-Safety ($source.Contains("QuoteDeploymentArgument(ExpandConstant('{app}'))") -and
    $source.Contains('QuoteDeploymentArgument(ReportPath)') -and $source.Contains('Slashes + Slashes')) 'Setup preserves backslashes before the closing Windows argument quote'
Assert-Safety ($source.Contains('ExecAndCaptureOutput') -and $source.Contains('DeploymentErrorExcerpt(Captured.StdErr)') -and
    $source -match 'else\s+begin\s+Result := False;\s+DeploymentDetail := ''未能获得本次安全交接报告') 'Missing report remains blocking and is diagnosed from standard error'
Assert-Safety ($pre.Contains("PrepareDeployment('uninstall', True)") -and $pre.Contains('Abort;')) 'Uninstall verifies safe preparation before file deletion'
Assert-Safety ($pre.Contains('PreserveAllData()') -and $pre.Contains('原始数据目录和恢复备份仍保留')) 'Optional export failure preserves original recovery data'
Assert-Safety ($source.Contains('CopyFile(Src, Dst, True)') -and $source.Contains('GetSHA256OfFile(Src) <> GetSHA256OfFile(Dst)')) 'Backup never overwrites and verifies bytes'
Assert-Safety ($source.Contains('PreserveBackupFiles(Src, Dst)') -and -not $source.Contains('PreserveFiles =')) 'Full data tree includes all recovery evidence rather than four filenames'
Assert-Safety (-not $source.Contains('DelTree(DataDir, DataDir)') -and -not $source.Contains('DelTree(LegacyDataDir, LegacyDataDir)') -and -not $source.Contains('DelTree(LocalDataDir, LocalDataDir)') -and -not $source.Contains('DelTree(LocalLegacyDir, LocalLegacyDir)')) 'Removal always retains current and legacy recovery data directories'
Assert-Safety ($source.Contains('Result := (ResultCode = 0) or (ResultCode = 10);')) 'Mandatory data preservation cannot turn a failed handover into permission'
Assert-Safety (-not $source.Contains('完全清除：') -and $source.Contains('仅保留原数据目录')) 'Export choice cannot erase still-needed original backups'
Assert-Safety ($source.Contains('function DelTree(const Path, Root: String)') -and $source.Contains('if HasReparseAncestor(Path) then')) 'Recursive deletion keeps root and ancestor guards'
Assert-Safety ($source.Contains('FindRec.Attributes and ReparsePointAttribute')) 'Copy and cleanup refuse reparse traversal'
Assert-Safety (-not $control.Contains('Get-CimInstance') -and $control.Contains('QueryFullProcessImageName')) 'Process identity lookup works independently of WMI'
Assert-Safety (-not ($control -match 'Stop-Process|\.Kill\(')) 'Deployment cannot forcibly kill unknown writers'
Assert-Safety ($control.Contains('$child.WaitForExit(600000)') -and $control.Contains('$read.Wait(600000)')) 'Standalone and cooperative restore waits are bounded'
Assert-Safety ($control.Contains('GetNamedPipeServerProcessId') -and $control.Contains('StartTicks')) 'Pipe server matches retained PID and creation time'
Assert-Safety ($control.Contains('$request + ''|'' + $child.ExitCode')) 'Standalone result must match this request and exit code'
$prepareIndex = $app.IndexOf('"--prepare-deployment"')
$singleInstanceIndex = $app.IndexOf('SingleInstanceGuard.TryBecomePrimary(')
Assert-Safety ($prepareIndex -ge 0 -and $singleInstanceIndex -gt $prepareIndex) 'Headless preparation is handled before GUI single-instance logic'
Assert-Safety ($app.Contains('DeploymentPipe.Start')) 'Running tray application implements cooperative deployment exit'
Assert-Safety ($control.Contains("'UNINSTALL/2'") -and $pipe.Contains('"UNINSTALL/2" => prepareRemoval')) 'Uninstall requests the versioned keep-settings handover without falling back to full recovery'
Assert-Safety ($control.Contains('--prepare-removal-preserving-settings') -and $app.Contains('permit = preserveAppliedSettings ? await DeploymentHandover.PrepareForUninstallAsync()')) 'Cold uninstall uses the same dedicated keep-settings preparation'
Assert-Safety ($window.Contains('preserveAppliedSettings: true') -and $window.Contains('await DeploymentHandover.PrepareForUninstallAsync()')) 'Running application routes removal through keep-settings preparation'
Assert-Safety ($app.Contains('DeploymentInProgress()') -and $source.Contains('ReleaseDeploymentMutex()')) 'Installer mutex blocks new GUI launches and releases on failure or completion'
$postInstall = [regex]::Match($source, 'procedure CurStepChanged\([\s\S]+?function InitializeUninstall').Value
Assert-Safety ($postInstall.Contains('ReleaseDeploymentMutex();')) 'Successful setup releases launch gate before postinstall Run entries'
Assert-Safety ($postInstall.Contains('UpdateChannelInstallationId') -and $postInstall.Contains('UpdateChannelAlphaInstallationId')) 'Installer records one-shot update-channel policy without launching the app'
Assert-Safety ($app.IndexOf('UpdateChannelInstallation.ApplyCurrentInstallation()') -lt $app.IndexOf('var mainWindow = new MainWindow()')) 'Install channel policy runs before UI and automatic update checks'
Assert-Safety ($app.Contains('if (ExitAlreadyPrepared || _windowsSessionEnding) return;')) 'Completed or rejected launch cannot start new recovery during ProcessExit'
$update = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\Services\UpdateService.cs'))
$updateUi = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\ViewModels\UpdateViewModel.cs'))
Assert-Safety ($update.Contains('DeploymentHandover.PrepareAsync(uninstall: false)') -and -not $updateUi.Contains('RequestExitForUpdate()')) 'Automatic update uses shared preparation and cannot exit on a timer during installer recovery'
Assert-Safety ($handover.Contains('BackendOperationGate.BlockNewOperations()') -and $handover.Contains('BlockNewOperations()')) 'New calls are gated throughout preparation'
Assert-Safety ($handover.Contains('InvokeReconcileAsync') -and $handover.Contains('"guard"') -and -not $handover.Contains('Guard.DisableAsync')) 'Uninstall guard recovery bypasses old intent self-lock and verifies state'
Assert-Safety ($handover.Contains('BackendRecovery.RestoreAsync(cpu, "all")')) 'CPU unknown record uses full verified recovery'
Assert-Safety ($handover.Contains('restoreCpuSets: restorePersistentSettings') -and $handover.Contains('PrepareCoreAsync(false, "卸载（保留已应用设置）", preserveAppliedSettings: true)')) 'Keep-settings removal drains helper without restoring persistent CPU sets'
Assert-Safety ($handover.Contains('restoreChanges: restorePersistentSettings') -and $handover.Contains('if (restorePersistentSettings)')) 'Full plugin, CPU and runtime-guard recovery remains an explicit operation'
Assert-Safety ($handover.Contains('!c.ExitConfirmed') -and $handover.Contains('!CanPreserveHistory(r)')) 'Active or unidentifiable evidence remains a blocker'
$tokens = $null; $errors = $null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'), [ref]$tokens, [ref]$errors)
Assert-Safety ($errors.Count -eq 0) ('Deployment script parses: ' + ($errors.Message -join '; '))
Write-Output 'Installer safety checks passed.'
