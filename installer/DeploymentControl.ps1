param(
    [string]$InstallRoot,
    [ValidateSet('install', 'uninstall')][string]$Mode = 'install',
    [string]$ReportPath,
    [switch]$LibraryOnly
)
$ErrorActionPreference = 'Stop'

# Native image queries work even when the WMI service is broken. No termination by image name.
function Initialize-ProcessQuery {
    if ('DeltaDeployment.Native' -as [type]) { return }
    # Windows PowerShell 5 otherwise resolves System.dll from the current directory,
    # which may contain the product's .NET 8 facade rather than .NET Framework.
    $compilerOptions = @{}
    if ($PSVersionTable.PSVersion.Major -le 5) {
        $compilerOptions.ReferencedAssemblies = @([System.ComponentModel.Win32Exception].Assembly.Location)
    }
    Add-Type @compilerOptions -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace DeltaDeployment {
    public static class Native {
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder image, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetNamedPipeServerProcessId(IntPtr handle, out uint pid);
        public static string Image(int pid) {
            var handle = OpenProcess(0x1000, false, pid);
            if(handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try { var image = new StringBuilder(32768); int length = image.Capacity;
                if(!QueryFullProcessImageName(handle, 0, image, ref length)) throw new System.ComponentModel.Win32Exception();
                return image.ToString();
            } finally { CloseHandle(handle); }
        }
    }
}
'@
}

function Test-UnderRoot([string]$ImagePath, [string]$Root) {
    return [IO.Path]::GetFullPath($ImagePath).StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Format-DeploymentFailure([string]$Stage, [Exception]$Failure) {
    $detail = $Failure.Message
    for ($inner = $Failure; $null -ne $inner; $inner = $inner.InnerException) {
        if ($inner -is [ComponentModel.Win32Exception]) {
            $detail += ' (Win32=' + $inner.NativeErrorCode + ')'
            break
        }
    }
    return $Stage + '：' + $detail
}

function Get-ScopedProcessIdentity($Process, [string]$Image, [string]$Root) {
    if (-not (Test-UnderRoot $Image $Root)) { return }
    $started = $Process.StartTime.ToUniversalTime().Ticks
    return [pscustomobject]@{ Process = $Process; Image = $Image; StartTicks = $started }
}

function Get-ProductProcesses([string]$Root) {
    $names = @('DeltaNFD', 'DeltaOptimizer', 'DeltaNFD.Backend.Ace', 'DeltaNFD.Backend.Runtime',
        'DeltaNFD.Backend.Gpu', 'DeltaNFD.Backend.CpuFrame', 'DeltaNFD.Backend.SystemMaintenance', 'DeltaNFD.Backend.Procman',
        'DeltaNFD.OfflineHelper')
    foreach ($name in $names) {
        foreach ($process in [Diagnostics.Process]::GetProcessesByName($name)) {
            try {
                try {
                    if ($process.HasExited) { continue }
                    $image = [DeltaDeployment.Native]::Image($process.Id)
                    $identity = Get-ScopedProcessIdentity $process $image $Root
                    if ($null -ne $identity) {
                        $identity
                        $process = $null # The caller now owns the Process object.
                    }
                } catch {
                    $failure = $_.Exception
                    if ($process.HasExited) { continue }
                    throw (Format-DeploymentFailure ("核对 $name 的进程身份，PID=" + $process.Id) $failure)
                }
            } finally { if ($null -ne $process) { $process.Dispose() } }
        }
    }
}

function Get-DeploymentPipeName([string]$Root) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Root).TrimEnd('\').ToUpperInvariant())
        return 'DeltaNFD.Deployment.' + [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 24)
    } finally { $sha.Dispose() }
}

