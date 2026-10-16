param([string]$TaskPath1, [string]$TaskPath2)
$ErrorActionPreference = 'Stop'
# 按修复后 ApplyTasks 的逻辑生成：最后一行无尾逗号
$paths = New-Object System.Collections.Generic.List[string]
$paths.Add($TaskPath1)
if ($TaskPath2) { $paths.Add($TaskPath2) }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("$ErrorActionPreference='SilentlyContinue'")
[void]$sb.AppendLine('$paths = @(')
for ($i = 0; $i -lt $paths.Count; $i++) {
    $comma = if ($i -lt $paths.Count - 1) { ',' } else { '' }
    [void]$sb.AppendLine("  '" + $paths[$i].Replace("'", "''") + "'" + $comma)
}
[void]$sb.AppendLine(')')
[void]$sb.AppendLine('$done=0')
[void]$sb.AppendLine("foreach(`$p in `$paths){ `$i=`$p.LastIndexOf('\'); `$tp=`$p.Substring(0,`$i+1); `$tn=`$p.Substring(`$i+1); if(Get-ScheduledTask -TaskPath `$tp -TaskName `$tn){ Disable-ScheduledTask -TaskPath `$tp -TaskName `$tn | Out-Null; `$done++ } }")
[void]$sb.AppendLine('Write-Output $done')
$inner = $sb.ToString()

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
Write-Output ("EXIT=" + $p.ExitCode + "  OUT=[" + $out.GetAwaiter().GetResult().Trim() + "]  ERR=[" + $err.GetAwaiter().GetResult().Trim() + "]")
