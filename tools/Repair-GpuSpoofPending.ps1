param([switch]$CheckOnly, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function Read-SettingsDocument([string]$Path) {
    $info = Get-Item -LiteralPath $Path
    if ($info.Length -gt 4MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '设置文件过大或为链接，停止处理。' }
    for ($parent = $info.Directory; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '配置目录包含链接，停止处理。' }
    }
    $raw = [IO.File]::ReadAllText($Path)
    $settings = ConvertFrom-Json -InputObject $raw
    if ($null -eq $settings -or $settings -is [array]) { throw '设置不是有效的 JSON 对象。' }
    return @{ Raw = $raw; Settings = $settings; Bytes = [IO.File]::ReadAllBytes($Path) }
}

function Get-Field($Object, [string]$Name, $Default) {
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    return $p.Value
}

function Assert-RecoveryCandidate($Settings, [string]$ClassGuid, [string]$Description, [bool]$HasBackup) {
    if ((Get-Field $Settings 'TempSpoofRestorePending' $false) -isnot [bool]) { throw '待还原标记类型异常。' }
    if (-not (Get-Field $Settings 'TempSpoofRestorePending' $false)) { throw '没有待还原记录，无需处理。' }
    $path = Get-Field $Settings 'TempSpoofRestorePath' ''
    if ($path -isnot [string] -or $path -notmatch '^SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_[0-9A-F]{4}&[^\\\r\n]+\\[^\\\r\n]+$') { throw '待还原目标不是可核对的 PCI 设备路径。' }
    if ($ClassGuid -ine '{4d36e968-e325-11ce-bfc1-08002be10318}') { throw '目标不是显示适配器。' }
    if ($Description.Length -ge 4096 -or $Description -notmatch '^@oem[0-9]+\.inf,%[^;\r\n\\]+%;[^\r\n]+$') { throw '目标仍是伪装名称或无法确认原生驱动描述，未清除记录。' }
    if ($HasBackup) { throw '原始型号备份仍存在，请使用工具的“恢复原始型号”，不要跳过还原。' }
    if ((Get-Field $Settings 'GpuSpoofFrameConfigured' $false) -or (Get-Field $Settings 'GpuSpoofEnabled' $false)) { throw '仍有帧格显卡伪装配置，请先在工具中关闭后再处理。' }
}

function Convert-RecoveredSettings([string]$Raw) {
    $document = ConvertFrom-Json -InputObject $Raw
    $patterns = @{
        TempSpoofRestorePending = '(?<!\\)"TempSpoofRestorePending"\s*:\s*(?:true|false)\b'
        TempSpoofRestorePath = '(?<!\\)"TempSpoofRestorePath"\s*:\s*"(?:[^"\\]|\\.)*"'
        TempSpoofRestoreId = '(?<!\\)"TempSpoofRestoreId"\s*:\s*"(?:[^"\\]|\\.)*"'
    }
    foreach ($name in @('TempSpoofRestorePending', 'TempSpoofRestorePath', 'TempSpoofRestoreId')) {
        if ($name -eq 'TempSpoofRestoreId' -and $null -eq $document.PSObject.Properties[$name]) { continue }
        $matches = [regex]::Matches($Raw, $patterns[$name])
        if ($matches.Count -ne 1) { throw ('字段重复或格式异常：' + $name) }
        $replacement = if ($name -eq 'TempSpoofRestorePending') { '"TempSpoofRestorePending": false' } else { '"' + $name + '": ""' }
        $Raw = [regex]::Replace($Raw, $patterns[$name], $replacement)
    }
    $verified = ConvertFrom-Json -InputObject $Raw
    if ($verified.TempSpoofRestorePending -ne $false -or $verified.TempSpoofRestorePath -ne '') { throw '配置转换核验失败。' }
    return $Raw
}

