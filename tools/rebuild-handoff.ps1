param([string]$MainPath, [string]$BackupPath)
$ErrorActionPreference = 'Stop'

$main = [System.IO.File]::ReadAllText($MainPath, [System.Text.Encoding]::UTF8)
$backup = [System.IO.File]::ReadAllText($BackupPath, [System.Text.Encoding]::UTF8)

function Get-Segment([string]$Text, [string]$StartMarker, [string]$EndMarker) {
    $s = $Text.IndexOf($StartMarker)
    if ($s -lt 0) { throw "start marker not found: $StartMarker" }
    $e = $Text.IndexOf($EndMarker, $s)
    if ($e -lt 0) { $e = $Text.Length }
    return $Text.Substring($s, $e - $s)
}

# ---- from BACKUP: the correct 24.4 + 24.5 sections (lost in the main file) ----
$seg2445 = Get-Segment $backup '### 24.4 ' '## 25. '

# ---- from CURRENT file: the good P18 body (25.1 .. 25.11) ----
$i251 = $main.IndexOf('### 25.1 ')
$i237 = $main.IndexOf('### 23.7 ')
$seg25body = $main.Substring($i251, $main.Length - $i251)   # 25.1 .. end (incl. 26 block + footer line)

# split P18 body into: 25.1-25.9 part and the tail (25.10 dup / 25.11 / ## 26 / 26.x / ## 25 stray / footer)
$i2510first = $seg25body.IndexOf('### 25.10 ')
$seg25bodyGood = $seg25body.Substring(0, $i2510first)                          # 25.1 .. 25.9 (correct)
$segTail = $seg25body.Substring($i2510first)                                   # dup 25.10 + 25.11 + ## 26 + 26.x + ## 25 stray + footer

# from segTail: extract the good 25.10/25.11 (the SECOND occurrence set lives after 25.9) and the ## 26 block
# simpler: rebuild tail = take everything AFTER the stray first '### 25.10' up to '### 25.11' end,
# but drop the duplicated stray 25.10 block that sits before 25.1
# -> strategy: keep seg25bodyGood (25.1-25.9), then from segTail remove the FIRST stray '### 25.10' block
#    (it ends right before '### 25.1 ' inside segTail? no - locate next '### 25.1 ' marker)
$iStray = $segTail.IndexOf('### 25.10 ')
$iNext251 = $segTail.IndexOf('### 25.1 ', $iStray)
if ($iNext251 -ge 0) {
    $segTail = $segTail.Substring($iNext251)   # drop the stray block before the real 25.1
}

# split off the ## 26 block from the tail (it must move AFTER 25.11)
$i26 = $segTail.IndexOf('## 26. P19')
$iFooter = $segTail.IndexOf('*接手后建议')
$seg26 = $segTail.Substring($i26, $segTail.Length - $i26)
$seg2511 = $segTail.Substring(0, $i26)

# drop footer from seg26 tail pieces
$iF = $seg26.IndexOf('*接手后建议')
if ($iF -ge 0) { $seg26 = $seg26.Substring(0, $iF) }
$iF2 = $seg2511.IndexOf('*接手后建议')
if ($iF2 -ge 0) { $seg2511 = $seg2511.Substring(0, $iF2) }

# ---- assemble final document ----
$cut = $main.IndexOf('### 25.10 ')
$head = $main.Substring(0, $cut)          # everything up to (not incl.) the stray 25.10
# head must end right after 24.3; remove any trailing whitespace-only line issues
$head = $head.TrimEnd() + "`r`n`r`n"

$final = $head + $seg2445.TrimEnd() + "`r`n`r`n" + $seg25bodyGood.TrimEnd() + "`r`n`r`n" + $seg2511.TrimEnd() + "`r`n`r`n" + $seg26.TrimEnd() + "`r`n`r`n" +
    "---`r`n`r`n" + '*接手后建议的第一件事：`git init` + 提交基线；第二件事：跑一遍 `tools\test-run2.ps1` 确认你的环境不被杀软拦。祝顺利。—— 晓夕*' + "`r`n"

[System.IO.File]::WriteAllText($MainPath, $final, (New-Object System.Text.UTF8Encoding($true)))
Write-Output ("repaired, total length: " + $final.Length)
