$ErrorActionPreference = 'Stop'
# 用与引擎一致的宽松解析验证新 allServices.json
$path = 'D:\' + [char]0x6211 + [char]0x8981 + [char]0x5E72 + [char]0x5927 + [char]0x6D3B + '\' + [char]0x4E09 + [char]0x89D2 + [char]0x5E27 + [char]0x4E0D + [char]0x6389 + [char]0x6D32 + '\src\DeltaNFD\Assets\TweakDb\allServices.json'
$raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$type = [System.Collections.Generic.List[object]]
$list = $ser.Deserialize($raw, $type)
Write-Output ("parsed services: " + $list.Count)

$blocked = 0
$withWarn = 0
$warnLines = 0
$blockedWithDefLE1 = 0
foreach ($s in $list) {
    $name = $s['ServiceName']
    $isBlocked = $s['IsBlocked']
    $b11 = $s['IsBlocked11']
    $def = [int]$s['DefaultStartMode']
    if ($isBlocked -or $b11) {
        $blocked++
        if ($def -le 1) { $blockedWithDefLE1++ }
    }
    if ($s.ContainsKey('WillBrake')) {
        $w = $s['WillBrake']
        if ($w -and $w.Count -gt 0) { $withWarn++; $warnLines += $w.Count }
    }
}
Write-Output ("blocked remaining: " + $blocked + "  (blocked with DefaultStartMode<=1: " + $blockedWithDefLE1 + "  must be 0)")
Write-Output ("services with WillBrake warnings: " + $withWarn + "  total warning lines: " + $warnLines)

# 抽查关键服务的封锁标志（必须全部为 False）
Write-Output ""
Write-Output "--- critical services spot check (all must be blocked=False) ---"
foreach ($n in @('ACPI','pci','disk','volmgr','Ntfs','FltMgr','DcomLaunch','RpcSs','WinDefend','TrustedInstaller','msiserver','usbhub','NetBT','sppsvc')) {
    $hit = $list | Where-Object { $_['ServiceName'] -eq $n } | Select-Object -First 1
    if ($hit) { Write-Output ("  {0,-16} blocked={1} b11={2} def={3}" -f $n, $hit['IsBlocked'], $hit['IsBlocked11'], $hit['DefaultStartMode']) }
}

# 抽查警告内容
Write-Output ""
Write-Output "--- WillBrake sample: KeyIso / dhcp ---"
$k = $list | Where-Object { $_['ServiceName'] -eq 'KeyIso' } | Select-Object -First 1
if ($k -and $k.ContainsKey('WillBrake')) { Write-Output ("  KeyIso: " + ($k['WillBrake'] -join ' | ')) }
