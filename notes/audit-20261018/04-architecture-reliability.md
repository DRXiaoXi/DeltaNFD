# 审计 04：多进程架构、更新事务、容灾与服务端

- 审计员：audit-arch（Teammate D）
- 审计日期：2026-10-18
- 工作区：`H:\coding\DeltaNFD`
- 审计方式：**只读**。未修改任何源码/配置，未运行 `dotnet build`、安装器、更新器、服务端或任何会写系统的程序，未联网。仅使用文件读取、静态检索与 PowerShell 只读统计（`Get-Content`/`Get-ChildItem`/`Select-String`/行数统计）。
- 产出文件：本文件（唯一写入）。
- 口径说明：`file.cs:行号` 均为**物理行号**（含空行，与 read 工具一致）。任务给出的"6 个壳约 1640 行"为**非空行**口径；本报告同时给出物理行数（1854）以免混淆。
- 重要限制：所有"CPU/内存/句柄/耗时"类运行期数据均**未实测**（未运行产品），本报告只做静态代码分析与规模统计；涉及运行期收益的结论均标注"待实测"。

---

## ① 一页摘要

### 总评

现有多进程/更新/容灾底座整体**优于常见同规模工具**：IPC 有严格帧协议、nonce、PID/路径/SHA256 双向身份校验；更新有 journal 事务、备份哈希校验、回滚、独立更新器与只读启动探针；未确认操作有"写前意图 + 人工核对 + 按备份恢复"闭环；紧急策略有 ECDSA 签名与序号防重放。**未发现 P0 级缺陷**。

主要短板集中在"自动化程度"而非"安全语义"：
1. 更新清单/安装包**没有内容签名**，信任根仅是 HTTPS + 域名白名单 + GitHub 账号（与紧急策略已有的签名管线形成明显不对称）；
2. 后端**没有 Job Object**，主进程被强杀后长任务后端可能残留，而残留会**阻止安装/更新**（部署脚本拒绝替换文件、不清理）；
3. 断电/半提交更新**没有启动自愈**，主程序启动时不检测未完成事务（只有人工 `--recover` 文档）；
4. 断言 1 的"合并单宿主"技术可行，但若按最简方式合并会引入**单进程串行化**（长任务最长 150 分钟阻塞全部模块）与更大故障域；更优解是"单 exe 多实例"（保留 6 进程隔离）；
5. 断言 4 的"先重命名 `.old_delete` 再写入"对**正在运行的 exe/dll 无效**，Windows 必须走 `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`；且 `CleanupService` 与更新清理无关（它是系统垃圾清理）。

### Top 5 架构/稳定性收益（按建议优先级）

| # | 收益 | 依据 | 风险/成本 |
|---|------|------|-----------|
| 1 | **更新事务断电自愈 + 启动检测**：半提交目录不再"静默混装"，启动即提示并引导恢复；对确认被占用文件使用重启后替换 | 现状无启动 active-session 检测（`FILE-UPDATES.md:58-64`）；事务只做到 journal+人工 `--recover`（`src/DeltaNFD.FileUpdater/Program.cs:34-46,69-74`） | 中风险（误判/回滚分支）；约 2–3 人日 |
| 2 | **后端进程治理**：Job Object（先只用于身份/退出确认）+ 启动孤儿检测（路径+PID+启动时间校验后协作 stop）；消除"孤儿阻塞更新" | 官方后端无 Job（全仓仅插件 `PluginJobHandle.cs:29-31`）；残留会使 `DeploymentControl.ps1:168-171` 直接拒绝替换文件 | 中风险（绝不能按名杀进程）；约 2–4 人日 |
| 3 | **单 exe 多实例宿主**：删除 6 套壳工程（物理 1854 行/非空 1640 行），保留 6 进程隔离；同步简化发布布局 | 6 个 `Program.cs` 仅 8–10 行且全部调用同一 `BackendHost.RunAsync`（`BackendHost.cs:98`）；6 个 csproj 同构 | 中风险（更新清单硬编码六后端 `FileManifest.cs:138-141`，必须整包过渡）；约 3–5 人日 |
| 4 | **更新清单签名**：复用紧急策略的 ECDSA 离线签名/固定公钥管线，补上供应链信任根 | 紧急策略已有完整实现（`EmergencyPolicy.cs:48-70`、`EmergencyPolicyTrust.cs:5-11`）；`update.json:1-28` 无任何签名字段 | 中风险（私钥运维/公钥轮换）；约 2–3 人日 |
| 5 | **服务端反馈闭环**：错误指纹聚合 + `suggested_action` + 作者审阅视图；附件正文控制字符过滤 | `feedback_app.py:56-103`（只校验 schema）、`:187`（响应无建议）；1000 份上限会被同类重复反馈占满（`:18-22`） | 低风险（字段可加不删）；约 2–3 人日 |

### 七条断言一句话结论

1. **部分真**——可合并，6 个壳确实只有 `Program.cs` + `*Commands.cs`，且共用 `BackendHost`；但"`merge-shared-backends.ps1` 可废弃"为**假**（它是发布布局共享依赖去重，单宿主后仍需在宿主+更新器之间去重）。
2. **部分真**——`ProcmanEngine` 确有 1 秒定时轮询；`GpuSpoofWatch` **没有定时器**（由启动与窗口激活触发）；`CpuFrameEngine` 另有 2 秒 tick。可改 WMI/ETW，但只能做"加速层"，必须保留低频对账。
3. **部分真**——无 Job Object 属真；但主进程死亡后管道 EOF 会让后端退出（有缓解），残留窗口在"不响应取消的长操作"，且残留会阻塞更新/卸载。
4. **部分真**——现事务"失败即回滚"语义完整；`AtomicCopy` 对被占用文件会失败；改 `.old_delete` 对运行中映像无效，需 `PendingFileRenameOperations`；`CleanupService` 并非启动清理器。
5. **真**——无 RunOnce/计划任务/断电自愈；已有事务 journal、`--recover` 文档、未确认操作人工核对、脱机状态原子存储等"人工恢复层"。
6. **真**——`feedback_app.py` 无错误指纹去重、无 `suggested_action`；但有 `requestId` 幂等去重（同 ID 不同字节 409）。
7. **真**——CI 无 `dotnet test`、无 JSON 校验；但有大量 `BackendSmokeTest` 自定义检查与两个 Python 单测；`test_nginx_feedback.py`、`tools/JsonVerify`、发布脚本均未接入 CI。

---

## ② 断言核实表

| 编号 | 断言 | 判定 | 核心证据 |
|------|------|------|----------|
| 1 | 6 个后端壳仅含 `Program.cs` + `*Commands.cs`，可合并单宿主，`merge-shared-backends.ps1` 可废弃 | **部分真**（合并可行；脚本可废弃=假） | `src/DeltaNFD.Backend.*/Program.cs` 各 8–10 行；六壳物理 1854 行/非空 1640 行；`installer/merge-shared-backends.ps1:11-37` |
| 2 | `ProcmanEngine`/`GpuSpoofWatch` 定时轮询，可改 WMI/ETW 事件驱动 | **部分真**（Procman 真；GpuSpoofWatch 假；另有 CpuFrame 2s） | `ProcmanEngine.cs:28,746-763`；`GpuSpoofWatch.cs:1-75`；`MainWindow.xaml.cs:428-459`；`CpuFrameEngine.cs:20-21,115-129` |
| 3 | 子进程未绑定 Job Object，主进程强杀会产生孤儿后端 | **部分真**（未绑定真；但管道断连会自退，残留限于长任务；后果是阻塞更新） | `PluginJobHandle.cs:29-31`（仅插件）；`BackendHost.cs:312-328,474-477`；`App.xaml.cs:38-61`；`DeploymentControl.ps1:168-171` |
| 4 | `FileTransaction` 覆盖被占用 exe/dll 失败可改"先重命名 `.old_delete` 再写入"，`CleanupService` 启动清理 | **部分真**（事务语义确认；改造假设有误；CleanupService 无关） | `FileTransaction.cs:39-96,141-153`；`AceCleanupEnvironment.cs:176,203`（仓库唯一 PendingRename 用法）；`CleanupService.cs:11-36` |
| 5 | 无 RunOnce/断电自愈机制 | **真**（无自动机制；有完整人工恢复层） | `FILE-UPDATES.md:58-64`；`FileUpdater/Program.cs:34-46,69-74`；`OfflineHelper/Program.cs:8-9` |
| 6 | `server/feedback/feedback_app.py` 无错误指纹去重、无 `suggested_action` | **真**（有 requestId 幂等去重） | `feedback_app.py:56-103,124-151,187` |
| 7 | CI `build.yml` 无 `dotnet test` 与 JSON 校验 | **真**（有替代检查与缺口） | `.github/workflows/build.yml:1-250`；全仓 `dotnet test` 0 处；`DeltaNFD.sln` 无测试项目 |

