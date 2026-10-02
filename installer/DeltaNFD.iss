; 三角帧不掉洲（Delta No FPS Drops / Delta NFD）安装脚本 · Inno Setup 6
; 编译：ISCC.exe DeltaNFD.iss
; 产物：三角帧不掉洲_DeltaNFD_安装包_0.89.0_x64.exe（自包含 .NET 运行时，目标机免装任何依赖）
;
; 当前对外版本：0.89.0 / OpenAlphaV0.89。旧版迭代记录不代表当前版本。
;
; 卸载设计（OpenAlphaV0.83 重写，见 HANDOFF §34）：
;   1. 卸载前结束残留的 DeltaNFD 进程（托盘驻留时进程仍在跑，会导致文件删不掉）；
;   2. 删除 4 个登录任务（DeltaNFD_AutoStart / DeltaNFD_FrameMode + 两个 DeltaOptimizer 旧名）；
;   3. AppData 数据由本脚本显式递归删除（不依赖 [UninstallDelete] 的通配语义）；
;   4. 卸载向导提供「完全清除 / 保留备份」二选一，默认完全清除；
;   5. 旧版遗留目录 %APPDATA%\DeltaOptimizer 与旧安装目录 {app}\DeltaOptimizer 一并清理。
;   历史坑：0.82 的 unins000.exe 没有 [UninstallDelete] 段，卸载后 %APPDATA%\Delta NFD、
;   %APPDATA%\DeltaOptimizer、%LOCALAPPDATA%\Delta NFD 及登录任务全部残留。

