# Delta NFD（Delta No FPS Drops）后端对接说明

本目录（`Services/` + `Native/`）是真实后端，与现有 Mock 体系并存，UI 层通过 `ServiceLocator` 直接取用。

## 新增了什么

| 服务 | 功能 | 需要管理员 | 生效方式 |
|---|---|---|---|
| `IGpuSpoofService`（`GpuSpoof`） | 显卡型号伪装：改注册表 `DeviceDesc`，可恢复 | 是 | **重启电脑**（注销不生效） |
| `ISystemTweakService`（`SystemTweaks`） | 系统精简 + 游戏微调 11 项（见下） | 是 | 重启 / 立即 |
| `IAdvancedTweakService`（`AdvancedTweaks`） | 高危分组开关 15 组（UI 合并在系统优化页内，不再有独立导航页） | 是 | 立即/重启 |
| `IFrameService`（`Frame`） | 帧格：一键帧格模式（激活/退出状态机）+ DWM 自动重启；另提供临时显卡伪装所需的登录自启动、重启与安全重载 | 是 | 立即/随重启 |
| `IGameProcessService`（`GameProcess`） | 游戏进程自动提权（High）+ 真实后台进程扫描/清理 + 工作集内存整理 | 是 | 立即 |
| `IPowerService`（`Power`） | 电源计划查询/切换（卓越性能动态导入）、休眠开关 | 是 | 立即 |
| `ICleanupService`（`Cleanup`） | 13 类垃圾路径扫描/清理（Temp/Prefetch/D3DSCache/更新缓存等） | 是 | 立即 |
| `IAdvancedTweakService`（`AdvancedTweaks`） | 高危分组开关 15 组：遥测/WER/Cortana/位置/广告/云同步/UWP 后台/更新禁用/Defender×2/SmartScreen/UAC/防火墙/AMSI/计划任务精简 | 是 | 立即/重启 |
| `IAceService`（`Ace`） | ACE 相关：一键清除 AntiCheatExpert（进程/服务/注册表/文件，等效官方卸载程序 + 游戏目录 AntiCheatExpert 清理） | 是 | 立即（sguard64/游戏运行时拒绝） |
| `ICpuTopologyService`（`Cpu`） | CPU 拓扑检测（厂商/大小核/超线程/AMD 多 CCD）+ 游戏核心锁定（ProcessorAffinity） | 否 | 立即（需游戏运行） |
| `IAceService`（`Ace`） | ACE 相关：一键清除 AntiCheatExpert（进程/服务/注册表/文件，等效官方卸载程序 + 游戏目录 AntiCheatExpert 清理） | 是 | 立即（sguard64/游戏运行时拒绝） |
| `IShaderService`（`Shader`） | 着色器维护：N 卡驱动版本体检（591/610/616 问题驱动、低于 572.83 过老）+ PSOCache 旧着色器清理 | 否（清理需退游戏） | 立即 |

UI 结构（批次一后调整）：系统优化页 = 一键优化 + 深度优化真实开关（无电源计划行）+ 系统清理 +
安全与隐私 15 组分组卡；原独立"安全与隐私"页与导航项已删除（`SecurityPrivacyPage` 已移除，
分组逻辑在 `SystemOptimizeViewModel`）。电源计划 UI 已移除（`IPowerService` 仍被一键优化内部使用）。
"优化项"卡（4 个一键开关）与"可清理的后台进程"卡已移除，一键优化按 settings.json 里持久化的开关执行
（`OptimizeCleanProcesses`/`OptimizeTrimMemory`/`OptimizeUltimatePower`/`GamePriorityEnabled`，默认前三者开）。
| `ISystemOptimizer`（`SystemOptimizer`） | **RealSystemOptimizer 真实实现**（替换 Mock）：一键优化真实执行（提权→杀进程→内存整理→电源→微调）、真实进程列表、真实内存/电源/游戏状态总览 | 是 | 立即 |

仍为 Mock：`GpuOptimizer`（显卡驱动设置 6 项）、仪表盘 CPU/GPU 占用与温度。