### 断言 1：六个后端壳与单宿主合并

**实际内容（逐项目）**

| 项目 | `Program.cs` | `*Commands.cs` | 处理器数/白名单数 | 特殊语义 |
|------|-------------|----------------|-------------------|----------|
| Ace | 8 行（`Program.cs:1-8`） | 115 行（`AceCommands.cs:1-115`） | 6/6 | `ace.clean`、`ace.core.clean` 完成即退出（`BackendModules.cs:196`） |
| CpuFrame | 8 行 | 299 行 | 29/29 | `OnStopping` 恢复帧格（`CpuFrameCommands.cs:46`）；`StatusSource` 1s（`:47`） |
| Gpu | 8 行 | 209 行 | 13/13 | 无 `OnStopping`/`StatusSource`（`GpuCommands.cs:12-37`） |
| Procman | 8 行 | 294 行 | 23/23 | `OnStopping` 恢复进程控制（`ProcmanCommands.cs:44-45`）；`StatusSource` 1s（`:46`）；启动优先级 BelowNormal（`:43`） |
| Runtime | 10 行 | 169 行 | 16/16 | 额外入口 `--runtime-system-probe`（`Program.cs:7-9`，转 `RuntimeSystemProbeProcess.Run`） |
| SystemMaintenance | 8 行 | 718 行 | 50/50 | `OnStopping = DefenderGuard.RestoreSessionAsync`（`SystemCommands.cs:28`）；`BxCatalog.ConfigureAssetRoot`（`:19-23`） |

- 六个 `Program.cs` 均只有一行主体：`BackendHost.RunAsync(XxxCommands.Options, args)`（Ace:7、CpuFrame:7、Gpu:7、Procman:7、System:7、Runtime:7-9）。
- 六个 csproj 除 `AssemblyName`/`RootNamespace` 外**完全同构**（`OutputType=WinExe`、`net8.0-windows`、`win-x64`、`UseAppHost`、仅 ProjectReference `DeltaNFD.Execution`）；且 Gpu/Runtime/SystemMaintenance 的 csproj 注释仍复制了"Ace 清理后端"说明（3 个 csproj 第 4–9 行），属注释漂移。
- 启动参数只有三个：`--backend <id> --pipe <name> --host-pid <pid>`（`BackendHost.cs:142-146`、`BackendClient.cs:164`）；诊断参数 `--describe`、`--self-check`（`BackendHost.cs:131-140`）。
- 路由层面没有 HTTP 路由：全部是命令白名单。白名单命令 **137 条**（Ace 6、Gpu 13、Runtime 16、CpuFrame 29、Procman 23、System 50；`BackendModules.cs:205-376`），宿主启动时强制"处理器与白名单完全一致，否则拒绝启动"（`BackendHost.cs:119-129`）。
- 协议帧类型 **10 种**（`hello/ready/invoke/progress/result/cancel/stop/status/ping/pong`，`BackendProtocol.cs:13-42`）。

**`merge-shared-backends.ps1` 的真实用途**

- 它把发布布局中的 **7 个组件目录**（`Backends/ace`、`gpu`、`runtime`、`system`、`cpuframe`、`procman`、`updater`，`merge-shared-backends.ps1:11`）按"相对路径 + SHA256"合并去重：相同路径相同哈希只保留一份，哈希冲突直接失败（`:25-28`），最后复制到 `out/Backends`（`:31-36`）。
- 由发布脚本调用：`publish-selfcontained.ps1:56`；发布必需文件校验也要求 `Backends/DeltaNFD.Backend.*.exe` 与 `Backends/DeltaNFD.FileUpdater.exe` 同在 `Backends/` 根（`:60-78`）。
- 结论：该脚本与"是否合并进程"**无关**；即便改单宿主，仍需在"后端宿主 + 独立更新器"两个自包含发布之间去重（更新器必须独立才能替换主程序，见 `FILE-UPDATES.md:52`）。因此"可废弃"为**假**；合并后可从 7 组件简化为 2 组件输入。

**合并为单进程宿主的兼容性风险（证据）**

1. **串行化**：`BackendHost` 一次只允许一个在途请求（`BackendHost.cs:349-354`，返回 `backend.busy`）；客户端每模块也是串行（`BackendClient.cs:480`）。当前 6 个独立进程=最多 6 路并发；合并为 1 进程后，`runtime.high-fps.repair` 的超时高达 **150 分钟**（`BackendModules.cs:243-244`），期间全部模块命令都会被 `backend.busy` 拒绝。
2. **退出语义不同**：Ace 的"完成即退出"（`BackendModules.cs:196` + `BackendHost.cs:258-268,282-287`）与常驻模块混在同一个宿主需要额外状态机。
3. **收尾恢复不同**：`OnStopping` 是每模块定制的（`CpuFrameCommands.cs:46`、`ProcmanCommands.cs:44-45`、`SystemCommands.cs:28`；Ace/Gpu/Runtime 无），合并后停止一个"多模块宿主"必须编排多次恢复。
4. **身份校验面**：握手校验实际映像路径、PID、启动时间、自报 SHA256、模块版本（`BackendClient.cs:397-446`）；`BackendModuleSpec.ExecutableName`（`BackendModules.cs:169-177`）与 `BackendPaths.Resolve` 的候选路径（`BackendPaths.cs:13-28,30-60`）都按"每模块一个 exe"设计。
5. **更新清单硬约束**：`FileUpdateRules.Required` 硬编码 `DeltaNFD.exe` 等 13 个文件（`FileManifest.cs:47-49`），并**强制要求六个后端的 `.exe/.dll/.deps.json/.runtimeconfig.json`**（`:138-141`）。旧版客户端自带的更新器（`FileUpdater/Program.cs:28` 用 `requireProduct=true` 读取清单）会拒绝"缺六后端"的新清单 → **布局变更无法通过文件增量升级完成，必须先整包安装**。
6. **部署脚本硬编码**：`installer/DeploymentControl.ps1:46-47` 的进程名单硬编码 `DeltaNFD`、6 个后端 exe 与已废弃的 `DeltaNFD.OfflineHelper`。合并后旧脚本在新布局下枚举不到新宿主（若保留旧名单），会走到 `:168-171` 的"仍有执行者，拒绝替换文件"。
7. **死字段**：`BackendModuleSpec.Persistent`（`BackendModules.cs:176-177`）在全部源码中无读取点（grep 仅插件同名属性），合并时可清理。

**推荐替代**：不要合成"单进程"，而做"单 exe 多实例"——一个 `DeltaNFD.Backend.Host.exe`，按 `--backend <id>` 启动 6 个进程，各自注册自己的 `BackendHostOptions`。可删除 6 个 csproj/`Program.cs`、统一 deps/runtimeconfig，同时保留进程隔离与串行边界；仍需更新清单与部署脚本同步（见 ⑥ Step 4）。

### 断言 2：轮询 vs WMI/ETW

**ProcmanEngine（真）**