#define MyAppName "三角帧不掉洲"
#define MyAppShortcutName "三角帧不掉洲"
#define MyAppShortName "Delta NFD"
#define MyAppEnglishName "Delta No FPS Drops"
#define MyAppDisplayName "三角帧不掉洲（Delta NFD）"
#define MyAppVersion "0.89.0"
#define MyAppDisplayVersion "OpenAlphaV0.89"
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
OutputDir=Output
OutputBaseFilename=三角帧不掉洲_DeltaNFD_安装包_{#MyAppVersion}_x64
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
; 卸载时也要能删掉受保护/只读的残留文件
Uninstallable=yes
; 应用支持「关闭窗口时最小化到托盘」，普通关闭请求不会结束进程；替换文件前强制结束占用安装文件的进程。
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll,*.chm,*.pri
; 是否启动新版本由安装完成页的复选框决定，不自动恢复旧进程。
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[InstallDelete]
; Delete old executable/assembly and PRI files after Restart Manager has closed a previous instance.
Type: files; Name: "{app}\DeltaOptimizer.*"

[CustomMessages]
chinese.CreateDesktopIcon=在桌面创建「三角帧不掉洲」快捷方式(&D)
chinese.AdditionalIcons=附加选项：
chinese.RunAfterInstall=安装完成后启动 {#MyAppDisplayName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppShortcutName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 应用清单要求管理员权限；沿用已提权安装器的令牌启动，避免以普通用户凭据启动失败。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:RunAfterInstall}"; Flags: postinstall nowait skipifsilent unchecked runascurrentuser
; 自动更新走 /SILENT：静默模式下 postinstall 条目不会执行，靠这条把新版本重新拉起来。
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runascurrentuser; Check: WizardSilent

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
  { 卸载时需要保留的「系统还原依据」文件 —— 只保留这几个，其余随目录一起删 }
  PreserveFiles = 'backups.json,optimize_backup.json,frame_reg_backup.json,frame_usb_power.json';
  ReparsePointAttribute = $400;

var
  { 卸载器自身路径：InitializeUninstall 时捕获，自删流程用（声明必须早于所有使用点） }
  UninstallerExePath: String;

function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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

{ 结束仍在运行的 DeltaNFD 进程（托盘驻留时进程不退出，会锁住 exe/dll 导致卸载不完整）。 }
function KillAppProcesses(): Boolean;
var
  ResultCode: Integer;
  Root, Command: String;
begin
  ResultCode := -1;
  Root := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  StringChangeEx(Root, '''', '''''', True);
  { Scope termination by executable path, not by a global image name. Query errors fail closed. }
  Command := '$ErrorActionPreference=''Stop''; try { $root=''' + Root + '''; '
    + '$items=@(Get-CimInstance Win32_Process -Filter ' + '''Name = "DeltaNFD.exe" OR Name = "DeltaOptimizer.exe"''' + '); '
    + 'foreach($p in $items) { if (-not $p.ExecutablePath) { throw ''Cannot determine executable path'' }; '
    + 'if ([IO.Path]::GetDirectoryName($p.ExecutablePath) -ieq $root) { '
    + 'Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; '
    + '$live=Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue; '
    + 'if ($live -and -not $live.WaitForExit(10000)) { throw ''Process exit timed out'' } } }; '
    + '$left=@(Get-CimInstance Win32_Process -Filter ' + '''Name = "DeltaNFD.exe" OR Name = "DeltaOptimizer.exe"''' + ' | '
    + 'Where-Object { -not $_.ExecutablePath -or [IO.Path]::GetDirectoryName($_.ExecutablePath) -ieq $root }); '
    + 'if ($left.Count) { exit 1 }; exit 0 } catch { exit 1 }';
  { Escape the embedded WMI double quotes for the Windows command line. }
  StringChangeEx(Command, '"', '\"', True);
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "' + Command + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log('卸载：安装目录进程退出检查=' + IntToStr(ResultCode));
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

{ 把「系统还原依据」备份复制到目标目录。
  文件名清单用逗号分隔手工解析，避免依赖 StringSplit 的可用性/签名。 }
function PreserveBackupFiles(const SourceDir, DestDir: String): Boolean;
var
  Remaining, OneName, Src, Dst: String;
  SeparatorPos: Integer;
begin
  Result := False;
  if HasReparseAncestor(SourceDir) or HasReparseAncestor(DestDir) then Exit;
  if not DirExists(SourceDir) then
  begin
    Result := True;
    Exit;
  end;

  if not DirExists(DestDir) then
    if not ForceDirectories(DestDir) then Exit;

  Remaining := PreserveFiles;
  while Remaining <> '' do
  begin
    SeparatorPos := Pos(',', Remaining);
    if SeparatorPos = 0 then
    begin
      OneName := Remaining;
      Remaining := '';
    end
    else
    begin
      OneName := Copy(Remaining, 1, SeparatorPos - 1);
      Remaining := Copy(Remaining, SeparatorPos + 1, Length(Remaining));
    end;

    if OneName <> '' then
    begin
      Src := AddBackslash(SourceDir) + OneName;
      if FileExists(Src) then
      begin
        Dst := AddBackslash(DestDir) + OneName;
        if HasReparseAncestor(Src) or HasReparseAncestor(Dst) then Exit;
        { Never overwrite another uninstall's backup; verify bytes before deleting the source. }
        if not CopyFile(Src, Dst, True) then Exit;
        try
          if GetSHA256OfFile(Src) <> GetSHA256OfFile(Dst) then Exit;
        except
          Log('卸载：备份校验失败 ' + Src);
          Exit;
        end;
        Log('卸载：已校验保留备份 ' + Src + ' -> ' + Dst);
      end;
    end;
  end;
  Result := True;
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
    '「是」= 保留备份（推荐）：先把 backups.json / optimize_backup.json 等还原依据复制到桌面'
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

function RestoreRuntimeGuardBeforeUninstall(): Boolean;
var
  ResultCode: Integer;
  ExePath: String;
begin
  Result := False;
  ResultCode := -1;
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ExePath) then
  begin
    Log('卸载：防护恢复程序缺失，停止卸载以保留状态。');
    Exit;
  end;
  Result := Exec(ExePath, '--restore-runtime-guard-for-uninstall',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log('卸载：运行库防护恢复退出码=' + IntToStr(ResultCode));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RootDir, DataDir, LegacyDataDir, LocalDataDir, LocalLegacyDir: String;
  DesktopBackupDir: String;
  CleanFailures: String;
begin
  { 卸载刚开始：先问用户怎么处理数据（交互式卸载才问）。 }
  if CurUninstallStep = usUninstall then
  begin
    if not KillAppProcesses() then
    begin
      Log('卸载：无法确认应用已退出，终止卸载，未删除程序或数据。');
      if not UninstallSilent then
        MsgBox('无法结束安装目录中的程序。请先从托盘退出工具，再重新卸载。尚未删除程序和数据。', mbError, MB_OK);
      Abort;
    end;
    AskWhatToKeep();
    if not RestoreRuntimeGuardBeforeUninstall() then
    begin
      Log('卸载：防护恢复未完成，未删除程序和备份。');
      if not UninstallSilent then
        MsgBox('脱机调度或运行库防护恢复未完成，已停止卸载以保留程序和恢复记录。请在设置页处理脱机恢复，或在运行库页关闭防护并处理权限/归属冲突后重试。详情见应用日志。', mbError, MB_OK);
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

  if ShouldKeepBackups() then
  begin
    DesktopBackupDir := AddBackslash(ExpandConstant('{userdesktop}')) + DataDirName + ' 备份\'
      + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
    { Create a fresh directory without overwriting backups from earlier uninstalls. }
    while DirExists(DesktopBackupDir) or FileExists(DesktopBackupDir) do
      DesktopBackupDir := DesktopBackupDir + '-new';
    Log('卸载：保留备份到 ' + DesktopBackupDir);
    if not PreserveBackupFiles(DataDir, DesktopBackupDir + '\current') or
      not PreserveBackupFiles(LegacyDataDir, DesktopBackupDir + '\legacy') then
    begin
      Log('卸载：备份复制或校验失败，保留全部数据目录。');
      if not UninstallSilent then
        MsgBox('程序已卸载，但备份复制或校验失败。原始数据目录已保留，请勿手动删除其中的还原备份。', mbError, MB_OK);
      Exit;
    end;
  end;

  if not DelTree(DataDir, DataDir) then
    CleanFailures := CleanFailures + ExpandConstant('{userappdata}\') + DataDirName + '；';
  { 旧版数据目录：应用首次启动已把它迁移进新目录，卸载时不应残留。 }
  if not DelTree(LegacyDataDir, LegacyDataDir) then CleanFailures := CleanFailures + LegacyDataDir + '；';
  if not DelTree(LocalDataDir, LocalDataDir) then CleanFailures := CleanFailures + LocalDataDir + '；';
  if not DelTree(LocalLegacyDir, LocalLegacyDir) then CleanFailures := CleanFailures + LocalLegacyDir + '；';

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
       路径与 [Setup] 的 AppId 对应（Inno 存储时会去掉花括号，故这里不加转义）。 }
  if not RegDeleteKeyIncludingSubkeys(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5_is1') then
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
