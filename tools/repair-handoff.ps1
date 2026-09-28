param([string]$MainPath, [string]$BackupPath)
$ErrorActionPreference = 'Stop'

$lines = [System.Collections.Generic.List[string]](
    [System.IO.File]::ReadAllLines($MainPath, [System.Text.Encoding]::UTF8))
$backup = [System.IO.File]::ReadAllLines($BackupPath, [System.Text.Encoding]::UTF8)

# 1) 从备份里取 §25 标题行
$hdr25 = $backup | Where-Object { $_.StartsWith('## 25.') } | Select-Object -First 1
if (-not $hdr25) { Write-Output 'FAIL: no ## 25 header in backup'; exit 1 }
Write-Output ("backup header: " + $hdr25)

# 2) 定位当前文件里的错位 §26 块（从 '## 26. P19' 行到 '### 25.1' 行之前）
$i26 = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('## 26. P19')) { $i26 = $i; break }
}
if ($i26 -lt 0) { Write-Output 'FAIL: ## 26 block not found'; exit 1 }
$i251 = -1
for ($i = $i26; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('### 25.1 ')) { $i251 = $i; break }
}
if ($i251 -lt 0) { Write-Output 'FAIL: ### 25.1 not found'; exit 1 }

# 3) 在 '### 25.1' 之前插回 §25 标题行
$lines.Insert($i251, $hdr25)
Write-Output ("inserted ## 25 header at line " + ($i251 + 1))

# 4) 现在文件里 §26 块（P19）内容仍在原处且已多余：找到并整体剪切（## 26 行到 ### 25.1 之前）
$i26b = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('## 26. P19')) { $i26b = $i; break }
}
$i251b = -1
for ($i = $i26b; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('### 25.1 ')) { $i251b = $i; break }
}
if ($i251b -lt 0) { Write-Output 'FAIL: ### 25.1 (second pass) not found'; exit 1 }
$count = $i251b - $i26b
$removed = $lines.GetRange($i26b, $count)
$lines.RemoveRange($i26b, $count)
Write-Output ("removed misplaced P19 block: " + $count + " lines")

# 5) 把 P19 块追加到 §25.11 之后（文件 footer 之前）
$footerIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('*接手后建议')) { $footerIdx = $i; break }
}
if ($footerIdx -lt 0) { Write-Output 'FAIL: footer not found'; exit 1 }
$lines.Insert($footerIdx, '---')
$lines.Insert($footerIdx + 1, '')
for ($i = $removed.Count - 1; $i -ge 0; $i--) {
    $lines.Insert($footerIdx + 2, $removed[$i])
}
Write-Output ("moved P19 block to end, " + $removed.Count + " lines")

[System.IO.File]::WriteAllLines($MainPath, $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Output 'HANDOFF.md repaired'
