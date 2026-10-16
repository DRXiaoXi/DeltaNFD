param([string]$Dir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue

# ---- verify Customization.json (strip full-line // comments first, as the engine tolerates them) ----
$path = Join-Path $Dir 'Customization.json'
$t = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$lines = $t -split "`r?`n"
$stripped = ($lines | Where-Object { $_.TrimStart() -notmatch '^//' }) -join "`n"
$type = [System.Collections.Generic.List[object]]
$list = $ser.Deserialize($stripped, $type)
$names = @($list | ForEach-Object { $_['Name'] })
Write-Output ("Customization items: " + $list.Count)
Write-Output ("Win10ClassicContextMenu present: " + [bool]($names -contains 'Win10ClassicContextMenu'))
Write-Output ("last three: " + (($names | Select-Object -Last 3) -join ', '))

# ---- meta.json round-trip (was skipped last run) ----
$metaPath = Join-Path $Dir 'meta.json'
$mt = [System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8)
$dictType = [System.Collections.Generic.Dictionary[string, object]]
$dict = $ser.Deserialize($mt, $dictType)
$removed = @()
foreach ($k in @('Win10ClassicContextMenu','Win10ClassicContextMenuDesc')) {
    if ($dict.Remove($k)) { $removed += $k }
}
$mt2 = $ser.Serialize($dict)
[System.IO.File]::WriteAllText($metaPath, $mt2, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("meta keys removed: " + ($removed -join ','))
$m2 = $ser.Deserialize($mt2, $dictType)
Write-Output ("meta keys now: " + $m2.Count)
Write-Output ("meta chinese ok: " + $m2['svc.svc-useless']['n'])
Write-Output ("meta win10 keys gone: " + (-not ($m2.ContainsKey('Win10ClassicContextMenu') -or $m2.ContainsKey('Win10ClassicContextMenuDesc'))))
