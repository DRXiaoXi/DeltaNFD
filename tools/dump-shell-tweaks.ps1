param([string]$Dir)
$ErrorActionPreference = 'Stop'
$custom = [System.IO.File]::ReadAllText((Join-Path $Dir 'Customization.json'), [System.Text.Encoding]::UTF8)

# 找到 Win10 右键菜单 / 剪切板 / 其他含 CLSID 或 /ve 的条目，完整输出
$items = [regex]::Matches($custom, '"Name":\s*"([^"]+)"')
for ($i = 0; $i -lt $items.Count; $i++) {
    $start = $items[$i].Index
    $end = if ($i -lt $items.Count - 1) { $items[$i + 1].Index } else { $custom.Length }
    $chunk = $custom.Substring($start, $end - $start)
    if ($chunk -match '86ca1aa0|/ve|CLSID') {
        Write-Output ('==== ' + $items[$i].Groups[1].Value + ' ====')
        Write-Output ($chunk.Substring(0, [Math]::Min(2200, $chunk.Length)))
        Write-Output ''
    }
}

Write-Output '=== all /ve Key usages across DB files ==='
foreach ($f in @('Customization.json','BasicSettingsTweaks.json','Debloat.json','WindowsTelemetry.json','RegistryTweaks.json','Security.json','UpRecommended.json')) {
    $t = [System.IO.File]::ReadAllText((Join-Path $Dir $f), [System.Text.Encoding]::UTF8)
    $c = ([regex]::Matches($t, '"Key":\s*"/ve"')).Count
    if ($c -gt 0) { Write-Output ("  " + $f + ": " + $c) }
}
