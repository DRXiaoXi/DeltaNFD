$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$meta = 'D:\' + [char]0x6211 + [char]0x8981 + [char]0x5E72 + [char]0x5927 + [char]0x6D3B + '\' + [char]0x4E09 + [char]0x89D2 + [char]0x5E27 + [char]0x4E0D + [char]0x6389 + [char]0x6D32 + '\src\DeltaNFD\Assets\TweakDb\meta.json'
$old = [System.IO.File]::ReadAllLines((Join-Path $dir 'meta-old.txt'), [System.Text.Encoding]::UTF8)[0]
$new = [System.IO.File]::ReadAllLines((Join-Path $dir 'meta-new.txt'), [System.Text.Encoding]::UTF8)[0]
$t = [System.IO.File]::ReadAllText($meta, [System.Text.Encoding]::UTF8)
if (-not $t.Contains($old)) { Write-Output 'OLD TEXT NOT FOUND'; exit 1 }
$t = $t.Replace($old, $new)
[System.IO.File]::WriteAllText($meta, $t, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'meta.json updated'
