; 三角帧不掉洲（Delta No FPS Drops / Delta NFD）安装脚本 · Inno Setup 6
; 编译：ISCC.exe DeltaNFD.iss
; 产物：三角帧不掉洲_DeltaNFD_安装包_0.9222_x64.exe（自包含 .NET 运行时，目标机免装任何依赖）
;
; 当前源码版本：0.9222 / Beta0.922-RC2。内部更新数值保持递增；发布显示为 Beta0.922-RC2。
;
; 安装/卸载统一安全交接：先封锁新启动，通知主程序完成恢复并退出，再核对所有官方执行者。
; 卸载保留备份时，在删除程序前复制并校验完整数据；历史记录仍待核对时保留原目录。
;   历史坑：0.82 的 unins000.exe 没有 [UninstallDelete] 段，卸载后 %APPDATA%\Delta NFD、
;   %APPDATA%\DeltaOptimizer、%LOCALAPPDATA%\Delta NFD 及登录任务全部残留。

#define MyAppName "三角帧不掉洲"
#define MyAppShortcutName "三角帧不掉洲"
#define MyAppShortName "Delta NFD"
#define MyAppEnglishName "Delta No FPS Drops"
#define MyAppDisplayName "三角帧不掉洲（Delta NFD）"
#define MyAppVersion "0.9222"
#define MyAppDisplayVersion "Beta0.922-RC2"
#define MyAppExeName "DeltaNFD.exe"
; 登录任务名（与 AppControlService / FrameService 创建的一致）
#define AutoStartTaskName "DeltaNFD_AutoStart"
#define FrameTaskName "DeltaNFD_FrameMode"
; AppData 数据目录名（当前版 + 旧版）
#define DataDirName "Delta NFD"
#define LegacyDataDirName "DeltaOptimizer"
#ifndef PublishDir
  #define PublishDir "publish\DeltaNFD"
#endif

