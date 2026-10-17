@echo off
chcp 65001 >nul
rem ============================================================
rem  Delta NFD / Delta No FPS Drops one-click installer build script
rem  Usage: run from repo root:  installer\make-installer.cmd
rem  Output: _buildcheck\installer-package-<build-id>\三角帧不掉洲_DeltaNFD_安装包_<version>_x64.exe
rem
rem  前端 + 六个官方后端的自包含布局统一由
rem  installer\publish-selfcontained.ps1 生成（各项目使用隔离 ArtifactsPath，避免
rem  共享中间目录互相覆盖 assets）；本脚本只负责发布布局 + 编译 Inno Setup 安装包。
rem
rem  NOTE (important): WinUI3 unpackaged publish drops the app's
rem  .xbf XAML files and DeltaNFD.pri from the publish dir
rem  (known dotnet-publish gap). The publish script copies them back in.
rem ============================================================
setlocal
cd /d "%~dp0\.." || goto :fail

set "BUILD_ID=%RANDOM%%RANDOM%"
set "PUB=%CD%\_buildcheck\installer-publish-%BUILD_ID%"
set "ART=%CD%\_buildcheck\installer-artifacts-%BUILD_ID%"
set "PKG=%CD%\_buildcheck\installer-package-%BUILD_ID%"

echo [1/2] Publish self-contained layout (frontend + 6 backends) ...
rem  注意：OpenAlphaV0.83 起安装包必须内置背景图；不要传 IncludeBundledBackground=false。
powershell -NoProfile -ExecutionPolicy Bypass -File "%CD%\installer\publish-selfcontained.ps1" ^
  -OutDir "%PUB%" -ArtifactsRoot "%ART%" -Configuration Release
if errorlevel 1 goto :fail

rem 背景图与官方后端缺失要显式失败（发布脚本已检查，这里再兜底一次）。
set "BGFOUND="
for %%F in ("%PUB%\Assets\background.*") do set "BGFOUND=%%F"
if not defined BGFOUND goto :missing_background
echo       background bundled: %BGFOUND%
if not exist "%PUB%\Backends\DeltaNFD.Backend.Ace.exe" goto :missing_backends
if not exist "%PUB%\Backends\DeltaNFD.Backend.Procman.exe" goto :missing_backends

set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (
  echo [ERROR] Inno Setup 6 not found. Install it first.
  goto :fail
)

echo [2/2] Compile installer ...
pushd installer
"%ISCC%" "/O%PKG%" "/DPublishDir=%PUB%" DeltaNFD.iss
if errorlevel 1 ( popd & goto :fail )
popd

echo.
echo 完成：%PKG%\三角帧不掉洲_DeltaNFD_安装包_[版本]_x64.exe
echo.
echo 提示：发布前还要用 installer\make-update-manifest.ps1 生成根目录 update.json
echo       （自动更新靠它判断新版本），并把它提交到 main 分支。
goto :eof

:fail
echo.
echo BUILD FAILED.
exit /b 1

:missing_background
echo.
echo [ERROR] The publish output has no Assets\background.* - the bundled background image is missing.
echo The installer MUST ship it since OpenAlphaV0.83 (otherwise the app falls back to a plain Mica backdrop).
echo Check that src\DeltaNFD\Assets\background.png exists and that IncludeBundledBackground is not set to false.
exit /b 1

:missing_backends
echo.
echo [ERROR] The official backend layout is missing from the publish output.
echo Expected Backends\DeltaNFD.Backend.Ace.exe and five more modules.
echo Check installer\publish-selfcontained.ps1 and src\DeltaNFD.Backend.*.
exit /b 1