- `RulesTickInterval = 1s`（`ProcmanEngine.cs:28`），循环为"一次规则 tick + 后台抑制 tick + 自动内存 tick，再 `Task.Delay(1s)`"（`:746-763`）。
- 但空闲成本低：无启用规则时 `RulesTickCoreAsync` 立即返回，不做进程快照（`:778-781`）；后台抑制默认关闭时 `ResponsivenessTickAsync` 立即返回（`ProcmanEngine.cs:1097-1100`）；自动内存每秒读一次内存压力，未达阈值直接返回（`ProcmanBackground.cs:159-181`）。
- 有启用规则时每 tick 执行 `Process.GetProcesses()` 全枚举 + 逐进程身份识别（`ProcmanEngine.cs:783-823`）。

**CpuFrameEngine（真，任务未列但同域）**

- `TickInterval = 2s`（`CpuFrameEngine.cs:20`），主循环 `:115-129`；每 tick 会 `RefreshIdentities` → `GameTargetService.Default.GetProcesses(forceRefresh: true)`（`:272-275`），后者缓存窗口也是 2 秒（`GameTargetService.cs:150-173`）；非自定义目标时按进程名枚举（`:173`），自定义目标时 `Process.GetProcesses()` 全枚举。
- 电源守护 `PowerGuardInterval = 30s`，由 `_powerGuardCount >= 15` 触发（`:21,211-213`）。

**后端状态推送（1s）**

- `BackendHostOptions.StatusInterval` 默认 1s（`BackendHost.cs:75`），仅空闲时推送（`:291-310`）；目前只有 CpuFrame 与 Procman 配了 `StatusSource`（`CpuFrameCommands.cs:47`、`ProcmanCommands.cs:46`），前端据此每秒收状态帧。

**前端进程页（1s）**

- `PollInterval = 1s`（`ProcessPage.xaml.cs:20`），`DispatcherTimer` 每秒刷新进程列表（`:25,156`）——即进程页可见时每次 tick 一次 `procman.process.list` IPC。

**GpuSpoofWatch（假：无定时器）**

- 整个文件 75 行只有注册表读取与判定（`GpuSpoofWatch.cs:15-74`），无 `Timer`/`Thread.Sleep`/循环。
- 触发点是：启动更新流程结束后调用一次（`MainWindow.xaml.cs:428-429`），以及窗口激活事件（`:433-437`，`_gpuWatchReady` 后去抖 `_gpuWatchInFlight`，`:439-460`）。

**改 WMI/ETW 评估**

- 可行性：`ManagementEventWatcher`（`Win32_ProcessStartTrace`/`__InstanceCreationEvent`）与 ETW（Kernel-Process）都需要管理员；产品整体已 `requireAdministrator`（`src/DeltaNFD/app.manifest:6-12`），权限不是障碍。全仓当前**没有任何** WMI 事件/ETW 实现（`ManagementEventWatcher`/`Win32_ProcessStartTrace` grep 0 处）。
- 可靠性风险：事件可能丢失/延迟（高负载、WMI 服务异常）；该仓库自己就在部署脚本里写了"Native image queries work even when the WMI service is broken"（`DeploymentControl.ps1:9`），说明 WMI 不可靠是项目已知前提。纯事件驱动一旦丢事件，规则不生效且**静默**。
- 建议：采用"事件加速 + 低频对账"混合（例如 Procman 规则：WMI 事件即时应用 + 10–30s 全量对账；CpuFrame：保留 2s tick 但把进程枚举/状态维护分级）。收益（CPU）需先测量，未实测前不建议直接替换。

### 断言 3：Job Object 与孤儿后端

- **官方后端无 Job Object**：全仓 Job Object 只出现在插件路径（`PluginJobHandle.cs:11-39`；使用点 `PluginRuntimeService.cs:242,282,334`），且明确 `LimitFlags = 0`、**不设** `KILL_ON_JOB_CLOSE`（`PluginJobHandle.cs:29-31`），注释说明"宿主崩溃时插件进程自主存活"（`PluginRuntimeService.cs:334`）。
- **未发现 v1/v2 Job 逻辑**。存在的是"后端布局两代"：旧布局 `Backends/<module>/<exe>` 与新共享布局 `Backends/<exe>`，`BackendPaths.CandidatePaths` 同时兼容（`BackendPaths.cs:39-40`）；`SystemCommands.cs:21` 注释仍按旧子目录假设写；协议只有 `ProtocolVersion = "1.0"`（`BackendProtocol.cs:54-57`）。
- **有缓解机制**：主进程退出时 `ProcessExit` 排空后端（`App.xaml.cs:38-61`）；后端在管道断开（EOF/IOException）时取消在途请求并退出（`BackendHost.cs:312-328`）；客户端清理路径对无未确认操作的会话执行 `_process.Kill(entireProcessTree: true)`（`BackendClient.cs:789-795`）。
- **残留窗口**：后端正在执行"不响应取消"的系统操作时，`finally` 会 `await inFlight` 后才退出（`BackendHost.cs:474-477`）；主进程被强杀不触发 `ProcessExit` 排空；`App.xaml.cs:56-60` 正常退出时也只给排空 **5 秒**宽限。
- **真实后果（比资源泄漏更严重）**：`DeploymentControl.ps1` 在交接后会重新枚举产品进程，只要仍有本安装目录下的执行者就抛错并**拒绝替换文件**（`:168-171`）；旧版主程序只做 `CloseMainWindow`（`:147-152`）。即残留后端会让"更新/卸载"直接失败，需人工结束进程。

### 断言 4：FileTransaction 与 `.old_delete`

**现有事务语义（确认）**

- `Apply` 前先 `ValidatePayload`（`FileTransaction.cs:39-45`）；对每个变化文件：存在则备份到事务目录并校验备份哈希、`Flush(true)`（`:53-68`）；随后 `journal.State=Applying` 逐文件写入（`:70-84`）；全部替换后核对整目录清单（`:85-86`）、执行启动验证（`:87`）、提交（`:88`）。
- 失败路径：捕获异常后 `Rollback`（`:90-95`）；回滚校验备份、拒绝覆盖"被其他程序修改过"的目标文件（`:120-137`），失败时记录 `RollbackFailed` 并保留现场。
- 单文件替换 = `AtomicCopy`：写 `target + ".file-update-<guid>"` 临时文件（`FileOptions.WriteThrough` + `Flush(true)`）→ `File.Move(temporary, target, overwrite: true)`（`:141-153`）。
- 更新器还有会话/安装目录/清单哈希校验（`FileUpdater/Program.cs:19-30`）、部署互斥（`:31-33`）、上一次未完成事务拒绝覆盖（`:34-46`）、Ready→批准→交接（`:48-66`）、新版只读启动探针（`:78-97`）。

**对"先重命名 `.old_delete` 再写入"的判断**

- 对**正在运行的 exe/dll**，Windows 加载器保持的映像/模块句柄通常不允许 DELETE/rename，因此"先改名再写入"仍会失败；改名对"被第三方以 FILE_SHARE_READ 打开但不允许删除"的文件也无帮助。
- 真正可行的是 **`MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)` / `PendingFileRenameOperations`**：仓库只有 ACE 清理在使用（`AceCleanupEnvironment.cs:176,203`；文档 `README-Backend.md:75`），更新链路未用。
- 现状其实已经"先让用户/后端退出再替换"：更新器调用 `DeploymentControl.ps1` 安全交接（`FileUpdater/Program.cs:57-66`），所以占用主要来自杀软、注入、其他进程；对这些场景"延迟替换"才有价值。
- 改造风险：引入"部分文件已改名/部分未完成"的中间态，需要扩展 journal 条目状态并在 Rollback 与启动检测中处理；断电窗口变大。建议只在"确认无法直接替换且已尝试正常回滚"后才登记延迟替换。
- **`CleanupService` 与更新无关**：它是系统垃圾清理（Temp/Prefetch/各类缓存，`CleanupService.cs:11-36`，`ScanAsync`/`CleanAsync` 在 `:38-63`），既没有 `.old_delete` 也没有更新事务清理逻辑。断言中"CleanupService 启动清理"当前不成立；应新增独立的"更新会话自愈检测"（主程序启动早期，或 `FileUpdater --recover` 提示）。