## 系统精简 + 游戏微调（ISystemTweakService，11 项）

Toggle ON = 应用优化值（走备份库），OFF = 还原默认。除原有 4 项外新增 7 项（社区流行调优键值）：

| 项 | 键值 |
|---|---|
| `SystemResponsiveness` | `HKLM\...\Multimedia\SystemProfile\SystemResponsiveness = 10`（默认 20） |
| `GameDvrOff` | 策略键 `GameBar\AllowAutoGameMode=0`、`AutoGameModeEnabled=0`、`GameDVR\AllowGameDVR=0` |
| `MouseAccelerationOff` | `SystemParametersInfo(SPI_SETMOUSE)` 写 `{0,0,0}`（HKCU `Control Panel\Mouse` 3 值备份，立即生效） |
| `HardwareGpuScheduling` | `GraphicsDrivers\HwSchMode = 2`（需重启+显卡支持） |
| `SvcHostMerge` | `Control\SvcHostSplitThresholdInKB = 0x38000` |
| `PowerThrottlingOff` | `Power\PowerThrottling\PowerThrottlingOff = 1` |
| `WSearchOff` | 服务 `WSearch` 的 `Start = 4`（原值备份）。**不碰 SysMain**：SysMain 承载内存压缩（MemCompression），禁用 SysMain 会连带关掉内存压缩 |
| `Prefetch`（三件套） | `Disable-MMAgent -ApplicationLaunchPrefetching -OperationAPI` + HKCU `ApplicationPreLaunch\Enabled = 0`（原值备份）；还原用 Enable-MMAgent + 写回备份。立即生效，无需重启 |

注意：`TweakBackupStore` 已支持 HKCU（`RegistryValueBackup.Hive`，默认 HKLM 兼容旧备份）。

## 运行库保护（IRuntimeGuardService）

背景：游戏/启动器会静默安装/覆盖 VC++ 运行库（三角洲本体通过 UE4 前置包，WeGame 通过 dependency_shared），
破坏开发环境的运行库版本。本服务**拦截所有运行库安装器**，无论来源。

- **IFEO 劫持**（主手段，路径无关）：`HKLM\...\Image File Execution Options\{安装器名}` →
  `Debugger = %windir%\System32\taskkill.exe`（原值备份，还原时无备份则删值）。名单（11 个）：
  `vc_redist.x64/x86/arm64.exe`（2015-2022）、`vcredist_x64/x86/ia64.exe` + `vcredist.exe`（2005~2013）、
  `DXSETUP.exe` + `dxwebsetup.exe`（DirectX）、`UE4PrereqSetup_x64/x86.exe`（UE4/UE5 前置包）。
- **文件 ACL 双保险**：定位三角洲目录（`HKLM\...\Tencent\WeGame\Games` InstallLocation + 扫描 `*\WeGameApps\rail_apps\DeltaForce*`），对 `UE4PrereqSetup_*.exe` 执行 `icacls /deny "*S-1-1-0:(X)"`，还原 `/remove:d`
- 状态：`GetStatusAsync()` 列出已装 VC++ 2015-2022（Uninstall 注册表）+ IFEO/ACL 拦截状态
- 注意：拦截会让**手动**安装上述任何运行库也失败——手动更新运行库或新机器配置环境前需先关闭拦截（UI 确认框已提示）
- 已知限制：安装器内部直接调 `msiexec /i VC_REDIST.MSI` 的场景无法按名拦截（劫持 msiexec 会全系统误伤）；
  .NET 安装器文件名带版本号，不在名单内

## ACE 相关（IAceService）

一键清除 AntiCheatExpert（等效 ACE 官方卸载程序 + 额外清理游戏目录 `DeltaForce\Binaries\Win64\AntiCheatExpert`）：

1. **守卫**：`sguard64.exe`（含 sguard32）或游戏本体运行时直接拒绝执行（用户明确要求）
2. **进程**：结束 ACE-Tray / ACE-Service64 / ACE-Service32 / ACE-Setup64 / ACE-Setup32
3. **服务**：`ACE-BASE / ACE-GAME / ACE-BOOT / ACE-CORE / ACE-SSC-DRV64 / ACE-ADVT`——
   自保护服务先 `sc sdset` 写入 SDDL `D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)`
   接管（与官方卸载程序一致），再 `sc stop` + `sc delete`