[Setup]
AppId={{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoTextVersion={#MyAppDisplayVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppDisplayVersion}
AppVerName={#MyAppDisplayName} · {#MyAppEnglishName} · {#MyAppDisplayVersion}
AppPublisher=逐梦之子-晓夕
AppPublisherURL=https://space.bilibili.com/630440925
DefaultDirName={autopf}\{#MyAppShortName}
UsePreviousAppDir=no
DefaultGroupName={#MyAppShortcutName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppDisplayName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; 安装程序自身也用应用图标
SetupIconFile=..\src\DeltaNFD\Assets\DeltaNFD.ico
#ifdef ValidateOnly
Output=no
#else
OutputDir=Output
#endif
OutputBaseFilename=三角帧不掉洲_DeltaNFD_安装包_{#MyAppVersion}_x64
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
; 卸载时也要能删掉受保护/只读的残留文件
Uninstallable=yes
; 显式安全交接负责退出；Restart Manager 不得强杀恢复后端或插件。
CloseApplications=no
CloseApplicationsFilter=*.exe,*.dll,*.chm,*.pri
; 是否启动新版本由安装完成页的复选框决定，不自动恢复旧进程。
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[InstallDelete]
; Delete old executable/assembly and PRI files after Restart Manager has closed a previous instance.
Type: files; Name: "{app}\DeltaOptimizer.*"
; Remove the retired product-owned repair payload and shortcut on upgrade.
Type: filesandordirs; Name: "{app}\Assets\DotNetRepair"
Type: files; Name: "{group}\.NET 环境修复.lnk"

[CustomMessages]
chinese.CreateDesktopIcon=在桌面创建「三角帧不掉洲」快捷方式(&D)
chinese.AdditionalIcons=附加选项：
chinese.RunAfterInstall=安装完成后启动 {#MyAppDisplayName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "DeploymentControl.ps1"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "Assets\DotNetRepair\*,Assets\VCRedistRepair\*,Assets\VCRedistRepair_unpacked\*,Assets\DirectXRepair\*,Assets\VCRedist2015-2022-*.exe,Assets\VCRedistRepair.exe"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppShortcutName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 应用清单要求管理员权限；沿用已提权安装器的令牌启动，避免以普通用户凭据启动失败。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:RunAfterInstall}"; Flags: postinstall nowait skipifsilent unchecked runascurrentuser
; 静默安装不启动应用；交互安装仅在完成页由用户主动勾选后启动。

[Code]

const
  { 安装器自己的进度步骤（与 Inno 的 TSetupStep 不冲突） }
  AutoStartTaskName = '{#AutoStartTaskName}';
  FrameTaskName = '{#FrameTaskName}';
  LegacyAutoStartTaskName = '{#LegacyDataDirName}_AutoStart';
  LegacyFrameTaskName = '{#LegacyDataDirName}_FrameMode';
  { 与 AppDataPaths 保持一致的数据目录名 }
  DataDirName = '{#DataDirName}';
  LegacyDataDirName = '{#LegacyDataDirName}';
  ReparsePointAttribute = $400;

var
  { 卸载器自身路径：InitializeUninstall 时捕获，自删流程用（声明必须早于所有使用点） }
  UninstallerExePath: String;
  PreserveDeploymentHistory: Boolean;
  DeploymentDetail: String;
  VerifiedBackupDir: String;
  DeploymentMutex: THandle;

function CreateMutexW(Attributes: LongWord; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Timeout: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';

procedure ReleaseDeploymentMutex();
begin
  if DeploymentMutex <> 0 then
  begin
    ReleaseMutex(DeploymentMutex);
    CloseHandle(DeploymentMutex);
  end;
  DeploymentMutex := 0;
end;

procedure DeinitializeSetup();
begin
  ReleaseDeploymentMutex();
end;

procedure DeinitializeUninstall();
begin
  ReleaseDeploymentMutex();
end;

function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function EnumerationError(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';

{ Refuse links anywhere along the path, including the cleanup root itself. }
function HasReparseAncestor(const Path: String): Boolean;
var
  Current, Parent: String;
  Attributes: LongWord;
begin
  Result := True;
  Current := ExpandFileName(Path);
  repeat
    Attributes := GetFileAttributesW(Current);
    if Attributes <> $FFFFFFFF then
      if (Attributes and ReparsePointAttribute) <> 0 then Exit;
    Parent := ExtractFileDir(Current);
    if Parent = Current then Break;
    Current := Parent;
  until Current = '';
  Result := False;
end;

{ ============================ 通用：任务 / 进程 / 目录 ============================ }

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
  { 注意：旧版这里 Result 未赋初值，任务本就不存在时会返回随机值。 }
  Result := False;
  if not TaskExists(TaskName) then
  begin
    Result := True;
    Exit;
  end;
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /TN "' + TaskName + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    ((ResultCode = 0) or not TaskExists(TaskName));
end;

{ 安装和卸载使用同一交接脚本：安全请求、原生路径身份核对、有界等待、逐项报告。 }
function PrepareDeployment(const Mode: String; ForUninstall: Boolean): Boolean;
var
  ScriptPath, ReportPath, Args: String;
  ResultCode, I: Integer;
  MutexWait: LongWord;
  Lines: TArrayOfString;
begin
  Result := False;
  DeploymentDetail := '';
  PreserveDeploymentHistory := False;
  if DeploymentMutex = 0 then
  begin
    DeploymentMutex := CreateMutexW(0, False, 'Local\DeltaNFD_7A3E9C4D_Deployment');
    if DeploymentMutex <> 0 then
    begin
      MutexWait := WaitForSingleObject(DeploymentMutex, 0);
      if (MutexWait <> 0) and (MutexWait <> $80) then
      begin
        CloseHandle(DeploymentMutex);
        DeploymentMutex := 0;
      end;
    end;
  end;
  if DeploymentMutex = 0 then
  begin
    DeploymentDetail := '无法建立安装/卸载交接门禁。';
    Exit;
  end;
  ScriptPath := ExpandConstant('{tmp}\DeploymentControl.ps1');
  ReportPath := ExpandConstant('{tmp}\DeltaNFD-deployment-report.txt');
  DeleteFile(ReportPath);
  if ForUninstall then
  begin
    if not CopyFile(ExpandConstant('{app}\Tools\DeploymentControl.ps1'), ScriptPath, False) then
    begin
      DeploymentDetail := '安全交接脚本缺失或无法复制。请在原安装目录修复安装后重试。';
      ReleaseDeploymentMutex();
      Exit;
    end;
  end
  else
    ExtractTemporaryFile('DeploymentControl.ps1');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -InstallRoot "' + ExpandConstant('{app}') + '" -Mode ' + Mode + ' -ReportPath "' + ReportPath + '"';
  ResultCode := -1;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    PreserveDeploymentHistory := ResultCode = 10;
    Result := (ResultCode = 0) or PreserveDeploymentHistory;
  end;
  if LoadStringsFromFile(ReportPath, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
      DeploymentDetail := DeploymentDetail + Lines[I] + #13#10;
  end
  else
  begin
    Result := False;
    DeploymentDetail := '未能获得本次安全交接报告，退出码=' + IntToStr(ResultCode);
  end;
  if Trim(DeploymentDetail) = '' then
  begin
    Result := False;
    DeploymentDetail := '安全交接报告为空，尚未替换或删除程序。';
  end;
  if not Result then ReleaseDeploymentMutex();
  Log('安装/卸载交接：' + IntToStr(ResultCode) + '；' + DeploymentDetail);
end;

{ 递归删除目录（Win32 封装，对已不存在/部分损坏的目录不会抛异常）。 }
function DelTree(const Path, Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if (Lowercase(AddBackslash(ExpandFileName(Path))) <> Lowercase(AddBackslash(ExpandFileName(Root)))) and
    (Pos(Lowercase(AddBackslash(ExpandFileName(Root))), Lowercase(AddBackslash(ExpandFileName(Path)))) <> 1) then
  begin
    Result := False;
    Exit;
  end;
  if HasReparseAncestor(Path) then
  begin
    Log('卸载：拒绝清理重解析路径 ' + Path);
    Result := False;
    Exit;
  end;
  if not DirExists(Path) then
    Exit;

  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if (FindRec.Attributes and ReparsePointAttribute) <> 0 then
          begin
            Log('卸载：跳过重解析点 ' + AddBackslash(Path) + FindRec.Name);
            Result := False;
          end
          else if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            if not DelTree(AddBackslash(Path) + FindRec.Name, Root) then Result := False;
          end
          else
          begin
            if not DeleteFile(AddBackslash(Path) + FindRec.Name) then
            begin
              Log('卸载：文件删除失败 ' + AddBackslash(Path) + FindRec.Name);
              Result := False;
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  if not RemoveDir(Path) then Result := False;
end;

{ 复制完整数据树，逐文件校验；不覆盖、不跟随链接。 }
function PreserveBackupFiles(const SourceDir, DestDir: String): Boolean;
var
  FindRec: TFindRec;
  Src, Dst: String;
  Attributes, ReadError: LongWord;
begin
  Result := False;
  if HasReparseAncestor(SourceDir) or HasReparseAncestor(DestDir) then Exit;
  Attributes := GetFileAttributesW(SourceDir);
  if Attributes = $FFFFFFFF then
  begin
    ReadError := EnumerationError();
    Result := (ReadError = 2) or (ReadError = 3);
    Exit;
  end;
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then Exit;
  if not ForceDirectories(DestDir) then Exit;
  if FindFirst(AddBackslash(SourceDir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if (FindRec.Attributes and ReparsePointAttribute) <> 0 then Exit;
          Src := AddBackslash(SourceDir) + FindRec.Name;
          Dst := AddBackslash(DestDir) + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            if not PreserveBackupFiles(Src, Dst) then Exit;
          end
          else
          begin
            if HasReparseAncestor(Src) or HasReparseAncestor(Dst) then Exit;
            if not CopyFile(Src, Dst, True) then Exit;
            try
              if GetSHA256OfFile(Src) <> GetSHA256OfFile(Dst) then Exit;
            except
              Log('卸载：备份校验失败 ' + Src);
              Exit;
            end;
          end;
        end;
      until not FindNext(FindRec);
      if EnumerationError() <> 18 then Exit;
    finally
      FindClose(FindRec);
    end;
  end
  else
  begin
    ReadError := EnumerationError();
    if (ReadError <> 2) and (ReadError <> 18) then Exit;
  end;
  Result := True;
end;

function PreserveAllData(): Boolean;
begin
  VerifiedBackupDir := AddBackslash(ExpandConstant('{userdesktop}')) + DataDirName + ' 备份\'
    + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
  while DirExists(VerifiedBackupDir) or FileExists(VerifiedBackupDir) do
    VerifiedBackupDir := VerifiedBackupDir + '-new';
  Result := PreserveBackupFiles(ExpandConstant('{userappdata}\') + DataDirName, VerifiedBackupDir + '\current') and
    PreserveBackupFiles(ExpandConstant('{userappdata}\') + LegacyDataDirName, VerifiedBackupDir + '\legacy') and
    PreserveBackupFiles(ExpandConstant('{localappdata}\') + DataDirName, VerifiedBackupDir + '\local-current') and
    PreserveBackupFiles(ExpandConstant('{localappdata}\') + LegacyDataDirName, VerifiedBackupDir + '\local-legacy');
  if Result then Log('卸载：全部数据备份已校验：' + VerifiedBackupDir);
end;

{ 删除一个目录下的全部空子目录（自底向上递归），目录本身为空时也一并删除。
  Inno 卸载只删"自己装过的文件"，createallsubdirs 建出的空目录骨架（几十个语言目录、
  Assets 子树等）会原样留下——VM 实测 0.83 卸载后安装目录里剩 260+ 个空目录即此根因。
  unins000.exe/dat 属于卸载器自身（由 SelfDeleteUninstaller 延迟处理），视为"透明"：
  只含卸载器文件的目录仍然可以删（rmdir 会在 unins000 未删前失败，属预期；
  cmd 兜底脚本最后会再 rmdir 一次）。其他任何文件 → 放弃该子树。 }
function RemoveEmptyTree(const Path: String): Boolean;
var
  FindRec: TFindRec;
  EntryPath: String;
begin
  Result := False;
  if HasReparseAncestor(Path) then Exit;
  if not DirExists(Path) then
    Exit;

  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          EntryPath := AddBackslash(Path) + FindRec.Name;
          if (FindRec.Attributes and ReparsePointAttribute) <> 0 then Exit;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            RemoveEmptyTree(EntryPath)  { 递归删空子目录；返回值不阻断兄弟节点 }
          else if (FindRec.Name <> 'unins000.exe') and (FindRec.Name <> 'unins000.dat') then
          begin
            Result := False;
            Exit;  { 目录里有真实文件：不是空目录骨架，整个子树放弃 }
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  { 枚举完没有退出 = 没有真实文件（子目录已递归清空或只有卸载器文件）→ 尝试删除；
    unins000 仍在本目录时 RemoveDir 会失败（占用），由 SelfDeleteUninstaller 的 cmd 兜底再删 }
  Result := RemoveDir(Path);
end;

{ 卸载器自删：Inno 有自己的自删机制，但 VM 实测 0.83 卸载后 unins000.exe/dat 仍留在原地
  （机制失效场景未知，可能是收尾被杀软拦截）。这里加独立兜底：
  由 cmd 延迟数秒（等卸载器进程完全退出）后删除 unins000.exe/dat 并 rmdir 安装目录。
  cmd 是系统文件永远可用；若 Inno 自删已生效（文件不存在）则本过程静默跳过。 }
procedure SelfDeleteUninstaller();
var
  UninstallerDir, ScriptCmd: String;
  ResultCode: Integer;
begin
  if UninstallerExePath = '' then
    Exit;
  if not FileExists(UninstallerExePath) then
    Exit;  { Inno 自己的自删已成功，无需兜底 }

  UninstallerDir := ExtractFilePath(UninstallerExePath);

  { ping -n 6 ≈ 5 秒等待，覆盖卸载器收尾（完成对话框/日志/自删尝试）；
    最后 rmdir 只在安装目录已完全为空时成功（非空时静默失败 = 保守，不误删用户文件）。
    Exec 只支持 6 参数（无工作目录参数）；cmd /d 绕过 autorun，全部绝对路径。 }
  ScriptCmd := '/d /c ping -n 6 127.0.0.1 >nul'
    + ' & del /f /q "' + UninstallerExePath + '"'
    + ' & del /f /q "' + UninstallerDir + 'unins000.dat"'
    + ' & rmdir "' + UninstallerDir + '"';

  if Exec(ExpandConstant('{cmd}'), ScriptCmd, '', SW_HIDE, ewNoWait, ResultCode) then
    Log('卸载：已安排延迟自删 ' + UninstallerExePath)
  else
    Log('卸载：启动自删脚本失败，unins000 将留在安装目录（可手动删除）');
end;

{ ============================ 安装阶段 ============================ }

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
  if Result <> '' then Exit;
  if not PrepareDeployment('install', False) then
    Result := '安装前安全交接未完成，尚未替换文件。' + #13#10 + DeploymentDetail;
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
  OldRuntimeDir: String;
  RetiredIndex: Integer;
  ChannelInstallId: String;
begin
  if CurStep = ssPostInstall then
  begin
    NewExePath := ExpandConstant('{app}\{#MyAppExeName}');
    { Record only; never start the frontend to edit settings. The app consumes each install once. }
    ChannelInstallId := ExpandConstant('{tmp}') + '|' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
    if not RegWriteStringValue(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1',
      'UpdateChannelInstallationVersion', '{#MyAppDisplayVersion}') then
      Log('更新通道：无法登记安装版本。');
    if Pos('alpha', Lowercase('{#MyAppDisplayVersion}')) = 1 then
      if not RegWriteStringValue(HKLM64,
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1',
        'UpdateChannelAlphaInstallationId', ChannelInstallId) then
        Log('更新通道：无法登记 Alpha 安装批次。');
    { Keep the Alpha batch across Beta installs, including Alpha -> Beta without an intervening app launch. }
    if not RegWriteStringValue(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1',
      'UpdateChannelInstallationId', ChannelInstallId) then
      Log('更新通道：无法登记安装批次。');
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
    { Files and task migration are complete. Release before [Run] starts the new GUI. }
    for RetiredIndex := 0 to 6 do
    begin
      case RetiredIndex of
        0: OldRuntimeDir := ExpandConstant('{app}\Assets\VCRedistRepair');
        1: OldRuntimeDir := ExpandConstant('{app}\Assets\VCRedistRepair_unpacked');
        2: OldRuntimeDir := ExpandConstant('{app}\Assets\DirectXRepair');
        3: OldRuntimeDir := ExpandConstant('{app}\Assets\VCRedist2015-2022-x64.exe');
        4: OldRuntimeDir := ExpandConstant('{app}\Assets\VCRedist2015-2022-x86.exe');
        5: OldRuntimeDir := ExpandConstant('{app}\Assets\VCRedistRepair.exe');
        6: OldRuntimeDir := ExpandConstant('{app}\Assets\DirectXRepair_unpacked');
      end;
      if FileExists(ExpandConstant('{userappdata}\Delta NFD\runtime-repair-active.json')) or
        FileExists(ExpandConstant('{userappdata}\Delta NFD\official-backend-unconfirmed\runtime.json')) then
        Log('旧操作尚待核对，暂时保留其安装资源证据：' + OldRuntimeDir)
      else if DirExists(OldRuntimeDir) then
      begin
        if not DelTree(OldRuntimeDir, OldRuntimeDir) then
          Log('退休的运行库资源未能完全清理：' + OldRuntimeDir);
      end
      else if FileExists(OldRuntimeDir) and not HasReparseAncestor(OldRuntimeDir) then
      begin
        if not DeleteFile(OldRuntimeDir) then
          Log('退休的运行库安装器未能清理：' + OldRuntimeDir);
      end;
    end;
    ReleaseDeploymentMutex();
  end;
end;

{ ============================ 卸载阶段 ============================ }

{ 用户的卸载选择：True = 额外保留还原备份（默认 False = 完全清除）。 }
var
  UninstallKeepBackups: Boolean;

{ 卸载开始时初始化选择状态。
  注意：Inno 的 InitializeUninstall 必须是 function Boolean（返回 False 会取消卸载）。
  ⚠ 绝对不要在这里调用 CreateInputOptionPage —— 卸载阶段没有安装向导，
     调用会直接抛 "Cannot call CreateInputOptionPage function during Uninstall."
     （OpenAlphaV0.83 首个卸载器的真实事故）。卸载期只能通过
     GetUninstallProgressForm + MsgBox 询问用户。 }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  UninstallKeepBackups := UninstallSilent;  { 静默卸载无法询问 → 安全默认保留备份 }
  { 提前捕获卸载器路径：usPostUninstall 阶段文件定位用 }
  UninstallerExePath := ExpandConstant('{uninstallexe}');
end;

{ 供进度窗口显示的一行中文提示（Inno Pascal 的 if 表达式不可用，故写成函数）。
  必须先于 AskWhatToKeep 声明：Pascal 不做前向引用。 }
function SelectUninstallHint(): String;
begin
  if UninstallKeepBackups then
    Result := '正在清理程序与数据（已选择保留还原备份）…'
  else
    Result := '正在清理程序与全部数据…';
end;

{ 在卸载进度窗口上弹出二选一询问，并同步改进度窗口的文字。
  只有交互式卸载（非 /SILENT、非 /VERYSILENT）才会问；
  静默卸载走 InitializeUninstall 里的安全默认，避免无人值守时误删还原备份。 }
procedure AskWhatToKeep();
var
  Form: TUninstallProgressForm;
  Answer: Integer;
begin
  if UninstallSilent then
    Exit;

  Form := GetUninstallProgressForm;

  if Form <> nil then
  begin
    Form.PageNameLabel.Caption := '正在卸载 ' + ExpandConstant('{#MyAppDisplayName}');
    Form.PageDescriptionLabel.Caption := '请选择数据清理方式';
  end;

  Answer := MsgBox('是否保留系统优化还原备份？' + #13#10 + #13#10 +
    '「是」= 保留备份（推荐）：先把全部设置、日志和恢复证据复制并校验到桌面'
    + '「Delta NFD 备份」文件夹，再删除数据目录。' + #13#10 + #13#10 +
    '「否」= 完全清除：连同设置、日志与全部备份一并删除，之后重装将无法一键还原系统优化。',
    mbConfirmation, MB_YESNO);

  UninstallKeepBackups := (Answer = idYes);

  if Form <> nil then
    Form.PageDescriptionLabel.Caption := SelectUninstallHint();
end;

{ 用户最终选择：保留备份返回 True。 }
function ShouldKeepBackups(): Boolean;
begin
  Result := UninstallKeepBackups;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RootDir, DataDir, LegacyDataDir, LocalDataDir, LocalLegacyDir: String;
  CleanFailures: String;
begin
  { 卸载刚开始：先问用户怎么处理数据（交互式卸载才问）。 }
  if CurUninstallStep = usUninstall then
  begin
    if not PrepareDeployment('uninstall', True) then
    begin
      if not UninstallSilent then
        MsgBox('卸载前安全交接未完成，程序和数据尚未删除。' + #13#10#13#10 + DeploymentDetail, mbError, MB_OK);
      Abort;
    end;
    if PreserveDeploymentHistory then
    begin
      UninstallKeepBackups := True;
      if not UninstallSilent then
        if MsgBox(DeploymentDetail + #13#10#13#10 +
          '继续卸载将保留原数据目录，并另存一份完整备份。历史记录仍待核对，是否继续？', mbConfirmation, MB_YESNO) <> idYes then
        begin
          ReleaseDeploymentMutex();
          Abort;
        end;
    end
    else AskWhatToKeep();
    { Backup failure aborts BEFORE Inno deletes the first program file. }
    if ShouldKeepBackups() then
      if not PreserveAllData() then
      begin
        if not UninstallSilent then
          MsgBox('完整备份复制或校验失败。已停止卸载，程序和原始数据均保留。', mbError, MB_OK);
        ReleaseDeploymentMutex();
        Abort;
      end;
    Exit;
  end;

  if CurUninstallStep <> usPostUninstall then
    Exit;

  CleanFailures := '';

  { 2. 登录任务：当前版 + 旧版命名，全部清掉。 }
  if not DeleteTask(AutoStartTaskName) then
    CleanFailures := CleanFailures + '开机自启动任务；';
  if not DeleteTask(FrameTaskName) then
    CleanFailures := CleanFailures + '帧格登录任务；';
  DeleteTask(LegacyAutoStartTaskName);
  DeleteTask(LegacyFrameTaskName);

  { 3. AppData 数据目录 }
  RootDir := ExpandConstant('{userappdata}');
  DataDir := AddBackslash(RootDir) + DataDirName;
  LegacyDataDir := AddBackslash(RootDir) + LegacyDataDirName;
  LocalDataDir := AddBackslash(ExpandConstant('{localappdata}')) + DataDirName;
  LocalLegacyDir := AddBackslash(ExpandConstant('{localappdata}')) + LegacyDataDirName;

  if not PreserveDeploymentHistory then
  begin
  if not DelTree(DataDir, DataDir) then
    CleanFailures := CleanFailures + ExpandConstant('{userappdata}\') + DataDirName + '；';
  { 旧版数据目录：应用首次启动已把它迁移进新目录，卸载时不应残留。 }
  if not DelTree(LegacyDataDir, LegacyDataDir) then CleanFailures := CleanFailures + LegacyDataDir + '；';
  if not DelTree(LocalDataDir, LocalDataDir) then CleanFailures := CleanFailures + LocalDataDir + '；';
  if not DelTree(LocalLegacyDir, LocalLegacyDir) then CleanFailures := CleanFailures + LocalLegacyDir + '；';

  end
  else Log('卸载：历史证据与全部原数据目录保留；完整副本=' + VerifiedBackupDir);

  { 4. 旧安装目录里遗留的 DeltaOptimizer 子目录（安装目录本体交给 Inno 自己删） }
  RootDir := AddBackslash(ExpandConstant('{app}')) + LegacyDataDirName;
  if not DelTree(RootDir, RootDir) then CleanFailures := CleanFailures + RootDir + '；';

  { 5. 删不掉时明确告知用户，避免「以为卸干净了」 }
  if CleanFailures <> '' then
  begin
    Log('卸载清理失败项：' + CleanFailures);
    if not UninstallSilent then
      MsgBox('以下内容未能自动删除，请手动清理：' + #13#10 + CleanFailures + #13#10 +
        '可关闭占用这些文件的程序（杀毒软件有时会锁定）后重试。', mbError, MB_OK);
  end;

  { 记录一行日志，便于排障 }
  Log('卸载完成：Delta NFD 数据目录与登录任务清理结束。');

  { 6. 注册表兜底：Inno 自己会删卸载登记项，这里显式再删一次，
       避免历史版本残留的 _is1 键在「已安装的应用」里留下幽灵条目。
       路径与 [Setup] 的 AppId 对应：实际 GUID 保留花括号，与安装阶段读写的键一致。 }
  if not RegDeleteKeyIncludingSubkeys(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1') then
    Log('卸载：注册表卸载登记项删除返回失败（可能已被 Inno 删除，通常无需处理）');

  { 7. 安装目录空目录骨架：Inno 卸载只删文件不删目录（createallsubdirs 的空目录全留下，
       VM 实测残留 260+ 个空目录）。这里自底向上清掉空目录树；
       RemoveDir 失败 = 目录非空或被占用，属正常情况（比如 unins000 自己还在里面）。 }
  if not RemoveEmptyTree(ExpandConstant('{app}')) then
    Log('卸载：安装目录仍有非空内容或暂时无法删除（正常，见下一条自删日志）');

  { 8. 卸载器自删：unins000.exe/dat 由 %TEMP% 副本在进程退出后延迟删除，
       否则它会永久留在安装目录（VM 实测残留）。 }
  SelfDeleteUninstaller();
end;