### 断言 5：RunOnce / 断电自愈

- **无 RunOnce/PendingRename/计划任务自愈**：全仓 `RunOnce` 仅出现在一个 UI 循环函数名（`BackendReviewFlow.cs:15-17`）；`PendingFileRenameOperations` 仅被运行库修复预检**读取**（`RuntimeRepairPreflight.cs:19`、`RuntimeStrongRepair.cs:150`），不写。
- **文档明确承认**："断电/进程被外部终止不会自动注册开机任务；下次增量拒绝覆盖尚未完成的事务。维护人员可以在管理员终端使用保留的旧 worker 执行恢复"（`FILE-UPDATES.md:58-64`）。
- **已有的人工恢复层（现状要写清楚）**：
  - 更新：事务 journal + 备份 + `FileUpdater --recover`（`Program.cs:69-74`）、`active-<hash>.json` 会话登记（`FileUpdateFallback.cs:26-41`）、整包回退一次性 nonce（`FileUpdateFallback.cs:43-67`）、`FileWorkerStatus` 防句柄占用（`FileWorkerStatus.cs:5-24`）。
  - 后端：写前执行意图 + 未确认记录 + 子进程枚举 + UI 核对 + 按备份恢复并复核归档（`BackendUnconfirmedStore.cs:16-58,118-...`、`BackendRecovery.cs:60-107`、`BackendAttention.cs:36-70`）。
  - 状态持久化样本：`OfflineModeStateStore` 使用 `FileOptions.WriteThrough` + `Flush(true)` + 原子 rename + 全局 mutex + 严格校验（`OfflineModeStateStore.cs:90-116,118-163,165-186`）。
  - GPU 伪装恢复状态有代次/路径匹配（`GpuSpoofRestoreState.cs:16-54`）。
  - 注册表原值备份集中在 `%APPDATA%\Delta NFD\backups.json`（`TweakBackupStore.cs:44,125-146`）。
- **OfflineHelper 现状**：已废弃，入口直接返回 2（`OfflineHelper/Program.cs:8-9`）；配套 `OfflinePowerService` 全部拒绝写入（`OfflineHelperPowerServices.cs:21-33`）。
- **结论**：断言为真，但"没有任何机制"应表述为"没有任何**自动**机制"；现状是"证据保全 + 人工核对/恢复"。

### 断言 6：feedback 去重与 suggested_action

- `validate_archive` 只做结构校验：4MiB 上限、1–4 个条目、文件名白名单（`report.json` + `logs/log-0[1-3].txt`）、展开上限 2MiB、拒绝加密/非常规压缩/符号链接、逐条大小与 UTF-8/NUL 校验、report 字段与 `logCount` 一致性（`feedback_app.py:56-103`）。
- 去重仅为 `requestId` 幂等：`receipt = "FB-" + requestId.hex`，同 receipt 同内容返回 200，不同内容 409（`feedback_app.py:124-151`）。
- **没有**错误指纹（异常类型/堆栈签名/日志内容哈希）聚合，也**没有** `suggested_action`：响应固定 `{"status":"accepted","receipt":...,"requestId":...}`（`:187`）；客户端上报的 `report.json` 结构也无该字段（`BugFeedbackService.cs:97-103`）。
- 附件/日志处理安全性：**好**——不解压执行、只读内存（`:60-84`）、临时文件 fsync 后 `os.link` 原子创建（`:139-151`）、存储根要求私有绝对目录、非 symlink（`:106-111`）、systemd 已做 `UMask=0077`/`StateDirectoryMode=0700`/`NoNewPrivileges`/`ProtectSystem=strict`（`deltanfd-feedback.service:17-27`）、prune 定时 30 天（`feedback_app.py:18-26,114-121`；`deltanfd-feedback-prune.timer:4-7`）。
- 缺失的防滥用维度：没有长期封禁/审计告警；匿名上传可被刷到 1000 份/512MiB 上限后 503（`:134-137`）——属既有设计权衡。

### 断言 7：CI 覆盖缺口

- `build.yml` 共 250 行，**没有 `dotnet test`**（全仓 grep 0 处）；`DeltaNFD.sln` 中没有 xUnit/NUnit 测试项目，唯一测试工程是 `tools/BackendSmokeTest`（自定义检查程序）。
- **没有 JSON 校验步骤**：CI 不校验根 `update.json`（`update.json:1-28`）、`files.json`、TweakDb 数据库；`tools/JsonVerify` 是现成的 TweakDb JSON 校验器（`tools/JsonVerify/Program.cs:1-44`），但全仓 grep `JsonVerify` **0 处引用**，CI 不调用。
- 替代检查确实很厚：`--update-response-checks`、`--update-source-fallback-checks`（`build.yml:52-56`）、`--update-checks`（`:233-234`）、`--file-update-checks` + Python 生成器单测（`:236-241`）、feedback 客户端 + Python 服务端单测（`:115-121`）、插件/运行库/帧格等 60+ 检查（`:64-249`）。
- **明确缺口**：
  1. `test_nginx_feedback.py`（Linux/Nginx 才跑，`test_nginx_feedback.py:17`）未接入 CI；CI 的 Python feedback 单测只跑 `test_feedback.py`（`build.yml:120`）。
  2. `tools/JsonVerify` 未接线；`update.json`/`files.json` 没有 schema/字段级 CI 校验（生成脚本 `make-update-manifest.ps1:138-152` 有自检，但 CI 不执行发布脚本）。
  3. `publish-selfcontained.ps1`、`merge-shared-backends.ps1`、`make-update-manifest.ps1`、`DeploymentControl.ps1` 的安装器侧检查（`tools/InstallerSafetyChecks.ps1` 等）未在 `build.yml` 调用；发布布局去重/合并路径无 CI 覆盖。
  4. 注意：CI 的主工程 `dotnet build` **会**通过 `CopyOfficialBackendsToBuildOutput` target 编译六个后端（`src/DeltaNFD/DeltaNFD.csproj:158-182`），所以"六后端未被编译"不成立；但 `PublishSelfContainedComponents=true` 的发布路径（`:194-201`）未被 CI 覆盖。
  5. 没有安装包内容签名/Authenticode 校验测试（见问题 A-01）。

---

## ③ 现状图（文字 + 箭头）

### 进程与 IPC

```
[DeltaNFD.exe 主程序]  (requireAdministrator, app.manifest:6-12)
   │  每模块一个命名管道（名字含随机 GUID；PipeOptions.CurrentUserOnly）
   │  启动参数: --backend <id> --pipe <name> --host-pid <pid>        (BackendClient.cs:157-168)
   │  握手: hello(nonce, 宿主数值版本, 数据根) → ready(模块版本/协议 minor/pid/startTicks/ImagePath/Sha256)
   │  校验: 客户端 PID==进程PID、映像路径、自报SHA256、启动时间、版本完全相等、协议minor≤宿主  (BackendClient.cs:397-446)
   │  帧: 4字节LE长度 + UTF-8 JSON；严格拒绝重复/未知字段、非法UTF-8；上限1MiB       (BackendProtocol.cs:47-143)
   ├──▶ DeltaNFD.Backend.CpuFrame.exe   常驻；2s tick；1s status 推送；OnStopping 恢复帧格
   ├──▶ DeltaNFD.Backend.Procman.exe    常驻；1s tick；1s status 推送；OnStopping 恢复进程控制
   ├──▶ DeltaNFD.Backend.SystemMaintenance.exe 常驻；OnStopping 恢复 Defender 临时状态
   ├──▶ DeltaNFD.Backend.Runtime.exe    常驻；长任务（150min 上限）；额外 --runtime-system-probe 入口
   ├──▶ DeltaNFD.Backend.Gpu.exe        常驻
   └──▶ DeltaNFD.Backend.Ace.exe        按需；ace.clean/ace.core.clean 完成即退出

命令执行: invoke → [串行在途1条] handler → result；支持 progress/cancel/stop/ping  (BackendHost.cs:270-471)
未知操作: 写前意图(盘) → 发送 → 若超时/断线/部分成功 → 未确认记录 + UI 通知 + 人工核对/恢复
后端崩溃: 读循环 EOF → 失败返回(不折算成功) → BackendCaller 下次调用自动重启(无未确认时)  (BackendCaller.cs:23-36)
```

