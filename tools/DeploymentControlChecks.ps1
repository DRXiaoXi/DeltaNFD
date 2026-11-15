# Execute the production orchestration with fake processes only. No setup, uninstall, or termination.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1') -LibraryOnly
$productionHeadless = ${function:Invoke-HeadlessRecovery}
$script:Passed = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:Passed++
    Write-Output ('PASS: ' + $Message)
}
$script:Snapshots = [Collections.Generic.Queue[object]]::new()
$script:Cooperative = $null
$script:Recovery = [pscustomobject]@{ Code = 0; Detail = 'isolated recovery completed' }
$script:HeadlessCalls = 0
$script:ExitDuringRequest = $false
function Get-ProductProcesses([string]$Root) {
    if ($script:Snapshots.Count -gt 0) { $script:Snapshots.Dequeue() }
}
function Request-CooperativeExit([string]$Root, [string]$Operation, $Owner) {
    if ($script:ExitDuringRequest) { $Owner.Process.HasExited = $true; throw 'fixture transport closed during exit' }
    return $script:Cooperative
}
function Invoke-HeadlessRecovery([string]$Root, [string]$Operation) {
    if ($Operation -eq 'install') { throw 'Regression: setup tried to start an installed executable' }
    $script:HeadlessCalls++
    return $script:Recovery
}
function New-FakeIdentity([string]$Image, [bool]$Exited, [bool]$WaitResult = $true) {
    $process = [pscustomobject]@{ Id = 123; HasExited = $Exited; WaitResult = $WaitResult; Waits = 0; Closes = 0; Disposed = $false }
    $process | Add-Member ScriptMethod WaitForExit { param($timeout) $this.Waits++; if ($this.WaitResult) { $this.HasExited = $true }; return $this.WaitResult }
    $process | Add-Member ScriptMethod CloseMainWindow { $this.Closes++; return $true }
    $process | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
    return [pscustomobject]@{ Image = $Image; StartTicks = 100; Process = $process }
}
function Assert-Blocked([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-Check ($failure -and $failure.Contains($Pattern)) $Message
}
$testRoot = Join-Path $PSScriptRoot ('..\artifacts\deployment-control-checks-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$testRoot = [IO.Path]::GetFullPath($testRoot)
[IO.File]::WriteAllText((Join-Path $testRoot 'DeltaNFD.exe'), 'This is test data, never executed.')

Assert-Check (Test-UnderRoot (Join-Path $testRoot 'Backends\DeltaNFD.Backend.Runtime.exe') $testRoot) 'Backend descendants belong to installation'
Assert-Check (-not (Test-UnderRoot ($testRoot + '-other\DeltaNFD.exe') $testRoot)) 'Sibling installation cannot be mistaken for target'
Assert-Check ((Get-DeploymentPipeName $testRoot) -eq (Get-DeploymentPipeName ($testRoot.ToUpperInvariant() + '\'))) 'Native and managed pipe identities normalize case and trailing slash'
Assert-Blocked { & $productionHeadless $testRoot 'install' } '安装检查不能启动' 'Production standalone recovery rejects setup before creating any child'
$before = $script:HeadlessCalls
$result = Invoke-Deployment $testRoot 'install'
Assert-Check ($result.Code -eq 0 -and $script:HeadlessCalls -eq $before) 'Upgrade over an exited legacy executable never launches it with new arguments'
$freshRoot = Join-Path $testRoot 'fresh-install'
[void][IO.Directory]::CreateDirectory($freshRoot)
$result = Invoke-Deployment $freshRoot 'install'
Assert-Check ($result.Code -eq 0 -and $script:HeadlessCalls -eq $before) 'Fresh installation does not start a frontend or recovery child'

$result = Invoke-Deployment $testRoot 'uninstall'
Assert-Check ($result.Code -eq 0 -and $script:HeadlessCalls -eq 1) 'Exited application uses standalone recovery before file removal'
$gone = New-FakeIdentity (Join-Path $testRoot 'DeltaNFD.exe') $true
$script:Snapshots.Enqueue(@($gone)); $script:Snapshots.Enqueue(@())
$result = Invoke-Deployment $testRoot 'install'
Assert-Check ($result.Code -eq 0 -and $gone.Process.Waits -eq 0) 'Exit between enumeration and handling succeeds without waiting or termination'
Assert-Check $gone.Process.Disposed 'Retained process identity is disposed after race'

$main = New-FakeIdentity (Join-Path $testRoot 'DeltaNFD.exe') $false
$script:ExitDuringRequest = $true
$before = $script:HeadlessCalls
$script:Snapshots.Enqueue(@($main)); $script:Snapshots.Enqueue(@())
$result = Invoke-Deployment $testRoot 'install'
Assert-Check ($result.Code -eq 0 -and $script:HeadlessCalls -eq $before) 'Exit during pipe connection completes setup without relaunching the old frontend'
$script:ExitDuringRequest = $false

$main = New-FakeIdentity (Join-Path $testRoot 'DeltaNFD.exe') $false
$main.Process | Add-Member -Force ScriptMethod CloseMainWindow { $this.HasExited = $true; throw 'fixture exited before close' }
$script:Snapshots.Enqueue(@($main)); $script:Snapshots.Enqueue(@())
$result = Invoke-Deployment $testRoot 'install'
Assert-Check ($result.Code -eq 0) 'Exit during legacy close succeeds and still verifies remaining processes'

$main = New-FakeIdentity (Join-Path $testRoot 'DeltaNFD.exe') $false
$script:Cooperative = [pscustomobject]@{ Code = 10; Detail = 'Historical ACE evidence retained' }
$before = $script:HeadlessCalls
$script:Snapshots.Enqueue(@($main)); $script:Snapshots.Enqueue(@())
$result = Invoke-Deployment $testRoot 'uninstall'
Assert-Check ($result.Code -eq 10 -and $result.Detail.Contains('ACE')) 'Safe exit with historical evidence returns mandatory data preservation'
Assert-Check ($script:HeadlessCalls -eq $before -and $main.Process.Closes -eq 0) 'Cooperative preparation is not repeated by a second worker'

$main = New-FakeIdentity (Join-Path $testRoot 'DeltaNFD.exe') $false
$script:Cooperative = [pscustomobject]@{ Code = 2; Detail = 'MSI child still running' }
$script:Snapshots.Enqueue(@($main))
Assert-Blocked { Invoke-Deployment $testRoot 'install' } 'MSI child' 'Active installer child blocks replacement and preserves detail'
Assert-Check ($main.Process.Waits -eq 0 -and $main.Process.Closes -eq 0) 'Failed cooperative recovery does not terminate main process'

$main = New-FakeIdentity (Join-Path $testRoot 'DeltaOptimizer.exe') $false $false
$script:Cooperative = $null
$script:Snapshots.Enqueue(@($main))
Assert-Blocked { Invoke-Deployment $testRoot 'uninstall' } '托盘' 'Legacy tray-only close produces actionable message without force kill'
Assert-Check ($main.Process.Closes -eq 1 -and $main.Process.Waits -eq 1) 'Legacy close gets one bounded wait'

$worker = New-FakeIdentity (Join-Path $testRoot 'Backends\DeltaNFD.Backend.CpuFrame.exe') $false
$script:Snapshots.Enqueue(@()); $script:Snapshots.Enqueue(@($worker))
Assert-Blocked { Invoke-Deployment $testRoot 'install' } '仍有执行者' 'Worker appearing after first snapshot blocks final file replacement'
Assert-Check $worker.Process.Disposed 'New worker snapshot is disposed on failure'

$script:Recovery = [pscustomobject]@{ Code = 2; Detail = 'Runtime guard failed to restore' }
$script:Snapshots.Enqueue(@())
Assert-Blocked { Invoke-Deployment $testRoot 'uninstall' } 'Runtime guard' 'Standalone recovery failure blocks uninstall with subsystem detail'

Write-Output ("Deployment control checks passed ($script:Passed assertions). Fixtures: $testRoot")