function Request-CooperativeExit([string]$Root, [string]$Operation, $Owner) {
    $pipe = $null
    $connecting = [Diagnostics.Stopwatch]::StartNew()
    # A running GUI can still be initializing its listener. Do not immediately close/minimize it.
    while ($connecting.ElapsedMilliseconds -lt 15000) {
        if ($Owner.Process.HasExited) { return $null }
        $candidate = New-Object IO.Pipes.NamedPipeClientStream('.', (Get-DeploymentPipeName $Root), [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $candidate.Connect(500)
            $pipe = $candidate
            break
        }
        catch [TimeoutException] { $candidate.Dispose() }
        catch { $candidate.Dispose(); throw }
    }
    if ($null -eq $pipe) { return $null }
    try {
        [uint32]$serverPid = 0
        if (-not [DeltaDeployment.Native]::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(), [ref]$serverPid) -or
            $serverPid -ne $Owner.Process.Id -or $Owner.Process.HasExited -or
            $Owner.Process.StartTime.ToUniversalTime().Ticks -ne $Owner.StartTicks) { throw '交接通道进程身份不匹配。' }
        $writer = New-Object IO.StreamWriter($pipe, (New-Object Text.UTF8Encoding($false)), 1024, $true)
        $reader = New-Object IO.StreamReader($pipe, [Text.Encoding]::UTF8, $false, 1024, $true)
        try {
            $writer.AutoFlush = $true
            # Version 2 explicitly requests retaining persistent settings. An old frontend
            # must reject it rather than silently performing the old full recovery.
            $requestLine = if ($Operation -eq 'uninstall') { 'UNINSTALL/2' } else { 'INSTALL/1' }
            $writer.WriteLine($requestLine)
            $read = $reader.ReadLineAsync()
            if (-not $read.Wait(600000)) { throw '安全交接仍未完成（10 分钟）。未强杀执行进程，请查看应用日志后重试。' }
            $response = $read.Result
            if ($response -notmatch '^(0|2|10)\|([A-Za-z0-9+/=]*)$') { throw '未收到有效的安全交接结果。' }
            return [pscustomobject]@{ Code = [int]$Matches[1]; Detail = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[2])) }
        } finally { $reader.Dispose(); $writer.Dispose() }
    } finally { $pipe.Dispose() }
}

function Wait-ProductExit($Identity, [int]$Milliseconds = 15000) {
    # Exit between enumeration and waiting is successful, not a termination failure.
    if ($Identity.Process.HasExited) { return $true }
    return $Identity.Process.WaitForExit($Milliseconds)
}

function Invoke-HeadlessRecovery([string]$Root, [string]$Operation) {
    # Setup must never launch the installed binary: older builds treat new switches as a GUI launch.
    if ($Operation -ne 'uninstall') { throw '安装检查不能启动主程序；独立恢复仅用于卸载。' }
    $request = [Guid]::NewGuid().ToString('N')
    $report = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Delta NFD\logs\deployment-' + $request + '.txt')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $Root 'DeltaNFD.exe'
    $start.Arguments = '--prepare-removal-preserving-settings --deployment-mode ' + $Operation + ' --deployment-request ' + $request
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (-not $child.WaitForExit(600000)) { throw '交接程序仍未退出（10 分钟）。尚未替换或删除文件，未强杀执行进程。' }
        if (-not [IO.File]::Exists($report)) { throw ('交接程序未生成本次报告，退出码=' + $child.ExitCode + '。请修复安装后重试。') }
        $lines = [IO.File]::ReadAllLines($report)
        if ($lines.Length -lt 1 -or $lines[0] -ne ($request + '|' + $child.ExitCode)) { throw '交接报告身份或退出码不匹配。' }
        return [pscustomobject]@{ Code = $child.ExitCode; Detail = ($lines | Select-Object -Skip 1) -join [Environment]::NewLine }
    } finally { $child.Dispose() }
}

