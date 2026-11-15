param(
    [Parameter(Mandatory=$true)][string]$VcToolsRoot,
    [Parameter(Mandatory=$true)][string]$WdkRoot,
    [string]$SdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string]$KitVersion = '10.0.26100.0',
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$vc = (Resolve-Path -LiteralPath $VcToolsRoot).Path
$wdk = (Resolve-Path -LiteralPath $WdkRoot).Path
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
$cl = Join-Path $vc 'bin\Hostx64\x64\cl.exe'
$link = Join-Path $vc 'bin\Hostx64\x64\link.exe'
$obj = Join-Path $out 'PendulumTimer.obj'
$sys = Join-Path $out 'DeltaNFDPendulumTimerV1.sys'
$compile = @('/nologo','/c','/TC','/kernel','/W4','/WX','/O2','/GS','/guard:cf','/Zl',
    '/D_AMD64_','/DAMD64','/D_WIN64','/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A000006',
    ('/I'+(Join-Path $wdk "Include\$KitVersion\km")),
    ('/I'+(Join-Path $sdk "Include\$KitVersion\shared")),
    ('/I'+(Join-Path $sdk "Include\$KitVersion\ucrt")),
    ('/I'+(Join-Path $vc 'include')),
    ('/Fo'+$obj),(Join-Path $PSScriptRoot 'PendulumTimer.c'))
& $cl @compile
if ($LASTEXITCODE -ne 0) { throw "Driver compile failed: $LASTEXITCODE" }
$lib = Join-Path $wdk "Lib\$KitVersion\km\x64"
$argsLink = @('/nologo','/driver','/subsystem:native,10.00','/entry:GsDriverEntry','/nodefaultlib',
    '/machine:x64','/integritycheck','/nxcompat','/dynamicbase','/guard:cf','/release',
    ('/out:'+$sys),$obj,(Join-Path $lib 'ntoskrnl.lib'),(Join-Path $lib 'hal.lib'),
    (Join-Path $lib 'wdmsec.lib'),(Join-Path $lib 'BufferOverflowK.lib'))
& $link @argsLink
if ($LASTEXITCODE -ne 0) { throw "Driver link failed: $LASTEXITCODE" }
Write-Output "Built unsigned driver: $sys"
Get-FileHash -LiteralPath $sys -Algorithm SHA256