function Test-HasBackup([string]$Path, [string]$Target) {
    if (-not [IO.File]::Exists($Path)) { return $false }
    $info = Get-Item -LiteralPath $Path
    if ($info.Length -gt 8MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '备份文件无法安全读取。' }
    $raw = [IO.File]::ReadAllText($Path)
    if ($raw.TrimStart()[0] -ne '[') { throw '备份文件格式异常，不把读取失败当作没有备份。' }
    $entries = ConvertFrom-Json -InputObject $raw
    $id = ('HKLM\' + $Target + '\DeviceDesc').ToLowerInvariant()
    foreach ($entry in @($entries)) {
        if ($null -eq $entry) { continue }
        if ([string]::IsNullOrWhiteSpace([string](Get-Field $entry 'Id' '')) -and
            ([string]::IsNullOrWhiteSpace([string](Get-Field $entry 'KeyPath' '')) -or [string]::IsNullOrWhiteSpace([string](Get-Field $entry 'ValueName' '')))) { throw '备份条目无法核对，不视为没有备份。' }
        if ((Get-Field $entry 'Id' '') -ieq $id) { return $true }
        if ((Get-Field $entry 'Hive' 'HKLM') -ieq 'HKLM' -and (Get-Field $entry 'KeyPath' '') -ieq $Target -and (Get-Field $entry 'ValueName' '') -ieq 'DeviceDesc') { return $true }
    }
    return $false
}

function Assert-ToolStopped {
    if (@(Get-Process | Where-Object { $_.ProcessName -eq 'DeltaNFD' -or $_.ProcessName -like 'DeltaNFD.*' }).Count -gt 0) {
        throw '请完全退出 Delta NFD（包括托盘）后再运行；脚本不会结束任何进程。'
    }
}

function Read-Device([string]$Path) {
    if ($Path -notmatch '^SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_[0-9A-F]{4}&[^\\\r\n]+\\[^\\\r\n]+$') { throw '无效的设备路径。' }
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Path)
    try {
        if ($null -eq $key) { throw '待还原设备不存在，未清除记录。' }
        return @{ ClassGuid = [string]$key.GetValue('ClassGUID'); Description = [string]$key.GetValue('DeviceDesc') }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Write-DurableNew([string]$Path, [byte[]]$Bytes) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Commit-Recovery([string]$Path, $Before, [scriptblock]$Validate) {
    $encoding = New-Object Text.UTF8Encoding($false)
    $replacement = $encoding.GetBytes((Convert-RecoveredSettings $Before.Raw))
    $backup = $Path + '.gpu-restore-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.bak'
    Write-DurableNew $backup $Before.Bytes
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backup)) -ne [Convert]::ToBase64String($Before.Bytes)) { throw '备份回读失败，未修改配置。' }
    & $Validate
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -ne [Convert]::ToBase64String($Before.Bytes)) { throw ('配置已变化，未覆盖。备份：' + $backup) }
    $temporary = $Path + '.gpu-restore-' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Write-DurableNew $temporary $replacement
        # Windows PowerShell 5 can coerce a null backup string to an invalid empty path.
        # Keep the atomic replacement's additional rollback copy alongside the verified backup.
        [IO.File]::Replace($temporary, $Path, ($backup + '.atomic'))
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -ne [Convert]::ToBase64String($replacement)) { throw ('修改后回读不一致，请保留备份核对：' + $backup) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    return $backup
}

