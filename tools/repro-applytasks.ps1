$ErrorActionPreference = 'Stop'

function Invoke-PS([string]$inner) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $env:SystemRoot + '\System32\WindowsPowerShell\v1.0\powershell.exe'
    $psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + "`$ErrorActionPreference='SilentlyContinue'; " + $inner
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    return [pscustomobject]@{ Exit = $p.ExitCode; Out = $out.GetAwaiter().GetResult().Trim(); Err = $err.GetAwaiter().GetResult().Trim() }
}

# variant A: trailing comma (what ApplyTasks generates today)
$a = @"
`$paths = @(
  'Microsoft\Windows\Autochk\Proxy',
)
Write-Output `$paths.Count
"@
$r = Invoke-PS $a
Write-Output ("A trailing-comma   : EXIT=" + $r.Exit + " OUT=[" + $r.Out + "] ERR=[" + ($r.Err -replace "`r`n", " | ") + "]")

# variant B: no trailing comma (the fix direction)
$b = @"
`$paths = @(
  'Microsoft\Windows\Autochk\Proxy'
)
Write-Output `$paths.Count
"@
$r = Invoke-PS $b
Write-Output ("B no-trailing-comma: EXIT=" + $r.Exit + " OUT=[" + $r.Out + "] ERR=[" + ($r.Err -replace "`r`n", " | ") + "]")
