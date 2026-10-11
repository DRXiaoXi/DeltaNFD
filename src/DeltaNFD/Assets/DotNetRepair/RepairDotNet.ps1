# Standalone offline entry: usable even when the main application's .NET host cannot start.
[CmdletBinding()]
param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
try {
    $expectedHash = '2DC2F346A4BCB53AB15AB6AB84348C02EB31B0A3D4E8E06AAC73B4C89F1B001AF01943ED4C5735FA5585F1DD731A942D2034577DFE937F4454367FF2D6EDF929'
    $installer = Join-Path $PSScriptRoot 'dotnet-runtime-8.0.31-win-x64.exe'
    if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Offline .NET installer is missing. Reinstall Delta NFD.' }
    for ($dir = Get-Item -LiteralPath $PSScriptRoot; $null -ne $dir; $dir = $dir.Parent) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installer path rejected.' }
    }
    $file = Get-Item -LiteralPath $installer
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -le 0 -or $file.Length -gt 157286400) { throw 'Invalid installer file.' }
    $verified = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA512]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($verified)).Replace('-', '') }
        finally { $sha.Dispose() }
        if ($hash -ne $expectedHash) { throw 'Offline .NET installer SHA512 mismatch. Reinstall Delta NFD.' }
        if ($VerifyOnly) { Write-Output 'Verified Microsoft .NET Runtime 8.0.31 x64 offline payload.'; exit 0 }
        Add-Type -AssemblyName System.Windows.Forms
        $choice = [System.Windows.Forms.MessageBox]::Show(
            "将打开微软 .NET 8 x64 官方安装器，可在其中安装或修复。不会卸载其他 .NET 版本。`n`n本工具自带的运行时文件缺损需重新安装三角帧不掉洲，安装系统 .NET 无法替换这些文件。`n`n是否继续？",
            '.NET 环境修复', 'YesNo', 'Question', 'Button2')
        if ($choice -ne 'Yes') { exit 0 }
        $folder = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Delta NFD\dotnet-repair\' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($folder) | Out-Null
        $installLog = Join-Path $folder 'offline-install.log'
        # Interactive installer offers its own repair/install choices; never force reboot or terminate it.
        $process = Start-Process -FilePath $installer -Verb RunAs -ArgumentList @('/norestart', '/log', ('"' + $installLog + '"')) -PassThru
        $process.WaitForExit()
        $code = $process.ExitCode
        $message = switch ($code) {
            0 { '微软安装器已完成，请重新检测工具 .NET 环境。' }
            3010 { '微软安装器要求重启，请保存工作并手动重启后重新检测。' }
            1641 { '微软安装器报告重启，请重新检测。' }
            1602 { '已取消安装或修复。' }
            default { "安装或修复未完成，退出码：$code。" }
        }
        [System.Windows.Forms.MessageBox]::Show($message + "`n日志：" + $installLog, '.NET 环境修复') | Out-Null
        if ($code -notin @(0, 3010, 1641, 1602)) { exit 1 }
    } finally { $verified.Dispose() }
} catch {
    Write-Error $_
    exit 1
}