function Invoke-SelfTest {
    $target = 'SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_28E0\4&test&0'
    $raw = '{"TempSpoofRestorePending":true,"TempSpoofRestorePath":"' + $target.Replace('\','\\') + '","Unknown":{"Enabled":true},"Unicode":"测试","RuntimeGuardEnabled":true}'
    $settings = ConvertFrom-Json -InputObject $raw
    $native = '@oem118.inf,%nvidia_dev.28e0%;NVIDIA GeForce RTX 4060 Laptop GPU'
    Assert-RecoveryCandidate $settings '{4d36e968-e325-11ce-bfc1-08002be10318}' $native $false
    $converted = Convert-RecoveredSettings $raw
    if ($converted -notmatch '"Unknown":\{"Enabled":true\}' -or $converted -notmatch '"Unicode":"测试"' -or $converted -notmatch '"RuntimeGuardEnabled":true') { throw 'Unrelated settings were changed' }
    $count = 2
    foreach ($case in @(@('not-a-display', $native, $false), @('{4d36e968-e325-11ce-bfc1-08002be10318}', 'GTX 750', $false), @('{4d36e968-e325-11ce-bfc1-08002be10318}', $native, $true))) {
        $refused = $false
        try { Assert-RecoveryCandidate $settings $case[0] $case[1] $case[2] } catch { $refused = $true }
        if (-not $refused) { throw 'Unsafe candidate accepted' }; $count++
    }
    $refused = $false
    try { Convert-RecoveredSettings ($raw.TrimEnd('}') + ',"TempSpoofRestorePending":true}') | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw 'Duplicate field accepted' }; $count++
    $root = Join-Path ([IO.Path]::GetTempPath()) ('DeltaNFD-script-test-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $path = Join-Path $root 'settings.json'
    [IO.File]::WriteAllText($path, $raw)
    $before = Read-SettingsDocument $path
    $backup = Commit-Recovery $path $before { }
    if ([IO.File]::ReadAllText($backup) -ne $raw -or [IO.File]::ReadAllText($path) -ne $converted) { throw 'Atomic commit/backup failure' }; $count++
    [IO.File]::WriteAllText($path, $raw)
    $refused = $false
    try { Commit-Recovery $path (Read-SettingsDocument $path) { throw 'device changed' } | Out-Null } catch { $refused = $true }
    if (-not $refused -or [IO.File]::ReadAllText($path) -ne $raw) { throw 'Validation failure changed settings' }; $count++
    Write-Host ('Self-test: ' + $count + ' passed. Fixtures only: ' + $root)
}

try {
    if ($SelfTest) { Invoke-SelfTest; exit 0 }
    Assert-ToolStopped
    $settingsPath = Join-Path $env:APPDATA 'Delta NFD\settings.json'
    $before = Read-SettingsDocument $settingsPath
    if (-not (Get-Field $before.Settings 'TempSpoofRestorePending' $false)) { Write-Host '没有临时显卡伪装待还原记录，无需处理。'; exit 0 }
    $target = [string](Get-Field $before.Settings 'TempSpoofRestorePath' '')
    $backupPath = Join-Path (Split-Path -Parent $settingsPath) 'backups.json'
    $device = Read-Device $target
    Assert-RecoveryCandidate $before.Settings $device.ClassGuid $device.Description (Test-HasBackup $backupPath $target)
    Write-Host ('已核对目标：' + $target)
    Write-Host ('当前型号：' + $device.Description)
    Write-Host '仅备份配置并清除临时还原记录；不写注册表、不重载显卡、不删除备份、不结束进程。'
    if ($CheckOnly) { Write-Host '检查通过，未修改文件。'; exit 0 }
    if ((Read-Host '确认清除失效记录请输入 YES，其余输入取消') -cne 'YES') { Write-Host '已取消。'; exit 0 }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($settingsPath).ToUpperInvariant()))).Replace('-','') } finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Global\DeltaNFD.Settings.' + $digest))
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw '设置正在由其他进程写入，请退出工具后重试。' }
        $validate = {
            Assert-ToolStopped
            $again = Read-Device $target
            if ($again.Description -cne $device.Description) { throw '设备描述已变化，未覆盖配置。' }
            Assert-RecoveryCandidate $before.Settings $again.ClassGuid $again.Description (Test-HasBackup $backupPath $target)
        }
        $backup = Commit-Recovery $settingsPath $before $validate
        Write-Host ('处理成功，可重新打开工具。原配置备份：' + $backup) -ForegroundColor Green
    } finally { if ($acquired) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
} catch { Write-Host ('未完成：' + $_.Exception.Message) -ForegroundColor Red; exit 1 }