### 更新事务

```
[检查] GitHub Releases API 或自建 https://deltaxiaoxi.xin/updates/update.json
   │   校验: schemaVersion==1、版本>当前、installer url https+allowedHosts+SHA256格式     (UpdateService.cs:1092-1154,1201-1266)
   ▼
[选择方式] 仅替换变更文件(默认) / 全量更新
   │
   ▼
[下载-增量] files.json(8MiB上限,哈希校验) → 变化文件 gzip 对象(逐对象大小/SHA256/解压后哈希)  (UpdateService.Files.cs:74-144)
   │   payload 落在 %ProgramData%\DeltaNFD-FileUpdates\<会话GUID>\，ACL 仅管理员+SYSTEM        (ProtectedSessions.cs:8-43)
   ▼
[确认] 主程序启动独立更新器: <session>\worker\DeltaNFD.FileUpdater.exe --apply <session> <manifestHash> <root>
   │   更新器: 会话ACL/安装登记目录/清单哈希/部署互斥/旧事务检查 → status=Ready                (FileUpdater/Program.cs:19-48)
   ▼
[主程序批准] 写 apply-approved.txt → 更新器启动 DeploymentControl.ps1(install) 请求安全退出
   │   DeploymentControl: 只协作退出/CloseMainWindow，不强杀；残留执行者 → 拒绝替换            (DeploymentControl.ps1:137-175)
   ▼
[替换] journal: Preparable→备份+校验+fsync → Applying(逐文件 Intent→替换→Done) → Verifying(整目录清单)
   │   启动验证: root\DeltaNFD.exe --verify-file-update <session> <nonce>（旧后端六文件版本复检）(FileUpdateHealth.cs:9-32)
   │   成功 → Committed → 更新卸载登记 DisplayVersion/安装ID → 重启主程序
   │   失败 → Rollback（备份回写，拒绝覆盖被外部修改的目标）→ 全量回退一次性续接 / 弹窗保留证据
   ▼
[容灾] 无 RunOnce；断电/被终止 = 事务目录保留，下次增量拒绝覆盖；人工 --recover 或整包安装   (FILE-UPDATES.md:58-64)
```

### 容灾与恢复

```
正常退出:  AppDomain.ProcessExit → BackendLifecycle.PrepareHandoverAsync（5s宽限）→ 各模块 stop/收尾恢复
异常退出:  无自动重启；未确认操作记录留在 %APPDATA%\...\official-backend-unconfirmed\，UI 提醒人工核对
后端崩溃:  BackendCaller 下次调用时重启（若上次结果未知则禁止自动启动第二份，要求核对）
更新中断:  active-<hash>.json 指向会话；journal 状态机可回滚；整包回退需要一次性 nonce + 用户原确认
系统修改:  TweakBackupStore(注册表原值集中备份) / GpuSpoofRestoreState / FramePowerLifecycle(电源代次)
脱机模式:  已移除；OfflineHelper 入口拒绝服务；OfflineModeStateStore 仅保留兼容/恢复数据
```

---

## ④ 实测数据（静态统计；未运行产品）

### 代码量（物理行，`Get-Content | .Count` 实测）

| 范围 | 行数 |
|------|------|
| 六个后端壳（`Program.cs`+`*Commands.cs`） | **1854 物理行 / 1640 非空行**（与任务口径一致） |
| 其中 `Program.cs` 合计 | 50 行（8×5 + 10×1） |
| `AceCommands` / `CpuFrameCommands` / `GpuCommands` / `ProcmanCommands` / `RuntimeCommands` / `SystemCommands` | 115 / 299 / 209 / 294 / 169 / 718 |
| IPC 宿主与契约：`BackendHost.cs` / `BackendModules.cs` / `BackendProtocol.cs` / `BackendFrameStream.cs` / `BackendArgs.cs` | 597 / 486 / 294 / 46 / 159 |
| 前端 IPC：`BackendClient.cs` / `BackendCaller.cs` / `BackendLifecycle.cs` / `BackendPaths.cs` / `BackendSessions.cs` / `BackendUnconfirmedStore.cs` | 820 / 138 / 172 / 81 / 66 / 507 |
| `UpdateCore` 五文件 | 452（154+153+78+24+43） |
| `FileUpdater/Program.cs` | 170 |
| 轮询实现：`CpuFrameEngine.cs` / `ProcmanEngine.cs` / `GpuSpoofWatch.cs` / `FramePowerLifecycle.cs` | 960 / 1241 / 75 / 59 |
| 服务端：`feedback_app.py` / `test_feedback.py` / `test_nginx_feedback.py` | 210 / 197 / 122 |
| CI `build.yml` | 250 |

### 协议与命令

| 指标 | 值 | 证据 |
|------|----|------|
| 帧类型 | 10 种 | `BackendProtocol.cs:13-42` |
| 白名单命令总数 | **137** | `BackendModules.cs:205-376` |
| 各模块命令数 | ace 6 / gpu 13 / runtime 16 / cpuframe 29 / procman 23 / system 50 | 同上 |
| 命令超时 | 2–15 分钟（多数）；`runtime.high-fps.repair` **150 分钟**、`runtime.vc14.repair` 42 分钟、System 多项 15 分钟 | `BackendModules.cs:208-374` |
| 后端宽限 | 命令超时 + 30s（后端自行取消） | `BackendHost.cs:65-66,391` |
| 握手/连接超时 | 连接 15s（客户端 `BackendClient.cs:195-196`，后端 `BackendHost.cs:181`）；hello 10s（`:200-212`，客户端 `:407-408`） |
| 帧/JSON 上限 | 1 MiB | `BackendProtocol.cs:57-58` |
| 状态推送间隔 | 1s（仅 CpuFrame/Procman 空闲时） | `BackendHost.cs:75,291-310` |

### 轮询周期

| 位置 | 周期 | 证据 |
|------|------|------|
| Procman 规则/响应/内存 tick | 1s | `ProcmanEngine.cs:28,746-763` |
| CpuFrame 监控 tick | 2s | `CpuFrameEngine.cs:20,115-129` |
| CpuFrame 电源守护 | 30s（15 tick） | `CpuFrameEngine.cs:21,211-213` |
| 后端 status 帧 | 1s（空闲） | `BackendHost.cs:75` |
| 前端进程页列表刷新 | 1s（页面可见且开关开启） | `ProcessPage.xaml.cs:20,25,156` |
| GpuSpoofWatch | 无定时器；启动后+窗口激活 | `MainWindow.xaml.cs:428-459` |
| 紧急策略拉取 | 15 分钟（每 1 分钟刷新 UI 状态、不联网） | `EmergencyPolicyUpdateService.cs:38-55` |

### 更新材料（仓库根 `update.json`）

| 项 | 值 |
|----|----|
| 当前版本 | `0.9210` / `Beta0.921` / beta 通道（`update.json:2-5`） |
| 发布包 | 2 个 URL（GitHub + 自建），`sizeBytes=248944034`，同一 SHA256 `4a97c82b…`（`:11-22`） |
| 文件清单 | `files.json`，`sizeBytes=814719`，`sha256=c00a7c7e…`（`:23-27`） |
| 签名 | **无任何签名字段**（整文件 28 行） |

### 服务端限制（静态配置实测）

