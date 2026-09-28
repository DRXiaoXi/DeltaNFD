# Snapshot System (PID 4) threads twice and report which kernel modules the hot threads belong to.
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class DrvEnum {
  [DllImport("psapi.dll")] public static extern bool EnumDeviceDrivers(IntPtr[] lpImageBase, int cb, out int lpcbNeeded);
  [DllImport("psapi.dll")] public static extern int GetDeviceDriverBaseName(IntPtr ImageBase, System.Text.StringBuilder lpBaseName, int nSize);
}
'@

# Build a base-address -> driver-name map so thread start addresses can be resolved to modules.
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
$bases.Sort()
$bases.Reverse()  # descending: first base <= address is the owning module

function Resolve-Module([long]$addr) {
  foreach ($b in $bases) {
    if ($b -le $addr) {
      $delta = $addr - $b
      if ($delta -lt 0x2000000) {  # within 32MB of module base
        return ('{0}+0x{1:x}' -f $mods[$b], $delta)
      }
      return '?'
    }
  }
  return '?'
}

$p = Get-Process -Id 4
$snap = @{}
foreach ($t in $p.Threads) { $snap[$t.Id] = $t.TotalProcessorTime.TotalMilliseconds }
Start-Sleep -Seconds 3
$p.Refresh()

$rows = foreach ($t in $p.Threads) {
  if ($snap.ContainsKey($t.Id)) {
    $d = $t.TotalProcessorTime.TotalMilliseconds - $snap[$t.Id]
    if ($d -gt 20) {
      [pscustomobject]@{
        TID      = $t.Id
        DeltaMS  = [math]::Round($d)
        Module   = Resolve-Module ([long]$t.StartAddress)
        State    = $t.ThreadState
      }
    }
  }
}

$total = 0
foreach ($t in $p.Threads) { if ($snap.ContainsKey($t.Id)) { $total += ($t.TotalProcessorTime.TotalMilliseconds - $snap[$t.Id]) } }
Write-Host ('System(PID 4) threads consuming CPU over 3s, total = {0:N0} ms ({1:N2} cores)' -f $total, ($total / 3000))
if ($rows) { $rows | Sort-Object DeltaMS -Descending | Select-Object -First 10 | Format-Table -AutoSize | Out-String -Width 120 }
else { Write-Host 'no individual thread above 20ms threshold' }
