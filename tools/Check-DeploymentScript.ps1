# This gate must itself remain ASCII so Windows PowerShell 5.1 can always read it.
[CmdletBinding()]
param([string]$ScriptPath = (Join-Path $PSScriptRoot '../installer/DeploymentControl.ps1'))
$ErrorActionPreference = 'Stop'
$ScriptPath = [IO.Path]::GetFullPath($ScriptPath)
$bytes = [IO.File]::ReadAllBytes($ScriptPath)
$nonAscii = @($bytes | Where-Object { $_ -ge 128 }).Count -gt 0
$bom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
if ($nonAscii -and -not $bom) { throw ('Non-ASCII deployment script must be UTF-8 with BOM for Windows PowerShell 5.1: ' + $ScriptPath) }
$strict = New-Object Text.UTF8Encoding($false, $true)
[void]$strict.GetString($bytes)
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    $windowsPS = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPS -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required to validate the installer script.' }
    & $windowsPS -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -ScriptPath $ScriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell 5.1 deployment gate failed.' }
    return
}
$tokens = $null; $issues = $null
[void][Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$issues)
if ($issues.Count) { throw ('Windows PowerShell 5.1 parse errors: ' + ($issues.Message -join '; ')) }
. $ScriptPath -LibraryOnly
Initialize-ProcessQuery
$image = [DeltaDeployment.Native]::Image($PID)
if ([string]::IsNullOrWhiteSpace($image)) { throw 'Native process-query initialization failed.' }
Write-Output ('PASS: actual Windows PowerShell ' + $PSVersionTable.PSVersion + ' parsed/imported deployment script and queried only itself: ' + $ScriptPath)