| 层 | 限制 | 证据 |
|----|------|------|
| 应用层 | 上传 10 次/分（burst 5）、health 30/分（burst 3）、上传并发 2 | `feedback_app.py:18-26,29-48` |
| 请求体 | 4MiB；展开 2MiB；report 64KiB；每日志 600000B；条目 1–4 | `feedback_app.py:18-19,57-79` |
| 存储 | 1000 份 / 512MiB / 保留 30 天 / 预留 64MiB 磁盘 | `feedback_app.py:20-22,26,134-138` |
| Nginx | 单 IP 2r/m、全局 10r/m（常量键）、并发 1/IP、4/全局、body 上限 4m | `nginx-feedback-http.conf:2-12`、`nginx-feedback-server.inc:2-30` |
| body 专用 tmpfs | 32MiB / 4096 inodes / noexec，缺 `.ready` 直接 503 | `var-lib-nginx-deltanfd_feedback_body.mount:8-9`、`nginx-feedback-server.inc:10-15` |
| 进程约束 | 单 worker/2 线程、CPU 50%、内存 192M、NonRoot、NoNewPrivileges | `deltanfd-feedback.service:9-27` |

### CI

- 250 行、无 `dotnet test`、无 JSON 校验；约 60 个 `BackendSmokeTest` 检查 + 2 个 Python 单测；`test_nginx_feedback.py` 未接入（`build.yml:120` 只跑 `test_feedback.py`）。

---

## ⑤ 问题清单（P0 / P1 / P2）

> 评级口径：P0=当前会导致数据损坏/远程代码执行且无补偿控制；P1=高影响或高概率，应在下个版本处理；P2=工程质量/体验/维护性改进。
> 本次**未发现 P0**。成本为主观估算（人日），仅供参考。

### P1

**A-01 更新清单与安装包没有内容签名（供应链信任根缺失）**
- 证据：`update.json:1-28` 无签名；客户端只校验 `schemaVersion/version/installer.https+allowedHosts/sha256格式/size`（`UpdateService.cs:1092-1154,1201-1266`）；安装包哈希由同一份未签名清单提供（`update.json:13-20`、`UpdateService.cs:814-816,1534-1567`）；发布脚本只算哈希不自签（`make-update-manifest.ps1:67-69,110-121`）；而紧急策略已有 ECDSA 签名/固定公钥/序号防重放（`EmergencyPolicy.cs:48-70`、`EmergencyPolicyTrust.cs:5-11`、`EmergencyPolicyStore.cs:39-69`）。
- 影响：自建服务器被入侵、GitHub 账号/token 泄露、CDN/CA 级攻击即可向全部用户下发管理员权限的安装包；这是产品最高的单点信任风险。
- 修复收益：把信任根从"服务器账号"降为"离线私钥"；与紧急策略共用同一套验签代码/发布流程，维护成本低。
- 风险：私钥丢失将无法发版，需要备份/双密钥与公钥轮换预案（参考 `emergency/README.md:28-39` 的密钥约定）；旧版客户端不认签名，需过渡期"先告警、后强制"。
- 成本：2–3 人日（复用紧急策略签名工具 `tools/EmergencyPolicyPublisher` 需确认是否可泛化）。

**A-02 官方后端无 Job Object；残留会阻塞更新/卸载**
- 证据：Job 仅插件使用且不 kill-on-close（`PluginJobHandle.cs:29-31`、`PluginRuntimeService.cs:334`）；后端依靠管道 EOF 退出（`BackendHost.cs:312-328`），但长操作时不响应取消会等到完成（`:474-477`），正常退出排空只等 5 秒（`App.xaml.cs:56-60`）；部署脚本发现残留执行者直接拒绝替换文件（`DeploymentControl.ps1:168-171`）。
- 影响：更新/卸载失败（有回滚，不损坏数据），用户困惑；残留管理员进程继续持有系统修改。
- 修复收益：更新成功率提升；进程身份与退出可确认；无需按名杀进程。
- 风险：Job Object 若设 `KILL_ON_JOB_CLOSE` 会让"主进程正常退出时后端被强杀"，破坏收尾恢复；建议先只用 Job 做"成员身份/退出确认"（LimitFlags=0），或仅对"无需恢复语义"的模块启用 kill-on-close。孤儿检测必须校验映像路径+PID+启动时间，且先协作 stop，不可按名杀。
- 成本：2–4 人日（含隔离测试）。

**A-03 断电/半提交更新无启动自愈检测**
- 证据：`FILE-UPDATES.md:58-64` 明确无开机任务；主程序启动流程只处理"整包 PendingUpdateVersion"（`MainWindow.xaml.cs:415-431,475-521`），不扫描 `%ProgramData%\DeltaNFD-FileUpdates\active-*.json` 或 journal；`FileUpdater` 的 finally 仅在自身进程正常走完时重启主程序（`Program.cs:124-159`）。
- 影响：替换中途断电 → 安装目录可能处于"部分新/部分旧"，主程序可能无法启动且没有提示，用户只能重装；旧事务也不会被自动清理。
- 修复收益：启动即识别并引导（回滚/完成/整包重装），把"人工救砖"变成产品内操作。
- 风险：只读扫描，不得自动改写未完成事务（沿用 `FileUpdateFallback.RequireSafeTransaction` 语义，`FileUpdateFallback.cs:33-41`）；误判会导致错误回滚，必须严格校验会话 ACL/根路径/journal 状态。
- 成本：1–2 人日。

**A-04 合并单胞主（若按最简方式）会串行化并扩大故障域**
- 证据：`BackendHost.cs:349-354` 单在途请求；长任务 150 分钟（`BackendModules.cs:243-244`）；`OnStopping` 每模块定制（`CpuFrameCommands.cs:46`、`ProcmanCommands.cs:44-45`、`SystemCommands.cs:28`）。
- 影响：合并后任何长任务/崩溃都会影响全部模块；"后端 busy"从按模块变成全局。
- 修复收益：若采用"单 exe 多实例"则只收敛工程结构、不动运行语义（见 ⑥ Step 4），可删除 6 套壳（1640 非空行）而不牺牲隔离。
- 风险：更新清单 `FileManifest.cs:47-49,138-141` 与旧 FileUpdater 强制六后端 → **必须整包过渡**；`DeploymentControl.ps1:46-47` 名单要同步。
- 成本：3–5 人日。

**A-05 部署控制脚本硬编码后端名单**
- 证据：`installer/DeploymentControl.ps1:46-47` 写死 `DeltaNFD.Backend.{Ace,Runtime,Gpu,CpuFrame,SystemMaintenance,Procman}` 与已废弃的 `DeltaNFD.OfflineHelper`。
- 影响：任何 exe 改名/合并/新增后端，更新/卸载交接会漏枚举；漏枚举时用管道协作退出兜底（`:75-108`），但旧构建可能不支持协议（`:147-152`）。
- 修复收益：布局演进不再依赖手工改脚本。
- 风险：不能退化成"按安装目录映像匹配一切进程"（会误判用户从该目录手动运行的其他程序）；建议从一份随包生成的后端清单读取，或按"映像路径 + 产品签名/哈希"匹配。
- 成本：1 人日。

### P2

**A-06 注册表备份缺少落盘保证与完整性校验**
- 证据：`TweakBackupStore.WriteAll` 只写 `.tmp` + `File.Move`（`TweakBackupStore.cs:204-220`），无 `FileOptions.WriteThrough`/`Flush(true)`（对比 `OfflineModeStateStore.cs:90-116` 的正确做法）；读取非 strict 时 JSON 损坏/IO 错误静默返回空（`:193-201`），恢复入口会误报"无备份"。
- 影响：断电/异常可能丢备份；用户"恢复默认"时原值丢失。
- 收益：备份与恢复闭环更可靠；风险：备份文件格式若加封装需迁移旧文件；成本 0.5–1 人日。

**A-07 日志默认明文机器名/用户名；反馈脱敏是黑名单正则**
- 证据：`Log.Startup` 写入 `Environment.MachineName`/`Environment.UserName`（`Log.cs:62-68`）；反馈上传前 `Redact` 仅做 `%USERPROFILE%` 替换、`[a-z]:\Users\<name>` 正则、"机器：/用户："行与 token 类字段（`BugFeedbackService.cs:119-126`）。
- 影响：本机日志本身含隐私（设计如此，日志留在用户电脑）；反馈上传存在漏脱敏可能（例如 UNC 路径、非 Users 目录下的用户名、机器名出现在非表头行）。
- 收益：降低隐私投诉风险；风险：脱敏过度降低排障价值；成本 1 人日（建议"白名单式"结构化日志头 + 上传前可预览已做）。

