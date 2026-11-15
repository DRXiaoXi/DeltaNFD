param([Parameter(Mandatory=$true)][string]$ScriptPath,
      [Parameter(Mandatory=$true)][string]$FixtureDirectory,
      [Parameter(Mandatory=$true)][string]$ReportPath)
$ErrorActionPreference='Stop'
$checks=[ordered]@{}
function Check([string]$name,[bool]$value) { $checks[$name]=$value; if(!$value) { throw ('Failed: '+$name) } }
function Reject([string]$name,[scriptblock]$action) { $rejected=$false; try { & $action } catch { $rejected=$true }; Check $name $rejected }
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
Check 'NoParserErrors' (@($errors).Count -eq 0)
$definitions=@($ast.FindAll({param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.Contains('public static class PendulumDriverSetupV2')},$true))
Check 'OneActualImplementation' ($definitions.Count -eq 1)
$code=$definitions[0].Value
# The real class is compiled; only its pure validation/path methods are invoked.
Add-Type -TypeDefinition $code
$path='C:\Program Files\DeltaNFD Pendulum Driver\DeltaNFDPendulumTimerV1.sys'
$native='\??\'+$path
$legacy='"'+$path+'"'
Check 'NativeImagePathPreservesSpaces' ([PendulumDriverSetupV2]::NativeImagePath($path) -ceq $native)
foreach($bad in @('relative.sys','C:relative.sys','\\server\share\x.sys','C:\x.sys:stream','C:\x\..\x.sys',('"'+$path+'"'))) {
    Reject ('RejectBadPath'+$checks.Count) { [PendulumDriverSetupV2]::NativeImagePath($bad) }
}
[PendulumDriverSetupV2]::ValidateConfiguration(1,3,$native,$path,$false)
[PendulumDriverSetupV2]::ValidateConfiguration(1,3,$legacy,$path,$true)
Check 'AcceptBoundCanonicalAndLegacyRepair' $true
Reject 'LegacyCannotAuthorizeStart' { [PendulumDriverSetupV2]::ValidateConfiguration(1,3,$legacy,$path,$false) }
Reject 'WrongServiceType' { [PendulumDriverSetupV2]::ValidateConfiguration(16,3,$native,$path,$true) }
Reject 'WrongStartType' { [PendulumDriverSetupV2]::ValidateConfiguration(1,2,$native,$path,$true) }
Reject 'OtherImagePath' { [PendulumDriverSetupV2]::ValidateConfiguration(1,3,'\??\C:\other.sys',$path,$true) }

