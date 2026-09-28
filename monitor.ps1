# 60s continuous monitor: catches intermittent burns that short samples miss.
# Tracks three separate channels for System (PID 4):
#   thr  = sum of its kernel threads' CPU (ms in the ~1s bucket)
#   pt   = process total CPU time delta (includes everything the OS attributes to System)
#   dpc/isr = system-wide DPC / interrupt time (% of all 32 logical CPUs)
param([int]$Seconds = 60)

$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class DrvEnum {
  [DllImport("psapi.dll")] public static extern bool EnumDeviceDrivers(IntPtr[] lpImageBase, int cb, out int lpcbNeeded);
  [DllImport("psapi.dll")] public static extern int GetDeviceDriverBaseName(IntPtr ImageBase, System.Text.StringBuilder lpBaseName, int nSize);
}
'@

$sz = [IntPtr]::Size
$needed = 0
$buf = [IntPtr[]]::new(2048)
[void][DrvEnum]::EnumDeviceDrivers($buf, $sz * 2048, [ref]$needed)
$n = $needed / $sz
$bases = New-Object System.Collections.Generic.List[long]
$mods = @{}
for ($i = 0; $i -lt $n; $i++) {
  $sb = New-Object System.Text.StringBuilder 260
  [void][DrvEnum]::GetDeviceDriverBaseName($buf[$i], $sb, 260)
  $b = [long]$buf[$i]
  $bases.Add($b)
  $mods[$b] = $sb.ToString()
}
$bases.Sort(); $bases.Reverse()

function Resolve-Module([long]$addr) {
  foreach ($b in $bases) {
    if ($b -le $addr) {
      $delta = $addr - $b
      if ($delta -lt 0x2000000) { return ('{0}+0x{1:x}' -f $mods[$b], $delta) }
      return '?'
    }
  }
  return '?'
}

$lp = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$prevT = @{}
$prevPt = 0
$hot = @{}          # tid -> @{addr; ms}
$buckets = New-Object System.Collections.Generic.List[object]
$deadline = (Get-Date).AddSeconds($Seconds)

while ((Get-Date) -lt $deadline) {
  $p = Get-Process -Id 4
  $cur = @{}
  foreach ($t in $p.Threads) {
    $cur[$t.Id] = $t.TotalProcessorTime.TotalMilliseconds
    if (-not $hot.ContainsKey($t.Id) -and $t.StartAddress) {
      $hot[$t.Id] = @{ addr = [long]$t.StartAddress; ms = 0.0 }
    }
  }
  $thrMs = 0.0
  foreach ($tid in @($cur.Keys)) {
    if ($prevT.ContainsKey($tid)) {
      $d = $cur[$tid] - $prevT[$tid]
      if ($d -gt 0) { $thrMs += $d; if ($hot.ContainsKey($tid)) { $hot[$tid].ms += $d } }
    }
  }
  $ptNow = $p.TotalProcessorTime.TotalMilliseconds
  $ptD = if ($prevPt) { $ptNow - $prevPt } else { 0 }
  $prevPt = $ptNow
  $prevT = $cur

  $dpc = 0.0; $isr = 0.0
  try {
    $dpc = (Get-Counter '\Processor(_Total)\% DPC Time' -SampleInterval 1 -MaxSamples 1).CounterSamples[0].CookedValue
    $isr = (Get-Counter '\Processor(_Total)\% Interrupt Time' -SampleInterval 1 -MaxSamples 1).CounterSamples[0].CookedValue
  } catch {}

  $buckets.Add([pscustomobject]@{
    time = (Get-Date -Format 'HH:mm:ss')
    thr  = [math]::Round($thrMs)
    pt   = [math]::Round($ptD)
    dpc  = [math]::Round($dpc, 2)
    isr  = [math]::Round($isr, 2)
  })
}

Write-Host ("=== per-second buckets (thr/pt in ms per bucket; dpc/isr in % of all {0} logical CPUs) ===" -f $lp)
$buckets | Format-Table -AutoSize | Out-String -Width 100 | Write-Host

$totThr = ($buckets | Measure-Object thr -Sum).Sum
$totPt  = ($buckets | Measure-Object pt  -Sum).Sum
$maxThr = ($buckets | Measure-Object thr -Maximum).Maximum
$avgDpc = ($buckets | Measure-Object dpc -Average).Average
$maxDpc = ($buckets | Measure-Object dpc -Maximum).Maximum
$avgIsr = ($buckets | Measure-Object isr -Average).Average
$maxIsr = ($buckets | Measure-Object isr -Maximum).Maximum
Write-Host ("summary: threads-total={0:N0}ms  pt-total={1:N0}ms  max-thread-bucket={2:N0}ms  avg/max DPC={3:N2}/{4:N2}%  avg/max ISR={5:N2}/{6:N2}%" -f $totThr, $totPt, $maxThr, $avgDpc, $maxDpc, $avgIsr, $maxIsr)
Write-Host ("(100% DPC/ISR = all {0} cores; 3.1% = one core busy)" -f $lp)

Write-Host '=== top System threads by CPU over the window ==='
$hot.GetEnumerator() |
  Where-Object { $_.Value.ms -gt 50 } |
  Sort-Object { $_.Value.ms } -Descending |
  Select-Object -First 10 @{n='TID';e={$_.Key}}, @{n='cpuMS';e={[math]::Round($_.Value.ms)}}, @{n='Start';e={Resolve-Module $_.Value.addr}} |
  Format-Table -AutoSize | Out-String -Width 120 | Write-Host
