param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../_buildcheck/frame-pacing/native'))
$ErrorActionPreference = 'Stop'
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswherePath)) { throw 'Visual Studio C++ Build Tools are required; this script does not install them.' }
$installation = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC x64 toolset not found.' }
$builder = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
$destination = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/') + '\'
& $builder (Join-Path $PSScriptRoot 'FramePacingLab.vcxproj') /nologo /m:1 /nr:false /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$destination" /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Write-Output (Join-Path $destination 'FramePacingLab.exe')