# Replace every P/Invoke declaration in an owned clone with managed fakes.
# Actual Install/Start/RepairPath bodies and their guards are otherwise unchanged.
$bodies=@{
 OpenSCManager='return new IntPtr(1);'
 OpenService='return new IntPtr(2);'
 CreateService='Fake.Creates++; Fake.Image=path; Fake.Type=type; Fake.Start=start; return new IntPtr(2);'
 StartService='Fake.Starts++; if(Fake.FailStart) { Fake.LastError=577; return false; } Fake.State=4; return true;'
 ControlService='Fake.State=1; status=new Status(); status.State=1; return true;'
 QueryServiceStatus='status=new Status(); status.State=Fake.State; return true;'
 DeleteService='Fake.Deletes++; return true;'
 QueryServiceConfig='needed=512; if(Fake.FailQuery) { Fake.LastError=5; return false; } Config c=new Config(); c.Type=Fake.Type; c.Start=Fake.Start; c.Path=IntPtr.Add(buffer,128); byte[] text=System.Text.Encoding.Unicode.GetBytes(Fake.Image+"\0"); Marshal.Copy(text,0,c.Path,text.Length); Marshal.StructureToPtr(c,buffer,false); return true;'
 ChangeServiceConfig='if(type!=0xffffffff || start!=0xffffffff || error!=0xffffffff || group!=null || tag!=IntPtr.Zero || deps!=null || account!=null || password!=null || display!=null) throw new Exception("Unexpected configuration mutation"); if(Fake.FailChange) { Fake.LastError=5; return false; } Fake.Changes++; Fake.Image=path; return true;'
 CloseServiceHandle='return true;'
}
$pattern='\[DllImport\([^\r\n]+?\)\]\s*static extern (?<return>[A-Za-z0-9]+) (?<name>[A-Za-z0-9]+)\((?<args>[^\r\n]*)\);'
$declarations=[regex]::Matches($code,$pattern)
Check 'AllTenNativeMethodsRecognized' ($declarations.Count -eq 10)
$fake=[regex]::Replace($code,$pattern,[System.Text.RegularExpressions.MatchEvaluator]{param($m)
    $name=$m.Groups['name'].Value
    if(!$bodies.ContainsKey($name)) { throw ('No fake for '+$name) }
    'static '+$m.Groups['return'].Value+' '+$name+'('+$m.Groups['args'].Value+') { '+$bodies[$name]+' }'
})
$fake=$fake.Replace('PendulumDriverSetupV2','PendulumDriverSetupV2Fake').Replace('Marshal.GetLastWin32Error()','Fake.LastError')
Check 'FakeHasNoDllImport' (!$fake.Contains('DllImport'))
$fake=$fake.Substring(0,$fake.LastIndexOf('}'))+@'
public static class Fake {
    public static uint Type,Start,State;
    public static string Image;
    public static int LastError,Changes,Starts,Creates,Deletes;
    public static bool FailChange,FailStart,FailQuery;
    public static void Reset(string image) {
        Type=1; Start=3; State=1; Image=image; LastError=Changes=Starts=Creates=Deletes=0;
        FailChange=FailStart=FailQuery=false;
    }
}
}
'@
Add-Type -TypeDefinition $fake
[PendulumDriverSetupV2Fake+Fake]::Reset($legacy)
[PendulumDriverSetupV2Fake]::RepairPath($path)
Check 'RepairsOnlyImageAndDoesNotStart' ([PendulumDriverSetupV2Fake+Fake]::Changes -eq 1 -and [PendulumDriverSetupV2Fake+Fake]::Starts -eq 0 -and [PendulumDriverSetupV2Fake+Fake]::Image -ceq $native)
[PendulumDriverSetupV2Fake]::RepairPath($path)
Check 'RepairRetryDoesNotRepeatChange' ([PendulumDriverSetupV2Fake+Fake]::Changes -eq 1)
foreach($state in @(2,3,4,7)) {
    [PendulumDriverSetupV2Fake+Fake]::Reset($legacy); [PendulumDriverSetupV2Fake+Fake]::State=$state
    Reject ('RepairRejectsState'+$state) { [PendulumDriverSetupV2Fake]::RepairPath($path) }
    Check ('NoMutationForState'+$state) ([PendulumDriverSetupV2Fake+Fake]::Changes -eq 0)
}
[PendulumDriverSetupV2Fake+Fake]::Reset('\??\C:\other.sys')
Reject 'RepairRejectsOtherServiceImage' { [PendulumDriverSetupV2Fake]::RepairPath($path) }
Check 'UnknownImageUntouched' ([PendulumDriverSetupV2Fake+Fake]::Changes -eq 0)
[PendulumDriverSetupV2Fake+Fake]::Reset($legacy)
Reject 'StartRejectsUnrepairedLegacyBeforeApi' { [PendulumDriverSetupV2Fake]::Start($path) }
Check 'NoStartForLegacy' ([PendulumDriverSetupV2Fake+Fake]::Starts -eq 0)
[PendulumDriverSetupV2Fake+Fake]::Reset($legacy); [PendulumDriverSetupV2Fake+Fake]::FailChange=$true
$message=''; try { [PendulumDriverSetupV2Fake]::RepairPath($path) } catch { $message=$_.Exception.ToString() }
Check 'ChangeFailurePreservesErrorCodeAndOperation' ($message.Contains('ChangeServiceConfig failed (Win32 5)') -and [PendulumDriverSetupV2Fake+Fake]::Changes -eq 0)
[PendulumDriverSetupV2Fake+Fake]::Reset($legacy); [PendulumDriverSetupV2Fake+Fake]::FailQuery=$true
Reject 'QueryFailureStopsRepair' { [PendulumDriverSetupV2Fake]::RepairPath($path) }
Check 'NoChangeAfterFailedQuery' ([PendulumDriverSetupV2Fake+Fake]::Changes -eq 0)
[PendulumDriverSetupV2Fake+Fake]::Reset($native); [PendulumDriverSetupV2Fake+Fake]::FailStart=$true
$message=''; try { [PendulumDriverSetupV2Fake]::Start($path) } catch { $message=$_.Exception.ToString() }
Check 'StartFailurePreservesWin32577' ($message.Contains('StartService failed (Win32 577)'))
[PendulumDriverSetupV2Fake+Fake]::Reset($native)
[PendulumDriverSetupV2Fake]::Start($path)
Check 'CanonicalStartUsesExistingService' ([PendulumDriverSetupV2Fake+Fake]::Starts -eq 1 -and [PendulumDriverSetupV2Fake+Fake]::Creates -eq 0)
[PendulumDriverSetupV2Fake+Fake]::Reset($legacy)
[PendulumDriverSetupV2Fake]::Install($path)
Check 'FreshInstallRegistersNativeImagePath' ([PendulumDriverSetupV2Fake+Fake]::Image -ceq $native -and [PendulumDriverSetupV2Fake+Fake]::Creates -eq 1 -and [PendulumDriverSetupV2Fake+Fake]::Starts -eq 1)

