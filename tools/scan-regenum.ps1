param([string]$Dir)
$ErrorActionPreference = 'Stop'
foreach ($f in @('RegistryTweaks.json','BasicSettingsTweaks.json','Customization.json','Debloat.json','WindowsTelemetry.json','Security.json','UpRecommended.json','Tasks.json')) {
    $t = [System.IO.File]::ReadAllText((Join-Path $Dir $f), [System.Text.Encoding]::UTF8)
    $items = [regex]::Matches($t, '"Name":\s*"([^"]+)"')
    for ($i = 0; $i -lt $items.Count; $i++) {
        $start = $items[$i].Index
        $end = if ($i -lt $items.Count - 1) { $items[$i + 1].Index } else { $t.Length }
        $chunk = $t.Substring($start, $end - $start)
        $types = [regex]::Matches($chunk, '"TweakType":\s*"([A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        if ($types -contains 'REGENUM') {
            Write-Output ("{0} :: {1}  ops: {2}" -f $f, $items[$i].Groups[1].Value, ($types -join ','))
        }
    }
}