4. **注册表**：删 `Uninstall\{141E7813-148F-4451-A632-2DA347859F5D}` 卸载项、
   `HKLM\SOFTWARE\AppDataLow`、`HKU\.DEFAULT\SOFTWARE\AppDataLow`
5. **文件**：`%ProgramFiles%\AntiCheatExpert` 整树 + 游戏目录 AntiCheatExpert；
   被占用文件走 `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` 重启后删除
- 扫描：`ScanAsync()` 列出运行中的 ACE/游戏进程、服务、目录体积、注册表残留（纯只读）
- 下次启动游戏时 ACE 会自动重新安装（UI 已注明）

## 着色器维护（IShaderService）

- **N 卡驱动体检**：读显示类注册表（`Class\{4d36e968-...}\00xx` 的 ProviderName/DriverVersion），
  WMI 惯例取末 5 位转 "572.83" 格式。判定：低于 **572.83** = 过老（着色器渲染旧方法）；
  major 为 **591 / 610 / 616** = 问题系列（着色器文件缺失）；其余正常。非 N 卡提示忽略本项。
- **PSOCache 清理**：`{游戏根}\DeltaForce\Saved\PSOCache`（目录定位复用 `DeltaForceLocator`）。
  游戏运行中拒绝执行（文件被锁）；清除后首局重新编译着色器（卡顿属正常）。服务内不触碰其它缓存目录。

## 高危分组（IAdvancedTweakService）

分组目录驱动（`AdvancedTweakService.Groups`）：注册表项备份原值后写入、服务 `Start=4`（备份原值）、
计划任务 `schtasks /Change /DISABLE`（还原 =ENABLE）。Defender 实时防护组额外调 `Set-MpPreference`。
状态判定 = 注册表值 + 服务 Start + 计划任务状态（一次 PowerShell `Get-ScheduledTask` 批量查询）。
`GetGroupsAsync()` 会触发一次 PS 查询（约 1~2 秒），页面加载时调用。

已知限制：TrustedInstaller 保护的计划任务（UpdateOrchestrator 部分）禁用可能被拒，消息中会提示跳过数量；
篡改保护开启时 Defender 实时防护关不掉，消息会给手动指引；SmartScreen 为注册表等效实现（不做 System32 文件改名删除）。

## RealSystemOptimizer（一键优化真实化）

- 优化项开关持久化在共享 `AppSettingsStore`（`OptimizeCleanProcesses`/`OptimizeTrimMemory`/`OptimizeUltimatePower`/`GamePriorityEnabled`）；
  ViewModel 中开关变化调 `RealSystemOptimizer.SaveItemEnabled(name, enabled)`。
- 一键优化真实步骤：游戏提权 → 杀后台进程 → 工作集整理 → 卓越性能电源 → 补齐 PrioritySeparation/SystemResponsiveness。
- 总览真实项：CPU 名（注册表）、CPU 占用（GetSystemTimes 采样）、内存（GlobalMemoryStatusEx）、
  GPU 名（真实枚举）、电源计划（powercfg）、游戏运行状态；GPU 占用仍为 0（无便捷通道）。
- 共享设置：所有服务统一走 `AppSettingsStore`（`%APPDATA%\Delta NFD\settings.json`），
  **不要**再各自读写该文件，会互相覆盖字段。

实现方式：

| 功能 | 实现手段 |
|---|---|
| 显卡型号 | `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\...\DeviceDesc`（仅显示类 `{4d36e968-e325-11ce-bfc1-08002be10318}`） |
| Hyper-V / VBS | 等效 HyperV-off 工具的完整流程：① `bcdedit /set hypervisorlaunchtype off` ② DISM 依次禁用 9 个 Hyper-V/虚拟机平台功能（`/Disable-Feature /FeatureName:<名> /NoRestart`，功能不存在即跳过）③ `HvHost`/`vmms` 服务停止并改手动 ④ DeviceGuard 关闭 VBS / HVCI / Credential Guard |
| 内存压缩 | `Disable-MMAgent -mc`（PowerShell 官方 cmdlet） |
| 分页合并 | `Disable-MMAgent -PageCombining` |
| 预读取 | `...\Memory Management\PrefetchParameters\EnablePrefetcher = 0` |

