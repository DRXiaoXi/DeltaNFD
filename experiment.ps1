# Decisive experiment:
#  Part 1: decompose System(PID4)'s reported CPU-time growth into
#          (a) sum of ALL processes' privileged(kernel) time  vs  (b) System's own reported delta
#          (c) machine-wide privileged time from raw perf counters (includes DPC/ISR)
#  Part 2: kill ACE-Tray / wegame / wegame_env
#  Part 3: re-measure System's growth for 12s
$ErrorActionPreference = 'Continue'

function Snap {
  $sumPriv = 0.0
  foreach ($pr in Get-Process) {
    try { $sumPriv += $pr.PrivilegedProcessorTime.TotalMilliseconds } catch {}
  }
  $sys = (Get-Process -Id 4).TotalProcessorTime.TotalMilliseconds
  $raw = Get-CimInstance Win32_PerfRawData_Counters_ProcessorInformation -Filter "Name='_Total'"
  [pscustomobject]@{
    priv    = $sumPriv
    sys     = $sys
    ts      = [double]$raw.Timestamp_Sys100NS
    kern    = [double]$raw.PercentPrivilegedTime   # 100ns units, cumulative
    dpc     = [double]$raw.PercentDPCTime
    isr     = [double]$raw.PercentInterruptTime
  }
}

$a = Snap; Start-Sleep -Seconds 8; $b = Snap
$secs = ($b.ts - $a.ts) / 1e7
$lp = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$allProcMs = $b.priv - $a.priv
$sysMs     = $b.sys  - $a.sys
$kernMs    = ($b.kern - $a.kern) / 1e4
$dpcMs     = ($b.dpc  - $a.dpc)  / 1e4
$isrMs     = ($b.isr  - $a.isr)  / 1e4
Write-Host ('=== baseline over {0:N2}s ({1} logical CPUs) ===' -f $secs, $lp)
Write-Host ('  System(PID4) reported CPU growth : {0,8:N0} ms  ({1:N2} cores)' -f $sysMs, ($sysMs / ($secs * 1000)))
Write-Host ('  sum ALL processes kernel time     : {0,8:N0} ms  ({1:N2} cores)' -f $allProcMs, ($allProcMs / ($secs * 1000)))
Write-Host ('  machine-wide privileged time      : {0,8:N0} ms  ({1:N2} cores)' -f $kernMs, ($kernMs / ($secs * 1000)))
Write-Host ('  machine-wide DPC time             : {0,8:N0} ms  ({1:N2} cores)' -f $dpcMs, ($dpcMs / ($secs * 1000)))
Write-Host ('  machine-wide ISR time             : {0,8:N0} ms  ({1:N2} cores)' -f $isrMs, ($isrMs / ($secs * 1000)))

Write-Host ''
Write-Host '=== killing ACE / launcher processes ==='
foreach ($n in 'ACE-Tray.exe', 'wegame.exe', 'wegame_env.exe') {
  $out = taskkill /f /im $n 2>&1
  Write-Host ("taskkill {0}: {1}" -f $n, ($out -join ' ').Trim())
}
Start-Sleep -Seconds 3

$c = Snap; Start-Sleep -Seconds 10; $d = Snap
$secs2 = ($d.ts - $c.ts) / 1e7
$sysMs2    = $d.sys  - $c.sys
$allProc2  = $d.priv - $c.priv
$kernMs2   = ($d.kern - $c.kern) / 1e4
$dpcMs2    = ($d.dpc  - $c.dpc)  / 1e4
$isrMs2    = ($d.isr  - $c.isr)  / 1e4
Write-Host ('=== after kill, over {0:N2}s ===' -f $secs2)
Write-Host ('  System(PID4) reported CPU growth : {0,8:N0} ms  ({1:N2} cores)' -f $sysMs2, ($sysMs2 / ($secs2 * 1000)))
Write-Host ('  sum ALL processes kernel time     : {0,8:N0} ms  ({1:N2} cores)' -f $allProc2, ($allProc2 / ($secs2 * 1000)))
Write-Host ('  machine-wide privileged time      : {0,8:N0} ms  ({1:N2} cores)' -f $kernMs2, ($kernMs2 / ($secs2 * 1000)))
Write-Host ('  machine-wide DPC time             : {0,8:N0} ms  ({1:N2} cores)' -f $dpcMs2, ($dpcMs2 / ($secs2 * 1000)))
Write-Host ('  machine-wide ISR time             : {0,8:N0} ms  ({1:N2} cores)' -f $isrMs2, ($isrMs2 / ($secs2 * 1000)))