function Invoke-Deployment([string]$Root, [string]$Operation) {
    $Root = [IO.Path]::GetFullPath($Root)
    if ($Root.Length -gt [IO.Path]::GetPathRoot($Root).Length) { $Root = $Root.TrimEnd('\') }
    $stage = '首次检查安装目录的运行进程'
    $identities = @()
    $result = $null
    try {
        $identities = @(Get-ProductProcesses $Root)
        $mains = @($identities | Where-Object { [IO.Path]::GetDirectoryName($_.Image) -ieq $Root -and [IO.Path]::GetFileName($_.Image) -in @('DeltaNFD.exe', 'DeltaOptimizer.exe') })
        foreach ($main in $mains) {
            $ownerLabel = $main.Image + '，PID=' + $main.Process.Id
            $stage = '联系运行中的旧版（' + $ownerLabel + '）'
            if ($main.Process.HasExited) { continue }
            try { $result = Request-CooperativeExit $Root $Operation $main }
            catch { if ($main.Process.HasExited) { continue }; throw }
            if ($null -eq $result) {
                # Older builds do not implement the protocol. Closing may minimize to tray;
                # explicitly explain that case, rather than destroying in-memory restore data.
                $stage = '等待旧版正常关闭（' + $ownerLabel + '）'
                try { if (-not $main.Process.HasExited) { [void]$main.Process.CloseMainWindow() } }
                catch { if (-not $main.Process.HasExited) { throw } }
                if (-not (Wait-ProductExit $main 10000)) { throw ('旧版程序不支持安全交接，请从托盘选择退出后重试。PID=' + $main.Process.Id + '；路径=' + $main.Image) }
            } else {
                $stage = '旧版返回交接结果（' + $ownerLabel + '）'
                if ($result.Code -notin @(0, 10)) { throw $result.Detail }
                $stage = '等待已交接的旧版退出（' + $ownerLabel + '）'
                if (-not (Wait-ProductExit $main)) { throw ('交接已完成，但主程序尚未退出：PID=' + $main.Process.Id + '；' + $main.Image) }
            }
        }
        if ($null -eq $result) {
            if ($Operation -eq 'install') {
                # Upgrade retains all settings and journals. An exited old version needs no cold start.
                $result = [pscustomobject]@{ Code = 0; Detail = '安装目录主程序已退出；设置和恢复证据保留，未启动旧程序。' }
            } elseif (Test-Path -LiteralPath (Join-Path $Root 'DeltaNFD.exe') -PathType Leaf) {
                $stage = '运行卸载前退出检查'
                $result = Invoke-HeadlessRecovery $Root $Operation
                if ($result.Code -notin @(0, 10)) { throw $result.Detail }
            } else { throw '主程序缺失，无法确认后台组件已安全退出。请在原目录修复安装后再卸载；数据未删除。' }
        }
        # Enumerate again: workers may have appeared while the first snapshot was taken.
        $stage = '写入前再次核对安装目录的运行进程'
        $remaining = @(Get-ProductProcesses $Root)
        try {
            $live = @($remaining | Where-Object { -not $_.Process.HasExited })
            if ($live.Count) { throw ('安装目录仍有执行者，未强杀、未替换文件：' + (($live | ForEach-Object { $_.Image + ' (PID=' + $_.Process.Id + ')' }) -join '；')) }
        } finally { foreach ($identity in $remaining) { $identity.Process.Dispose() } }
        return $result
    } catch {
        throw (Format-DeploymentFailure $stage $_.Exception)
    } finally { foreach ($identity in $identities) { $identity.Process.Dispose() } }
}

if ($LibraryOnly) { return }
$code = 2
try {
    if (-not $InstallRoot -or -not $ReportPath) { throw '交接参数缺失。' }
    try { Initialize-ProcessQuery } catch { throw (Format-DeploymentFailure '初始化进程身份检查' $_.Exception) }
    $result = Invoke-Deployment $InstallRoot $Mode
    $code = $result.Code
    $detail = $result.Detail
} catch {
    $detail = $_.Exception.Message
}
if ($ReportPath) {
    try {
        # The UTF-8 BOM preserves Chinese text when Inno reads the report.
        [IO.File]::WriteAllText($ReportPath, $detail, (New-Object Text.UTF8Encoding($true)))
        exit $code
    } catch {
        $failure = $_.Exception.GetBaseException()
        # Inno's output capture may use an OEM code page different from the child console.
        [Console]::Error.WriteLine(('DELTANFD_REPORT_WRITE_FAILED {0} 0x{1:X8}' -f $failure.GetType().Name, $failure.HResult))
        exit 2
    }
}
[Console]::Error.WriteLine('DELTANFD_REPORT_PATH_MISSING')
exit 2
