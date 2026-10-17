# 03 前端 UI / 性能审计（WinUI 3 非打包）

- 审计员：C（只读；未修改源码、未运行应用、未 build、未联网）
- 时间：2026-10-18
- 工作区：`H:\coding\DeltaNFD`
- 技术前提（已核实）：`net8.0-windows10.0.19041.0` + `UseWinUI=true` + `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `Microsoft.WindowsAppSDK 1.8.260804001`（`src/DeltaNFD/DeltaNFD.csproj`）。**不是 WPF**，用户方案中的 WPF 手法必须逐条改写。
- 版本痕迹：本机已有 0.9210 构建产物与运行日志（`src/DeltaNFD/bin/.../win-x64`，2026-10-18 02:35）。

---

## ① 摘要 Top5

1. **"打开慢"主要不是首帧慢，而是首帧后两件事叠加：ContentDialog 串行阻塞 + 一次 9.4 秒全量扫描。** 实测日志：`启动 → 主窗口启动 → 导航 dashboard` 仅 **313 ms**（21:15:03.936→04.249），但 `优化检测` 从 21:15:05.634 跑到 21:15:15.012 = **9376 ms**（代码自报同值），期间有一次 **6.3 秒** 的后端调用空档（21:15:08.673→14.985）。扫描与弹窗链是并行的，但扫描独占系统后端（`BackendCaller._gate` 串行），且主页在扫描完成前一直显示"检测中…"。（`src/DeltaNFD/Services/OptimizationScan.cs:47,152`、`Views/DashboardPage.xaml.cs:24-32`、`%APPDATA%\Delta NFD\logs\app_2026-10-17_21-15-03.log`）
2. **启动链被"必须等用户点掉才能继续"的弹窗串行阻塞（最多 4 个，条件触发）**：`ContentFrame_Loaded`（`:237`，`async void`）依次 `await` 游戏目录弹窗（未检测到目录时必弹，`MainWindow.xaml.cs:263,606`）→ 公告弹窗（每版本一次，`:327`）→ 更新结果/新版本弹窗（`:341`）→ 内存限制弹窗（`:344`）。其中 `StartupGameDirectoryCheckCompleted` 只在游戏目录步骤结束后置位（`:269-272`），而主页健康检测由它门控（`DashboardPage.xaml.cs:49-52`），所以用户不点弹窗，主页就一直是"检测中…"。
3. **图片解码零优化，且是最容易被低估的内存项**：全仓 `DecodePixel` grep = **0**（`src/DeltaNFD` 全量 `.cs/.xaml`）。设置页一屏内 7 张 1080×1080 JPEG + 1 张 1024×1024 + 456×389 PNG + 256×256 logo，按 BGRA 解码 ≈ **36.07 MiB**；公告二维码 1284×2280 单张 ≈ **11.17 MiB**；内置背景 1586×992（安装目录实测尺寸）≈ **6.00 MiB**。
4. **长列表风险被高估，真实瓶颈是 IPC 往返数与串行闸门**：`allServices.json` 的 658 条**从不进入 UI**（3 个消费点全在服务组展开/守卫文本/默认启动模式，且 `UseBlockedDb=true` 的组一个都没有，见 ②A6）；SystemOptimizePage 单栏目最大 **37 行**（Debloat），不会一次创建上千元素。真正的开销是 `LoadItemsAsync` 里**逐行 await 一次 gear IPC**（`BxDbViewModel.cs:473-485`）与逐组服务状态 IPC（`:328-347`），且这些调用都排在 `BackendCaller` 的**模块级单闸门**后面（`BackendCaller.cs:41`，System 模块共用一个 `SemaphoreSlim`），会与主页 9.4 s 扫描互相排队。
5. **实时流已有 1 Hz 节流（后端推送），但前端是"每秒无条件重算+广播"**：`BackendHost.StatusInterval = 1s`（`src/DeltaNFD.Execution/BackendHost.cs:75`），CPU/帧格与 Procman 都配置了 `StatusSource`（`BackendCpuFrame/…/CpuFrameCommands.cs:47`、`BackendProcman/…/ProcmanCommands.cs:46`）。前端 `FrameService.OnCpuFrameStatus` 每秒无条件触发 5 个 `PropertyChanged` + `UpdateStatus()`（`:136-156`）；`ProcessPage` 每秒重取 300 行快照并重建 300 个 VM（`ProcessPage.xaml.cs:191-266`）。窗口最小化到托盘后轮询是否继续取决于 `AppWindow.Hide()` 是否触发 `Unloaded`（代码只在此处停表），需真机确认（见 ⑥-7）。

---

## ② 断言核实表（真 / 部分真 / 假 + file:line + WinUI 3 可行性）

| # | 用户方案中的断言 | 判定 | 证据（file:line） | WinUI 3 可行性（非打包） |
|---|---|---|---|---|
| A1 | WinUI 的 `BitmapImage` 有 `DecodePixelWidth` 属性 | **真** | `Microsoft.WinUI.dll` 元数据中出现 `DecodePixelWidth`×3、`DecodePixelType`×4、`DecodePixelHeight`×3（`src/DeltaNFD/bin/x64/Release/.../win-x64/Microsoft.WinUI.dll`，UTF-8 字符串扫描） | 可用。`Microsoft.UI.Xaml.Media.Imaging.BitmapImage.DecodePixelWidth/DecodePixelType` 存在 |
| A2 | `XAML Image Source=` 直接给 Uri 时无法设置 DecodePixelWidth | **真** | 全仓 4 处 `BitmapImage` 均由 `new Uri(...)` 直接构造，无 `DecodePixel*`：`MainWindow.xaml.cs:100,296,821`、`SettingsPage.xaml.cs:60`；XAML `Assets/` 资源简写 12 处：`MainWindow.xaml:24`、`DashboardPage.xaml:32`、`SettingsPage.xaml:483,540,556,572,593,607,621,635,649,663` | 需替代写法：①XAML 嵌套 `<Image><Image.Source><BitmapImage DecodePixelWidth="120" DecodePixelType="Logical" UriSource="ms-appx:///..."/></Image.Source></Image>`；②代码 `new BitmapImage { DecodePixelWidth = 120, UriSource = new Uri(path) }`。`ImageBrush` 同样可嵌套 `<ImageBrush.ImageSource><BitmapImage .../>` |
| A3 | `VirtualizingStackPanel.VirtualizationMode` 可用 | **部分真（需真机核实）** | `ProcessPage.xaml:85` 已写 `VirtualizingStackPanel.VirtualizationMode="Recycling"`；XAML 编译产物存在（`bin/.../Views/ProcessPage.xbf`，2026-10-18 02:35，20,169 B），说明编译器接受该附加属性；`Microsoft.WinUI.dll` 元数据有 `VirtualizingStackPanel`、`VirtualizationMode`、`get_VirtualizationModeProperty`、`ItemsStackPanel` | 属性合法可用，但 **WinUI ListView 默认 ItemsPanel 是 `ItemsStackPanel` 而非 `VirtualizingStackPanel`**，该附加属性是否被 `ItemsStackPanel` 读取需真机验证；另外 `Recycling` 大概率是默认值，显式声明可能是冗余（真机可用 `VisualTreeHelper` 统计实化行数验证） |
| A4 | `x:Bind` 编译绑定可用 | **真（且已全量落地）** | `x:Bind` 共 **399 处**（Views 18 个 XAML 中 14 个使用；`MainWindow.xaml` 0 处），经典 `{Binding}` **0 处**（`Views/*.xaml` + `MainWindow.xaml` 统计） | 可用，且 `x:Bind` 默认 `OneTime`，模板中变化字段已按 `Mode=OneWay` 标注（如 `ProcessPage.xaml` 行模板 `CpuText/WorkingSetText/PidText`） |
| A5 | ListView 默认虚拟化 + 默认 Recycling | **部分真** | `ProcessPage.xaml:85`；`ProcessPage.xaml.cs:19` `PageSize=300`；列表显式关闭了容器动画（`:86`） | `ListView` 默认用 `ItemsStackPanel` 虚拟化；`Recycling` 为默认模式的文档口径属实，但本仓库未做"实化行数"验证（见 ⑥） |
| A6 | 658 条 `allServices` 在 SystemOptimizePage 用 ItemsControl 渲染 | **假** | `allServices.json` 658 条**不进入任何 ItemsControl**。`BxCatalog.AllServices` 的 3 个消费点均非 UI 渲染：①`BxDbViewModel.cs:747`（`DescribeOpsForGuard` 文本拼接，仅在应用批次含服务组时）、②`BxService.cs:208/610`（服务组展开）、③`BxService.cs:2153`（服务默认启动模式）；且①②所在分支由 `group.UseBlockedDb` 控制，而 `BxCatalog.cs:510-526` 的 **13 个组均未设置 `UseBlockedDb=true`（默认 false）→ 该分支当前是死代码**。UI 侧服务组只渲染 **13 组** | — |
| A7 | SystemOptimizePage 的 ItemsControl 一次创建上千元素 | **假（量级远低）** | 单栏目最大条目数：Debloat **37**、WindowsTelemetry 30、Customization 16、BasicSettings/Security/RegistryTweaks 15、Tasks 14、UpRecommended 5、Placebo 2（`Assets/TweakDb/*.json` 转换计数；9 个 UI 栏目合计 **149 条**）；`pending` 栏目额外并入深度优化 20 项（`BxDbViewModel.cs:453-470`）→ 单次实化最大 **37 行**（`pending` = 5+20=25 行），每行含内层 `Details` ItemsControl（`SystemOptimizePage.xaml:151-161`） | ItemsControl 默认 `ItemsPanel=StackPanel` **不虚拟化**，但 37 行量级可接受；若切 `ListView` 放在外层 `ScrollViewer` 内会失去虚拟化，需改造页面根容器 |
| A8 | 无 Debounce 实现 | **真** | 全仓 `debounce` grep = 0；`Throttle` 仅命中更新检查状态枚举（`MainWindow.xaml.cs:535`）与插件配置文本 | 需自行实现：`DispatcherQueueTimer` 或 `bool _pending + Task.Delay(250)` 合并 |
| A9 | 每秒有全页绑定更新 | **部分真** | `FrameService.OnCpuFrameStatus`（`:136-156`）每秒无条件通知 `GamePowerHasError/GamePowerStatusText/MemoryCleanStatusText`（3 个）+ `UpdateStatus()→StatusText`（1 个），`IsGameRunning` 为条件通知；仅 **FramePage** 订阅（`FramePage.xaml.cs:172-173`），并把 `GamePowerStatusText` 等列入 `FeaturePresentationProperties`（17 项，`:151-159`），每次相关通知触发 `RefreshFeaturePresentation()` → 17 个 `PropertyChanged`（`:161-165`）。LabViewModel 只关心 `IsGameRunning`（`LabViewModel.cs:299-305`） | 不是全页，是 FramePage 局部 + 服务层无条件计算 |
| A10 | 每次点击开关都走一次 IPC 往返 | **假（对扩展库开关）/ 真（对帧格开关的副作用）** | 扩展库开关：`BxToggle_Toggled` → `ToggleAsync` 只改本地意图，无 IPC（`SystemOptimizePage.xaml.cs:127-152`、`BxDbViewModel.cs:644-666`）；真正 IPC 在「应用更改」逐项执行（`BxDbViewModel.cs:894+`，每项一次 `ApplyItemAsync`，超时 15 分钟，`BackendModules.cs:356`）。帧格页每个 setter 直接 `_ = SyncBackendExecutionAsync()` 发 4–8 条后端命令且**不串行、无合并**（`FrameService.cs:229,340,353,366,396,409,422,438,451,464,477…`，实现 `:906-976`） | — |
| A11 | 有全局搜索框 | **假（只有进程页有搜索）** | 全仓 `AutoSuggestBox` **0**；唯一搜索框 `ProcessPage.xaml:67`（`SearchBox`，`TextChanged="SearchBox_TextChanged"`，无 debounce，每次键入立即 `RefreshListAsync` → 一次 IPC，`ProcessPage.xaml.cs:179-185`）；Settings/SystemOptimize 均无搜索控件 | 数据源可选：`BxCatalog.Database`（10 个栏目共 149 条 + 深度 20）、`BxCatalog.ServiceGroups`（13 组）、`BxCatalog.AllServices`（658 条，可只按名搜服务）、`meta.json`（172 键中文名/说明） |
| A12 | 首屏关键路径可并行/延后 | **真** | `App.xaml.cs:64-136` 同步段 + `MainWindow.xaml.cs:237-346` 串行 await 链；可并行项见 ④ | — |

---

## ③ 实测数据

### 3.1 规模

| 项 | 数值 | 来源 |
|---|---|---|
| `src/DeltaNFD` .cs | **153 个 / 27,805 行** | PowerShell 统计（不含 obj/bin）。与交接值 27,785 差 20 行（统计口径/换行计数差异），量级一致 |
| XAML（含 .xaml.cs） | 19 文件 / 3,671 行；其中 Views 下 XAML 17 个 | 同上 |
| `x:Bind` / `{Binding}` | 399 / 0 | `Views/*.xaml` + `MainWindow.xaml` |
| 单文件最大 | `FrameService.cs` 1,566 行；`MainWindow.xaml.cs` 1,229 行；`BxDbViewModel.cs` 1,106 行；`SettingsPage.xaml` 647 行；`SystemOptimizePage.xaml.cs` 434 行 | 逐文件 `Measure-Object -Line` |
| ItemsControl / ListView（按页，开标签精确计数） | SystemOptimize 3/0；Lab 3/0；NvSettings 1/0；Settings 1/1；ProcessPage 2/1；PluginPage 0/2；GpuSpoofPage 0/1 | XAML `<ItemsControl` / `<ListView` 开标签正则计数。交接口径为 SystemOptimize 7、Lab 7、ProcessPage 7、Settings 4、PluginPage 4、GpuSpoof 2、NvSettings 2，疑为"含内层/含 ListView 内模板"的宽松计数；本表按元素节点精确计，用于判断实化元素量级 |
| TweakDb | allServices 658（`IsBlocked/IsBlocked11` 30）；WindowsTelemetry 30；Debloat 37；Customization 16；BasicSettings 15；Security 15；RegistryTweaks 15；Tasks 14；UpRecommended 5；Placebo 2；special 8 键；meta 172 键；ServiceWarningsZh 132 键 | JSON 反序列化计数 |
| 服务组 | 13 组（`svc-print`…`svc-defrag`），组内服务 1–10 个 | `BxCatalog.cs:510-526` |

### 3.2 图片解码内存（BGRA 4 B/px；由文件尺寸计算）

| 资源 | 像素 | 文件 | 解码内存 | 使用点 |
|---|---|---|---|---|
| `FeedbackGroupQr.png` | 1284×2280 | 672,222 B | **11.17 MiB** | 首启公告 `MainWindow.xaml.cs:296`（无 DecodePixel） |
| `Assets/background.png` | 1586×992（安装目录实测） | 1,722,681 B | **6.00 MiB** | 全窗口背景 `MainWindow.xaml.cs:821`（无 DecodePixel；`.gitignore` 忽略、发布清单含 1.7 MB） |
| 7× 1080×1080 JPEG（5 个 `bug-hunter-*` + `lead-tester` + `shangguan-cash-support`） | 1080×1080 | 48.9–189.5 KB | **4.449 MiB × 7 = 31.14 MiB** | 设置页 ImageBrush 60×60 圆形头像 `SettingsPage.xaml:556,572,593,607,621,635,649` |
| `token-provider.jpg` | 1024×1024 | 177,434 B | **4.00 MiB** | 同上 `SettingsPage.xaml:540` |
| `bug-hunter-anonymous-yanzhu.png` | 456×389 | 290,001 B | **0.677 MiB** | 同上 `SettingsPage.xaml:663` |
| `DeltaNFD.png` | 256×256 | 120,268 B | **0.250 MiB** | 标题栏 26×26、主页 48×48、设置 48×48 |
| **设置页合计** | — | — | **≈ 36.07 MiB**（一屏内全部实化，`IsExpanded=False` 的 Expander 内 6 张也在内，见 ⑥-4） | — |

补充：`Expander`（Bug 排雷员）`IsExpanded="False"`（`SettingsPage.xaml:584`），但 `ImageBrush` 的 `ImageSource` 在 XAML 解析时即创建 BitmapImage，**折叠不阻止解码**（真机可用 `MemoryUsage` 验证）。

### 3.3 启动时序（本机真实日志，2026-10-17 21:15）

| 时刻 | 事件 | 间隔 |
|---|---|---|
| 03.936 | `=== 启动 ===`（`Log.Startup`，含 14 天旧日志清理同步 IO） | — |
| 04.050 | `主窗口启动`（`App.xaml.cs:99`，位于 `new MainWindow()` **之前**） | **114 ms** = 命令行/单实例守卫/`Log.Startup`/`UpdateChannelInstallation`（`App.xaml.cs:64-98`） |
| 04.249 | `导航 → dashboard`（`MainWindow.xaml.cs:1223`，首次导航由 ctor `:123` 触发） | **199 ms** = `new MainWindow()`：XAML 解析 + `RefreshPluginNavigation` + `ApplyBackgroundImage`。`Activate()`（`:116`）在 `new MainWindow()` 之后（`App.xaml.cs:116`），但首次导航日志已含 ctor 全部同步工作 |
| 05.449 | 更新自动检查开始 | — |
| 05.634 | 优化检测全量扫描开始 | — |
| 05.767 | 更新检查 `UpToDate`（本机网络快，**不代表最坏情况**） | 318 ms |
| 08.673 → 14.985 | 单次后端调用空档（TelemetryServices 时间戳跳变） | **6.31 s**；同段内 08.205 有一次 `导航 → settings`，故空档**归属未定**（可能是该栏目的 WMI/wevtutil 慢查询，也可能是首次进入设置页的 XAML 实化 + 36 MiB 图片解码，见 ⑥-5/⑥-11） |
| 15.012 | 优化检测完成 | **9.376 s**（扫描总耗时，代码自报 `OptimizationScan.cs:152`） |
| 05.054→05.670 | GPU 后端冷启动（`官方后端启动`→`就绪`） | **616 ms** |
| 05.670→05.768 | system 后端启动/就绪 | **98 ms** |

补充说明（避免夸大）：上述弹窗链**全部是条件触发**——重复启动且"游戏目录已配置 + 公告已确认（`CurrentAnnouncementVersion` 已记录）+ 无待安装更新 + 无 `removememory`"时，`ContentFrame_Loaded` 只做 `BackendAttention.RefreshPersisted()` + `OfflineModeRetirement.RecoverAsync()`（无残留时早返回）+ `EnsureGameDirectoryOnStartupAsync`（`FindRoots()` 命中即 return，`:601-604`），秒级完成。因此 P0-1 的实际受害场景是**首装/首启、换机、公告版本更新后、或游戏目录丢失**这几类；而手机上的"每次打开都慢"更可能来自 P0-3 的 9.4 s 扫描（每次冷启必跑）。日志本次即是"首启后 3 分钟内"场景，两条链并行发生。

### 3.4 代码中的超时值（用于估算最坏耗时）

| 环节 | 超时 | 位置 |
|---|---|---|
| 更新清单获取（每源） | **15 s**，GitHub + 自建**串行** → 最坏 ~30 s | `UpdateService.cs:117`（`checkTimeoutSeconds:15`）、`:1450`、`:266` |
| 更新下载空闲 | 90 s（仅下载，不在启动路径） | `UpdateService.cs:40` |
| bcdedit 内存限制检测 | **20 s** | `CpuTopologyService.cs:832` |
| 机箱形态 PowerShell 探测 | **20 s**（每进程一次，`_chassisProbe` 静态缓存；启动即 fire-and-forget） | `FrameService.cs:1328-1329`（`ProbeChassisKindCoreAsync`，`:1316,1324`） |
| 后端连接/握手 | 15 s / 10 s | `BackendClient.cs:196,408` |
| 单个后端命令 | 2–15 min（各模块白名单） | `BackendModules.cs:205-376`，如 `SystemBxApply=15min`、`SystemBxStatus=5min` |
| 设置写入互斥 | 30 s | `AppSettingsStore.cs:364` |
| 旧脱机助手排空 | 35 s（仅存在残留记录时） | `OfflineModeCoordinator.cs:210+` |
| 全盘目录搜索 | 弹窗文案自述"可能数十秒" | `MainWindow.xaml.cs:611` |

### 3.5 首屏关键路径逐项判定（哪些必须阻塞）

| 步骤 | 位置 | 阻塞内容 | 判定 |
|---|---|---|---|
| 命令行解析 / 单实例 / `Log.Startup`（含旧日志清理）/ `UpdateChannelInstallation` | `App.xaml.cs:66-98` | UI 线程同步；`CleanupOldLogs` 枚举日志目录 | 可接受但可挪到后台（用户不感知） |
| `new MainWindow()`：`InitializeComponent` + `RefreshPluginNavigation`（读插件索引+逐包 manifest）+ `ApplyBackgroundImage`（解码 6 MiB） | `MainWindow.xaml.cs:72,83,126` | 决定首帧 | **必要**（首帧内容），但可"先显示占位再加载背景/插件菜单" |
| `_ = ServiceLocator.Frame`（FrameService 构造） | `App.xaml.cs:102` → `FrameService.cs:86-133` | LoadSettings/注册表迁移/`ProbeLaptopChassisAsync`（起 PowerShell 进程） | 不必阻塞：可延后到首帧后 |
| `_ = ServiceLocator.GameProcess`（仅 `GamePriorityEnabled`） | `App.xaml.cs:103-106` | `GameProcessMonitor` → 冷启动 CPU/帧格后端（约 0.6 s，含 `Process.Start` + 握手） | 不必阻塞 |
| 游戏目录检查（未配置时弹窗 + `FindRoots` 同步枚举固定盘） | `MainWindow.xaml.cs:263,599-647`；`DeltaForceLocator.cs:66-147` | 弹窗必须等用户；`FindRoots` 遍历每块固定盘的 3 个 WeGame 库路径 | 弹窗**必须**（否则后续功能无目标）；扫描本身可后台 + 超时 |
| 公告弹窗（每版本一次） | `MainWindow.xaml.cs:281-337` | 串行等待用户 | 可延后到用户首次操作后/非模态 InfoBar |
| 更新结果/新版本弹窗 | `MainWindow.xaml.cs:415-431,475-577` | 网络最坏 30 s + 弹窗 | **必须延后**（当前排在公告之后，最坏 30 s+） |
| 内存限制检测（后端 IPC + bcdedit） | `MainWindow.xaml.cs:353-407` | 再冷启一个后端 + 20 s 超时 | 必须延后（可用后台检测 + InfoBar） |
| 显卡伪装失效检查 | `MainWindow.xaml.cs:439-460` | GPU 后端 IPC | 已延后（`_gpuWatchReady` 后），合理 |
| 主页优化全量扫描 | `DashboardPage.xaml.cs:31` → `OptimizationScan.cs:47` | 9.4 s 连续系统后端调用（单实例串行，期间其他系统后端操作排队） | **必须**（主页分数），但应改为增量/缓存优先，而不是每次冷扫 |

---

## ④ 问题清单（P0/P1/P2）

> 收益 = 预计可观测改善；风险 = 改动引入回归的可能性；成本 = 人日量级（0.5 = 半天）。

### P0

| ID | 问题 | 证据 | 收益 | 风险 | 成本 |
|---|---|---|---|---|---|
| P0-1 | 启动后被**最多 4 个串行动态触发的模态对话框**卡住（游戏目录→公告→更新结果/新版本→内存限制，均为条件触发，实际路径取决于本机状态），且主页健康检测被 `StartupGameDirectoryCheckCompleted` 门控；用户不点弹窗 → 主页一直"检测中…" | `MainWindow.xaml.cs:237-346`（`await EnsureGameDirectoryOnStartupAsync` → 公告 `:327` → `TryHandleUpdateOnStartupAsync` → `CheckMemoryLimitOnStartupAsync`）；`DashboardPage.xaml.cs:49-52` | 首屏可交互时间；消除"打开几秒没反应/白屏"观感 | 中（弹窗顺序有业务约束：同刻只能一个 ContentDialog，更新必须在公告后） | 1 |
| P0-2 | 图片全部无 `DecodePixelWidth`：设置页一屏 ≈36.07 MiB、公告二维码 11.17 MiB、背景 6.00 MiB | 见 3.2；`grep DecodePixel`=0；`MainWindow.xaml.cs:296,821`、`SettingsPage.xaml:540-663` | 常驻内存 -36 MB 级；设置页首次进入更快 | 低（仅需保持清晰度，120–160 px 足够 60×60 头像） | 0.5 |
| P0-3 | 主页"优化检测"每次冷启都全量扫 9.4 s，且串行占用系统后端（单实例 `BackendCaller._gate`），期间导航到系统优化页/应用更改都会排队 | 日志 9,376 ms；`OptimizationScan.cs:47-169`；`BackendCaller.cs:38-50` | 首屏"检测中"时长；系统优化页操作响应 | 中（分数语义变化需保持"未知不冒充正常"） | 1.5 |

### P1

| ID | 问题 | 证据 | 收益 | 风险 | 成本 |
|---|---|---|---|---|---|
| P1-1 | ProcessPage 1 s 全量重取 300 行 + 重建 300 个 VM + 选中时再来一次详情 IPC；页面隐藏/托盘隐藏后是否继续取决于 `Unloaded` 是否触发（仅 `Unloaded` 停表） | `ProcessPage.xaml.cs:20,25,156,157-167,191-266`；`ProcessPageViewModels.cs:15-45`；`MainWindow.xaml.cs:974-984`（`HideToTray` 只 `AppWindow.Hide()`） | 空闲 CPU/GC 与 IPC 减半以上 | 低（列表实时性允许 2 s） | 1 |
| P1-2 | `BxDbViewModel.LoadItemsAsync` 串行 IPC：每栏目 1 次状态批量；但（a）服务组每行 1 次 `GetServiceGroupStateAsync`（13 次，有缓存兜底），（b）多档位条目**逐行** `await GetGearInfoAsync`（最多 37 次往返），（c）「应用更改」逐项 IPC（每项独立命令） | `BxDbViewModel.cs:328-347,473-485,894+`；`BackendBxService.cs:24-42,66+` | 系统优化页打开与切栏目明显变快 | 中（后端命令需新增批量接口，或前端并行化但后端 `_gate` 串行） | 2 |
| P1-3 | 帧格页开关：每次 setter 直接 `_ = SyncBackendExecutionAsync()`，4–8 条后端命令，**无串行化、无合并**；连点会产生重叠任务（靠 `BackendCaller._gate` 串行执行但顺序不保证） | `FrameService.cs:229,340,353,366,…`、`:906-976`（内部 `ApplyBoost`→`ApplyTweaks`→`SetPowerLock`→`EnsureMonitor`） | 连点不卡、状态不抖动、后端不排队 | 中（涉及状态收敛，需"最后一次为准"语义） | 1 |
| P1-4 | 服务端 1 Hz 推送 status，但 `Procman` 的状态**无人订阅**（仅 CPU/帧格有 pump）；且 CPU/帧格每秒在 UI 线程无条件 notify（含 `UpdateStatus()` 字符串拼接） | `BackendProcman/…/ProcmanCommands.cs:46`；`SubscribeStatusAsync` 仅 `BackendCpuFrameService.cs:59` 使用；`FrameService.cs:136-156` | 后端空转 CPU、UI 线程每秒工作 | 低 | 0.5 |
| P1-5 | 无搜索：SystemOptimize/Settings 无搜索框；进程页搜索无 debounce（每次键入一次 IPC + 清空选中详情） | `ProcessPage.xaml:67`、`ProcessPage.xaml.cs:179-185`；全仓 `AutoSuggestBox`=0 | 658 服务 + 149 条目的可发现性 | 低（纯前端过滤） | 1 |

### P2

| ID | 问题 | 证据 | 收益 | 风险 | 成本 |
|---|---|---|---|---|---|
| P2-1 | 背景图 `Image` 全窗口 `Stretch=UniformToFill`，无 DecodePixel；用户自定义 4K 图将解码 ~31.6 MiB 并常驻 | `MainWindow.xaml.cs:809-834`；`BackgroundManager.cs:ResolveCurrent`；`AppSettings.BackgroundImageEnabled` 默认 `true`（`AppSettingsStore.cs:129`） | 自定义背景场景内存 | 低 | 0.3 |
| P2-2 | `ItemsControl` 全部在 `ScrollViewer` 内、不虚拟化（当前量级可接受，但 Debloat 37 行 × 十数个元素 ≈ 400+ 元素，切栏目时 `Items.Clear()` 全量重建） | `SystemOptimizePage.xaml:7,98`；`BxDbViewModel.cs:634-641` | 切栏目流畅度 | 低-中 | 1（若要换 `ListView` 需去掉外层 ScrollViewer） |
| P2-3 | `MainWindow` 构造期同步做 `Log.Startup`（旧日志清理 IO）、插件索引解析、背景解码；`FrameService` 构造期起 `powershell.exe` 探测机箱 | `App.xaml.cs:97`；`Log.cs:71-117`；`MainWindow.xaml.cs:83,126`；`FrameService.cs:132,1324-1345` | 冷启动 100–300 ms 级 | 低 | 0.5 |
| P2-4 | 主题色 `SetAccentColor` 每次 `persist` 都写设置文件（Global mutex + JSON 全量写），设置页滑块/色块连点有写放大 | `App.xaml.cs:189-263`；`AppSettingsStore.cs:356-390`；`SettingsPage.xaml:139-141`（遮罩滑块 `ValueChanged` 每次都写） | 设置页操作抖动 | 低 | 0.5 |
| P2-5 | 首启公告的二维码 `BitmapImage` 在 `ContentDialog` 内容中直接 new，无 `DecodePixelWidth`；`MaxWidth=360` 但源是 1284 宽 | `MainWindow.xaml.cs:294-298` | 单次 11.17 MiB → ~1 MiB | 低 | 0.2 |

---

## ⑤ 建议动作（文件 / 实现方式 / 验收指标 / 回滚）

### A. 启动链解耦（对应 P0-1、P2-3）

1. **文件**：`src/DeltaNFD/MainWindow.xaml.cs`
   - 把 `ContentFrame_Loaded` 里的 `EnsureGameDirectoryOnStartupAsync` 拆成 `DetectGameDirectoryAsync`（纯检测，后台线程，`Task.Run(() => DeltaForceLocator.FindRoots())`，加 1500 ms 软超时：超时先用 `AppSettings.GameRootOverride`，弹窗延后 2 s 或改为 `InfoBar` 引导）。
   - `StartupGameDirectoryCheckCompleted` 改为"检测完成（不论是否已配置）即置 true"，不再等用户在对话框里点完（`MainWindow.xaml.cs:271`、`DashboardPage.xaml.cs:49`）。
   - `TryHandleUpdateOnStartupAsync` / `CheckMemoryLimitOnStartupAsync` 用 `_ = DispatcherQueue.TryEnqueue(...)` 延后到 `ContentFrame.Loaded` 之后 + `await Task.Delay(1500)`；弹窗仍保持"同一时刻一个"的串行队列（新增 `_dialogGate = SemaphoreSlim(1,1)`）。
   - `ProbeLaptopChassisAsync`（`FrameService.cs:132`）延迟到首帧后 3 s 且仅在需要机箱判定的页面（帧格/进程后台）。
   - `Log.CleanupOldLogs()` 改为 `Task.Run`（`Log.cs:90`）。
   - **验收**：冷启动三次取中位数，`主窗口启动 → 导航 dashboard` 仍 ≤ 400 ms；`ContentFrame_Loaded` 完成（日志加一行 `启动后续初始化完成`）≤ 1.5 s（不含用户未点的弹窗）；主页"检测中…"消失时间 ≤ 2 s。
   - **回滚**：改动集中在 `MainWindow.xaml.cs` 单个方法 + `Log.cs` 一行；`git revert` 该文件即可，无数据格式变更。

2. **文件**：`src/DeltaNFD/Services/OptimizationScan.cs`
   - `RunIfNotScannedAsync` 前先读持久化轻量缓存（`%APPDATA%\Delta NFD\optimization-scan.json`，存 score/时间戳/各状态哈希），有缓存先渲染，再后台刷新；把 `FireChanged()` 从"每项一次"改为 150 ms 合并（`DispatcherQueueTimer`）。
   - **验收**：主页分数首次可见 ≤ 300 ms（缓存命中）；后台刷新完成 ≤ 12 s；扫描期间系统优化页操作不排队（后端命令改为可抢占或分段）。
   - **回滚**：新增缓存文件可删除，逻辑分支用 `OptimizationScan.UseCache` 常量关闭。

### B. 图片解码（对应 P0-2、P2-1、P2-5）

1. **文件**：`src/DeltaNFD/Views/SettingsPage.xaml`（540–663 行 9 张头像：1×1024 + 7×1080 + 1 PNG）
   - 每处 `ImageBrush` 改为嵌套 BitmapImage，例如：
     ```xml
     <Ellipse.Fill>
       <ImageBrush>
         <ImageBrush.ImageSource>
           <BitmapImage UriSource="ms-appx:///Assets/Acknowledgements/token-provider.jpg"
                        DecodePixelWidth="120" DecodePixelType="Logical" />
         </ImageBrush.ImageSource>
       </ImageBrush>
     </Ellipse.Fill>
     ```
     头像显示 60×60，`DecodePixelWidth=120`（2× DPI 余量）。
   - `SettingsPage.xaml:483` `AboutLogoImage`（48×48）同样改嵌套 `DecodePixelWidth="96"`。
2. **文件**：`src/DeltaNFD/MainWindow.xaml.cs:296`（二维码）
   - `new BitmapImage { DecodePixelWidth = 720, UriSource = new Uri(path) }`（`MaxWidth=360` × 2 DPI）。
3. **文件**：`src/DeltaNFD/MainWindow.xaml.cs:821`（背景）
   - `var bmp = new BitmapImage { UriSource = new Uri(path) }; bmp.DecodePixelWidth = Math.Max(1280, (int)Content.XamlRoot.Size.Width);`（窗口尺寸变化时重建，`SizeChanged` 节流 300 ms）。对 `ms-appx:///Assets/DeltaNFD.png`（`MainWindow.xaml:24`、`DashboardPage.xaml:32`）改 `DecodePixelWidth="64"`。
   - **验收**：`dotnet build` 后运行，`SettingsPage` 内存增量 ≤ 12 MiB（原 ≈36.07 MiB）；二维码弹窗 ≤ 2 MiB；背景 ≤ 8 MiB（1586 宽）；目视无模糊（DPI 150%）。
   - **回滚**：仅 XAML/几行 C#，逐文件 revert。

### C. 长列表 / IPC 往返（对应 P1-1、P1-2、P2-2）

1. **文件**：`src/DeltaNFD/Views/ProcessPage.xaml.cs`
   - `PollInterval` 1 s → 2 s；`Loaded`/`Unloaded` 之外增加 `Window.VisibilityChanged` 或 `AppWindow` 监听（WinUI 非打包可用 `App.MainWindow.AppWindow` + `Window.Activated`/`SizeChanged`）来暂停轮询；托盘隐藏时置 `_paused`。
   - `RefreshListAsync`：改为先取 `ProcmanList` 的轻量 diff（已用 `StableList.Apply`），但**不要每轮新建 300 个 VM**——把 `rows = result.Processes.Select(new ProcessRowVm)` 改为"按 IdentityKey 复用已有行，仅更新字段"，并复用 `List<ProcessRowVm>` 缓冲。
   - `LoadDetailAsync` 只在选中项的 `(Pid,StartTicks,Path)` 或 `OwnedPropertyCount` 变化时调用（当前每秒都调用，`:259`）。
   - **验收**：进程页空闲 10 分钟 CPU ≤ 1%（原 ~3–5% 量级，需真机 Profile 确认）；切走页面 1 s 内无 IPC（日志无 `ProcmanList` 调用）。
   - **回滚**：单文件，`git revert`。

2. **文件**：`src/DeltaNFD/ViewModels/BxDbViewModel.cs`
   - `LoadItemsAsync` 的 gear 预取（`:473-485`）改为一次批量：新增后端命令 `SystemBxGearInfoBatch`（`BxService.cs` + `BackendModules.cs` 白名单 + 超时），或前端 `Task.WhenAll` 并行（受 `_gate` 串行限制，收益有限，故推荐批量命令）。
   - 服务组行同时发起 `ResolveServiceGroupAsync` 与状态查询（`Task.WhenAll`），并在 `OptimizationScan` 有缓存时跳过状态查询（`:331` 已部分做，扩展到服务组）。
   - **验收**：进入 SystemOptimize 页（默认 basic 15 行）首屏 ≤ 150 ms；切到 Debloat（37 行）≤ 400 ms；日志 IPC 次数从 ~40 降到 ≤ 4。
   - **回滚**：新增命令向后兼容（旧客户端不发），前端加 `UseBatchGearInfo` 开关。

3. **若确需虚拟化，具体落点（当前量级不必须，按收益排序）**

   | 位置 | 当前元素 | 现控件 | 建议 | 注意 |
   |---|---|---|---|---|
   | `SystemOptimizePage.xaml:98` | ≤ 37 行（Debloat 最大），每行含 3 个按钮/开关 + 内层 `:151` Details | `ItemsControl`（外层 `ScrollViewer` `:7`） | 若未来条目破百：外层改 `ListView`（`ItemContainerTransitions` 清空 + `ItemTemplate` 复用现 `DataTemplate`），**删掉根 `ScrollViewer`**（ListView 自带滚动，否则虚拟化失效） | `x:Bind` 的 `Mode=TwoWay` 在 `ListView` 模板中仍可用 |
   | `ProcessPage.xaml:85` | 已 300 行/页 | `ListView` + `VirtualizingStackPanel.VirtualizationMode="Recycling"` | 保持；补一个 `ItemsStackPanel` 的 `CacheLength` 调整试验（默认单位是"屏"） | 该附加属性是否被 `ItemsStackPanel` 消费需真机核实（见 ⑥-3） |
   | `NvSettingsPage.xaml:109` | 9 行（`NvContracts.cs` 中 `Id = 0x` 共 9 项） | `ItemsControl` | 无需改 | — |
   | `LabPage.xaml:22/82/219` | 4 / 2 / 3 行 | `ItemsControl` | 无需改 | — |
   | `ProcessPage.xaml:134/274` | 能力项 ~7 行 / 持有记录 0–若干 | `ItemsControl` | 无需改（详情面板内，量小） | — |
   | `SettingsPage.xaml:80` | 主题色块（常量数量） | `ItemsControl` | 无需改 | — |

   **`ItemsRepeater` 不推荐**：本项目已全面 `x:Bind` + `ListView`，`ItemsRepeater` 需手写 `Layout`/`ItemTemplate` 且无内置选中/键盘导航，改造面远大于收益（当前最大 37 行）。

### D. 实时流 / 状态节流（对应 P1-4、P1-3）

1. **文件**：`src/DeltaNFD/Services/FrameService.cs:136-156`
   - `OnCpuFrameStatus` 只在值**实际变化**时 `OnPropertyChanged`（`_gamePowerStatusText`、`_memoryCleanStatusText` 先比较后赋值）；`UpdateStatus()` 只在 `StatusText` 变化时调用（`StatusText` setter 已做相等判断，`:598-607`，但 `DescribeStatus()` 仍每秒拼字符串 → 加 `LastComputedAt` 300 ms 节流）。
   - 加 300–500 ms 合并窗：`_statusApplyQueued` + `DispatcherQueue.TryEnqueue` 去重（参考 `OnPluginsChanged` 的合并写法 `MainWindow.xaml.cs:143-155`）。
2. **文件**：`src/DeltaNFD.Execution/BackendHost.cs:75` + `BackendProcman/…/ProcmanCommands.cs:46` + `BackendCpuFrame/…/CpuFrameCommands.cs:47`
   - Procman 无订阅者 → 去掉 `StatusSource`（或 `StatusInterval = 5s`）；CPU/帧格保持 1 s（游戏运行判定需要），但前端消费端合并。
   - **验收**：空闲时进程 CPU：UI 线程每秒 PropertyChanged 次数从 ~22 降到 ≤ 2；`Backend.Procman.exe` 空闲 CPU ≈ 0。
   - **回滚**：`StatusSource` 一行恢复即可。

3. **文件**：`src/DeltaNFD/Services/FrameService.cs:906`
   - `SyncBackendExecutionAsync` 加 `SemaphoreSlim _syncGate = new(1,1)` + 300 ms debounce（`_syncPending` 标记 + `Task.Delay(300)` 合并），保证"最后一次设置状态为准"，连点只执行一轮。
   - **验收**：快速连续切换 10 次开关，后端命令总次数 ≤ 2 轮；最终状态与 UI 一致（读 `BackendCpuFrameService.Latest`）。
   - **回滚**：去抖参数常量化（`SyncDebounceMs`），置 0 即原行为。

### E. 搜索与交互（对应 P1-5）

1. **文件**：`src/DeltaNFD/Views/SystemOptimizePage.xaml`（顶部 `StackPanel`，第 10–13 行后插入）+ `SystemOptimizePage.xaml.cs`
   - 增加 `AutoSuggestBox PlaceholderText="搜索优化项 / 服务 / 注册表项"`；`TextChanged` 用 200 ms `DispatcherQueueTimer` 去抖；匹配源：当前栏目 `Items` 的 `DisplayName/Description/Id` + `BxCatalog.AllServices`（658 条按 `ServiceName`）+ `meta.json`。
   - 命中结果用现有 `Items` 列表过滤（不新建集合），并显示"命中 N 项"。
2. **文件**：`src/DeltaNFD/Views/ProcessPage.xaml.cs:179-185`
   - `SearchBox_TextChanged` 加 250 ms 去抖，避免每次键入一次 `ProcmanList` IPC。
3. **文件**：`src/DeltaNFD/Views/SettingsPage.xaml`
   - 可选：顶部加"设置项搜索"（纯前端锚点滚动），或在导航栏加全局搜索入口（`MainWindow.xaml` `NavigationView.AutoSuggestBox`，WinUI 支持）。
4. **验收**：输入"遥测"命中 ≥ 3 项且 ≤ 100 ms 响应；进程搜索每 250 ms 最多 1 次 IPC。
   - **回滚**：新增控件可移除，无数据变更。

### F. 设置写放大（对应 P2-4）

- **文件**：`src/DeltaNFD/Views/SettingsPage.xaml.cs:624-632`（`DimSlider_ValueChanged`）改为 200 ms 去抖后再 `BackgroundManager.SetDimOpacity`；`App.xaml.cs:189` `SetAccentColor` 的 `persist` 由调用方在 `PointerReleased` 时传 `true`，拖动中传 `false`。
- **验收**：拖动遮罩滑块 3 s，`settings.json` 写入次数 ≤ 3（原每次 `ValueChanged` 一次）。
- **回滚**：单文件参数化 `PersistDelayMs=0`。

---

## ⑥ 未验证事项（需真机 ETW / 性能剖析，本审计只读无法覆盖）

1. **首帧真实耗时**：日志只有 `主窗口启动` 与 `导航 → dashboard` 两个时间戳，未覆盖 `App.OnLaunched` 之前（XAML/Bootstrap 加载、JIT、WinAppSDK 初始化）。需 ETW `Microsoft-Windows-XAML` + `dotnet-trace` 采样冷启动（`--profile` 或 `PerfView /StartCollect`）。
2. **UI 线程阻塞归因**：`OnLaunched` 中 `Log.Startup`、`new MainWindow()`、`ServiceLocator.Frame` 构造的实际同步耗时只能估算（源码无埋点）。建议加 `Stopwatch` 分段日志（只读审计未改）。
3. **`VirtualizingStackPanel.VirtualizationMode` 对 `ListView`（ItemsStackPanel）是否生效**、`Recycling` 是否默认值：需真机 `VisualTreeHelper` 统计 `ListViewItem` 实化数量（滚动到 300 行底部观察回收复用）。
4. **BitmapImage 解码时机**：`ImageBrush.ImageSource` 在 `Expander` 折叠时是否仍解码 9 张图（预期"是"，需 `MemoryUsage`/`VMMap` 前后对比）；`DecodePixelType.Logical/Physical` 默认值需实测确认 100%/150% DPI 下的实际解码尺寸。
5. **9.4 s 全量扫描的构成**：日志显示 6.3 s 集中在 `TelemetryServices`（WindowsTelemetry 30 项）等多注册表/计划任务查询，但后端内部逐项耗时未埋点；需 `DeltaNFD.Backend.SystemMaintenance` ETW 或 `System.Diagnostics.Stopwatch` 分段（只读审计不修改）。
6. **`ProcessPage` 1 s 轮询的实际 CPU/GC 占用**：需 `dotnet-counters`（`cpu-usage`、`gc-heap`、`alloc-rate`）在进程页停留 5 分钟采样；本次仅做静态推断（每秒 300 VM 分配）。
7. **托盘隐藏时是否仍轮询**：`HideToTray` 只执行 `AppWindow.Hide()`（`MainWindow.xaml.cs:974-984`），不导航、不停 `DispatcherTimer`；`AppWindow.Hide()` 是否触发页面 `Unloaded` 需真机验证（WinUI 窗口隐藏与 XAML 卸载无必然绑定）。若未触发，则隐藏后仍每秒 `ProcmanList` + 300 VM 分配。验证方法：托盘隐藏后 60 s 观察日志/IPC 计数。
8. **帧格开关连点的实际命令重叠数**：需后端 ETW 或后端日志记录每次 `SyncBackendExecutionAsync` 的起止，确认是否发生重叠与乱序。
9. **内存数字（36.07 MiB / 11.17 MiB）为按像素计算的解码下界**（BGRA 4 B/px），未计入 `BitmapImage` 内部缓存、D3D 纹理对齐与 DPI 缩放；需真机 `Windows Performance Recorder` 的 `Microsoft-Windows-DXGI`/`d3d9` provider 或 `MemoryUsage` 快照。
10. **图片真实显示尺寸**：`Expander` 折叠时 `Ellipse` 仍为 60×60；`bug-hunter-anonymous-yanzhu.png` 456×389 的 60×60 圆形裁切是否会在 2× DPI 轻微模糊，需目视确认。
11. **日志 6.31 s 空档的真实归属**：21:15:08.205 恰好有一次 `导航 → settings`（首次进入设置页），与 08.673 起的 TelemetryServices 慢查询重叠。设置页首次导航需解析 647 行 XAML（48 个 `x:Bind`）+ 实化 9 张图（≈36 MiB 解码）+ 1 个 `ListView`，极可能就是该空档的来源。需在 `OnNavigatedTo`/`Loaded` 加 `Stopwatch` 或 ETW `Microsoft-Windows-XAML` 确认，届时 P0-2 的优先级应上调。
12. **`allServices` 死分支**：`group.UseBlockedDb` 分支（`BxService.cs:208,610`、`BxDbViewModel.cs:747`）当前 13 个组均为 `false`，是否为预留能力需与作者确认；若确认不再使用，可删 658 条 JSON 的加载路径（当前 `AllServices` 仅在 `GetServiceWillBrake`/`DefaultStartMode` 被间接使用）。

---

### 附：本报告使用的统计口径

- 行数：`Get-Content | Measure-Object -Line`（不含 obj/bin）。
- 图片尺寸：本地二进制头解析（PNG IHDR / JPEG SOF0），`background.png` 取本机安装目录 `C:\Program Files\Delta NFD\Assets\background.png`（1586×992，1,722,681 B，与发布清单 `sizeBytes=1722681` 一致）。
- 启动日志：`%APPDATA%\Delta NFD\logs\app_2026-10-17_21-15-03.log` / `..._21-15-05.log`（只读引用，未修改）。
- 未使用 `dotnet build`、未运行应用、未联网。
