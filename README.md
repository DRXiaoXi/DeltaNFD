# 三角帧不掉洲 · Delta No FPS Drops（Delta NFD）

> **开发日志与完整交接文档（HANDOFF.md）仅保留在本地，不随仓库分发。**

针对《三角洲行动》（Delta Force）的 Windows 系统级游戏优化工具，WinUI 3 原生应用。**本工具为非官方粉丝工具，与腾讯游戏、《三角洲行动》官方无关；三角洲行动及相关标识归腾讯所有。**

![.NET](https://img.shields.io/badge/.NET-8.0-blue) ![WinUI](https://img.shields.io/badge/WinUI-3%20%2F%20Windows%20App%20SDK%201.8-orange) ![Platform](https://img.shields.io/badge/Windows-10%201809%2B%20%2F%2011-lightgrey) ![License](https://img.shields.io/badge/License-Source%20Available%20%2F%20Noncommercial-orange)

部分功能会**真实修改系统设置**。配置类操作会尽可能备份原值并提供恢复入口；文件清理、APPX 移除等操作无法自动恢复。

## 功能一览

### 🔧 深度优化（11 项真实开关）
| 类别 | 项目 |
| --- | --- |
| 系统精简 | 关闭 Hyper-V / VBS（含内核隔离、Credential Guard，等效 HyperV-off 全流程）· 内存压缩 · 分页合并 · 预读取三件套（Prefetch / ApplicationPreLaunch / OperationAPI） |
| 游戏微调 | SystemResponsiveness=10 · Game Bar / GameDVR 关闭 · 鼠标加速关闭 · HAGS 硬件加速 GPU 调度 · svchost 服务合并 · Power Throttling 关闭 · Windows Search 服务禁用 |

每项带真实状态显示（如「已彻底关闭（hypervisor off…）」）；关闭开关时会尝试按备份恢复原值。

### 🛡 安全与隐私（15 组高危开关）
遥测与数据采集 · 错误报告（WER）· Cortana 与输入个性化 · 位置与搜索 · 广告与推荐内容 · 云同步与传递优化 · UWP 后台应用 · **禁用 Windows 更新** · **Defender 实时防护** · **Defender 服务全套** · **SmartScreen** · **UAC 降级** · **防火墙关闭** · AMSI / WPBT / Smart App Control 等 · 计划任务批量精简

每组带风险等级徽章（低/中/高/极高）与副作用说明，极高风险项默认停在「取消」。

### 🎭 显卡型号伪装
- 修改注册表 `DeviceDesc`（设备管理器与游戏读取到的名称），不改硬件 ID 与驱动，性能不变
- 预置 1050 Ti / RX 540 / GTX 750，支持自定义型号
- **永久生效**（直到手动恢复）或**临时模式**（立即重载显卡，或写入伪装型号并立即重启；登录后工具自动还原原型号）
- 自动备份原始型号，一键恢复；写入权限不足时自动接管注册表键所有权

### 🎮 帧格（游戏运行期自动化）
- **一键帧格模式**：状态机设计，激活状态跨重启保持，退出时自动还原全部配置
- 三角洲进程启动后自动重启 DWM（处理 DWM 异常 CPU 占用）
- **电源计划锁定**：帧格模式开启时自动切换并保持「卓越性能」电源计划，退出时还原原计划
- 游戏进程优先级自动提升

### 📊 主页
游戏运行状态实时检测、一键帧格模式总开关、最近对局延迟与优化摘要。

### 🎨 外观自定义
- 设置页自由切换**主题色**（8 种预设 + 自定义颜色，全局即时生效并保存）
- 明暗主题（浅色 / 深色 / 跟随系统，重启保持）
- 自定义背景图 + 背景遮罩浓度滑条（默认 70%）
- 安装包内置默认背景图（首次启动即生效）；也可在设置页自行选择图片，选择后覆盖内置图，恢复默认可回到内置图
- 启动时自动识别三角洲游戏目录；识别失败会提示手动定位。设置页也可更改目录，着色器 / ACE / 运行库保护等功能共用同一目录

### 🔄 软件更新
- 设置页「软件更新」卡：启动后自动检查新版本（可关闭），发现更新时提醒并挂导航徽标
- **只提醒、不自动装**：下载与安装都需要你点确认；安装包先校验 SHA256 与大小，校验不过不会执行
- 更新流程：工具退出 → 静默覆盖安装 → 自动重新打开新版本；设置、备份与日志都保留
- 只有通过安装包安装的版本支持自动更新；从压缩包手动解压运行会提示前往发布页下载
- 支持配置多下载地址回退；自动检查开启时每次启动检查，只有新版才弹窗，「稍后再更新」不屏蔽下次启动提醒

## 运行库修复方式

- **高帧性修复**：保留原有 Beta0.9 C++ AIO 流程，校验资源后卸载检测到的运行库条目，再完整重装并复检。
- **兼容性修复**：先校验资源并共用高帧性修复的受控 C++ 卸载阶段，再修复微软 DirectX 旧版组件、安装 C++ 并复检。卸载未完成时停止后续安装。不会调用第三方 DirectX Repair 程序，也不替换 Windows 自带的 DirectX 11/12 核心。
- **运行库防护**：继续使用新版实现，修复前应先关闭防护。

## Alpha0.914 内测功能

- **进程管理**：属性操作分为“当前”和“总是”，分别调整本次进程或按完整应用路径保存持续规则；支持 CPU 优先级、亲和性、CPU 集合、效率模式、动态提升、I/O、内存及受支持的 GPU 调度设置。
- **后台抑制**：持续高负载时临时降低合适后台应用的优先级，负载回落后恢复。排除三角洲主进程、系统进程、前台应用及已有手动设置或启用规则的进程。
- **工具 .NET 检测与离线修复**：检查工具实际依赖，优先使用随包携带的官方 .NET 8 x64 安装器；主程序无法启动时可使用开始菜单独立入口。
- **运行库进度与全量卸载**：修复显示当前步骤；全量卸载先确认候选清单，通过身份核验后执行，不自动重装。
- **后端异常即时处理**：报错弹窗直接提供核对与可信恢复入口，处理失败可原地重试，保留真实未确认证据。

Alpha 预发布仅向内测通道提供；公测通道仍筛选 Beta/Release。仓库根目录 `update.json` 保留公测版本，内测更新通过 GitHub 预发布及对应 Release 附件 `update.json` 提供。

## 系统要求

- Windows 10 1809+ 或 Windows 11（x64）
- .NET 8 SDK（仅构建需要）
- 以管理员身份运行（程序清单已声明 `requireAdministrator`，启动时会弹 UAC）

## 构建与运行

```bash
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Debug -p:Platform=x64
```

构建产物为**非打包（unpackaged）自包含部署**，无需 MSIX 签名，直接运行：

```
src\DeltaNFD\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\DeltaNFD.exe
```

> **VC++ 运行库**：AIO 修复安装器使用完整解包目录 `src\DeltaNFD\Assets\VCRedistRepair\`（保留原有目录结构及 `payload_manifest_sha256.csv`），另需 `VCRedist2015-2022-x64.exe` 和 `VCRedist2015-2022-x86.exe`。这些文件只作为安装器的构建输入，不单独发布；运行 `installer\make-installer.cmd` 会将应用与运行库一起封装为单个 `三角帧不掉洲_DeltaNFD_安装包_<版本>_x64.exe`。x86/x64 包可从[微软官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)获取，AIO 修复包按其[上游项目](https://github.com/abbodi1406/vcredist)说明获取。

> **.NET 离线修复**：安装包携带微软 .NET Runtime 8.0.31 x64 原版安装器。设置页优先校验并使用离线包；主程序无法启动时，可从开始菜单的“.NET 环境修复”打开独立入口。系统运行时安装器不能替换工具自带的缺损文件，这种情况需重新安装工具。发布脚本自动获取并核验固定 SHA512 的构建输入；也可先运行 `powershell -File installer/prepare-dotnet-repair.ps1` 准备资源。

生成安装包需要 Windows、.NET 8 SDK 和 Inno Setup 6：

兼容性修复另需 `Assets\DirectXRepair\` 微软官方离线资源。运行 `installer\prepare-directx-resources.ps1` 可下载并只解包，不执行安装；脚本核对固定 SHA256 和逐文件清单。`make-installer.cmd` 会自动调用该准备步骤。资源来源：[微软 DirectX End-User Runtimes（June 2010）](https://www.microsoft.com/en-us/download/details.aspx?id=8109)。

```cmd
installer\make-installer.cmd
```

脚本每次使用独立的 `_buildcheck\installer-package-<build-id>\` 输出目录，并在结束时打印完整安装包路径；分发前从该目录取用本次生成的文件。

安装器检测到进程占用旧版安装文件时会提示关闭；确认后通过 Windows Restart Manager 强制结束相关进程，避免启用托盘驻留时覆盖安装卡住。安装完成页勾选「运行」后，会沿用安装器的管理员权限启动应用。

新安装默认位于 `C:\Program Files\Delta NFD`。若旧版仍安装在 `C:\Program Files\DeltaOptimizer`，请先通过 Windows「已安装的应用」卸载旧版，再运行新安装包；安装器仅在旧程序文件仍存在时阻止跨目录覆盖。卸载后遗留的登录任务会在新文件安装成功后尝试迁移，任务权限异常会给出警告而不阻断安装。用户设置、备份和日志位于 `%APPDATA%`，卸载程序不会删除它们。

打包完成后，可将生成的单个安装器作为 GitHub Release 附件发布；源码仓库不包含安装器或独立的 VC++ 包。

### 发布一个新版本（含自动更新清单）

自动更新默认读取 GitHub Releases API（由 `Assets/UpdateConfig.json` 配置），按所选通道选择已发布版本，并校验安装包 SHA256。用户配置中的官方直连 raw `main/update.json` 地址会被迁移为 Releases API；弱网加速插件仍优先读取代理后的静态 `main/update.json`，因此正式发布时也必须维护静态清单。历史源码 tag/ZIP 中的清单可能对应上一版安装包，应以当前 `main` 和实际已发布资产为准。发布步骤：

1. 同步主程序、五个官方后端、官方助手及共享程序集的 `Version` / `InformationalVersion`，以及安装脚本的 `MyAppVersion` / `MyAppDisplayVersion`；发布新公告时再更新首启公告常量。
2. 运行 `installer\make-installer.cmd` 生成安装包。
3. 用 `installer\make-update-manifest.ps1` 生成清单（会写入 SHA256、字节数与显示版本，并打印发布清单）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File installer\make-update-manifest.ps1 `
     -InstallerPath "<安装包路径>" -Version 0.91 -DisplayVersion "Beta0.91" `
     -InstallerUrl "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.91/<附件名>" `
     -NotesFile "notes/0.91.txt"
   ```

4. 把安装包上传到该 tag 的 Release，**并把生成的 `update.json` 提交到 `main`**。

本地重打包不应覆盖线上清单；同版本不同哈希的安装包也不会向已安装同版本的用户提示更新。正式发布修复版应递增内部版本，并在对应资产上传后更新清单。
5. 校验：`dotnet run --project tools/BackendSmokeTest -- --update-live`。

> **更新源与镜像**：发布方改 `src/DeltaNFD/Assets/UpdateConfig.json`（随程序分发，升级安装会覆盖）；用户想自己加国内镜像，用设置页「更新源配置」生成并编辑 `%APPDATA%\Delta NFD\update-source.json`（优先级更高、升级不覆盖）。两种方式都要把**镜像主机写进 `allowedHosts`**，并在 `update.json` 的 `installers` 里加上镜像地址（`-MirrorUrl` 即可）。所有地址共用同一条 SHA256，客户端下载后强制校验，镜像内容被替换会被拒绝。
>
> **弱网加速插件**：`src/Plugin.WeakNetBoost` 是一个可选拓展插件，装上后用户可以在插件页开关「弱网加速」，由插件代为改写上面那个用户级更新源，把检查更新与安装包下载指向 gh-proxy 加速节点（改写前整份备份，关闭或卸载时还原）。要让它真的加速下载，发布时必须用 `-MirrorFirst` 生成 `update.json`，并把 `gh-proxy.com` 写进 `allowedHosts`；只加 `-MirrorUrl` 时镜像排在同一条清单的后面，宿主仍会先试直连。打包用 `tools\pack-plugin.ps1 -ProjectPath src\Plugin.WeakNetBoost`。
>
> 注意：`0.82.0` 及更早版本不含更新器，收不到自动更新，需要用户先手动安装一次带更新器的版本。

也可以打开根目录的 `DeltaNFD.sln`（含全部三个项目）用 Visual Studio 2022 构建：

| 项目 | 说明 |
| --- | --- |
| `src/DeltaNFD` | WinUI 3 主程序 |
| `src/Plugin.WeakNetBoost` | 可选拓展插件「弱网加速」（独立管理员后端 + 声明式前端，不引用宿主） |
| `src/Example.Plugin.HardwareReport` | 拓展插件只读示例（规范 1.0 参考实现） |
| `tools/BackendSmokeTest` | 只读冒烟测试（枚举显卡 / 读取各项状态，不修改系统） |
| `tools/GpuBackupTool` | 显卡原始型号一次性备份工具（只备份，不修改注册表） |
| `tools/pack-plugin.ps1` | 把插件工程打包成可导入的 `.dnfdplugin` |

## 项目结构

```
src/DeltaNFD/
├── MainWindow.xaml            # NavigationView 导航壳 + 自定义标题栏 + Mica 背景板
├── App.xaml                   # 全局主题（强调色 / 卡片 / 按钮样式，主题色可在设置页切换）
├── Native/Privilege.cs        # SeTakeOwnershipPrivilege 等原生特权启用
├── Models/                    # 数据模型
├── Services/                  # 全部业务后端（接口 + 实现，见下）
├── ViewModels/                # MVVM（CommunityToolkit.Mvvm）
└── Views/                     # 页面：主页 / 系统优化 / 显卡伪装 / 帧格 / 着色器 / ACE / 实验室 / 设置
```

后端服务一览（`ServiceLocator` 统一注册）：

| 服务 | 职责 |
| --- | --- |
| `ISystemOptimizer` | 一键优化执行、系统总览、后台进程（真实实现） |
| `ISystemTweakService` | 深度优化 11 项的状态 / 应用 / 还原 |
| `IAdvancedTweakService` | 安全与隐私 15 组分组开关 |
| `IGpuSpoofService` | 显卡型号伪装与恢复 |
| `IFrameService` | 帧格模式状态机、DWM 重启、临时伪装生效 |
| `IGameProcessService` | 游戏进程提权、进程扫描/清理、内存整理 |
| `IPowerService` / `ICleanupService` | 电源计划切换 / 系统垃圾清理 |
| `TweakBackupStore` | 注册表原始值统一备份（`%APPDATA%\Delta NFD\backups.json`），恢复即写回 |
| `AppSettingsStore` | 全部功能开关持久化（`settings.json`） |

## 风险提示 / 免责声明

- 本工具会**真实修改系统设置**。关闭 Defender、SmartScreen、UAC、防火墙、Windows 更新等会**显著降低系统安全性**。多数配置类项目可尝试按备份还原，但恢复依赖本工具的备份文件；文件清理、APPX 移除等不能自动恢复。
- 「重载显卡立即生效」「重启 DWM」等操作会短暂黑屏/闪屏，可能导致正在运行的游戏崩溃。
- 本项目与腾讯游戏、《三角洲行动》官方无关。

## 致谢

感谢 [abbodi1406 / VisualCppRedist AIO](https://github.com/abbodi1406/vcredist) 及其上游贡献者。本项目的运行库修复功能使用了该项目提供的 AIO 运行库整合资源和 `Installer.cmd` 安装脚本，并在此基础上集成资源校验、调用和诊断记录。

Microsoft Visual C++ 运行库由微软提供；感谢上游项目对运行库整合、安装脚本和兼容性的持续维护。

## 协议

自 Beta0.91 起，本项目采用 [源码可用非商业许可证 1.0](LICENSE)，不再宣称采用标准开源许可证。允许遵守协议的免费、非商业使用；禁止收费转售、闭源二次开发、用于收费产品、商业使用及删除原作者署名。派生作品须保持源码公开、保留作者与修改说明；商业使用需事先另行获得书面授权。

历史 GPL 版本的既有授权不被追溯撤回。第三方内容继续适用各自许可证，新限制不对其重新授权，详见 [第三方与历史授权](THIRD-PARTY-NOTICES.md)。

优化项均为对 Windows 公开系统设置（注册表 / 服务 / 计划任务 / 电源策略）的调整，键值整理自公开流传的系统调优资料与社区实践；「无省电释放模式」电源计划文件基于 [AtlasOS](https://github.com/Atlas-OS/Atlas) 项目导出并改名，致谢 AtlasOS 及其贡献者。
