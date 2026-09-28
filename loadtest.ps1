# Load test: if System(PID4)'s reported CPU growth tracks OTHER processes' kernel load,
# it is just machine-wide accounting; if it stays flat while others' kernel time rises,
# it is System's own hidden burn (thread hidden from enumeration).
$ErrorActionPreference = 'Continue'
$lp = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors

function Measure-Phase([string]$label, [int]$secs) {
  $s1 = (Get-Process -Id 4).TotalProcessorTime.TotalMilliseconds
  $o1 = 0.0
  foreach ($pr in (Get-Process | Where-Object { $_.Id -ne 4 })) {
    try { $o1 += $pr.PrivilegedProcessorTime.TotalMilliseconds } catch {}
  }
  $k1 = $null
  try { $k1 = (Get-Counter '\Processor Information(_Total)\% Privileged Time' -SampleInterval ([math]::Max(2, $secs - 2)) -MaxSamples 1).CounterSamples[0].CookedValue } catch {}
  Start-Sleep -Seconds $secs
  $s2 = (Get-Process -Id 4).TotalProcessorTime.TotalMilliseconds
  $o2 = 0.0
  foreach ($pr in (Get-Process | Where-Object { $_.Id -ne 4 })) {
    try { $o2 += $pr.PrivilegedProcessorTime.TotalMilliseconds } catch {}
  }
  $sysRate  = ($s2 - $s1) / $secs / 1000          # cores
  $otherRate = ($o2 - $o1) / $secs / 1000         # cores
  Write-Host ('{0,-12} System(PID4): {1:N2} cores   other-processes-kernel: {2:N2} cores   machine-privileged%: {3}' -f $label, $sysRate, $otherRate, $(if ($k1) { [math]::Round($k1, 2) } else { 'n/a' }))
}

Measure-Phase 'baseline' 6

# 4 concurrent syscall-heavy spinners (~ user-mode process doing kernel calls)
$jobs = @()
1..4 | ForEach-Object {
  $jobs += Start-Process powershell -ArgumentList '-NoProfile','-Command',"`$sw=[Diagnostics.Stopwatch]::StartNew(); while(`$sw.Elapsed.TotalSeconds -lt 40){ `$null=[System.IO.File]::Exists('C:\Windows') }" -PassThru -WindowStyle Hidden
}
Start-Sleep -Seconds 4
Measure-Phase 'under-load' 10

foreach ($j in $jobs) { try { $j | Stop-Process -Force } catch {} }
Start-Sleep -Seconds 3
Measure-Phase 'after-load' 6
