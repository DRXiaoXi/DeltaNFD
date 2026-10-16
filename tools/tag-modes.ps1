param([string]$Root)
$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$metaPath = Join-Path $Root 'Assets\TweakDb\meta.json'
$lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines($metaPath, $utf8))

function Add-Mode([string]$KeyName, [string]$Mode) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains('"' + $KeyName + '": {')) {
            if ($lines[$i].Contains('"m":')) { Write-Output ("skip (has m): " + $KeyName); return }
            $lines[$i] = $lines[$i].Replace(': { ', ': { "m": "' + $Mode + '", ')
            Write-Output ("tagged " + $KeyName + " -> " + $Mode)
            return
        }
    }
    Write-Output ("NOT FOUND: " + $KeyName)
}

# enable-type items (optimization = turning something ON)
$enableKeys = @(
    'HAGS', 'TimerResolution', 'HPETName', 'Win32PrioritySeparation',
    'SystemResponsiveness', 'svccombine', 'PagingSetttings', 'TSX',
    'TakeOwnership', 'ShowAllIconsInTaskBar', 'OldBatteryFlyout',
    'OldPhotoViewer', 'OldContextMenu', 'ContextMenuDelay'
)
foreach ($k in $enableKeys) { Add-Mode $k 'enable' }

# debloat section: whole-section default = remove
Add-Mode 'sec.debloat' 'remove'

[System.IO.File]::WriteAllLines($metaPath, $lines, $utf8)

# validate
$json = [System.IO.File]::ReadAllText($metaPath, $utf8) | ConvertFrom-Json
$bad = 0
foreach ($k in $enableKeys) {
    $v = $json.$k
    if ($null -eq $v) { Write-Output ("missing: " + $k); $bad++ }
    elseif ($v.m -ne 'enable') { Write-Output ("mode not set: " + $k); $bad++ }
}
if ($json.'sec.debloat'.m -ne 'remove') { Write-Output 'sec.debloat mode missing'; $bad++ }
Write-Output ("validation failures: " + $bad)