Hyper-V/VBS 项的恢复（`RestoreAsync(SystemTweak.HyperVAndVbs)`）：`bcdedit … auto` + DISM 尽力启用
`Microsoft-Hyper-V-All`（家庭版没有该功能会跳过，需在「启用或关闭 Windows 功能」手动勾选）+ DeviceGuard
按备份逐项还原。关闭一次涉及 DISM，**可能耗时 1~2 分钟**，UI 上要有 busy 提示。

## 帧格服务（IFrameService）

- 后台每 2 秒检测 `DeltaForceClient-Win64-Shipping.exe`；`DwmRestartOnGameStart` 启用时，游戏进程启动后自动结束
  `dwm.exe`（Windows 立即自动拉起新 DWM，恢复异常 CPU 占用）。每次游戏会话只处理一次，游戏退出后重新武装；
  失败不自动重试，可用 `RestartDwmNow()` 手动触发。
- 开关持久化在 `%APPDATA%\Delta NFD\settings.json`；`INotifyPropertyChanged` 事件已通过
  `DispatcherQueue` 派发到 UI 线程（服务须在 UI 线程首次构造，ServiceLocator 的访问顺序已保证）。

### 一键帧格模式（激活状态机）

- `FrameModeEnabled`（只读）= `FrameModeActive`，激活状态跨重启持久化。**开启/关闭必须走
  `ActivateFrameModeAsync()` / `DeactivateFrameModeAsync()`**，确认对话框等 UI 流程统一在 `Views/FrameModeFlow.cs`。
- 开启 / 退出：按帧格页的各项开关执行并停止 DWM、双 CCD、电源锁定与响应加速等帧格专属行为；GPU 伪装不再作为帧格功能提供。
- 登录任务 `DeltaNFD_FrameMode` 仍由帧格启动流程及临时显卡伪装恢复流程共用；临时 GPU 伪装只允许在帧格未激活时使用重启流程。

### 临时显卡伪装的重启还原

- 页面只提供「永久生效」与「临时」；临时模式可选立即重载显卡或重启生效。
- 重启流程先写入伪装型号并登记目标注册表路径，再创建 `schtasks /SC ONLOGON /RL HIGHEST` 登录任务；任务创建失败时不重启并尝试回滚显卡型号。
- 自动重启后工具在登录时启动，读取待还原路径并写回备份的 `DeviceDesc`；仅还原成功后清除待处理登记。失败会保留登记并重试，最多连续三次，后续启动仍可重试。
- 原始型号已写回注册表后，Windows 可能要到下一次重启才刷新当前已加载设备的显示名称。
- 旧版已保存的「临时（帧格）」设置会迁移为启动时自动还原，并从帧格配置中清除。

## 权限（重要，已替你改好）

`app.manifest` 已加 `<requestedExecutionLevel level="requireAdministrator" ... />`：

- 启动时必弹 UAC；**VS 里调试需要以管理员身份重启 VS**，否则编译后无法启动调试。
- 后端每一步操作前也会自查 `ElevationHelper.IsElevated`，未提权时返回失败结果而不是抛异常。
- `Enum\PCI` 设备键默认管理员只读（TrustedInstaller 所有），后端遇 `Access Denied` 会自动启用
  `SeTakeOwnershipPrivilege` 接管所有权并授予 Administrators 完全控制（等效 regedit 手动「取得所有权」）。

## 前端调用示例

### 显卡型号伪装页

