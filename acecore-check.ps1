Write-Host '=== ACE-CORE / ACE-BASE service registry ==='
foreach ($k in 'ACE-CORE', 'ACE-BASE') {
  $key = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$k" -ErrorAction SilentlyContinue
  if ($key) { Write-Host "$k EXISTS: ImagePath=$($key.ImagePath) Start=$($key.Start)" }
  else { Write-Host "$k not registered" }
}

Write-Host '=== ACE sys files on disk ==='
Get-ChildItem 'C:\Program Files\AntiCheatExpert' -Recurse -Filter '*.sys' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
Get-ChildItem 'C:\Windows\System32\drivers\ACE-*.sys' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }

Write-Host '=== drivers whose ImagePath references ACE / random session driver ==='
Get-CimInstance Win32_SystemDriver |
  Where-Object { $_.PathName -match 'ACE|B9RB320706|AntiCheat' } |
  Format-Table Name, State, StartMode, PathName -AutoSize | Out-String -Width 220 | Write-Host

Write-Host '=== current burn ==='
$s1 = (Get-Process -Id 4).TotalProcessorTime.TotalMilliseconds
Start-Sleep 8
$s2 = (Get-Process -Id 4).TotalProcessorTime.TotalMilliseconds
Write-Host ("burn rate = {0:N2} cores" -f (($s2 - $s1) / 8000))
