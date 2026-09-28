param([string]$Dir)
$ErrorActionPreference = 'Stop'
if (-not $Dir) { Write-Output 'usage: -Dir <TweakDb path>'; exit 1 }
$raw = [System.IO.File]::ReadAllText((Join-Path $Dir 'Tasks.json'), [System.Text.Encoding]::UTF8)

foreach ($id in @('WebCamTelemetry','TaskXBOX','TaskDiagonsis','TaskFlighting','TaskAnalize','TaskProxy','TaskRC','TaskClean','TaskHDD')) {
    $i = $raw.IndexOf('"Name": "' + $id + '"')
    if ($i -lt 0) { Write-Output ($id + ': NOT in Tasks.json'); continue }
    $next = $raw.IndexOf('"Name": "', $i + 10)
    if ($next -lt 0) { $next = $raw.Length }
    $chunk = $raw.Substring($i, $next - $i)
    $types = [System.Text.RegularExpressions.Regex]::Matches($chunk, '"TweakType": "([A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value }
    $paths = [System.Text.RegularExpressions.Regex]::Matches($chunk, '"Path": "([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    Write-Output ("== {0}  types: {1}" -f $id, ($types -join ','))
    foreach ($p in $paths) { Write-Output ("   path: " + $p) }
}
