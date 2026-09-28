# Enumerate ALL loaded kernel modules with full paths (EnumDeviceDrivers + GetDeviceDriverFileName),
# highlight ACE ones, and test whether the on-disk file is currently locked.
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class ModEnum {
  [DllImport("psapi.dll")] public static extern bool EnumDeviceDrivers(IntPtr[] lpImageBase, int cb, out int lpcbNeeded);
  [DllImport("psapi.dll", CharSet=CharSet.Unicode)] public static extern int GetDeviceDriverBaseNameW(IntPtr ImageBase, System.Text.StringBuilder lpBaseName, int nSize);
  [DllImport("psapi.dll", CharSet=CharSet.Unicode)] public static extern int GetDeviceDriverFileNameW(IntPtr ImageBase, System.Text.StringBuilder lpFilename, int nSize);
}
'@

$sz = [IntPtr]::Size
$needed = 0
$buf = [IntPtr[]]::new(2048)
[void][ModEnum]::EnumDeviceDrivers($buf, $sz * 2048, [ref]$needed)
$n = $needed / $sz
Write-Host ("loaded kernel modules: {0}" -f $n)

$ace = @()
for ($i = 0; $i -lt $n; $i++) {
  $nb = New-Object System.Text.StringBuilder 260
  $pf = New-Object System.Text.StringBuilder 520
  [void][ModEnum]::GetDeviceDriverBaseNameW($buf[$i], $nb, 260)
  [void][ModEnum]::GetDeviceDriverFileNameW($buf[$i], $pf, 520)
  $name = $nb.ToString()
  $path = $pf.ToString()
  if ($name -match 'ACE|302706|B9RB' -or $path -match 'ACE|302706|AntiCheat') {
    $ace += [pscustomobject]@{ Base = ('0x{0:X}' -f [long]$buf[$i]); Module = $name; Path = $path }
  }
}
Write-Host '=== ACE / session-driver modules currently loaded ==='
if ($ace.Count -gt 0) { $ace | Format-Table -AutoSize | Out-String -Width 200 | Write-Host }
else { Write-Host '(none found)' }

$target = 'C:\Program Files\AntiCheatExpert\ACE-CORE302706.sys'
Write-Host ("=== lock test: {0} ===" -f $target)
if (Test-Path $target) {
  $vi = (Get-Item $target).VersionInfo
  Write-Host ("file version: {0}  product: {1}  company: {2}  size: {3:N0} bytes" -f $vi.FileVersion, $vi.ProductName, $vi.CompanyName, (Get-Item $target).Length)
  try {
    $fs = [IO.File]::Open($target, 'Open', 'ReadWrite', 'None')
    $fs.Close()
    Write-Host 'LOCK TEST: file is NOT locked (can be opened exclusively) -> not backing a loaded image'
  } catch {
    Write-Host ("LOCK TEST: file IS locked/in use -> {0}" -f $_.Exception.InnerException.Message)
    if (-not $_.Exception.InnerException) { Write-Host ("  ({0})" -f $_.Exception.Message) }
  }
} else {
  Write-Host 'file not found'
}