```csharp
private readonly IGpuSpoofService _spoof = ServiceLocator.GpuSpoof;

// 1. 枚举显卡（纯读，不弹 UAC）
var adapters = await _spoof.GetAdaptersAsync();
// adapters[i].DisplayName  → 展示名（如 "NVIDIA GeForce RTX 4060"）
// adapters[i].IsMasked     → 是否已伪装（还提供 HasBackup / DeviceDesc / RegistryPath）

// 2. 伪装（用预置型号，也可传任意字符串实现自定义型号）
var presets = _spoof.PresetNames;   // GTX 1050 Ti / RX 540 / GTX 750
var result = await _spoof.SpoofAsync(adapters[0], presets[0]);
if (result.Success && result.RequiresReboot)
{
    // 提示用户重启电脑后生效（注销不会生效）
}

// 3. 恢复原始型号
var restore = await _spoof.RestoreAsync(adapters[0]);
```

### 系统优化开关（绑定到优化页的 Toggle）

```csharp
private readonly ISystemTweakService _tweaks = ServiceLocator.SystemTweaks;

// 读取状态：IsOptimized == true 表示「已关闭」，可直接作为开关的 On 状态
foreach (var s in await _tweaks.GetStatusesAsync())
{
    // s.Tweak（枚举）/ s.DisplayName / s.Description / s.Detail / s.IsOptimized
}

// 开关切换
var r = await _tweaks.DisableAsync(SystemTweak.HyperVAndVbs);   // 关闭 = 优化（该项可能耗时 1~2 分钟）
if (r.Success && r.RequiresReboot) { /* 提示重启生效 */ }
// 撤销：
await _tweaks.RestoreAsync(SystemTweak.HyperVAndVbs);

// 仪表盘可选：查询系统此刻是否真的在跑 VBS/HVCI（约 1~2 秒）
var runtime = await _tweaks.GetVbsRuntimeSummaryAsync();
```

### 与现有 ISystemOptimizer 的关系

未动。`GetOptimizeItemsAsync()` 返回的 `OptimizeItem` 目前仍是 Mock 数据；
若想把真实优化项挂进现有优化页，可用 `TweakStatus` 的 `DisplayName` 生成 `OptimizeItem`，
并用 `s.Tweak` 枚举作为唯一键去调 `DisableAsync/RestoreAsync`。

## 备份与恢复

- 所有被改的注册表值在修改前都会记录原始值到 `%APPDATA%\Delta NFD\backups.json`
  （`RegistryValueBackup`：键路径 / 值名 / 类型 / 数据）。重复修改只保留**最早**的原始值。
- 恢复 = 从备份写回 + 删除备份条目；「修改前该值不存在」的项恢复时会删除该值（回到系统默认）。
- 备份文件丢失则无法恢复，此时接口返回失败并提示。
- **与 bat 脚本的备份（`%APPDATA%\GPUSwitcher_Backup`）不互通**：用 bat 伪装的显卡，请先用 bat 恢复，
  再用本工具伪装。

## 其他行为细节

- 所有接口返回 `OperationResult`（`Success` / `Message` / `RequiresLogoff` / `RequiresReboot`），不向 UI 抛异常。
- 伪装状态判定（`DisplayAdapterInfo.IsMasked`）：DeviceDesc 命中 "1050 Ti" / "RX 540" / "GTX 750"
  或存在本工具备份。
- 只改 DeviceDesc 字符串，不改 PCI 硬件 ID 和驱动，性能不变；属于注册表层面的名称伪装。
- 关闭 VBS/内核隔离会降低系统安全性（恶意代码防护减弱），属于用户自己的取舍；界面上建议加一句风险提示。
- 若还想彻底关掉 Hyper-V 底座（`bcdedit /set hypervisorlaunchtype off`，会影响 WSL2/虚拟机），可后续单独加开关。

## 冒烟测试（只读，不改动系统）

`tools/BackendSmokeTest` 是个小控制台工程，直接编译主工程的后端源文件，只执行「枚举显卡 + 读取优化项状态 + VBS 运行时查询」：

```
dotnet run --project tools\BackendSmokeTest
```

用来在不跑 UI 的情况下验证后端逻辑。**它不包含任何伪装/关闭操作**。