**A-08 服务端无错误指纹去重 / 无 suggested_action / 无作者审阅聚合**
- 证据：`feedback_app.py:56-103,187`；存储上限 1000 份（`:18-22,134-137`）。
- 影响：同类问题重复占配额；作者需人工逐份查看；玩家得不到下一步建议。
- 收益：配额利用率、响应速度、重复率统计；风险：客户端旧版本不带指纹字段，需可选兼容；成本 2–3 人日。

**A-09 反馈服务匿名上传可被刷满存储（无长期封禁/告警）**
- 证据：Nginx 单 IP 2r/m + 全局 10r/m（`nginx-feedback-http.conf:2-12`），应用层全局令牌桶（`feedback_app.py:29-48`），但没有封禁名单/告警；达到 1000 份即 503（`:134-137`）。
- 影响：反馈服务不可用（不影响更新站，二者分离，`nginx-feedback-server.inc:53-56` 也限制了子路径）。
- 收益：增加"每日新增/存储水位"日志告警即可；风险：误封正常用户（本设计本来就用全局常量键，增封禁需谨慎）；成本 1 人日。

**A-10 轮询开销未量化；前端进程页 1s 全量列表轮询**
- 证据：`ProcessPage.xaml.cs:20,156`；`CpuFrameEngine.cs:20,140,272-275`；`GameTargetService.cs:150-173`；`ProcmanEngine.cs:28,778-781`。
- 影响：常驻 CPU/IO 未知，无法证明"改 WMI/ETW 的收益>风险"；进程页可见时每秒一次跨进程列表。
- 收益：测量后可能把空闲 CPU 降低（具体幅度待实测）；风险：事件丢失/权限/兼容；成本：测量 1–2 人日 + 混合改造 3–5 人日。
- 建议顺序：**先测量后改造**；进程页刷新可先降到 3–5s 或页面不可见暂停。

**A-11 CI 缺口：无 `dotnet test`、无 JSON 校验、nginx 测试/发布脚本未接线**
- 证据：见断言 7；`tools/JsonVerify` 未被引用；`test_nginx_feedback.py:17` 需 Linux+Nginx。
- 收益：schema/清单/发布布局回归在合并前发现；成本 1–2 人日；风险：CI 时长增加（可控）。

**A-12 后端版本必须与宿主完全相等，滚动升级不可行**
- 证据：`BackendClient.cs:452-457`（模块三段版本必须等于宿主 `AppVersion.NumericText`）；协议次版本只允许后端不高于宿主（`:425-426`）。
- 影响：任何"部分文件更新"后如果版本不一致，后端拒绝连接；这是防降级特性，但同时意味着**不存在灰度/滚动升级**，更新事务必须"整套原子替换"。
- 收益：明确文档与更新器约束即可；风险/成本低（0.5 人日，更新 `FILE-UPDATES.md`）。

**A-13 文档漂移（低风险但影响运维）**
- 证据：`emergency/README.md:22` 的"允许停用命令"列表（9 条）小于代码 `CanBlock` 列表（15 条，`EmergencyPolicy.cs:41-46`，新增 vc14/highfps/file-use/shader-clean）；`emergency/README.md:71` 仍写"GitHub 加速开启时允许失败后使用固定代理"，而代码只允许 GitHub 主机（`EmergencyPolicyUpdateService.cs:127-130`）；`AGENTS.md` 记录加速已取消。
- 收益：避免运维按旧文档配错；成本 0.5 人日。

**A-14 `BackendModuleSpec.Persistent` 为死字段**
- 证据：`BackendModules.cs:169-177` 定义，源码无读取点（grep 仅插件同名属性 `PluginRuntimeService.cs:105` 等）。
- 收益：合并/重构时清理；成本 <0.5 人日。

---

## ⑥ 分阶段迁移方案（每步可独立验证 / 回滚）

> 原则：每一步都保持"证据保全 + 可回滚"；不改变既有安全语义（不强杀、不自动重放、不写 RunOnce 除非产品明确决策）。

### Step 0：基线测量（不改代码，1–2 天）
- 内容：用现有 `tools/BackendSmokeTest`（`--procman-core-checks`、`T6RuntimeMeasure` 仅读）+ 进程监控记录：6 后端空闲/游戏时 CPU、私有字节、句柄、前端进程页打开时 IPC 频率；记录文件数/安装体积。
- 验证：数据落盘为独立报告，作为后续"轮询改造收益"的基线。
- 回滚：无需（只读）。

### Step 1：CI 补强（独立 PR，1 天）
- 内容：把 `tools/JsonVerify` 接入 CI 校验 TweakDb；新增根 `update.json` / `files.json` 的 schema 校验（可用 PowerShell/Python 小脚本，或复用 `UpdateService.TryParseManifest` 于 BackendSmokeTest 的新检查）；新增 Linux job 运行 `test_nginx_feedback.py`；对 `publish-selfcontained.ps1`/`merge-shared-backends.ps1` 做 dry-run 检查。
- 验证：故意破坏一个 JSON 字段/一项 Nginx 规则，确认 CI 变红。
- 回滚：撤销 workflow 变更。

### Step 2：更新事务韧性（2–3 天）
- 2a 启动检测：主程序启动早期（`--verify-file-update` 之外）只读扫描 `%ProgramData%\DeltaNFD-FileUpdates\active-*.json` 与 journal：状态 `Committed` → 提示更新完成；`Applying/Verifying/Intent` → 弹"更新未完成"向导（重试完成/`--recover`/整包重装）。不改写事务文件。
- 2b 延迟替换：`AtomicCopy` 失败且已回滚后，对"确认被占用"的文件登记 `PendingFileRenameOperations`（复用 `AceCleanupEnvironment` 的 MoveFileEx 声明，`AceCleanupEnvironment.cs:176,203`），journal 增加 `Deferred` 状态；启动时清理 `.old_delete`/延迟删除。
- 验证：隔离目录模拟"文件被占用"（用测试进程持句柄）+ 模拟断电（在 journal Applying 后强杀更新器）→ 检查启动向导与回滚结果；`--file-update-checks` 回归。
- 回滚：关闭启动检测入口（feature flag/删除入口），事务格式兼容旧读取；延迟替换仅在显式开关下启用。

### Step 3：后端进程治理（3–4 天）
- 3a Job Object（LimitFlags=0 或仅 `JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION` 语义评估后）：记录成员关系用于退出确认与子进程枚举（替代/增强 `BackendUnconfirmedStore.CaptureDescendants`）。
- 3b 启动孤儿检测：主程序启动时枚举 `Backends` 下后端 exe 进程（使用映像路径 + PID + 启动时间 + 模块 id 校验，绝不含"按名杀"），先发 stop 管道（若残留进程持有旧管道无法连接则记录证据），不自动强杀；提供"清理残留后端"的手动入口。
- 验证：`--plugin-continuous-checks` 相似风格新增 `--backend-orphan-checks`：模拟主进程被杀（测试父进程）→ 后端退出/残留判定 → 手动清理路径；`--backend-attention-checks` 回归。
- 回滚：Job 先只做记录（不影响退出），孤儿检测可关闭；清理入口需显式确认。

