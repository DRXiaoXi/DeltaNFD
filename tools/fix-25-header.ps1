param([string]$MainPath, [string]$BackupPath)
$ErrorActionPreference = 'Stop'

$main = [System.IO.File]::ReadAllText($MainPath, [System.Text.Encoding]::UTF8)
$backup = [System.IO.File]::ReadAllText($BackupPath, [System.Text.Encoding]::UTF8)

# 1) 从备份提取 P18 标题 + 引言块（## 25 行到 ### 25.1 之前）
$s = $backup.IndexOf('## 25. P18')
$e = $backup.IndexOf('### 25.1')
if ($s -lt 0 -or $e -lt 0) { throw 'backup markers not found' }
$hdrBlock = $backup.Substring($s, $e - $s).TrimEnd()   # 标题 + 引言

# 2) 在主文件的 ### 25.1 之前插入该块
$i = $main.IndexOf('### 25.1 ')
if ($i -lt 0) { throw '### 25.1 not found in main' }
$main = $main.Insert($i, $hdrBlock + "`r`n`r`n")

# 3) 删除文件尾部的孤立 blockquote（"> 9 项需求一次打包…"，它原属被误删的 ## 25 标题段）
$quote = '> 9 项需求一次打包'
$q = $main.LastIndexOf($quote)
if ($q -ge 0) {
    # 回退到本行行首
    $lineStart = $main.LastIndexOf("`n", $q) + 1
    $end = $main.IndexOf('⑨版本 AlphaV0.76 + 打包。', $q)
    if ($end -ge 0) {
        $end += '⑨版本 AlphaV0.76 + 打包。'.Length
        # 连同前面的空行一起删
        $main = $main.Remove($lineStart, $end - $lineStart).Insert($lineStart, '')
        Write-Output 'stray quote removed'
    }
}

[System.IO.File]::WriteAllText($MainPath, $main, (New-Object System.Text.UTF8Encoding($true)))
Write-Output 'done'
