param([string]$Dir)
$ErrorActionPreference = 'Stop'
$debloat = [System.IO.File]::ReadAllText((Join-Path $Dir 'Debloat.json'), [System.Text.Encoding]::UTF8)

Write-Output ("Debloat.json size: " + $debloat.Length)
Write-Output ("APPX occurrences: " + ([regex]::Matches($debloat, 'APPX')).Count)
Write-Output ("CMD occurrences: " + ([regex]::Matches($debloat, '"CMD"')).Count)
Write-Output ("REG occurrences: " + ([regex]::Matches($debloat, '"REG[_A-Z]*"')).Count)

# tolerant: for each APPX tweak, grab Path within the following 300 chars
Write-Output ''
Write-Output '=== APPX paths (tolerant scan) ==='
$seen = @{}
foreach ($m in [regex]::Matches($debloat, 'APPX')) {
    $chunk = $debloat.Substring($m.Index, [Math]::Min(300, $debloat.Length - $m.Index))
    $pm = [regex]::Match($chunk, '"Path":\s*"([^"]+)"')
    if ($pm.Success -and -not $seen.ContainsKey($pm.Groups[1].Value)) {
        $seen[$pm.Groups[1].Value] = $true
        Write-Output ("  " + $pm.Groups[1].Value)
    }
}

# item names + their op types (Name ... next Name)
Write-Output ''
Write-Output '=== items: name -> op types ==='
$nameMatches = [regex]::Matches($debloat, '"Name":\s*"([^"]+)"')
for ($i = 0; $i -lt $nameMatches.Count; $i++) {
    $start = $nameMatches[$i].Index
    $end = if ($i -lt $nameMatches.Count - 1) { $nameMatches[$i + 1].Index } else { $debloat.Length }
    $chunk = $debloat.Substring($start, $end - $start)
    $types = [regex]::Matches($chunk, '"TweakType":\s*"([A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    Write-Output ("  {0}: {1}" -f $nameMatches[$i].Groups[1].Value, ($types -join ','))
}

# all CMD ON-side commands (multiline tolerant)
Write-Output ''
Write-Output '=== CMD ON-side commands ==='
foreach ($m in [regex]::Matches($debloat, '"Value":\s*"((?:[^"\\]|\\.){10,400})"')) {
    $v = $m.Groups[1].Value
    if ($v -match 'cmd |reg |taskkill|rd |del |schtasks|powershell') {
        Write-Output ("  " + ($v -replace '\\\\', '\').Substring(0, [Math]::Min(240, $v.Length)))
    }
}
