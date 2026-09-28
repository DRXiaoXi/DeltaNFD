$ErrorActionPreference = 'Stop'
# 按引擎同款逻辑模拟：真实注册表枚举 → 人工剔除 2026 命名条目 → 应产生 2 条 2026 黄色提醒
function Test-Redist([bool]$Remove2026) {
    $all = @()
    $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    foreach ($item in (Get-ItemProperty $roots -ErrorAction SilentlyContinue)) {
        if ($item.DisplayName -and $item.DisplayName -match 'Visual C\+\+') {
            $arch = if ($item.DisplayName -match '\(x64\)') { 'x64' } else { 'x86' }
            $all += [pscustomobject]@{ Name = $item.DisplayName; Arch = $arch; Ver = [string]$item.DisplayVersion }
        }
    }
    if ($Remove2026) { $all = $all | Where-Object { $_.Name -notmatch '2026' } }

    # v14 分支（2015/2017/2019/2022/2026 命名）取最高版本
    $v14 = $all | Where-Object { $_.Name -match '20(15|17|19|22|26)' }
    $baselineOk = $true
    foreach ($arch in @('x64','x86')) {
        $best = $v14 | Where-Object { $_.Arch -eq $arch } | ForEach-Object { [version]($_.Ver -replace '^[vV]','') } | Sort-Object -Descending | Select-Object -First 1
        if (-not $best -or $best -lt [version]'14.42.34433.0') { $baselineOk = $false }
    }

    $result = @()
    foreach ($arch in @('x64','x86')) {
        $branchOk = $baselineOk
        $has2026 = [bool]($all | Where-Object { $_.Arch -eq $arch -and $_.Name -match '2026' -and $_.Name -notmatch 'Visual C\+\+ v14' })
        if ($branchOk -and -not $has2026) { $result += "2026 ($arch) 未安装（推荐 >= 14.51.36247.0）" }
    }
    return $result
}

Write-Output ("real machine (has 2026): warnings = " + (Test-Redist $false).Count)
Write-Output ("simulated missing 2026 : warnings = " + (Test-Redist $true).Count)
Test-Redist $true | ForEach-Object { Write-Output ("  would warn: " + $_) }
