param(
    [string]$MetaPath,
    [string]$OldText,
    [string]$NewText
)
$ErrorActionPreference = 'Stop'
$t = [System.IO.File]::ReadAllText($MetaPath, [System.Text.Encoding]::UTF8)
if (-not $t.Contains($OldText)) { Write-Output 'OLD TEXT NOT FOUND'; exit 1 }
$t = $t.Replace($OldText, $NewText)
[System.IO.File]::WriteAllText($MetaPath, $t, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'meta.json updated'
