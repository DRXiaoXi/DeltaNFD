param(
    [Parameter(Mandatory=$true)][string]$Layout,
    [string]$Coordinator = (Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'),
    [switch]$Probe
)
$ErrorActionPreference = 'Stop'
if ($Probe) {
    . $Coordinator -LibraryOnly
    Initialize-ProcessQuery
    $self = [DeltaDeployment.Native]::Image($PID)
    if (-not $self.EndsWith('\powershell.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected probe process' }
    if ((Get-DeploymentPipeName $Layout) -notmatch '^DeltaNFD.Deployment.[0-9A-F]{24}$') { throw 'Bad pipe identity' }
    Write-Output ('PASS: Windows PowerShell ' + $PSVersionTable.PSVersion + ' native query compiled in ' + (Get-Location))
    exit 0
}
$Layout = [IO.Path]::GetFullPath($Layout)
$Coordinator = [IO.Path]::GetFullPath($Coordinator)
$shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($directory in @($Layout, (Join-Path $Layout 'Backends'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'System.dll'))) { throw 'Expected self-contained facade missing' }
    Push-Location -LiteralPath $directory
    try {
        & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -Layout $Layout -Coordinator $Coordinator -Probe
        if ($LASTEXITCODE -ne 0) { throw 'Production PowerShell coordinator compilation failed' }
    } finally { Pop-Location }
}
Write-Output '2 production-host compilation checks passed. Queried only the probe process; no exit requests, installer, services or registry writes.'
