param([ValidateSet('Install','Start','RepairPath','Remove','Status')][string]$Action = 'Status')
$ErrorActionPreference = 'Stop'
if (-not ('PendulumBootPolicyV1' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PendulumBootPolicyV1 {
    [StructLayout(LayoutKind.Sequential)] struct CI { public uint Length, Options; }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int kind, ref CI info, uint length, out uint returned);
    public static bool Read() {
        CI info=new CI { Length=8 }; uint returned;
        int status=NtQuerySystemInformation(103,ref info,8,out returned);
        if(status!=0 || returned!=8 || info.Length!=8) throw new InvalidOperationException("Current-boot code-integrity status could not be verified.");
        return (info.Options & 2)!=0;
    }
}
'@
}
$name = 'DeltaNFDPendulumTimerV1'
$targetDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DeltaNFD Pendulum Driver'
$target = Join-Path $targetDirectory ($name+'.sys')
$receiptPath = Join-Path $targetDirectory 'active-driver.json'
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\'+$name
if ($Action -eq 'Status') {
    $registered = Test-Path -LiteralPath $serviceKey
    $registration = if ($registered) { Get-ItemProperty -LiteralPath $serviceKey } else { $null }
    $testMode = try { [PendulumBootPolicyV1]::Read() } catch { $null }
    [pscustomobject]@{ TestSigningEnabled=$testMode; ServiceRegistered=(Test-Path -LiteralPath $serviceKey); DriverFile=$target;
        DriverFileExists=(Test-Path -LiteralPath $target); ReceiptExists=(Test-Path -LiteralPath $receiptPath);
        ImagePath=$registration.ImagePath; ServiceType=$registration.Type; StartType=$registration.Start }
    return
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请以管理员身份运行驱动准备脚本。' }
$os = [Environment]::OSVersion.Version
if ($os.Major -ne 10 -or $os.Build -lt 17763 -or $os.Build -ge 22000 -or ![Environment]::Is64BitOperatingSystem) {
    throw '此实验驱动仅用于 Windows 10 x64 1809 及以后版本。'
}
if ($Action -in @('Install','Start') -and ![PendulumBootPolicyV1]::Read()) { throw '请先开启测试签名模式（TESTSIGNING）并手动重启电脑；本次启动尚未允许测试签名驱动，未安装或启动驱动。' }
foreach ($path in @([Environment]::GetFolderPath('ProgramFiles'),$targetDirectory)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '驱动目标目录不能是重解析点。'
    }
}
if (-not ('PendulumDriverSetupV2' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
public static class PendulumDriverSetupV2 {
    [StructLayout(LayoutKind.Sequential)] struct Status { public uint Type,State,Controls,Exit,Specific,Checkpoint,WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct Config { public uint Type,Start,Error; public IntPtr Path,Group; public uint Tag; public IntPtr Dependencies,Account,Display; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenSCManager(string m,string d,uint a);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenService(IntPtr m,string n,uint a);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateService(IntPtr m,string n,string display,uint access,uint type,uint start,uint error,string path,string group,IntPtr tag,string deps,string account,string password);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool StartService(IntPtr s,int n,IntPtr args);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool ControlService(IntPtr s,uint control,out Status status);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatus(IntPtr s,out Status status);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(IntPtr s);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryServiceConfig(IntPtr s,IntPtr buffer,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ChangeServiceConfig(IntPtr s,uint type,uint start,uint error,string path,string group,IntPtr tag,string deps,string account,string password,string display);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr h);
    static Win32Exception Failure(string operation) {
        int code=Marshal.GetLastWin32Error();
        return new Win32Exception(code,operation+" failed (Win32 "+code+"): "+new Win32Exception(code).Message);
    }
    public static string NativeImagePath(string path) {
        if(String.IsNullOrEmpty(path) || path.Length<4 || !Char.IsLetter(path[0]) || path[1]!=':' || path[2]!='\\' ||
            path.IndexOf('"')>=0 || path.IndexOf('\0')>=0 || path.IndexOf(':',2)>=0 ||
            !String.Equals(Path.GetFullPath(path),path,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected a canonical local drive path without quotes or arguments.");
        // A driver image name is an NT object path, not an executable command line.
        return @"\??\"+path;
    }
    public static void ValidateConfiguration(uint type,uint start,string image,string path,bool allowLegacy) {
        string expected=NativeImagePath(path);
        if(type!=1 || start!=3 || !(String.Equals(image,expected,StringComparison.OrdinalIgnoreCase) ||
            (allowLegacy && String.Equals(image,"\""+path+"\"",StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Service identity, start type or ImagePath changed; refusing unknown configuration.");
    }
    static string CheckConfig(IntPtr s,string path,bool allowLegacy) {
        IntPtr b=Marshal.AllocHGlobal(8192);
        try { uint needed; if(!QueryServiceConfig(s,b,8192,out needed)) throw Failure("QueryServiceConfig");
            Config c=(Config)Marshal.PtrToStructure(b,typeof(Config));
            string image=Marshal.PtrToStringUni(c.Path);
            ValidateConfiguration(c.Type,c.Start,image,path,allowLegacy); return image;
        } finally { Marshal.FreeHGlobal(b); }
    }
    static void RequireStopped(IntPtr s) {
        Status status; if(!QueryServiceStatus(s,out status)) throw Failure("QueryServiceStatus");
        if(status.State!=1) throw new InvalidOperationException("Driver must be STOPPED before repairing ImagePath; nothing was changed.");
    }
    public static void Install(string path) {
        string image=NativeImagePath(path);
        IntPtr m=OpenSCManager(null,null,3); if(m==IntPtr.Zero) throw Failure("OpenSCManager");
        try { IntPtr s=CreateService(m,"DeltaNFDPendulumTimerV1","DeltaNFD Pendulum Timer (Win10 experimental)",0xf01ff,1,3,1,image,null,IntPtr.Zero,null,null,null);
            if(s==IntPtr.Zero) throw Failure("CreateService");
            try { if(!StartService(s,0,IntPtr.Zero)) throw Failure("StartService"); } finally { CloseServiceHandle(s); }
        } finally { CloseServiceHandle(m); }
    }
    public static void Remove() {
        IntPtr m=OpenSCManager(null,null,1); if(m==IntPtr.Zero) throw new Win32Exception();
        try { IntPtr s=OpenService(m,"DeltaNFDPendulumTimerV1",0x10024);
            if(s==IntPtr.Zero) { if(Marshal.GetLastWin32Error()==1060) return; throw new Win32Exception(); }
            try { Status state; if(!ControlService(s,1,out state) && Marshal.GetLastWin32Error()!=1062) throw new Win32Exception();
                for(int i=0;i<50;i++) { if(!QueryServiceStatus(s,out state)) throw new Win32Exception(); if(state.State==1) break; Thread.Sleep(100); }
                if(state.State!=1) throw new InvalidOperationException("驱动仍在运行；请先关闭帧格并退出工具，记录和文件已保留。");
                if(!DeleteService(s)) throw new Win32Exception();
            } finally { CloseServiceHandle(s); }
        } finally { CloseServiceHandle(m); }
    }
    public static void Start(string path) {
        IntPtr m=OpenSCManager(null,null,1); if(m==IntPtr.Zero) throw Failure("OpenSCManager");
        try { IntPtr s=OpenService(m,"DeltaNFDPendulumTimerV1",0x11); if(s==IntPtr.Zero) throw Failure("OpenService");
            try { CheckConfig(s,path,false); if(!StartService(s,0,IntPtr.Zero) && Marshal.GetLastWin32Error()!=1056) throw Failure("StartService"); }
            finally { CloseServiceHandle(s); }
        } finally { CloseServiceHandle(m); }
    }
    public static void RepairPath(string path) {
        IntPtr m=OpenSCManager(null,null,1); if(m==IntPtr.Zero) throw Failure("OpenSCManager");
        try { IntPtr s=OpenService(m,"DeltaNFDPendulumTimerV1",7); if(s==IntPtr.Zero) throw Failure("OpenService");
            try { string old=CheckConfig(s,path,true); RequireStopped(s);
                if(String.Equals(old,NativeImagePath(path),StringComparison.OrdinalIgnoreCase)) return;
                if(!ChangeServiceConfig(s,0xffffffff,0xffffffff,0xffffffff,NativeImagePath(path),null,IntPtr.Zero,null,null,null,null)) throw Failure("ChangeServiceConfig");
                CheckConfig(s,path,false);
            }
            finally { CloseServiceHandle(s); }
        } finally { CloseServiceHandle(m); }
    }
}
'@
}
if ($Action -eq 'Install') {
    if ((Test-Path -LiteralPath $serviceKey) -or (Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $receiptPath)) {
        throw '已有同名服务、驱动或未完成记录；请先 Status 核对。旧版带引号路径可用 RepairPath 修正，不重复 Install 或覆盖既有内容。'
    }
    $source = Join-Path $PSScriptRoot ($name+'.sys')
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $lock = [IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($lock.Length -gt 1048576) { throw '驱动文件大小异常。' }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $manifest.DriverSHA256) { throw '驱动包 SHA256 不匹配。' }
        $signature = Get-AuthenticodeSignature -LiteralPath $source
        if ($signature.Status -ne 'Valid') { throw '驱动签名未被当前系统信任；请先按实验说明准备测试环境，脚本不会修改证书信任或启动安全设置。' }
        [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $targetDirectory -AclObject $acl
        $copy = [IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { $lock.CopyTo($copy); $copy.Flush($true) } finally { $copy.Dispose() }
        $receipt = @{ Service=$name; Path=$target; SHA256=$manifest.DriverSHA256; CreatedUtc=[DateTime]::UtcNow.ToString('o'); State='Prepared' }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json))
        $record = [IO.FileStream]::new($receiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { $record.Write($bytes,0,$bytes.Length); $record.Flush($true) } finally { $record.Dispose() }
    } finally { $lock.Dispose() }
    [PendulumDriverSetupV2]::Install($target)
    Write-Output '驱动已安装并启动；尚未申请精度。请在钟摆页检测并保存持有值。'
} else {
    if (!(Test-Path -LiteralPath $receiptPath)) { throw '没有本工具的驱动准备记录，不处理未知服务。' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.Service -ne $name -or $receipt.Path -ne $target) { throw '准备记录归属不匹配。' }
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $receipt.SHA256) {
        throw '驱动文件缺失或变化，保留记录供核对。'
    }
    if (Test-Path -LiteralPath $serviceKey) {
        $service = Get-ItemProperty -LiteralPath $serviceKey
        [PendulumDriverSetupV2]::ValidateConfiguration($service.Type,$service.Start,$service.ImagePath,$target,$true)
    }
    if ($Action -eq 'Start' -or $Action -eq 'RepairPath') {
        if (!(Test-Path -LiteralPath $serviceKey)) { throw '驱动尚未登记，请先 Install。' }
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '驱动文件不能是重解析点。' }
        $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $lock = [IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            if ($receipt.SHA256 -ne $manifest.DriverSHA256 -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $manifest.DriverSHA256) { throw '驱动与原实验包SHA不匹配，保留记录供核对。' }
            $signature = Get-AuthenticodeSignature -LiteralPath $target
            if ($signature.Status -ne 'Valid') { throw ('驱动签名未通过：'+$signature.Status+'；'+$signature.StatusMessage) }
            if ($Action -eq 'RepairPath') {
                $image = [PendulumDriverSetupV2]::NativeImagePath($target)
                if ($service.ImagePath -eq $image) { Write-Output '驱动路径已经正确；可执行 Start。'; return }
                $backup = Join-Path $targetDirectory ('imagepath-repair-'+[Guid]::NewGuid().ToString('N')+'.json')
                $repair = @{ Service=$name; Path=$target; SHA256=$receipt.SHA256; OldImagePath=$service.ImagePath; NewImagePath=$image; CreatedUtc=[DateTime]::UtcNow.ToString('o'); State='Prepared' }
                $bytes = [Text.Encoding]::UTF8.GetBytes(($repair | ConvertTo-Json))
                $record = [IO.FileStream]::new($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
                try { $record.Write($bytes,0,$bytes.Length); $record.Flush($true) } finally { $record.Dispose() }
                [PendulumDriverSetupV2]::RepairPath($target)
                Write-Output ('驱动路径已修正，旧路径记录已保存：'+$backup+'。尚未启动驱动，请执行 Start。')
            } else {
                if ($service.ImagePath -ne [PendulumDriverSetupV2]::NativeImagePath($target)) { throw '检测到旧版带引号路径，请先执行 RepairPath，再执行 Start。' }
                [PendulumDriverSetupV2]::Start($target)
                Write-Output '已启动原登记驱动；尚未申请精度，请在钟摆页检测或开启帧格。'
            }
        } finally { $lock.Dispose() }
        return
    }
    [PendulumDriverSetupV2]::Remove()
    Remove-Item -LiteralPath $target
    $archive = Join-Path $targetDirectory ('removed-driver-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::Move($receiptPath,$archive)
    Write-Output '驱动服务已停止并删除，本工具驱动文件已移除；准备记录已保留归档。'
}