# Open an owned inert file through NtOpenFile. No service/driver load API is called.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PendulumOwnedPathProbe {
    [StructLayout(LayoutKind.Sequential)] struct Unicode { public ushort Length,Maximum; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public uint Length; public IntPtr Root,Name; public uint Flags; public IntPtr Security,Qos; }
    [StructLayout(LayoutKind.Sequential)] struct IoStatus { public IntPtr Status,Information; }
    [DllImport("ntdll.dll")] static extern int NtOpenFile(out IntPtr handle,uint access,ref Attributes attributes,out IoStatus status,uint share,uint options);
    [DllImport("ntdll.dll")] static extern int NtClose(IntPtr handle);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll",CharSet=CharSet.Unicode)] static extern int RtlDosPathNameToNtPathName_U_WithStatus(string path,out Unicode name,IntPtr part,IntPtr relative);
    [DllImport("ntdll.dll")] static extern void RtlFreeUnicodeString(ref Unicode name);
    public static string ConvertDos(string path) {
        Unicode name; int status=RtlDosPathNameToNtPathName_U_WithStatus(path,out name,IntPtr.Zero,IntPtr.Zero);
        if(status<0) throw new System.ComponentModel.Win32Exception((int)RtlNtStatusToDosError(status));
        try { return Marshal.PtrToStringUni(name.Buffer,name.Length/2); } finally { RtlFreeUnicodeString(ref name); }
    }
    public static uint OpenReadOnly(string path) {
        IntPtr text=Marshal.StringToHGlobalUni(path),name=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Unicode)));
        try {
            Unicode u=new Unicode(); u.Length=checked((ushort)(path.Length*2)); u.Maximum=(ushort)(u.Length+2); u.Buffer=text;
            Marshal.StructureToPtr(u,name,false);
            Attributes a=new Attributes(); a.Length=(uint)Marshal.SizeOf(typeof(Attributes)); a.Name=name; a.Flags=0x40;
            IntPtr file; IoStatus io; int status=NtOpenFile(out file,0x80000000,ref a,out io,7,0x40);
            if(status>=0) { NtClose(file); return 0; }
            return RtlNtStatusToDosError(status);
        } finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(text); }
    }
}
'@
[IO.Directory]::CreateDirectory($FixtureDirectory) | Out-Null
$fixture=Join-Path $FixtureDirectory 'inert file with spaces.txt'
[IO.File]::WriteAllText($fixture,'Owned inert path fixture; never loaded or executed.')
$good=[PendulumOwnedPathProbe]::OpenReadOnly([PendulumDriverSetupV2]::NativeImagePath($fixture))
$quoted=[PendulumOwnedPathProbe]::OpenReadOnly([PendulumOwnedPathProbe]::ConvertDos(('"'+$fixture+'"')))
Check 'RealNativePathOpensOwnedFileReadOnly' ($good -eq 0)
Check 'QuotedNativeNameReproduces123' ($quoted -eq 123)
$report=[ordered]@{ PowerShell=$PSVersionTable.PSVersion.ToString(); Checks=$checks; GoodNativePathWin32=$good; QuotedNativePathWin32=$quoted; RealServiceCalls=$false; DriverLoad=$false; TimerRequests=$false }
[IO.File]::WriteAllText($ReportPath,($report | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
Write-Output ($checks.Count.ToString()+' image-path checks passed; owned read-only probe: quoted='+$quoted+', native='+$good)
