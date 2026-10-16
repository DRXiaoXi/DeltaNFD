# 三角帧不掉洲 · Delta No FPS Drops（Delta NFD）

> 本分支为 **Alpha0.9195 源码**，包含文件替换更新、手动问题反馈和新版运行库修复流程。最新资源准备及构建方法见 [0.9195 源码构建说明](notes/Alpha09195源码构建说明.md)；下文保留的旧版本功能说明不替代该说明。本次仅同步源码，不发布安装包或修改在线更新清单。

> **开发日志与完整交接文档（HANDOFF.md）仅保留在本地，不随仓库分发。**

针对《三角洲行动》（Delta Force）的 Windows 系统级游戏优化工具，WinUI 3 原生应用。**本工具为非官方粉丝工具，与腾讯游戏、《三角洲行动》官方无关；三角洲行动及相关标识归腾讯所有。**

![.NET](https://img.shields.io/badge/.NET-8.0-blue) ![WinUI](https://img.shields.io/badge/WinUI-3%20%2F%20Windows%20App%20SDK%201.8-orange) ![Platform](https://img.shields.io/badge/Windows-10%201809%2B%20%2F%2011-lightgrey) ![License](https://img.shields.io/badge/License-GPL--3.0-blue)

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
- 自定义背景图 + 背景遮罩浓度滑条（默认 55%，保留已保存的个人设置）
- 安装包内置默认背景图（首次启动即生效）；也可在设置页自行选择图片，选择后覆盖内置图，恢复默认可回到内置图
- 启动时自动识别三角洲游戏目录；识别失败会提示手动定位。设置页也可更改目录，着色器 / ACE / 运行库保护等功能共用同一目录

### 🔄 软件更新
- 设置页「软件更新」卡：启动后自动检查新版本（可关闭），发现更新时提醒并挂导航徽标
- **只提醒、不自动装**：下载与安装都需要你点确认；安装包先校验 SHA256 与大小，校验不过不会执行
- 更新流程：工具安全交接并退出 → 静默覆盖安装 → 手动重新打开新版本；设置、备份与日志都保留
- 只有通过安装包安装的版本支持自动更新；从压缩包手动解压运行会提示前往发布页下载
- 支持配置多下载地址回退；自动检查开启时每次启动检查，只有新版才弹窗，「稍后再更新」不屏蔽下次启动提醒

## 运行库检测、全量重装与防护（Beta0.9191）

- **兼容性检测**：真实安装的 14.51.x 或显示名含 Visual C++ v14 的 MSI 组件为红色；其他非 14.42.x 的 14.x 组件为黄色。包装登记和残留最高黄色，与已核验同架构同版本组件对应的包装为正常；Appx 系统包仅作说明。主页同步结果；颜色不代表 DLL 损坏或已验证游戏掉帧。
- **C++ 14.x 全量重装**：展示卸载计划，用户确认后按 MSI、签名包装或登记的卸载命令逐项处理，再安装固定 2015–2022 14.42.34433 x64/x86 原包并复检。未知包装有登记命令时也列入计划；缺少命令或 ARM64 条目保留并说明。运行库为系统共享组件，操作会影响使用它的软件；不保证提高游戏帧率。
- **操作门槛**：需管理员运行、手动关闭防护；安装事务忙、待核对、失败、未知结果或需要重启时停止，不强杀安装器。旧 14.42 包装仍登记时使用原包 `/repair` 补组件。
- **旧版残留**：卸载后、基线安装前，备份并受控清理核验过的旧 v14/2026 包装和孤儿登记；覆盖 HKLM/HKCU、32/64 位视图。固定旧 INF 存在时核验内容，缺失时按登记侧规则只清登记。保留身份无法确认的其他条目与备份证据。
- **运行库防护**：保留基础 V14 / 完全防护、关闭、强制清理安装器拦截与旧 UE4 拒绝执行恢复。防护恢复不属于运行库重装。
- 旧高帧、兼容、强力、AIO、通用整批/强制卸载与 DirectX 安装继续下线，历史未完成事务只可核对/归档，不重放操作。新流程见 [实现记录](notes/14x全量重装修复上线-20261015.md)。

## 其他功能与历史入口调整

- **进程管理**：属性操作分为“当前”和“总是”，分别调整本次进程或按完整应用路径保存持续规则；支持 CPU 优先级、亲和性、CPU 集合、效率模式、动态提升、I/O、内存及受支持的 GPU 调度设置。
- **后台抑制与自动内存回收**：保护工具自身、三角洲、系统进程、前台应用及已有手动设置或启用规则的进程，支持用户排除。内存回收按压力触发并设置冷却，不直接排除高 CPU 进程。进程管理按普通进程、三角洲、总是规则和后台管理分开显示，刷新保留当前行与选择。
- **工具 .NET**：继续自包含分发；产品检测、修复、离线安装器及开始菜单修复入口已移除，布局验收只在构建工具中进行。
- **后端异常即时处理**：报错弹窗直接提供核对与可信恢复入口，处理失败可原地重试，保留真实未确认证据。
- **运行库事务核对**：保留历史活动记录与日志，核对安装器退出状态后归档；不启动安装器、不表示修复成功或回滚。
- **虚拟化环境核验**：读取当前内核 DMA 保护状态，区分实际运行冲突、配置提醒与未知信息，保留硬件安全标准并避免读取失败误报。
- **旧入口移除**：下线脱机模式、旧帧格全局内存清理、CPU 实验室旧锁核器与方案分享。

Alpha 预发布仅向内测通道提供；公测通道仍筛选 Beta/Release。仓库根目录 `update.json` 保留公测版本，内测更新通过 GitHub 预发布及对应 Release 附件 `update.json` 提供。

## 系统要求

- Windows 10 1809+ 或 Windows 11（x64）
- .NET 8 SDK、PowerShell 7（仅构建与准备资源需要；安装后运行不需要）
- 以管理员身份运行（程序清单已声明 `requireAdministrator`，启动时会弹 UAC）

## 构建与运行

完整本地构建需要默认背景和 `Assets/Vc14Baseline14.42` 中的固定 14.42.34433 双架构微软原包。旧 AIO、DirectX 与 .NET 修复保持下线，不要从历史源码或交接归档恢复。

```powershell
installer\make-installer.cmd
```

```bash
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Debug -p:Platform=x64
```

构建产物为**非打包（unpackaged）自包含部署**，无需 MSIX 签名，直接运行：

```
src\DeltaNFD\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\DeltaNFD.exe
```

> **VC++ 原包**：使用 `src/DeltaNFD/Assets/Vc14Baseline14.42/vc_redist.x64.exe` 与 `vc_redist.x86.exe`，安装前校验固定 SHA256；不得把最新下载包改名冒充 14.42 基线。原包保留微软原始条款。

> **.NET**：工具自带 .NET 8 x64，产品不提供检测或修复安装器；部署不完整时重新安装完整工具。构建脚本继续核对实际自包含布局。

生成安装包需要 Windows、.NET 8 SDK 和 Inno Setup 6：

当前构建不需要 AIO 或 DirectX 修复资源。生成安装包不代表已获得上传或推送版本的授权。

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

感谢 [Henry++ / Mem Reduct](https://github.com/henrypp/memreduct) 对 Windows 内存回收工具的持续维护。新版后台内存回收参考其按内存压力触发、冷却和分类清理的设计思路；本项目以 Windows 接口独立实现逐进程回收与排除保护，未复制、移植或集成 Mem Reduct / routine 的 GPL 源码或二进制。Mem Reduct 的原始内容继续适用其 GPLv3 授权。

感谢 [abbodi1406 / VisualCppRedist AIO](https://github.com/abbodi1406/vcredist) 及其上游贡献者。历史版本曾集成其 AIO 资源与 `Installer.cmd`，原授权和署名继续保留；当前新版不再集成这些资源或执行脚本。

Microsoft Visual C++ 运行库由微软提供；感谢上游项目对运行库整合、安装脚本和兼容性的持续维护。

## 协议

自 Alpha0.915 起，本项目原创代码采用 [GNU GPL 第 3 版](LICENSE)（SPDX：`GPL-3.0-only`）。Copyright (c) 2026 DRXiaoXi（逐梦之子-晓夕）及项目贡献者。

GPLv3 允许使用、修改、商业使用和收费分发；分发本程序或其受 GPL 覆盖的修改版时，应遵守 GPLv3，保留版权与许可证声明、标明修改，并按 GPLv3 提供完整对应源代码和必要构建材料。本版不再附加原非商业限制。程序按原样提供，不附带担保。

历史版本按其发布时的许可证获得的权利继续保留；本次切换不追溯收回既有授权。第三方运行库、资源和依赖继续适用各自许可证，不因项目切换 GPLv3 被重新授权，详见 [第三方与历史授权](THIRD-PARTY-NOTICES.md)。

优化项均为对 Windows 公开系统设置（注册表 / 服务 / 计划任务 / 电源策略）的调整，键值整理自公开流传的系统调优资料与社区实践；「无省电释放模式」电源计划文件基于 [AtlasOS](https://github.com/Atlas-OS/Atlas) 项目导出并改名，致谢 AtlasOS 及其贡献者。
