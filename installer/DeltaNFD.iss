; 三角帧不掉洲（Delta No FPS Drops / Delta NFD）安装脚本 · Inno Setup 6
; 编译：ISCC.exe DeltaNFD.iss
; 产物：Output\三角帧不掉洲_DeltaNFD_安装包_0.82.0_x64.exe（自包含 .NET 运行时，目标机免装任何依赖）

#define MyAppName "三角帧不掉洲"
#define MyAppShortName "Delta NFD"
#define MyAppEnglishName "Delta No FPS Drops"
#define MyAppDisplayName "三角帧不掉洲（Delta NFD）"
#define MyAppVersion "0.82.0"
#define MyAppDisplayVersion "OpenAlphaV0.82"
#define MyAppExeName "DeltaNFD.exe"
#ifndef PublishDir
  #define PublishDir "publish\DeltaNFD"
#endif

[Setup]
AppId={{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppDisplayName} · {#MyAppEnglishName} · {#MyAppDisplayVersion}
AppPublisher=逐梦之子-晓夕
AppPublisherURL=https://space.bilibili.com/630440925
DefaultDirName={autopf}\{#MyAppShortName}
UsePreviousAppDir=no
DefaultGroupName={#MyAppDisplayName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppDisplayName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; 安装程序自身也用应用图标
SetupIconFile=..\src\DeltaNFD\Assets\DeltaNFD.ico
OutputDir=Output
OutputBaseFilename=三角帧不掉洲_DeltaNFD_安装包_{#MyAppVersion}_x64
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
; 应用支持「关闭窗口时最小化到托盘」，普通关闭请求不会结束进程；替换文件前强制结束占用安装文件的进程。
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll,*.chm,*.pri
; 是否启动新版本由安装完成页的复选框决定，不自动恢复旧进程。
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 卸载不删用户数据（设置/备份/日志在 %APPDATA%\Delta NFD，按惯例保留）

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[InstallDelete]
; Delete old executable/assembly and PRI files after Restart Manager has closed a previous instance.
Type: files; Name: "{app}\DeltaOptimizer.*"

[CustomMessages]
chinese.CreateDesktopIcon=在桌面创建「三角帧不掉洲（Delta NFD）」快捷方式(&D)
chinese.AdditionalIcons=附加选项：
chinese.RunAfterInstall=安装完成后启动 {#MyAppDisplayName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppDisplayName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 应用清单要求管理员权限；沿用已提权安装器的令牌启动，避免以普通用户凭据启动失败。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:RunAfterInstall}"; Flags: postinstall nowait skipifsilent unchecked runascurrentuser

[Code]
const
  LegacyAutoStartTaskName = 'DeltaOptimizer_AutoStart';
  LegacyFrameTaskName = 'DeltaOptimizer_FrameMode';
  AutoStartTaskName = 'DeltaNFD_AutoStart';
  FrameTaskName = 'DeltaNFD_FrameMode';

function TaskExists(const TaskName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + TaskName + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function CreateLogonTask(const TaskName, ExePath: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Create /TN "' + TaskName + '" /TR ""' + ExePath + '"" /SC ONLOGON /RL HIGHEST /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if Result then
    Result := TaskExists(TaskName);
end;

function DeleteTask(const TaskName: String): Boolean;
var
  ResultCode: Integer;
begin
  if not TaskExists(TaskName) then
  begin
    Result := True;
    Exit;
  end;
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /TN "' + TaskName + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    ((ResultCode = 0) or not TaskExists(TaskName));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousInstallDir: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1',
    'InstallLocation', PreviousInstallDir) and (PreviousInstallDir <> '') and
    (Lowercase(AddBackslash(PreviousInstallDir)) <> Lowercase(AddBackslash(ExpandConstant('{app}')))) and
    (FileExists(AddBackslash(PreviousInstallDir) + 'DeltaNFD.exe') or
     FileExists(AddBackslash(PreviousInstallDir) + 'DeltaOptimizer.exe')) then
  begin
    Result := '检测到旧版安装在 ' + PreviousInstallDir + '。为了避免跨目录升级留下两套程序和失效的卸载记录，请先在 Windows“已安装的应用”中卸载旧版，再重新运行安装包。旧版设置和备份保存在 AppData，不会随卸载删除。';
  end;
end;

function MigrateLogonTask(const LegacyName, NewName, LabelText, ExePath: String): String;
begin
  Result := '';
  if not TaskExists(LegacyName) and not TaskExists(NewName) then
    Exit;
  if not CreateLogonTask(NewName, ExePath) then
  begin
    Result := LabelText + '创建或更新失败';
    Exit;
  end;
  if not DeleteTask(LegacyName) then
    Result := LabelText + '的旧任务未能清理';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  NewExePath: String;
  TaskWarning: String;
  FrameWarning: String;
begin
  if CurStep = ssPostInstall then
  begin
    NewExePath := ExpandConstant('{app}\{#MyAppExeName}');
    TaskWarning := MigrateLogonTask(LegacyAutoStartTaskName, AutoStartTaskName,
      '开机自启动任务', NewExePath);
    FrameWarning := MigrateLogonTask(LegacyFrameTaskName, FrameTaskName,
      '帧格登录任务', NewExePath);
    if FrameWarning <> '' then
    begin
      if TaskWarning <> '' then TaskWarning := TaskWarning + '；';
      TaskWarning := TaskWarning + FrameWarning;
    end;
    if TaskWarning <> '' then
    begin
      Log('安装后任务迁移警告：' + TaskWarning);
      if not WizardSilent then
        MsgBox('新程序已安装，但' + TaskWarning + '。请在任务计划程序中检查登录任务。', mbError, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteTask(AutoStartTaskName);
    DeleteTask(FrameTaskName);
    DeleteTask(LegacyAutoStartTaskName);
    DeleteTask(LegacyFrameTaskName);
  end;
end;
