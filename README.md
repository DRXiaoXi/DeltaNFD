# 三角帧不掉洲 · Delta No FPS Drops（Delta NFD）

> **开发日志与完整交接文档（HANDOFF.md）仅保留在本地，不随仓库分发。**

针对《三角洲行动》（Delta Force）的 Windows 系统级游戏优化工具，WinUI 3 原生应用。**本工具为非官方粉丝工具，与腾讯游戏、《三角洲行动》官方无关；三角洲行动及相关标识归腾讯所有。**

![.NET](https://img.shields.io/badge/.NET-8.0-blue) ![WinUI](https://img.shields.io/badge/WinUI-3%20%2F%20Windows%20App%20SDK%201.8-orange) ![Platform](https://img.shields.io/badge/Windows-10%201809%2B%20%2F%2011-lightgrey) ![License](https://img.shields.io/badge/License-GPL--3.0-green)

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

> **VC++ 运行库**：AIO 修复安装器使用完整解包目录 `src\DeltaNFD\Assets\VCRedistRepair_unpacked\`（保留原有目录结构及 `payload_manifest_sha256.csv`），另需 `VCRedist2015-2022-x64.exe` 和 `VCRedist2015-2022-x86.exe`。这些文件只作为安装器的构建输入，不单独发布；运行 `installer\make-installer.cmd` 会将应用与运行库一起封装为单个 `三角帧不掉洲_DeltaNFD_安装包_<版本>_x64.exe`。x86/x64 包可从[微软官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)获取，AIO 修复包按其[上游项目](https://github.com/abbodi1406/vcredist)说明获取。

生成安装包需要 Windows、.NET 8 SDK 和 Inno Setup 6：

```cmd
installer\make-installer.cmd
```

脚本每次使用独立的 `_buildcheck\installer-package-<build-id>\` 输出目录，并在结束时打印完整安装包路径；分发前从该目录取用本次生成的文件。

安装器检测到进程占用旧版安装文件时会提示关闭；确认后通过 Windows Restart Manager 强制结束相关进程，避免启用托盘驻留时覆盖安装卡住。安装完成页勾选「运行」后，会沿用安装器的管理员权限启动应用。

新安装默认位于 `C:\Program Files\Delta NFD`。若旧版仍安装在 `C:\Program Files\DeltaOptimizer`，请先通过 Windows「已安装的应用」卸载旧版，再运行新安装包；安装器仅在旧程序文件仍存在时阻止跨目录覆盖。卸载后遗留的登录任务会在新文件安装成功后尝试迁移，任务权限异常会给出警告而不阻断安装。用户设置、备份和日志位于 `%APPDATA%`，卸载程序不会删除它们。

打包完成后，可将生成的单个安装器作为 GitHub Release 附件发布；源码仓库不包含安装器或独立的 VC++ 包。

### 发布一个新版本（含自动更新清单）

自动更新读取仓库 `main` 分支根目录的 `update.json`，比对版本后会提示用户下载对应安装包。发布步骤：

1. 同步主程序和官方助手 csproj 的 `Version` / `InformationalVersion`、`MainWindow` 的首启公告常量，以及安装脚本的 `MyAppVersion` / `MyAppDisplayVersion`。
2. 运行 `installer\make-installer.cmd` 生成安装包。
3. 用 `installer\make-update-manifest.ps1` 生成清单（会写入 SHA256、字节数与显示版本，并打印发布清单）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File installer\make-update-manifest.ps1 `
     -InstallerPath "<安装包路径>" -Version 0.90.0 -DisplayVersion "PreBeta0.9" `
     -InstallerUrl "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.90.0/<附件名>" `
     -NotesFile "<更新说明.txt>"
   ```

4. 把安装包上传到该 tag 的 Release，**并把生成的 `update.json` 提交到 `main`**。
5. 校验：`dotnet run --project tools/BackendSmokeTest -- --update-live`。

> **更新源与镜像**：发布方改 `src/DeltaNFD/Assets/UpdateConfig.json`（随程序分发，升级安装会覆盖）；用户想自己加国内镜像，用设置页「更新源配置」生成并编辑 `%APPDATA%\Delta NFD\update-source.json`（优先级更高、升级不覆盖）。两种方式都要把**镜像主机写进 `allowedHosts`**，并在 `update.json` 的 `installers` 里加上镜像地址（`-MirrorUrl` 即可）。所有地址共用同一条 SHA256，客户端下载后强制校验，镜像内容被替换会被拒绝。
>
> 注意：`0.82.0` 及更早版本不含更新器，收不到自动更新，需要用户先手动安装一次带更新器的版本。

也可以打开根目录的 `DeltaNFD.sln`（含全部三个项目）用 Visual Studio 2022 构建：

| 项目 | 说明 |
| --- | --- |
| `src/DeltaNFD` | WinUI 3 主程序 |
| `tools/BackendSmokeTest` | 只读冒烟测试（枚举显卡 / 读取各项状态，不修改系统） |
| `tools/GpuBackupTool` | 显卡原始型号一次性备份工具（只备份，不修改注册表） |

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

## 协议

本项目基于 [GPL-3.0](LICENSE) 协议开源。

优化项均为对 Windows 公开系统设置（注册表 / 服务 / 计划任务 / 电源策略）的调整，键值整理自公开流传的系统调优资料与社区实践；「无省电释放模式」电源计划文件基于 [AtlasOS](https://github.com/Atlas-OS/Atlas) 项目导出并改名，致谢 AtlasOS 及其贡献者。
