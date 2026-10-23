# ASCII test harness; scripts and fake evidence are the only files written.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root 'installer/DeploymentControl.ps1'
$gate = Join-Path $PSScriptRoot 'Check-DeploymentScript.ps1'
$fixture = Join-Path $root ('artifacts/deployment-encoding-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$data = [IO.File]::ReadAllBytes($script)
& $gate -ScriptPath $script
if ($LASTEXITCODE -ne 0) { throw 'Production script host check failed.' }
Write-Output 'PASS: production script is parsed and initialized by actual Windows PowerShell 5.1.'
$bad = Join-Path $fixture 'no-bom.ps1'
[IO.File]::WriteAllBytes($bad, $data[3..($data.Length-1)])
$rejected = $false
try { & $gate -ScriptPath $bad } catch { $rejected = $_.Exception.Message.Contains('UTF-8 with BOM') }
if (-not $rejected) { throw 'The original BOM-less regression was accepted.' }
Write-Output 'PASS: original BOM-less packaging regression is rejected regardless of OS ANSI code page.'
$invalid = Join-Path $fixture 'invalid-utf8.ps1'
[IO.File]::WriteAllBytes($invalid, [byte[]](239,187,191,255))
$rejected = $false
try { & $gate -ScriptPath $invalid } catch { $rejected = $true }
if (-not $rejected) { throw 'Invalid UTF-8 bytes were accepted.' }
Write-Output 'PASS: malformed UTF-8 cannot reach installer generation.'
$broken = Join-Path $fixture 'syntax-error.ps1'
[IO.File]::WriteAllText($broken, "function Broken {", (New-Object Text.UTF8Encoding($true)))
$rejected = $false
try { & $gate -ScriptPath $broken } catch { $rejected = $true }
if (-not $rejected) { throw 'Windows PowerShell syntax error was accepted.' }
Write-Output 'PASS: Windows PowerShell 5.1 syntax errors cannot reach installer generation.'
Write-Output 'Deployment encoding checks passed (4 cases). No setup/uninstall was executed.'
exit 0
