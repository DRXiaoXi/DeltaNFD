@echo off
chcp 65001 >nul
rem ============================================================
rem  Delta NFD / Delta No FPS Drops one-click installer build script
rem  Usage: run from repo root:  installer\make-installer.cmd
rem  Output: _buildcheck\installer-package-<build-id>\三角帧不掉洲_DeltaNFD_安装包_<version>_x64.exe
rem
rem  NOTE (important): WinUI3 unpackaged publish drops the app's
rem  .xbf XAML files and DeltaNFD.pri from the publish dir
rem  (known dotnet-publish gap). Without them the app crashes at
rem  startup with "Cannot locate resource 'ms-appx:///...'".
rem  This script copies them from the build output after publish.
rem ============================================================
setlocal
cd /d "%~dp0\.." || goto :fail

if not exist "src\DeltaNFD\Assets\VCRedistRepair_unpacked\Installer.cmd" goto :missing_runtime
if not exist "src\DeltaNFD\Assets\VCRedistRepair_unpacked\payload_manifest_sha256.csv" goto :missing_runtime
if not exist "src\DeltaNFD\Assets\VCRedist2015-2022-x64.exe" goto :missing_runtime
if not exist "src\DeltaNFD\Assets\VCRedist2015-2022-x86.exe" goto :missing_runtime

set "BUILD_ID=%RANDOM%%RANDOM%"
set "OBJ=%CD%\_buildcheck\installer-obj-%BUILD_ID%"
set "BINBASE=%CD%\_buildcheck\installer-bin-%BUILD_ID%"
set "BIN=%BINBASE%\x64\Release\net8.0-windows10.0.19041.0\win-x64"
set "PUB=%CD%\_buildcheck\installer-publish-%BUILD_ID%"
set "PKG=%CD%\_buildcheck\installer-package-%BUILD_ID%"

echo [1/4] Build Release x64 ...
dotnet build src\DeltaNFD\DeltaNFD.csproj -c Release -p:Platform=x64 -p:IncludeBundledBackground=false "-p:BaseIntermediateOutputPath=%OBJ%/" "-p:BaseOutputPath=%BINBASE%/"
if errorlevel 1 goto :fail

echo [2/4] Publish self-contained ...
dotnet publish src\DeltaNFD\DeltaNFD.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:IncludeBundledBackground=false "-p:BaseIntermediateOutputPath=%OBJ%/" "-p:BaseOutputPath=%BINBASE%/" -o "%PUB%"
if errorlevel 1 goto :fail
if exist "%PUB%\Assets\background.*" (
  echo [ERROR] Public installer still contains a bundled background image.
  goto :fail
)

echo [3/4] Copy missing WinUI resources (xbf + app pri) ...
rem  /S: page xbf files live in subfolders (Views\...) matching source layout
robocopy "%BIN%" "%PUB%" *.xbf DeltaNFD.pri /S /NFL /NDL /NJH /NP
if errorlevel 8 goto :fail

set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (
  echo [ERROR] Inno Setup 6 not found. Install it first.
  goto :fail
)

echo [4/4] Compile installer ...
pushd installer
"%ISCC%" "/O%PKG%" "/DPublishDir=%PUB%" DeltaNFD.iss
if errorlevel 1 ( popd & goto :fail )
popd

echo.
echo 完成：%PKG%\三角帧不掉洲_DeltaNFD_安装包_[版本]_x64.exe
goto :eof

:fail
echo.
echo BUILD FAILED.
exit /b 1

:missing_runtime
echo [ERROR] VC++ runtime repair assets are missing from src\DeltaNFD\Assets.
echo Restore VCRedistRepair_unpacked\Installer.cmd, its payload manifest, and the x86/x64 2015-2022 installers.
exit /b 1
