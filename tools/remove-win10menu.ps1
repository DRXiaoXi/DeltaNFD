param([string]$Dir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions

# ---- Customization.json: remove last item Win10ClassicContextMenu ----
$path = Join-Path $Dir 'Customization.json'
$t = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$idx = $t.IndexOf('"Name": "Win10ClassicContextMenu"')
if ($idx -lt 0) { Write-Output 'item not found (already removed?)'; }
else {
    $itemStart = $t.LastIndexOf('{', $idx)
    $itemEnd = $t.LastIndexOf('}')
    $comma = $t.LastIndexOf(',', $itemStart)
    if ($itemStart -lt 0 -or $itemEnd -le $itemStart -or $comma -lt 0) { Write-Output 'boundary error'; exit 1 }
    $t = $t.Remove($comma, $itemEnd - $comma + 1)
    [System.IO.File]::WriteAllText($path, $t, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output 'item removed'
}

# verify: parse + count + absence
$t2 = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$type = [System.Collections.Generic.List[object]]
$list = $ser.Deserialize($t2, $type)
$names = $list | ForEach-Object { $_['Name'] }
Write-Output ("items now: " + $list.Count)
Write-Output ("Win10ClassicContextMenu still present: " + [bool]($names -contains 'Win10ClassicContextMenu'))
Write-Output ("last item: " + $names[-1])

# ---- meta.json: remove the two keys via round-trip ----#
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
# verify round-trip readability of a Chinese value
$m2 = $ser.Deserialize($mt2, $dictType)
Write-Output ("meta keys now: " + $m2.Count + "  sample svc-useless n: " + $m2['svc.svc-useless']['n'])