### Step 4：单 exe 多实例宿主（3–5 天，需整包过渡）
- 4a 新增 `DeltaNFD.Backend.Host.csproj`（一个 `Program.cs` + 模块注册表：6 个 `*Commands` 保留为模块类），`--backend <id>` 选择 `BackendHostOptions`；产物 `DeltaNFD.Backend.Host.exe`。
- 4b 过渡期双轨：发布布局同时保留 6 个旧 exe 与新 host exe；`BackendPaths` 优先 host（新文件清单同时包含两者）；`DeploymentControl.ps1` 名单加 host；`FileManifest.Required` 在**新版**客户端放宽为"host 或 6 旧 exe 至少一套"，保证新清单可被新更新器接受；**旧客户端升级到该版本必须走整包**（旧更新器 Required 六后端，`FileManifest.cs:138-141`）。
- 4c 观察一版后再删除旧 6 个 csproj 与旧 exe（文件清单 Remove 项用 `PreviousSha256` 精确匹配，`FileTransaction.cs:47-48`）。
- 验证：`--describe`/`--self-check` 对 6 模块逐一验证；`--dotnet-layout-checks`、发布布局 required 列表、六后端只读通信检查；新旧布局在隔离目录各跑一次真实端到端（不装本机）。
- 回滚：保留旧 exe 与旧清单（`BackendPaths` 兼容两条路径 `BackendPaths.cs:39-40`），切换 `BackendPaths` 候选顺序即可回退。

### Step 5：轮询混合化（先测量后改造，3–5 天）
- 5a Procman 规则：WMI 进程创建事件（`ManagementEventWatcher`，仅管理员）即时应用 + 10–30s 全量对账兜底；事件失败必须落日志并自动退回轮询。
- 5b 前端进程页：1s → 3–5s，页面不可见时暂停（`_timer.Stop` 已有路径 `ProcessPage.xaml.cs:174`）。
- 5c CpuFrame：保留 2s tick（状态语义依赖），先把"进程枚举"与"状态推送"解耦/降频。
- 验证：Step 0 基线对比（CPU/句柄）；隔离测试模拟事件丢失 → 对账补齐。
- 回滚：feature flag 恢复纯轮询。

### Step 6：服务端反馈闭环（2–3 天，独立部署）
- 内容：客户端 `report.json` 增加可选 `fingerprint`（日志内容 SHA256 或异常签名）与 `suggestedAction`（由客户端规则表产生）；服务端按 fingerprint 聚合计数（新增索引文件/JSONL），响应加 `suggestedAction` 与 `duplicateOf`（不破坏旧字段）；存储水位告警。
- 验证：`test_feedback.py` 增加指纹聚合/建议字段用例；旧客户端（无字段）仍通过。
- 回滚：新字段均可选，删除响应字段即可；聚合索引可重建。

### Step 7：更新清单签名（2–3 天，需过渡期）
- 内容：`update.json` 增加 `signature`（ECDSA P-256，复用 `EmergencyPolicyVerifier` 的密钥/格式经验）；客户端验签逻辑放在 `TryParseManifest`（`UpdateService.cs:1201`）；`files.json` 哈希继续由 `update.json` 绑定；发布脚本增加 `sign` 步骤。
- 验证：`--update-checks` 增加签名用例（有效/篡改/未知 key/过期）；真实发布先用"只告警"模式观察。
- 回滚：保留"无签名仍按 HTTPS+白名单接受"的兼容开关，一个版本后再强制。

---

## ⑦ 未验证事项与审计限制

1. **未运行任何程序**（遵守"只读审计/不运行修复更新安装器服务端"）：所有 CPU/内存/句柄/耗时数据均未实测；"轮询改事件驱动收益"、"合并单宿主 busy 影响"、"Job Object 行为"均为代码分析结论，需实测确认。
2. **未做真实断电/强杀实验**：断言 3/断言 5 的残留窗口与恢复路径基于代码与文档推断（`BackendHost.cs:474-477`、`FILE-UPDATES.md:58-64`），未在 Windows 上复现。
3. **未验证合并单宿主原型**：Step 4 是建议方案，未写任何代码/未做原型。
4. **服务端未部署验证**：本机无 Linux/Nginx，`test_nginx_feedback.py` 未运行（其自身要求 `os.name == "posix"` 且安装了 Nginx，`test_nginx_feedback.py:17`）；线上 `deltaxiaoxi.xin` 由 OpenClaw 部署，实际限流/存储/Prune 状态未知（`server/feedback/README-OpenClaw部署.md` 为部署说明，未核实线上）。
5. **仓库无 `_buildcheck` 发布布局**（本机已清理），无法实测安装包体积、后端文件数、共享 DLL 去重收益；相关结论来自 `publish-selfcontained.ps1:60-78` 与 `update.json:14` 的静态信息。
6. **未审计第三方依赖供应链**（NuGet 包、WindowsAppSDK、运行时二进制），仅审计了本仓库代码与脚本。
7. **未核实 git 历史/分支差异**，也未审计 Alpha/Beta 发布流程的实际执行记录；发布合规性以仓库内文件为准。
8. **安装器（Inno Setup）细节不在本报告范围**（由审计 A 负责）；本报告只引用与更新/交接相关的 `DeploymentControl.ps1` 行为。
9. **未运行 `dotnet build`/`dotnet test`**，因此"编译通过/检查通过"类结论不可由本报告给出；本报告只描述代码与 CI 配置的静态事实。

---

## 附：关键证据索引（便于复核）

| 主题 | 文件:行 |
|------|---------|
| 统一宿主与命令白名单 | `src/DeltaNFD.Execution/BackendHost.cs:89-173,243-471`；`src/DeltaNFD.Contracts/BackendModules.cs:205-376` |
| IPC 协议与握手校验 | `src/DeltaNFD.Contracts/BackendProtocol.cs:47-143`；`src/DeltaNFD/Services/Backends/BackendClient.cs:148-231,397-446` |
| 后端超时/取消/断线语义 | `BackendClient.cs:469-570,580-673`；`BackendHost.cs:270-471` |
| 未确认操作与恢复闭环 | `BackendUnconfirmedStore.cs:16-58,118-...`；`BackendRecovery.cs:60-107`；`BackendAttention.cs:36-70` |
| 单宿主合并风险 | `BackendHost.cs:349-354`；`CpuFrameCommands.cs:46`；`ProcmanCommands.cs:43-45`；`SystemCommands.cs:28`；`FileManifest.cs:47-49,138-141`；`DeploymentControl.ps1:46-47,168-171` |
| 轮询实现 | `CpuFrameEngine.cs:20-21,115-129,211-213,272-275`；`ProcmanEngine.cs:28,746-763,778-781,1097-1100`；`ProcmanBackground.cs:159-181`；`ProcessPage.xaml.cs:20,25,156` |
| GpuSpoofWatch 触发 | `GpuSpoofWatch.cs:15-74`；`MainWindow.xaml.cs:428-459` |
| 更新事务 | `src/DeltaNFD.UpdateCore/FileTransaction.cs:39-153`；`FileManifest.cs:41-153`；`FileUpdateFallback.cs:26-67`；`FileUpdater/Program.cs:11-170` |
| 更新启动/交接 | `src/DeltaNFD/Services/UpdateService.Files.cs:63-72,173-261`；`src/DeltaNFD/Services/UpdateService.cs:1092-1266,1534-1567`；`installer/DeploymentControl.ps1:75-175` |
| 脱离自愈现状 | `FILE-UPDATES.md:50-64`；`App.xaml.cs:38-75`；`OfflineModeStateStore.cs:90-186`；`OfflineHelper/Program.cs:8-9` |
| 注册表备份 | `TweakBackupStore.cs:44,125-146,193-220` |
| 日志/隐私 | `Log.cs:62-68`；`BugFeedbackService.cs:97-126` |
| 服务端 | `server/feedback/feedback_app.py:18-103,124-202`；`nginx-feedback-http.conf:1-12`；`nginx-feedback-server.inc:2-56`；`deltanfd-feedback.service:1-30`；`test_feedback.py:56-171`；`test_nginx_feedback.py:17-111` |
| 紧急策略 | `EmergencyPolicy.cs:29-122`；`EmergencyPolicyStore.cs:39-114`；`EmergencyPolicyUpdateService.cs:20-149`；`EmergencyPolicyTrust.cs:5-11` |
| CI | `.github/workflows/build.yml:1-250`；`tools/JsonVerify/Program.cs:1-44`；`DeltaNFD.sln` 项目项 |
