# DeltaNFD 核心代码只读审计报告（审计员 B）

- 审计对象：`src/DeltaNFD.Execution`（104 .cs）、`src/DeltaNFD.Shared`（24 .cs / 2786 行）、`src/DeltaNFD.Contracts`（30 .cs / 3315 行），并交叉核对前端 `src/DeltaNFD` 与 `tools/BackendSmokeTest`。
- 审计方式：只读静态分析（PowerShell 批量扫描 + 人工阅读关键文件）。
- 审计时间：2026-10-18 02:59–03:20（Asia/Shanghai）
- 硬限制（本会话）：禁止修改源码、禁止 `dotnet build`、禁止联网。**所有结论均为静态阅读结论，未经编译/运行验证**（见 §6）。
- 唯一可写产物：本文件。

---

## 0. 计数口径先说清（本报告最重要的一条前置事实）

**任务给定的"25390 行"精确等于"非空行数（`$_ -ne ''`）"，不是 `Get-Content` 物理行数，也不是 trim 后非空行数。** 实测：

| 口径 | Execution 合计 | 是否等于给定 25390 |
|---|---|---|
| `(Get-Content f).Count`（物理行，LF 换行下 = LF 数 + 1） | 27860 | 否 |
| **非空行 `$_ -ne ''`** | **25390** | **✅ 精确命中** |
| trim 后非空 `$_.Trim() -ne ''` | 25364 | 否（差 26，TweakDb/BxService.cs 4 行等缩进空白行） |
| 非空且非注释起始行（code） | 24066 | 否 |

逐目录分解（非空行口径，合计 = 25390）：

| 目录 | 文件数 | 非空行 |
|---|---|---|
| `Execution/`（根） | 46 | 10839 |
| `Execution/AlphaRestore/` | 22 | 3576 |
| `Execution/CpuFrame/` | 8 | 3602 |
| `Execution/Procman/` | 20 | 3327 |
| `Execution/TweakDb/` | 1 | 2286 |
| `Native/` | 5 | 1193 |
| 项目根（`BackendEnvironment.cs`、`BackendHost.cs`） | 2 | 567 |
| **合计** | **104** | **25390 ✅** |

→ 任务给的 "AlphaRestore(22)、CpuFrame(8)、Procman(20)、TweakDb(1)、根目录 46" **全部精确成立**；容易忽略的是另有 `Native/`（5）与项目根（2），共 104。

**本报告后续行数一律用"非空行"口径**，与给定数据可直接对比。

已核验的给定数据（全部命中，无一例外）：

| 文件 | 给定 | 实测非空行 | 结论 |
|---|---|---|---|
| TweakDb/BxService.cs | 2286 | **2286** | 真 |
| Services/UpdateService.cs | 1487 | **1487** | 真 |
| Services/FrameService.cs | 1381 | **1381** | 真 |
| SystemTweakService.cs | 1264 | **1264** | 真（注意 trim 口径是 1256，差 8） |
| Procman/ProcmanEngine.cs | 1138 | **1138** | 真 |
| MainWindow.xaml.cs | 1093 | **1093** | 真 |
| PluginRuntimeService.cs | 1092 | **1092** | 真 |
| CpuFrame/FrameTweaksService.cs | 980 | **980** | 真 |
| LabViewModel.cs | 969 | **969** | 真 |
| BxDbViewModel.cs | 958 | **958** | 真 |
| CpuFrame/CpuTopologyService.cs | 914 | **914** | 真 |
| CpuFrameEngine.cs | 882 | **882** | 真 |
| ProcessPage.xaml.cs | 861 | **861** | 真 |
| BackendClient.cs | 757 | **757** | 真 |
| Native/NvApiDrs.cs | 743 | **743** | 真 |
| Execution/RuntimeGuardService.cs | 145 | **145** | 真 |
| AlphaRestore/RuntimeGuardService.cs | 379 | **379** | 真 |
| Procman/ProcmanCpuTopologyReader.cs | 270 | **270** | 真 |
| Procman/ProcmanTopology.cs | 53 | **53** | 真 |
| Vc14Catalog.cs | 353 | **353** | 真 |
| Contracts/TweakDb/BxCatalog.cs | 457 | **457** | 真 |
| Shared/Services/AmdCcdModelCatalog.cs | 58 | **58** | 真 |
| Shared/Native/OfflineCpuSetCatalog.cs | 172 | **172** | 真 |
| 前端 BackendCpuTopologyService.cs | 51 | **51** | 真 |
| Views/ProcessPage.CpuTopology.cs | 106 | **106** | 真 |
| Shared（24 文件） | 2786 | **2786** | 真 |
| Contracts（30 文件） | 3315 | **3315** | 真 |

两处**必须纠正的口径偏差**（详见 §2.6）：
1. **"TODO 2 处、XXX 1 处"在当前快照下无法复现**：对 `src/` 全量 `.cs` 用词边界 + 大小写敏感搜 `\bTODO\b|\bXXX\b` → **0 命中**；大小写不敏感仅 3 个假阳性（`"AutoDownload"` 含 "toDo"、`Convert.ToDouble(saved)` 含 "toDo"、注释 `VEN_xxxx&DEV_xxxx`）。全仓唯一 `TODO` 词是 `Assets/TweakDb/Debloat.json:199` 的 UWP 包名 `"ToDo"`。
2. **"空 catch 33 处"只在"`src/` 全量（含前端）"口径成立**；本次三工程（Execution/Shared/Contracts）内是 **21 处**（Execution 19 + Shared 2 + Contracts 0）。前端另有 11 处、tools 另有 17 处。

---

## 1. ① 摘要 Top5

**1. CPU 拓扑不是"三套并列实现"，而是"2 个独立 Win32 读者 + 2 个纯函数算法模块 + 2 个前端/适配壳 + 2 个轻量组掩码工具"。真正值得合并的是 P/Invoke 声明层，不是业务算法。**
两个读者各自服务语义不同的消费者：`CpuTopologyService`（实验室，要 CCD/亲和性决策）vs `ProcmanCpuTopologyReader`（进程管理，要逐 CPU 勾选 + CPUID 逐核 P/E 探测）。可安全合并的是 **`GROUP_AFFINITY` 结构体 4 份拷贝 + `PROCESSOR_NUMBER` 2 份 + 缓冲协商逻辑 2 份**（合计约 32 行），而不是把 CCD 判定与 CPU Set 映射强行统一。合并后能顺带修掉一个隐性问题：两处缓冲查询算法的健壮性不一致（`ProcmanCpuTopologyReader.cs:39-61` 有 3 次重试 + 16MB 上限 + 长度越界校验；`CpuTopologyService.cs:922-934` 单次、无上限、失败即返回）。**结论：部分真（方向对、数字需修正）。**

**2. 四个"硬编码配置目录"里只有 1 个是真数据表，1 个是半内置，2 个根本不是数据表。真正的防篡改信任根在别处且已重复定义两份。**
`BxCatalog` 只是 JSON 加载器（条目本体来自 `Assets/TweakDb/*.json` 16 个文件约 400KB），`OfflineCpuSetCatalog` 是纯 Win32 读取器（零数据表），`Vc14Catalog` 是规则/正则集，只有 `AmdCcdModelCatalog`（30 条 AMD SKU）是真数据表。**而运行库修复的信任根 `14.42` 基线 SHA256 被写在两个文件里**：`Vc14Reinstall.cs:52-56`（`BaselineHashes`）与 `AlphaRestore/RuntimeGuardService.cs:251-256`（`V14InstallerSha256`），当前两处**逐字节相同**但无任何编译期/测试期断言它们必须相同。**抽离成外部 JSON 会直接削弱防篡改**（改 JSON 即可让任意 EXE 通过校验），必须保留内联；真正该做的是"单一定义 + 引用"，而不是"抽离"。**结论：部分真。**

**3. `Execution/` 根目录 46 个文件 100% 是 `namespace DeltaNFD.Services`，目录与 namespace 基本解耦；移动文件的唯一硬约束是冒烟测试 csproj 的 29 条显式路径。**
`Execution/CpuFrame/` 里 8 个文件有 7 个属 `DeltaNFD.Services`、2 个属 `DeltaNFD.Execution.CpuFrame`；`Execution/TweakDb/BxService.cs` 属 `DeltaNFD.Services.TweakDb`。所以"按领域移入子目录"不会改变 namespace、不会造成类型冲突。但 `tools/BackendSmokeTest/BackendSmokeTest.csproj` 有 **102 条 `<Compile Include>`，其中 29 条精确指向 `Execution/` 根文件、2 条通配（`Execution\Vc14*.cs`、`Execution\AlphaRestore\*.cs`）**——移动任何根文件必须同步改这 29 条，否则测试工程直接编译失败。**结论：真（补充了 csproj 这个真正风险点）。**

**4. 缺单元测试工程的实质是"测试与被测代码同工程、无隔离"，而不是"没测试"；且存在明确的"零测试"清单。**
`BackendSmokeTest` 把 103/104 个 Execution 源文件编进测试 EXE（唯一例外 `GpuDeviceReloadService.cs`），csproj 共 **102 条 `<Compile Include>` 中 43 条指向 Execution**（29 条精确到根文件 + 5 条 CpuFrame 精确 + 1 条 TweakDb 精确 + 2 条项目根 + 4 条通配 `Vc14*.cs`/`AlphaRestore\*`/`Procman\*`/`Native\*`）；`Program.cs` 1241 行、**147 个命令开关（其中 107 个 `--*-checks`）**，正文远多于任务给的"60+"。类型级覆盖实测：**220 个 public/internal 类型中 166 个在测试源码中出现，54 个零引用**，其中真正算"核心逻辑"的零测试类是：`RuntimeRepairPreflight`(239)、`RuntimeUninstallCommand`(147)、`RuntimeSxsResolver`(133)、`RuntimeRepairReadiness`(113)、`ProcmanControlCoordinator`(101)、`RegistryAclHelper`(75)、`GpuDeviceReloadService`(64)、`RuntimeRepairPolicy`(59)、`Vc14RegistrationCleanup`(56)、`ProcmanCpuSampler`(53)、`ProcmanAdditionalCapabilities`(51)、`Vc14PackageTrust`(38)、`RuntimeCppOverwrite`(23)、`NativeProcman`(21)。已覆盖部分质量不低：有 19 个检查文件用 `Fake/Platform/Store` 注入做行为断言。**结论：真。**

**5. 没有发现"整段复制粘贴"——最长连续相同代码块只有 6 行；真正的重复是"小工具函数与 P/Invoke 声明的多份手抄"，合计约 165 行。**
用"非空行多重集交集"量化并设无关文件对作基线：无关对基线 3.5%–4.3%，而"Vc14 vs AlphaRestore 卸载流程"只有 2.6%–6.2%、"Vc14Catalog vs RuntimeCppOfficial" 仅 0.6%、"拓扑两读者" 4.1%——**均不高于或仅略高于基线**，说明它们是"同构不同码"（结构相似、代码各自实现），不是复制粘贴。真正确定性的重复是：`FirstLine` ×5、`WriteLease` ×4、`GROUP_AFFINITY` ×4、`AcquireWriteLease` ×3、`SplitLines` ×3、`GetDirectorySizeMb` ×3、`PROCESSOR_NUMBER` ×2。**结论：部分真（推翻了任务示例中"Vc14*/Runtime* 修复流程大段复制"的假设，但证实了工具函数级复制）。**

---

## 2. ② 断言核实表

### 2.1 断言 1：CPU/CCD 拓扑是否真有三套实现

**结论：部分真。** 参与拓扑的确实有 8 个文件，但"三套实现"的精确表述应为 **"2 个独立 Win32 拓扑读者 + 2 个纯函数算法模块 + 2 个前端/适配壳 + 2 个轻量组掩码工具"**。真正调用 `GetLogicalProcessorInformationEx` 的只有 2 个。

| # | 文件（非空行） | 职责 | Win32 调用（声明行 / 调用行） | 实际调用方 |
|---|---|---|---|---|
| 1 | `Shared/Services/ICpuTopologyService.cs`（103） | 纯契约：`CpuCoreInfo`/`CcdInfo`/`CpuTopology` 模型 + 9 方法接口 | **无**（仅注释提及 API，:106） | `ServiceLocator.cs:36/:110`、`OfflineCpuSetScheduler.cs:16`、`CpuFrameEngine.cs:25` |
| 2 | `Shared/Services/CcdTopologyDetector.cs`（76） | 纯函数：cores+dies+l3 → CCD，Die 优先 / L3 回退 / 型号上限校验 | **无** | `CpuTopologyService.cs:72`；`CcdModelChecks.cs:120` |
| 3 | `Execution/CpuFrame/CpuTopologyService.cs`（914） | 产品实现：读拓扑 → P/E 分类 → CCD 判定 → 亲和性/双单 CCD 调度/内存限制 | `GetLogicalProcessorInformationEx` 声明 :920，调用 :925/:928（包装 :922-934 `QueryTopology`，:939-955 `WalkTopology`） | `CpuFrameEngine.cs:71 new`、`:170-184` 等 30+ 处静态调用；`OfflineCpuSetScheduler.cs:23`；冒烟测试 33 处 |
| 4 | `Execution/Procman/ProcmanCpuTopologyReader.cs`（270） | procman 专用：核/缓存/Die/组 + CPU Set + 逐核 CPUID P/E + Ryzen CCD 推定 | `GetLogicalProcessorInformationEx` :28（调用 :68/:100）、`GetSystemCpuSetInformation` :31（:70）、`SetThreadGroupAffinity` :35（:266/:273）、`GetCurrentProcessorNumberEx` :36 | `ProcmanCommands.cs:143`、`ProcmanTopology.cs:37`；冒烟测试 15 处 |
| 5 | `Execution/Procman/ProcmanTopology.cs`（53） | 轻量只读缓存（`Lazy`）：系统组掩码，失败回退 `GetActiveProcessorGroupCount/Count` | `GetActiveProcessorGroupCount` :27、`GetActiveProcessorCount` :30（调用 :42/:45） | `ProcmanBackground.cs:28`、`ProcmanCapabilities.cs:219/:261`、`ProcmanCpuSampler.cs:26`、`ProcmanCpuTopologyReader.cs:65` |
| 6 | `Shared/Native/OfflineCpuSetCatalog.cs`（172） | 只读 CPU Sets 目录 + 掩码→ID 映射（拒绝不完整/冲突/被他人占用） | `GetSystemCpuSetInformation` :34（:48/:60） | `OfflineCpuSetScheduler.cs:60/:480/:486`；冒烟测试 13 处 |
| 7 | `DeltaNFD/Services/Backends/BackendCpuTopologyService.cs`（51） | **前端壳**：`ICpuTopologyService` 的 IPC 转发，并同步 `CpuFrameStatusState` 镜像 | 无 | `ServiceLocator.cs:36`（唯一 new 点） |
| 8 | `DeltaNFD/Views/ProcessPage.CpuTopology.cs`（106） | **UI 构建**：用 `ProcmanTopologyResult` 画 CPU 勾选面板，与 1–7 完全不同源 | 无 | `ProcessPage` partial 内部 |

**Win32 调用重复次数（实测）**

| API | 声明处 | 调用处 |
|---|---|---|
| `GetLogicalProcessorInformationEx` | 2 处：`CpuTopologyService.cs:920`、`ProcmanCpuTopologyReader.cs:28` | 4 处：:925、:928、:68、:100 |
| `GetSystemCpuSetInformation` | 2 处：`OfflineCpuSetCatalog.cs:34`（`IntPtr` 签名）、`ProcmanCpuTopologyReader.cs:31`（`nint` 签名） | 4 处：:48、:60、:70 |
| `GetActiveProcessorGroupCount` / `GetActiveProcessorCount` | 1 处：`ProcmanTopology.cs:27/:30` | 各 1 处 |
| `GROUP_AFFINITY` | **4 处定义**：`CpuSets.cs:17`、`ProcmanTopology.cs:9`、`ProcmanCapabilities.cs:123`、`ProcmanCpuTopologyReader.cs:19`（前三个字段完全一致 `nuint Mask`；第四个是 `ulong Mask` 且命名 `GroupAffinity`） | — |
| `PROCESSOR_NUMBER` | **2 处**：`ProcmanTopology.cs:19`、`ProcmanCpuTopologyReader.cs:25` | — |

**可否合并为单一带缓存实现？可以，但仅限互操作层。**
- **可合并（低风险，收益确定）**：`GROUP_AFFINITY`/`PROCESSOR_NUMBER` 结构体统一定义（约 32 行重复），`Query`/`QueryTopology` 缓冲协商统一（保留 `ProcmanCpuTopologyReader.cs:39-61` 的健壮版本），`FirstLine`/`SplitLines` 工具函数。
- **不宜合并（风险高、收益低）**：`CcdTopologyDetector`（已有 148 行专门断言）与 `ProcmanCpuTopologyReader.Decode`（字节级解码 + `SetThreadGroupAffinity` 线程绑定探测）服务两个不同合同：实验室侧要"CCD 调度决策"，procman 侧要"逐 CPU 勾选 + 软亲和"。强行统一会把 CCD 契约灌进 `Contracts/ProcmanContracts.cs`（`ProcmanTopologyResult` :310）。
- **"带缓存"现状不对称**：`ProcmanTopology.SystemGroupMasks` 有 `Lazy`（:32），`CpuTopologyService` 的 `RuleAvailableMask` 有 `Lazy`（:161），但 **`GetTopologyAsync` 每次调用都完整 `ReadTopology()`（:37）无缓存**。影响面需分清：**2 秒 tick 路径 `ApplyCpuSchedulingTick`（:529）走的是 `RuleAvailableMask.Value` 缓存，不会重读拓扑**（设计正确）；无缓存的实际代价在"用户点击/离线调度"路径——`ApplyDualCcdSchedulingAsync`（:440）、`OfflineCpuSetScheduler.ApplyOnceAsync`（:49）、`LabViewModel` 刷新各自触发一次全量读取。因此 P2-2 的正确表述是"无缓存但不在热路径"，不应作为性能问题处理。

**实际调用链（实测）**
- 实验室：`LabViewModel.cs:52` → `ServiceLocator.Cpu` → `BackendCpuTopologyService.cs:12` → `BackendCpuFrameService.cs:165` → IPC → `CpuFrameCommands.cs:231` → `CpuFrameEngine.cs:427` → `CpuTopologyService.cs:30`。
- Procman：`ProcessPage.xaml.cs:288` → `BackendProcmanService.cs:77` → IPC → `ProcmanCommands.cs:143` → `ProcmanCpuTopologyReader.cs:63`。

> **file:line**：`ICpuTopologyService.cs:110`、`CcdTopologyDetector.cs:7`、`CpuTopologyService.cs:10/:918-934`、`ProcmanCpuTopologyReader.cs:14/:26-61`、`ProcmanTopology.cs:6/:32`、`OfflineCpuSetCatalog.cs:23/:34`、`BackendCpuTopologyService.cs:8`、`ProcessPage.CpuTopology.cs:10`、`CpuSets.cs:17`、`ProcmanCapabilities.cs:123`。

### 2.2 断言 2：硬编码配置目录（BxCatalog / AmdCcdModelCatalog / OfflineCpuSetCatalog / Vc14Catalog）

**结论：部分真 —— 四个文件里 1 个真硬编码数据表、2 个半内置、1 个不是数据表；且"真硬编码数据表"包含任务未列出的信任根常量。**

| 文件（非空行） | 是否真硬编码数据表 | 实际内容 | 证据 |
|---|---|---|---|
| `Contracts/TweakDb/BxCatalog.cs`（457） | **半内置** | ① `Sections` 10 个栏目定义（:132-145）② `ServiceGroups` 13 组 + 组内服务名清单（:503-521）③ `FullHyperVItemName` 常量（:128）④ 兼容性规则/中文提示（:222-300）。**条目本体全部外部 JSON**：`LoadAll()` :349-381 逐栏目读 `section.JsonFile`；`LoadAllServices()` :341-347 读 `allServices.json`；`LoadSpecials()` 读 `special.json`；`LoadMeta()` :451-486 读 `meta.json` | `Assets/TweakDb/` 实测 16 个 JSON 约 400KB（allServices 115KB、WindowsTelemetry 65KB、meta 27KB） |
| `Shared/Services/AmdCcdModelCatalog.cs`（58） | **真硬编码数据表** | 30 条 AMD SKU 精确清单（:15-47）+1 条品牌正则（:49-51）。`CcdCount => 2` 是常量（:8） | `CcdModelChecks.cs` 148 行专门断言，含 23 条"必须拒绝"的负例 SKU |
| `Shared/Native/OfflineCpuSetCatalog.cs`（172） | **不是数据表** | 纯 Win32 读取 + 解析 + 映射；数据全部运行时来自 `GetSystemCpuSetInformation`。零硬编码 SKU/哈希/路径 | :42-78 `TryRead`、:81-129 `TryMapMaskToIds`、:139-191 `TryParse` |
| `Execution/Vc14Catalog.cs`（353） | **半内置** | ① `Families` 4 条固定 MSI 升级码（:15-20）② 颜色分类规则（:28-48）③ 名称/版本正则（:52-62）④ 身份校验（:63-70）。**原包哈希不在此文件** | 另见下表 |

**任务未列出、但确实是"真硬编码数据表"的三处（这才是抽离收益/风险的核心）**

| 常量 | 位置 | 性质 | 抽离风险 |
|---|---|---|---|
| `BaselineHashes`（x64/x86 SHA256） | `Vc14Reinstall.cs:52-56` | **运行库修复信任根**：`:431` 安装前重算内置 14.42 原包哈希比对 | **高**：抽离成外部 JSON 等于"改文件即可让任意 EXE 通过校验"，直接削弱防篡改 |
| `V14InstallerSha256`（同组 x64/x86 SHA256） | `AlphaRestore/RuntimeGuardService.cs:251-256` | 第二份拷贝，`VerifyRepairPackage` :319-331 使用；实际路径由 :321 的 `file.Replace("VCRedist2015-2022-", "vc_redist.")` 隐式映射到与 `BaselineHashes` **同一个文件** `Assets/Vc14Baseline14.42/vc_redist.{x64,x86}.exe` | **高**：同一信任根两份定义，任一份漂移都会导致"校验通过但装错包"或"误报损坏" |
| `RepairPayloadManifestSha256` / `RepairInstallerCmdSha256` | `AlphaRestore/RuntimeGuardService.cs:247-248` | AIO 辅助组件清单/脚本锁定哈希，:306-316 校验 | **高**：同上 |

内嵌资源型清单（**已经是"从资源读"而非内联代码**，抽离意义小）：`RuntimeCppOfficial.catalog.json`(9171B)、`Vc14FileRepair.catalog.json`(3179B)、`Vc14OwnershipOriginals.catalog.json`(468B)，经 `DeltaNFD.Execution.csproj:43-45` 的 `<EmbeddedResource>` 编译进 DLL，由 `GetManifestResourceStream` 读取（`RuntimeCppOfficial.cs:19`、`Vc14FileRepair.cs:26`、`Vc14OwnershipOriginals.cs:17`）。**且 `Vc14FileRepair.catalog.json` 加载时会反向校验 `OriginalSha256 == Vc14Reinstall.BaselineHashes[arch]`（`Vc14FileRepair.cs:32`）**——这说明项目已经在做"两份数据互校"，只是没覆盖 `RuntimeGuardService.V14InstallerSha256`。

**判断与建议**
- **必须保留内联**：`BaselineHashes`、`V14InstallerSha256`、`RepairPayloadManifestSha256`、`RepairInstallerCmdSha256`。改为**单一定义 + 引用**（把 `V14InstallerSha256` 合并进 `BaselineHashes` 的消费方），并加一条"两处必须相等"的测试断言。
- **保留 C# 内联**：`AmdCcdModelCatalog`（30 SKU）。它同时是 CCD 判定算法的约束上界（`CcdTopologyDetector.FitsModel` 用 `model.CcdCount`/`PhysicalCores` 否决误判），抽离后 JSON 缺失/被改会静默退化为 `Unknown`（不崩溃但功能失效）。
- **抽离收益小**：`BxCatalog` 的 `ServiceGroups`/`Sections`（13+10 条，改动频率极低）。且注意：后端按内置 Id 解析并拒绝伪造 Id（`FindItem` :174-186、`FindServiceGroup` :188-191），抽离后必须保留 Id 存在性校验，否则等于打开"任意服务名注入"。
- **无需处理**：`OfflineCpuSetCatalog`。

> **file:line**：`BxCatalog.cs:132-145/:341-347/:349-381/:451-486/:503-521`、`AmdCcdModelCatalog.cs:15-47`、`OfflineCpuSetCatalog.cs:42`、`Vc14Catalog.cs:15-20`、`Vc14Reinstall.cs:52-56/:431`、`AlphaRestore/RuntimeGuardService.cs:247-256/:306-331`、`Vc14FileRepair.cs:26-33`、`DeltaNFD.Execution.csproj:43-45`。

### 2.3 断言 3：Execution/ 根目录 46 个 .cs 按领域分组与移动风险

**结论：真（46 个，namespace 高度统一），移动的编译风险几乎为零，真正风险在冒烟测试 csproj 与 partial 组。**

**namespace 实测分布**（全 104 文件）

| namespace | 文件数 | 分布 |
|---|---|---|
| `DeltaNFD.Services` | 53 | `Execution/` 根 46 + `CpuFrame/` 7 |
| `DeltaNFD.Services.AlphaRestore` | 22 | `AlphaRestore/` 全部 |
| `DeltaNFD.Execution.Procman` | 20 | `Procman/` 全部 |
| `DeltaNFD.Native` | 5 | `Native/` 全部 |
| `DeltaNFD.Execution.CpuFrame` | 2 | `CpuFrameEngine.cs`、`GamePowerThrottling.cs` |
| `DeltaNFD.Backends` | 1 | `BackendHost.cs` |
| `DeltaNFD.Services.TweakDb` | 1 | `TweakDb/BxService.cs` |

→ **46 个根文件 100% 是 `DeltaNFD.Services`，无一例外；目录与 namespace 已基本解耦**（`Execution/CpuFrame/` 内 7/8 个属 `DeltaNFD.Services`）。因此**把文件移入新子目录不会改变 namespace、不产生类型冲突**。

**46 个根文件的领域分组（建议，按上表可切 7 组）**

| 领域 | 数量 | 文件（非空行） |
|---|---|---|
| **Vc14 运行库修复** | 17 | Vc14Reinstall(668)、Vc14ResidueCleanup(387)、Vc14Catalog(353)、Vc14DependencyPolicy(330)、Vc14SafeInstaller(274)、Vc14MsiStateDiagnostics(239)、Vc14BetaUninstall(125)、Vc14FileRepair(125)、Vc14StagedRepair(121)、Vc14Diagnostics(91)、Vc14MsiOrphans(76)、Vc14BundleIdentity(69)、Vc14OwnershipOriginals(63)、Vc14RegistrationCleanup(56)、Vc14InstallerErrors(51)、Vc14PackageTrust(38)、Vc14Maintenance(23) |
| **Runtime 防护/编排** | 8 | RuntimeGuardProtection(528)、RuntimeFileUse(319)、RuntimeGuardBackends(223)、RuntimeGuardService(145)、RuntimeLegacyInstallationReview(76)、RuntimeInstallationReview(61)、RuntimeGuardRecoveryReport(61)、RuntimeHighFpsMaintenance(60) |
| **Ace 反作弊** | 7 | AceVirtualizationCheck(606)、AceService(537)、AceCleanupEnvironment(193)、AceCoreFileCleanup(136)、AceRegistrySafety(107)、ApplicationPreLaunchControl(60)、AceCleanupGuard(26) |
| **SystemTweaks** | 3 | SystemTweakService(1264)、AdvancedTweakService(618)、DefenderGuard(423) |
| **Gpu** | 4 | NvProfileService(237)、GpuSpoofService(210)、GpuSpoofWatch(68)、GpuDeviceReloadService(64) |
| **PowerAndMemory** | 3 | PowerService(478)、PagefileService(196)、ProcessMaintenanceService(163) |
| **Shaders** | 2 | ShaderService(578)、ShaderRecoveryCleanup(65) |
| **共用/杂项** | 2 | CleanupService(173)、RegistryAclHelper(75) |

（46 = 17+8+7+3+4+3+2+2 ✅）

**移动风险（按严重度）**

1. **P1 — 冒烟测试 29 条显式路径会断链**：`tools/BackendSmokeTest/BackendSmokeTest.csproj` 共 102 条 `<Compile Include>`，其中 **29 条精确指向 `src\DeltaNFD.Execution\Execution\<file>.cs`**（清单见 §3 附录），另有通配 `Execution\Vc14*.cs`、`Execution\AlphaRestore\*.cs`、`Execution\Procman\*.cs`、`Native\*.cs`。移动根文件必须同步改这 29 条 → 否则测试工程 CS2001/CS0246。
2. **P2 — partial 组不可拆散**：`Vc14Reinstall` 跨 `Vc14Reinstall.cs:49` + `Vc14StagedRepair.cs:11`；`ProcmanEngine` 跨 `ProcmanEngine.cs:14` + `ProcmanBackground.cs:7` + `ProcmanMemory.cs:7`；`ProcmanCapabilities` 跨 `ProcmanCapabilities.cs:18` + `ProcmanAdditionalCapabilities.cs:6`；`SystemTweakService` 在**同一文件内**有两个 partial 声明（:7 只放 1 个 extern，:30 是主类）——后者是异常写法，建议合并。
3. **P3 — 命名混乱**：项目名 `DeltaNFD.Execution` 与目录 `Execution/` 同名（路径 `src/DeltaNFD.Execution/Execution/…`），且"Execution"目录同时含实验室服务、运行库修复、GPU、电源、ACE 五类领域。
4. **P3 — InternalsVisibleTo**：`DeltaNFD.Execution.csproj:32` 对 `DeltaNFD.OfflineHelper` 开放 internal；`OfflineHelperPowerServices.cs` 用 `ServiceLocator`/`PowerService` 桩避免电源写入（:3-8 注释）。移动 `CpuTopologyService.cs` 不需改它（其路径未被 OfflineHelper 引用，已核实 OfflineHelper 只链接 3 个 `DeltaNFD/Services/OfflineHelper*.cs`）。

> **file:line**：`DeltaNFD.Execution.csproj:32/:43-45`、`BackendSmokeTest.csproj`（29 条）、`Vc14Reinstall.cs:49`+`Vc14StagedRepair.cs:11`、`ProcmanEngine.cs:14`+`ProcmanBackground.cs:7`+`ProcmanMemory.cs:7`、`ProcmanCapabilities.cs:18`+`ProcmanAdditionalCapabilities.cs:6`、`SystemTweakService.cs:7/:30`。

### 2.4 断言 4：缺失单元测试工程的实际缺口

**结论：真（无 `*Tests*.csproj`），但需精确表述为"无隔离的单元测试工程，测试以源码内联方式寄生在冒烟工程"。**

**现状实测**
- 解决方案 12 个工程，**无任何 `*Tests*.csproj` / `*Test*.csproj`**；唯一测试载体 `tools/BackendSmokeTest`（128 个 .cs，`Program.cs` 1241 行）。
- 14 个 `--xxx` 以外的**命令开关实测 147 个**（其中 107 个 `--*-checks`，36 个 `live|soak|readonly|native|ipc|fixture|child|replay|benchmark` 型）→ 任务"60+"成立但严重低估。
- **Execution 104 个源文件中有 103 个被编进冒烟测试 EXE**（唯一例外 `GpuDeviceReloadService.cs`），43 条 `<Compile Include>` 指向 Execution；等价于"产品代码 + 断言混编，无 dll 边界"。

**已覆盖的纯逻辑（引用密度实测，按类名在测试源码中出现次数）**

| 被测目标 | 引用数 | 覆盖形态 | 需管理员 |
|---|---|---|---|
| `Vc14Reinstall` | 82 | 卸载计划/分阶段/文件修复回退分支 | 否 |
| `Vc14DependencyPolicy` | 34 | **4 份真实日志快照回放**（`Vc14DependencyChecks.cs` 尾部断言 `count==4`） | 否 |
| `CpuTopologyService` | 33 | `ClassifyHybridCores`、`BuildCcdAdvice`、`ApplyAffinityRuleTick`/`RestoreAffinityRule`（`AffinityReliabilityChecks.cs:35-74`） | 否（用测试子进程当目标） |
| `Vc14Catalog` | 32 | 分类规则、MSI 身份正则 | 否 |
| `BxCatalog` | 31 | 拒绝伪造 Id、资产缺失回退空（`SystemBxChecks.cs`） | 否 |
| `RuntimeGuardProtection` | 16 | 防护状态机 | 部分 |
| `TweakBackupStore` | 16 | 备份语义（临时目录 + 隔离根） | 否 |
| `DefenderGuard` | 16 | 只读状态查询 | 部分 |
| `NvApiDrs` | 10 | `ClassifyShaderCache`、`SetShaderCacheSize` 注入式 | 否 |
| `AmdCcdModelCatalog` | 10 | 30 SKU 正例 + 23 负例（`CcdModelChecks.cs:27-46`） | 否 |
| `CcdTopologyDetector` | 2 | `CcdModelChecks.cs:120` 直接调 `Detect` | 否 |
| `OfflineCpuSetCatalog`/`Planner` | 13 / 8 | 映射拒绝规则 + 规划 6 场景 | 否 |
| `FileTransaction` | 17 | 事务/回滚（`FileUpdateChecks.cs` 340 行） | 否 |
| `Vc14FileRecovery`/`Vc14FileRepairSource` | 11 | 强制重装回退分支（`Vc14FileRepairChecks.cs` Fake 注入） | 否 |
| `SafeInstallerEngine` | 3（但含完整 fake） | `ISafeInstallerPlatform`/`ISafeInstallerStore`/`SafeInstallerFileStore` 全注入 | 否 |

→ 19 个检查文件使用 `Fake/Platform/Store` 注入做行为断言（`SafeInstallerChecks.cs`、`Vc14FileRepairChecks.cs`、`RuntimeFileUseChecks.cs` 等），**质量不低**，应给予肯定。

**类型级覆盖实测**：220 个 public/internal 类型中 **166 个在测试源码中出现（75.5%），54 个零引用**。零引用者中**核心逻辑类（剔除纯 DTO/枚举）清单**：

| 零测试核心类 | 非空行 | 风险说明 |
|---|---|---|
| `RuntimeRepairPreflight` | 239 | 含 `WaitForInstallerIdle`（同步 `Thread.Sleep(2s)` 轮询）与 WinSxS 权限检测，无任何断言 |
| `RuntimeUninstallCommand` | 147 | 卸载命令解析/引号拆分，安全敏感 |
| `RuntimeSxsResolver` | 133 | WinSxS 路径解析 + 重解析点校验 |
| `RuntimeRepairReadiness` | 113 | 修复前置条件判定 |
| `ProcmanControlCoordinator` | 101 | 跨进程控制权协调 |
| `RegistryAclHelper` | 75 | 注册表 ACL 操作 |
| `GpuDeviceReloadService` | 64 | 唯一未被编进测试的 Execution 文件 |
| `RuntimeRepairPolicy` | 59 | 修复策略/错误分类 |
| `Vc14RegistrationCleanup` | 56 | v14 INF 包装登记清理 |
| `ProcmanCpuSampler` | 53 | CPU 采样（除零/空拓扑边界） |
| `ProcmanAdditionalCapabilities` | 51 | 附加能力集 |
| `Vc14PackageTrust` | 38 | 包装签名/身份信任 |
| `RuntimeCppOverwrite` | 23 | 旧 C++ 原包覆盖 |
| `NativeProcman` | 21 | P/Invoke 封装 |

**建议先补（均可无管理员运行）**
1. **`Vc14Reinstall.BaselineHashes` ↔ `RuntimeGuardService.V14InstallerSha256` 一致性（P0 测试缺口）**：一条断言（两字典 x64/x86 相等）+ 一条文件断言（`Assets/Vc14Baseline14.42/vc_redist.{x64,x86}.exe` 存在、SHA256 匹配、`FileVersion == "14.42.34433.0"`）。**纯文件读取，零权限**，一次防住"信任根漂移"这个最高危故障。
2. **`SafeInstallerEngine` 补"恢复期身份冲突"用例（P1）**：现有 fake 覆盖较全，但缺"记录中 PID/创建时间与实际服务不符时拒绝并保留证据"这一 AGENTS.md 明确要求的场景。
3. **`CpuTopologyService.ClassifyHybridCores` 的 Intel 语义矩阵（P1）**：源码注释三次强调"最容易搞反"（:57-61、:110-119），现有覆盖不足；应补 6 组（单档/双档/空/0 与 1/1 与 2/全 0）。纯静态函数，零依赖。
4. **`RuntimeRepairPreflight.WaitForInstallerIdle` 的超时语义（P2）**：注入时钟/退避函数后可纯逻辑测；顺带把 `Thread.Sleep` 改为可注入延迟。
5. **不建议优先补**：`BxService`（2286 行，25 处引用已覆盖解析骨架）、`ProcmanEngine`（1138 行，14 处引用多为 IPC 契约）——与真实系统强耦合，单测 ROI 最低。

> **file:line**：`BackendSmokeTest.csproj`（Compile 段）、`Program.cs`（147 开关）、`CcdModelChecks.cs:27-46/:120`、`Vc14FileRepairChecks.cs:12`、`SafeInstallerChecks.cs:15/:129-160`、`Vc14DependencyChecks.cs`（4 快照回放）、`CpuTopologyService.cs:57-61/:110-119`、`RuntimeRepairPreflight.cs:132-141`。

### 2.5 断言 5：重复代码证据（含方法学纠偏）

**结论：部分真 —— 任务示例的"Vc14*/Runtime* 修复流程大段复制"不成立；但存在大量确定性"工具函数/P-Invoke 手抄"，合计约 165 行。**

**方法学**：直接算"行集合交集"会把 `{`、`}`、`using …`、`});` 等琐碎行算进相似度，导致严重高估。因此本报告同时给出：①原始 ②剔除琐碎行后的**多重集交集**（考虑重复出现次数）③**无关文件对作基线**。

| 组 | A ↔ B | 原始 | 剔除琐碎后 | 判定 |
|---|---|---|---|---|
| *基线1* | `Vc14Catalog` ↔ `AceService` | 18.8% | **3.5%** | 无关对基线 |
| *基线2* | `PowerService` ↔ `ShaderService` | 28.4% | **4.3%** | 无关对基线 |
| 1 | `Vc14Reinstall` ↔ `AlphaRestore/RuntimeUnifiedUninstallBackend` | 25.2% | **2.6%** | **低于基线 → 非同源复制** |
| 2 | `Vc14Reinstall` ↔ `AlphaRestore/RuntimeUnifiedUninstaller` | 26.9% | **6.2%** | 略高 → 少量同构 |
| 3 | `Vc14Catalog` ↔ `AlphaRestore/RuntimeCppOfficial` | 25.6% | **0.6%** | **远低于基线 → 完全不同源** |
| 4 | `CpuTopologyService` ↔ `ProcmanCpuTopologyReader` | 28.1% | **4.1%** | ≈基线 → 仅 P/Invoke 层面的同构 |
| 5 | `RuntimeGuardService` ↔ `AlphaRestore/RuntimeGuardService`（同名） | 15.3% | **7.6%** | 最高之一 → 适配器内确有搬运 |
| 6 | `RuntimeBetaRepair` ↔ `RuntimeStrongRepair` | 23.0% | **6.7%** | 同构 |
| 7 | `AceService` ↔ `AceCleanupEnvironment` | 36.8% | **12.3%** | **最高**，路径枚举/进程判定确有复制 |
| 8 | `RuntimeUnifiedUninstaller` ↔ `RuntimeBetaRepair` | 19.6% | **2.6%** | 低于基线 |

**最长连续相同块（LCS 精确计算）**：全部候选对中最长仅 **6 行**（`RuntimeGuardService` ↔ `AlphaRestore/RuntimeGuardService`），其余为 1–3 行。
→ **不存在"整段复制粘贴"**。这些文件是"同构不同码"：结构对仗、命名对齐，但每行都重写过。**任务示例中"Vc14Catalog vs RuntimeCppOfficial catalog"尤其不成立（0.6%，比无关文件还低）。**

**但确定性重复确实存在（这才是可安全重构的部分）**

| 重复单元 | 份数 | 位置 | 合计行 | 语义是否已分叉 |
|---|---|---|---|---|
| `FirstLine(string)` | **5** | `CpuTopologyService.cs:887`(5)、`AceService.cs:608`(5)、`AdvancedTweakService.cs:640`(5)、`PowerService.cs:543`(5)、`SystemTweakService.cs:1416`(5) | 25 | 否（4 份逐字符相同，1 份仅变量名 `br` vs `lineBreak`） |
| `GetDirectorySizeMb` | **3** | `AceCleanupEnvironment.cs:149`(11)、`CleanupService.cs:98`(18)、`ShaderService.cs:626`(12) | 41 | **是**（CleanupService 版多 `pattern` 参数） |
| `AcquireWriteLease`（Mutex 30s 等待 + Abandoned 处理 + 中文错误） | **3** | `ProcmanChangeLog.cs:160`(14)、`ProcmanRuleStore.cs:108`(14)、`BxService.cs:29`(22) | 50 | 否（仅资源名与提示文案不同） |
| `WriteLease : IDisposable` | **4** | `ProcmanChangeLog.cs:174`(9)、`ProcmanRuleStore.cs:122`(9)、`BxService.cs:50`(9)、`TweakBackupStore.cs:120`(5) | 32 | **是：Dispose 语义已分叉**——前两者刻意不 Dispose Mutex（`finally { /* Mutex 由 owner 持有到进程结束，不 Dispose */ }`），后两者 `finally { mutex.Dispose(); }` |
| `GROUP_AFFINITY` 结构体 | **4** | `CpuSets.cs:17`(7)、`ProcmanTopology.cs:9`(7)、`ProcmanCapabilities.cs:123`(7)、`ProcmanCpuTopologyReader.cs:19`(5) | 26 | **是**：前三个 `nuint Mask`，第四个 `ulong Mask` + 类名 `GroupAffinity`（x64 下等价，x86 下 ABI 不同） |
| `PROCESSOR_NUMBER` | 2 | `ProcmanTopology.cs:19`(5)、`ProcmanCpuTopologyReader.cs:25`(1) | 6 | 否 |
| `SplitLines` | 3 | `CpuTopologyService.cs:884`、`AceVirtualizationCheck.cs:668`、`SystemTweakService.cs:1423`（各 2 行，逐字符相同） | 6 | 否 |
| **合计** | | | **≈186 行**（去重后净减约 150 行） | |

**额外发现（跨命名空间同名私有类）**：`WriteLease` 4 份分布在 3 个命名空间（`DeltaNFD.Execution.Procman` ×2、`DeltaNFD.Services.TweakDb`、`DeltaNFD.Services`）、`Lease` 3 份分布 3 个命名空间。**这些是"同名不同义"，是最容易误改的一类。**

> **file:line**：见上表；另 `AlphaRestore/RuntimeGuardService.cs:20`（同名类的转发点：`new DeltaNFD.Services.RuntimeGuardService().GetStatusAsync()`）。

### 2.6 断言 6：其他工程质量问题

**结论：部分真 —— "空 catch 33 处"口径需限定；"TODO 2/XXX 1"无法复现；超大方法、async void、Thread.Sleep、静态可变状态均为真且已定位。**

**（a）空 catch 的精确口径**

| 范围 | 严格 `catch { }`（无注释） | 空或仅注释 |
|---|---|---|
| `src/` 全量 | **33** | 93 |
| `src/DeltaNFD.Execution` | **19** | 38 |
| `src/DeltaNFD.Shared` | 2 | 4 |
| `src/DeltaNFD.Contracts` | **0** | 0 |
| 前端 `src/DeltaNFD` | 11 | 47 |
| `tools/BackendSmokeTest` | 17 | 66 |

→ **"33 处"成立但仅在 `src/` 全量口径；本次三工程内 21 处。** Execution 的 19 处代表位置（均可追到"静默失败"风险）：
- `CpuTopologyService.cs:543`、`:553` —— **硬锁核失败静默**（`try { ApplyHardAffinity(...) } catch { }`），用户在 UI 上看到"已生效"但实际未锁。**这是本次审计发现的最实际的正确性隐患。**
- `CpuTopologyService.cs:707`、`:910` —— HeteroPolicy 恢复失败、子进程超时清理静默。
- `CpuFrameEngine.cs:367`、`:515`、`:958` —— 实例失败、"个别实例失败可接受"、**Dispose 里 `StopMonitorAsync().GetAwaiter().GetResult()` 异常全吞**。
- `ProcmanEngine.cs:123`、`:355`、`:880` —— WorkingSet 读取、规则应用、restore 回调。
- `OfflineCpuSetScheduler.cs:368`、`ProcmanCpuSampler.cs:48`、`AceCleanupEnvironment.cs:230`、`Vc14Catalog.cs:296`（PATH 非法项跳过，有注释、可接受）。

**（b）超大方法（>150 行，实测 13 个 / 其中 Execution 6 个）**

| 方法 | 位置 | 行数 |
|---|---|---|
| `BackendHost.ServeAsync` | `BackendHost.cs:175` | **316** |
| `BxService.EvaluateItem` | `BxService.cs:322` | **282** |
| `AceVirtualizationCheck.BuildSystemItems` | `AceVirtualizationCheck.cs:127` | **202** |
| `ShaderService.DiagnoseAt` | `ShaderService.cs:340` | **198** |
| `AceService.CleanPhasedAsync` | `AceService.cs:243` | **190** |
| `SystemTweakService.DisableHyperVAsync` | `SystemTweakService.cs:346` | **164** |
| `Vc14Reinstall.BuildRemovalPlan` | `Vc14Reinstall.cs:65` | 124 |
| `FrameTweaksService.VerifyAndRestorePowerSchemeContentAsync` | `FrameTweaksService.cs:730` | 122 |
| `OfflineCpuSetScheduler.ApplyOnceAsync` | `OfflineCpuSetScheduler.cs:37` | 118 |
| *(前端)* `BxDbViewModel.LoadItemsAsync` / `ApplyChangesAsync` | `:280` / `:769` | 302 / 277 |
| *(前端)* `OfflineModeCoordinator.DisableAsync` | `:24` | 167 |
| *(前端)* `GpuSpoofPage.SpoofButton_Click` | `:17` | 167 |
| *(前端)* `UpdateService.LaunchInstallerAsync` | `:615` | 159 |
| *(前端)* `FrameService.ActivateFrameModeCoreAsync` | `:636` | 155 |
| *(前端)* `PluginManifestParser.ParseElement` | `:86` | 152 |

**（c）`async void`**：`src/` 全量 **107**；**Execution / Shared / Contracts = 0**；全部在 `src/DeltaNFD`（`ProcessPage.xaml.cs` 17、`PluginPage.xaml.cs` 9、`RuntimePage.xaml.cs` 8、`SettingsPage.xaml.cs` 6、`ProcessPage.Background.cs` 6、`ShaderRecoveryPage.xaml.cs` 6 …）。**这是 WinUI 事件处理器的正常模式，不算缺陷**，但均无统一异常兜底（仅 `ShaderViewModel.cs:159` 注释提到"不让 async void 调用方崩溃"）。

**（d）`Thread.Sleep`**：`src/` 全量 5 处，Execution 3 处：
- `AlphaRestore/RuntimeRepairPreflight.cs:138` `Thread.Sleep(TimeSpan.FromSeconds(2))` —— 位于 `WaitForInstallerIdle(TimeSpan)` 的 `while` 轮询内（:132-141），**同步阻塞**。若被 async 链或 UI 线程调用会卡 2s×N。
- `Vc14SafeInstaller.cs:264` `Thread.Sleep(100)` —— Installer 服务启动状态轮询，12s 上限，可接受。
- 前端 `TrayIconWindow.cs:286`(400ms)、`RealSystemOptimizer.cs:270`(300ms)、`FileUpdater/Program.cs:53`(100ms 控制台轮询)。

**（e）静态可变状态（真，且是最需关注的一类）**
- `CpuTopologyService.cs:156/:159` `DualCcdSchedulingActive` / `SingleCcdExcludeActive`（`public static { get; private set; }`）—— **跨实例、跨调用共享**。
- `CpuTopologyService.cs:366/:367` `_dualCcdGameMask` / `_dualCcdOtherMask`；`:734` `_heteroOverrideBackup`；`:167` `AffinityRuleOriginals`（static Dictionary，跨实例共享原值备份）。
- **双源状态不一致隐患**：后端 `CpuTopologyService` 的静态标志与前端 `CpuFrameStatusState` 镜像（由 `BackendCpuTopologyService.cs:25-26/:34/:48-49` 在成功返回后**乐观写入**）是两套状态；后端重启/超时/部分失败时前端仍显示"已生效"。`CpuFrameEngine.cs:176-184` 又用静态标志驱动 tick 分支——同一事实三处存储。
- `NvApiDrs.cs:124-199` **18 个静态字段**（`_initialized`/`_available` + 16 个函数指针），延迟初始化；有 `lock`（6 处）但 `_initialized` 与函数指针写入不原子。
- `BxService.cs:306/:307/:1163/:1164`、`DefenderGuard.cs:60/:77` 静态缓存。
- `ProcmanCapabilities.cs:27/:28` `[ThreadStatic]` 句柄钉住。

**（f）`Dispose` 缺失/异常吞噬**：Execution 内实现 `IDisposable` 仅 4 个：`CpuFrameEngine:17`、`ProcmanEngine:14`、`ProcessPropertyGate.Lease:26`、`ProcmanCapabilities.PinScope:38`。**`BxService`（2286 行，static Mutex :34）不实现 IDisposable**；`ProcmanChangeLog`/`ProcmanRuleStore` 的 Mutex 刻意不 Dispose。两处 `Dispose` 都吞异常且同步等异步：`CpuFrameEngine.Dispose` = `try { StopMonitorAsync().GetAwaiter().GetResult(); _gamePower.Sync([]); } catch { }`；`ProcmanEngine.Dispose` = `_ = StopRulesEngineAsync().GetAwaiter().GetResult()`。

**（g）硬编码绝对路径**：全 `src/` 仅 **1 处真硬编码**：`Execution/AceCoreFileCleanup.cs:22` `internal const string Root = @"C:\Program Files\AntiCheatExpert";`（另有 `AceContracts.cs:181`、`AceService.cs:504` 同路径，但是注释）。业务上 ACE 固定安装位可接受，但**未用 `ProgramFiles` 环境变量**，Program Files 被重定向的环境会失效。其余 60+ 处均正确使用 `Environment.GetFolderPath` / `AppContext.BaseDirectory` / `BackendEnvironment.AssetsRoot`——**这方面做得规范，应肯定**。

**（h）TODO / XXX（口径纠偏）**：`src/` 全量词边界大小写敏感搜索 **0 命中**；大小写不敏感仅 3 个假阳性（`AdvancedTweakService.cs:131` 的 `"AutoDownload"`、`PluginDeclarativeView.xaml.cs:166` 的 `Convert.ToDouble(saved)`、`GpuSpoofService.cs:61` 注释 `VEN_xxxx&DEV_xxxx`）；全仓唯一 `TODO` 词是 `Debloat.json:199` 的 UWP 应用名 `"ToDo"`。→ **"TODO 2 处、XXX 1 处"在当前快照不可复现，判"假"**（不影响结论，但说明该数据可能来自其他快照或含假阳性）。

**（i）`lock` 使用**：Execution 内 `lock (...)` 共 **91 处**，集中在 `ProcmanEngine.cs`(19)、`CpuTopologyService.cs`(13)、`ProcmanControlCoordinator.cs`(9)、`ProcmanBackground.cs`(7)、`BxService.cs`(7)、`ProcmanChangeLog.cs`(6)、`NvApiDrs.cs`(6) —— 与静态可变状态分布一致，说明作者有并发意识，但状态仍分散在多个类型。

> **file:line**：`CpuTopologyService.cs:156/:159/:167/:366/:367/:543/:553/:707/:734/:910`、`CpuFrameEngine.cs:367/:515/:958`、`ProcmanEngine.cs:123/:355/:880`、`BackendHost.cs:175`、`BxService.cs:322`、`AceVirtualizationCheck.cs:127`、`ShaderService.cs:340`、`AceService.cs:243`、`SystemTweakService.cs:346`、`RuntimeRepairPreflight.cs:132-141`、`Vc14SafeInstaller.cs:264`、`NvApiDrs.cs:124-199`、`AceCoreFileCleanup.cs:22`、`BackendCpuTopologyService.cs:25-26/:34/:48-49`、`CpuFrameEngine.cs:176-184`。

---

## 3. ③ 实测数据

### 3.1 同名类完整表（跨文件，含命名空间，`src/` 排除 obj/bin）

| 类名 | 文件数 | 命名空间数 | 明细 | 是否真冲突 |
|---|---|---|---|---|
| `Program` | 9 | **9** | 6 个后端 + OfflineHelper + 2 插件 | 否（9 个命名空间） |
| `ProcessPage` | 6 | 1 | `Views/*.cs` 全 partial | 否（partial） |
| `RuntimePage` | 5 | 1 | `Views/*.cs` 全 partial | 否（partial） |
| `WriteLease` | **4** | **3** | `ProcmanChangeLog.cs`、`ProcmanRuleStore.cs`（同 NS `Execution.Procman`）、`BxService.cs`（`Services.TweakDb`）、`TweakBackupStore.cs`（`Services`） | **否但需警惕：私有嵌套类，语义已分叉** |
| `Lease` | 3 | 3 | `BackendOperationGate.cs`、`PluginAuthorizationStore.cs`、`ProcessPropertyGate.cs` | 否，同上 |
| `ProcmanEngine` | 3 | 1 | `ProcmanEngine.cs`/`ProcmanBackground.cs`/`ProcmanMemory.cs` 全 partial | 否（partial） |
| `UpdateService` | 3 | 1 | `UpdateService.cs`/`.Fallback.cs`/`.Files.cs` 全 partial | 否（partial） |
| `SettingsPage` | 3 | 1 | `Views/SettingsPage*.cs` 全 partial | 否（partial） |
| `SystemTweakService` | 2 | 1 | **同一文件两个 partial 声明**（`SystemTweakService.cs:7` 与 `:30`） | 否（partial），但**写法异常**，建议合并 |
| `ProcmanCapabilities` | 2 | 1 | `ProcmanCapabilities.cs:18` + `ProcmanAdditionalCapabilities.cs:6` 均 `static partial` | 否（partial） |
| `Vc14Reinstall` | 2 | 1 | `Vc14Reinstall.cs:49` + `Vc14StagedRepair.cs:11` 均 `static partial` | 否（partial） |
| `FramePage` / `GpuSpoofPage` | 2 / 2 | 1 / 1 | `Views/*.cs` partial | 否（partial） |
| **`RuntimeGuardService`** | **2** | **2** | `Execution/RuntimeGuardService.cs`（145 行，`DeltaNFD.Services`，`public sealed`）↔ `Execution/AlphaRestore/RuntimeGuardService.cs`（379 行，`DeltaNFD.Services.AlphaRestore`，`internal sealed`，Alpha0.916 修复适配器，内部 `new DeltaNFD.Services.RuntimeGuardService()` 转发） | **否（不同 NS 不冲突）但同名易混淆，且适配器与公开实现相似度 7.6%** |
| `PowerService` | 2 | **1**（`DeltaNFD.Services`） | `Execution/PowerService.cs`（真实） ↔ `OfflineHelper/OfflineHelperPowerServices.cs`（**脱机助手内的 fail-closed 桩**，仅 `HeteroPolicySettings` 数据 + `OfflinePowerService`） | **是：同 NS 同名不同程序集**，靠"OfflineHelper 不链接 Execution 真实现"规避；一旦引用关系变化即 CS0436/歧义 |
| `ServiceLocator` | 2 | **1** | `DeltaNFD/Services/ServiceLocator.cs` ↔ `OfflineHelper/OfflineHelperPowerServices.cs` | **同上** |
| `Vc14Reinstall` | 2 | 1 | 见上 | 否 |
| `ProcmanCommands` | 2 | 2 | `Backend.Procman/ProcmanCommands.cs`（处理器）↔ `Contracts/ProcmanCommands.cs`（命令名常量） | 否（不同 NS） |
| `RelayProgress` | 2 | 2 | `Backend.Ace/AceCommands.cs` ↔ `Backend.SystemMaintenance/SystemCommands.cs` | 否（不同 NS） |

**结论**：任务给的 15 组同名类**全部核实为真**，但其中 **11 组是 partial（预期内）**；真正需要关注的是 **`RuntimeGuardService`（同名不同义、易混淆）** 与 **`PowerService`/`ServiceLocator`（同命名空间同名、跨程序集，靠链接关系规避冲突）**。任务给的"Program x9、ProcessPage x6、RuntimePage x5、ProcmanEngine x3、SettingsPage x3、UpdateService x3、ProcmanCommands x2、ServiceLocator x2、SystemTweakService x2、Vc14Reinstall x2、RuntimeGuardService x2、ProcmanCapabilities x2、PowerService x2、FramePage x2、GpuSpoofPage x2" **15 组数量全部精确命中**。

### 3.2 Top 文件（Execution，非空行）

| # | 文件 | 非空行 | 类型 |
|---|---|---|---|
| 1 | `Execution/TweakDb/BxService.cs` | 2286 | 服务实现（注册表/UWP/任务超大类） |
| 2 | `Execution/SystemTweakService.cs` | 1264 | 服务实现 |
| 3 | `Execution/Procman/ProcmanEngine.cs` | 1138 | 服务实现（partial ×3） |
| 4 | `Execution/CpuFrame/FrameTweaksService.cs` | 980 | 服务实现 |
| 5 | `Execution/CpuFrame/CpuTopologyService.cs` | 914 | 服务实现（含静态状态） |
| 6 | `Execution/CpuFrame/CpuFrameEngine.cs` | 882 | 服务实现 |
| 7 | `Native/NvApiDrs.cs` | 743 | P/Invoke |
| 8 | `Execution/Vc14Reinstall.cs` | 668 | 运行库修复（partial ×2） |
| 9 | `Execution/AdvancedTweakService.cs` | 618 | 服务实现 |
| 10 | `Execution/AceVirtualizationCheck.cs` | 606 | 服务实现 |
| 11 | `Execution/ShaderService.cs` | 578 | 服务实现 |
| 12 | `Execution/CpuFrame/OfflineCpuSetScheduler.cs` | 548 | 调度器 |
| 13 | `Execution/AceService.cs` | 537 | 服务实现 |
| 14 | `BackendHost.cs` | 534 | 管道宿主（含 316 行 `ServeAsync`） |
| 15 | `Execution/RuntimeGuardProtection.cs` | 528 | 防护状态机 |

→ 任务给的前 15 组数值**全部命中**（任务列表混入了前端文件，本表为 Execution 内 Top15）。

### 3.3 Execution 根目录 46 文件 → 冒烟测试显式路径清单（29 条，移动文件时必须同步修改）

`tools/BackendSmokeTest/BackendSmokeTest.csproj` 中指向 `Execution/` 根文件的 **29** 条：
`AdvancedTweakService`、`AceService`、`AceCoreFileCleanup`、`AceVirtualizationCheck`、`AceCleanupEnvironment`、`AceRegistrySafety`、`AceCleanupGuard`、`CleanupService`、`DefenderGuard`、`GpuSpoofService`、`GpuSpoofWatch`、`NvProfileService`、`ShaderRecoveryCleanup`、`PowerService`、`RuntimeGuardService`、`RuntimeHighFpsMaintenance`、`RuntimeFileUse`、`RuntimeGuardProtection`、`RuntimeGuardRecoveryReport`、`RuntimeLegacyInstallationReview`、`RuntimeInstallationReview`、`RuntimeGuardBackends`、`ShaderService`、`SystemTweakService`、`ApplicationPreLaunchControl`、`PagefileService`、`ProcessMaintenanceService`、`RegistryAclHelper` + 通配 `Execution\Vc14*.cs`（覆盖 17 个 Vc14 文件）。
另有 5 条 CpuFrame 显式、1 条 TweakDb 显式、3 条通配（`AlphaRestore\*`、`Procman\*`、`Native\*`）、2 条项目根（`BackendEnvironment.cs`、`BackendHost.cs`）。

### 3.4 重复代码量化（最终口径，含基线）

见 §2.5 表。结论摘要：
- **最长连续相同代码块 = 6 行**（`RuntimeGuardService` 与 Alpha 适配器）。
- 剔除琐碎行后的多重集相似度：ACE 清理 **12.3%**（最高）> 同名守卫 **7.6%** > Beta/Strong **6.7%** > 卸载器 **6.2%** > 拓扑读者 **4.1%** > 基线 **3.5–4.3%** > 卸载后端 **2.6%** > catalog **0.6%**（最低）。
- **确定性重复工具单元 7 组、约 186 行**（`FirstLine`×5、`WriteLease`×4、`GROUP_AFFINITY`×4、`AcquireWriteLease`×3、`SplitLines`×3、`GetDirectorySizeMb`×3、`PROCESSOR_NUMBER`×2）。

### 3.5 零测试核心类（54 个零引用类型中剔除 DTO 后的核心逻辑类）

见 §2.4 表（14 个，合计 1173 非空行）。

---

## 4. ④ 问题清单

### P0（安全/正确性，建议本轮处理）

**P0-1 同一运行库信任根两份定义，无一致性断言**
- 现象：14.42 基线 SHA256 同时硬编码在 `Vc14Reinstall.cs:52-56`（`BaselineHashes`）与 `AlphaRestore/RuntimeGuardService.cs:251-256`（`V14InstallerSha256`），当前逐字节相同但无任何机制保证。
- 后果：任一份漂移 → 一边校验通过、一边报"原包损坏"，或校验通过却装错包。
- 证据：`Execution/AlphaRestore/RuntimeGuardService.cs:251-256`、`Vc14Reinstall.cs:52-56`、`:431`、`:321` 的 `Replace("VCRedist2015-2022-", "vc_redist.")` 隐式映射。

**P0-2 硬锁核失败被静默吞噬，UI 仍报"已生效"**
- 现象：`CpuTopologyService.ApplyCpuSchedulingTick` 对每个游戏进程 `try { ApplyHardAffinity(process, mask); } catch { }`。
- 后果：权限不足/进程受保护时亲和性未写入，但 `DualCcdSchedulingActive = true`（:513）与前端 `CpuFrameStatusState` 镜像（`BackendCpuTopologyService.cs:48-49`）仍显示成功。
- 证据：`CpuTopologyService.cs:543`、`:553`、`:512-513`；`BackendCpuTopologyService.cs:25-26/:34/:48-49`。

**P0-3 CCD 调度状态三处存储、无单一事实源**
- 现象：后端静态字段（`CpuTopologyService.cs:156/:159`）+ 引擎 tick 分支（`CpuFrameEngine.cs:176-184/:930-931`）+ 前端镜像（`CpuFrameStatusState`）各存一份。
- 后果：后端重启或部分失败后，前后端状态漂移；离线恢复/退出清理依赖静态字段，跨进程不可见。
- 证据：同上 + `CpuTopologyService.cs:167`（`AffinityRuleOriginals` static）+ `:366/:367`。

### P1（可靠性/可维护性，建议下个迭代处理）

**P1-1 `Execution/` 根目录重构的编译断链风险**
- `tools/BackendSmokeTest/BackendSmokeTest.csproj` 29 条显式路径 + 1 条 `Execution\Vc14*.cs` 通配 → 移动文件必须同步。证据见 §3.3。

**P1-2 `RuntimeRepairPreflight.WaitForInstallerIdle` 同步 2s 轮询**
- `RuntimeRepairPreflight.cs:132-141`：`while` + `Thread.Sleep(2s)`，无 `CancellationToken` 检查、无注入时钟；该文件 234 行且**零测试**。

**P1-3 超大方法集中在安全敏感路径**
- `AceVirtualizationCheck.BuildSystemItems`(202)、`AceService.CleanPhasedAsync`(190)、`SystemTweakService.DisableHyperVAsync`(164)、`Vc14Reinstall.BuildRemovalPlan`(124)。这些方法同时是"写系统状态 + 拼多阶段报告"的巨型 if/foreach，任何分支遗漏都难被发现。

**P1-4 `WriteLease` 4 份且 Dispose 语义已分叉**
- `ProcmanChangeLog.cs:174`/`ProcmanRuleStore.cs:122` 刻意不 Dispose Mutex；`BxService.cs:50` 的 `finally { }` 是空块（也不 Dispose）；`TweakBackupStore.cs:120` 则 `finally { mutex.Dispose(); }`。**4 份里 3 种语义**，统一时若选错方向 → Mutex 提前释放（并发写坏文件）或句柄泄漏。

**P1-5 两个 `IDisposable` 实现在 Dispose 里同步等异步并吞异常**
- `CpuFrameEngine.cs:958` `try { StopMonitorAsync().GetAwaiter().GetResult(); _gamePower.Sync([]); } catch { }`——Dispose 可能死锁（若 Monitor 线程需 UI 上下文）且失败不可见。
- `ProcmanEngine.cs` `Dispose() => _ = StopRulesEngineAsync().GetAwaiter().GetResult()`。

**P1-6 同命名空间同名的跨程序集桩（`PowerService`、`ServiceLocator`）靠链接关系规避冲突**
- `OfflineHelper/OfflineHelperPowerServices.cs` 与 `DeltaNFD/Services/ServiceLocator.cs`、`Execution/PowerService.cs` 同属 `DeltaNFD.Services`。当前 OfflineHelper 不引用主工程故不冲突；一旦有人给 OfflineHelper 加主工程引用 → CS0436。**建议改名为 `OfflinePowerServiceStub`/`OfflineServiceLocator`。**

**P1-7 `RuntimeRepairPreflight`/`RuntimeUninstallCommand`/`RuntimeSxsResolver`/`RuntimeRepairReadiness` 零测试**
- 合计 632 行安全敏感逻辑（安装器互斥 239、卸载命令解析 147、WinSxS 解析 133、修复前置判定 113）无任何断言。

### P2（技术债，可排期）

**P2-1 拓扑 P/Invoke 声明层 4+2 份重复**（`GROUP_AFFINITY`×4、`PROCESSOR_NUMBER`×2、缓冲协商×2），且两处缓冲算法健壮性不一致（`ProcmanCpuTopologyReader.cs:39-61` 有 3 次重试 + 16MB 上限 + 返回长度越界校验；`CpuTopologyService.cs:922-934` 单次、无上限、失败即返回 `(-1, zero, 0)`）。

**P2-2 `GetTopologyAsync` 无结果缓存（但不在热路径，优先级可下调）**：2 秒 tick 的 `ApplyCpuSchedulingTick`（`CpuTopologyService.cs:529`）使用 `RuleAvailableMask` 的 `Lazy`（:161），不重读拓扑；`GetTopologyAsync` 每次完整 `ReadTopology()`（:37）只发生在用户触发的 `ApplyDualCcdSchedulingAsync`（:440）/ `OfflineCpuSetScheduler.ApplyOnceAsync`（:49）/ 实验室刷新。**原判断"tick 下重复读取"经复核不成立**，保留此项仅为"同一进程内多次点击会多次读"的轻微浪费。

**P2-3 工具函数 7 组重复约 186 行**（§2.5 表）。

**P2-4 `SystemTweakService.cs` 同一文件两个 partial 声明**（:7 只放 1 个 `SystemParametersInfo` extern、:30 主类）——无功能影响但误导。

**P2-5 `NvApiDrs` 18 个静态可变字段，`_initialized`/`_available` 与函数指针写入非原子**（:124-199）。

**P2-6 `AceCoreFileCleanup.cs:22` 硬编码 `C:\Program Files\AntiCheatExpert`**，未用 `ProgramFiles` 环境变量。

**P2-7 前端 107 个 `async void` 无统一异常兜底**（`src/DeltaNFD/Views/*`）。

**P2-8 `BxService`（2286 行、static Mutex）不实现 `IDisposable`**。

**P2-9 TODO/XXX 基线数据失真**：给定"2 处 / 1 处"在当前快照为 0 处（3 个假阳性）。建议改为自动化统计（Roslyn 分析器）而非人工数。

---

## 5. ⑤ 建议动作

> 排序原则：先做"零风险、可静态验证"的（1、2），再做"需回归测试护航"的（3、4），最后做"纯重构"（5、6）。人日为单人估算，含自测，不含 code review 往返。

### 动作 1（P0-1）运行库信任根单一化 — 0.5 人日
- **文件**：`Execution/Vc14Reinstall.cs:52-56`、`Execution/AlphaRestore/RuntimeGuardService.cs:251-332`、`tools/BackendSmokeTest/`（新增 `Vc14BaselineHashChecks.cs`）
- **重构方式**：删除 `RuntimeGuardService.V14InstallerSha256`，改为引用 `Vc14Reinstall.BaselineHashes`；把 `:321` 的 `file.Replace("VCRedist2015-2022-", "vc_redist.")` 改为显式常量映射（消除隐式契约）。
- **验收**：新增 `--vc14-baseline-hash-checks`，断言 ①`BaselineHashes` 两值与 `Assets/Vc14Baseline14.42/vc_redist.{x64,x86}.exe` 实际 SHA256 相等 ②FileVersion == `14.42.34433.0` ③`Vc14FileRepairSource.Packages` 的 `OriginalSha256` 与之相等。**无需管理员**。
- **回滚**：纯替换+新增测试文件，`git revert` 单提交。
- **风险**：低（不改行为，仅去重+加断言）。

### 动作 2（P0-2）硬锁核失败可见化 — 1 人日
- **文件**：`Execution/CpuFrame/CpuTopologyService.cs:529-587`（`ApplyCpuSchedulingTick`）、`:434-522`（`ApplyDualCcdSchedulingAsync`）
- **重构方式**：把 `catch { }` 改为累计 `(pid, reason)` 到 `List`，返回值/日志中输出"本次 N 个进程锁核失败（原因示例）"；`ApplyDualCcdSchedulingAsync` 在 `gameApplied == 0` 时**不设** `DualCcdSchedulingActive = true`（或设为 `ActiveWithWarnings`），避免静默假成功。
- **验收**：`--affinity-reliability-checks` 增加"目标进程不可写时返回失败/告警"用例（现有测试已用子进程做目标，可扩展为只读句柄子进程）。**无需管理员**。
- **回滚**：行为变更点集中，单提交 revert。若担心影响用户，可先只加日志不改状态机（拆两次提交）。
- **风险**：中（改变"激活"语义，需与前端 `CpuFrameStatusState` 同步）。

### 动作 3（P1-1 + P2-1 + P2-3）Execution 根目录分域重构 + 原生层去重 — 3 人日
- **文件**：`Execution/*.cs`（46 个）→ 新建 `Execution/Vc14/`、`Execution/Runtime/`、`Execution/Ace/`、`Execution/SystemTweaks/`、`Execution/Gpu/`、`Execution/Power/`、`Execution/Shaders/`；`Execution/Native/`（新建 `Win32Affinity.cs` 承载 `GROUP_AFFINITY`/`PROCESSOR_NUMBER`/缓冲协商）；`tools/BackendSmokeTest/BackendSmokeTest.csproj`（29+1 条路径）
- **重构方式**：①先建 `Native/Win32Affinity.cs` 并让 4 处 `GROUP_AFFINITY`、2 处 `PROCESSOR_NUMBER`、2 处缓冲协商改引用（单独一次提交，零行为变更）②再建 `Services/TextLines.cs`（`FirstLine`/`SplitLines`）与 `Services/DirectoryMetrics.cs`（`GetDirectorySizeMb`）③最后移动目录并同步 csproj（纯移动，单独一次提交）。**不要**合并 `CcdTopologyDetector` 与 `ProcmanCpuTopologyReader.Decode`。
- **验收**：每步后 `dotnet build` 解决方案 + 跑 **受影响的** `--checks`（至少 `--ccd-model-checks`、`--affinity-reliability-checks`、`--procman-topology-checks`、`--system-bx-checks`、`--vc14-*-checks`）。**注意**：本次审计禁止 build，故该动作必须在允许编译的环境执行。
- **回滚**：按 3 次提交分别 revert；纯移动提交可用 `git revert` 干净回退（因为 csproj 同步改）。
- **风险**：中低。唯一硬依赖是 csproj 29 条路径；partial 组（`Vc14Reinstall`、`ProcmanEngine`、`ProcmanCapabilities`）必须整组同目录。

### 动作 4（P1-2 + P1-7）`RuntimeRepairPreflight` 零测试补齐 + 去同步阻塞 — 2 人日
- **文件**：`Execution/AlphaRestore/RuntimeRepairPreflight.cs:132-141`、`tools/BackendSmokeTest/`（新增 `RuntimePreflightChecks.cs`）
- **重构方式**：把 `Thread.Sleep` 与 `DateTime.UtcNow` 抽为 `Func<TimeSpan, CancellationToken, Task>` 延迟 + `TimeProvider`；`InstallerBusy()` 抽为可注入 `Func<bool>`。**行为不变**，只是可测。
- **验收**：新增 `--runtime-preflight-checks`：①空闲立即返回 true ②忙到超时返回 false ③取消时抛 `OperationCanceledException` ④`CheckWinSxSPermissions` 对不存在目录返回既定文案。**无需管理员**（`InstallerBusy` 用 fake）。
- **回滚**：单提交 revert。
- **风险**：低。

### 动作 5（P1-4 + P1-5）`WriteLease` 统一 + Dispose 异步修正 — 1.5 人日
- **文件**：`Shared/Services/TweakBackupStore.cs:120-124`、`Execution/Procman/ProcmanChangeLog.cs:174`、`Execution/Procman/ProcmanRuleStore.cs:122`、`Execution/TweakDb/BxService.cs:50`；`Execution/CpuFrame/CpuFrameEngine.cs:958`、`Execution/Procman/ProcmanEngine.cs`
- **重构方式**：新建 `Shared/Services/NamedMutexLease.cs`（`Acquire(name, timeout)` + `IDisposable`，**统一为"ReleaseMutex 但不 Dispose"**——因为 Mutex 一直活到进程结束是 4 处中 2 处已确认的安全选择，且 Dispose 后 `ReleaseMutex` 会抛 `ObjectDisposedException`）。`Dispose()` 改为 `DisposeAsync()`/`IAsyncDisposable` 或加超时（`GetAwaiter().GetResult()` 改 `Wait(TimeSpan)`）。
- **验收**：`--file-update-checks`、`--procman-*-checks`、`--system-bx-checks` 全绿；新增并发用例（两线程抢同一 Mutex，断言第二个抛"请稍后重试"且不丢锁）。
- **回滚**：单提交 revert（但注意 `WriteLease` 语义变更风险，需在测试环境跑并发用例后再合）。
- **风险**：中（Mutex 语义变更，涉及多进程并存）。

### 动作 6（P1-6 + P2-4 + P2-5 + P2-6）小技术债批处理 — 1 人日
- `OfflineHelper/OfflineHelperPowerServices.cs` 内 `ServiceLocator`/`PowerService` 改名为 `OfflineServiceLocator`/`OfflinePowerServiceCatalog`（改 2 处引用）。
- `Execution/SystemTweakService.cs:7` 的 partial 合并进 :30 主类。
- `Native/NvApiDrs.cs:124-199` 初始化改 `Lazy<T>` 或 `lock` 内一次性赋值。
- `Execution/AceCoreFileCleanup.cs:22` 改 `Path.Combine(Environment.GetFolderPath(SpecialFolder.ProgramFiles), "AntiCheatExpert")`。
- **验收**：`dotnet build` + `--nv-shader-cache-checks` + `--ace-*-checks` + `--phase1-checks`。
- **回滚**：4 个小提交分别可 revert。
- **风险**：低。

### 不建议做（明确反对）
- **不要把 `BaselineHashes` / `RepairPayloadManifestSha256` 抽离到外部 JSON**：抽离即削弱防篡改，与项目多轮"固定哈希清单是刻意设计"的约定冲突。
- **不要合并 `CcdTopologyDetector` 与 `ProcmanCpuTopologyReader.Decode`**：两者服务不同合同，合并会让 CCD 逻辑侵入 `ProcmanContracts`。
- **不要为了消除 `async void` 而改 WinUI 事件签名**：这是框架要求的模式，成本远大于收益。

### 人日合计
0.5 + 1 + 3 + 2 + 1.5 + 1 = **9 人日**（不含 review/回归窗口）。若只做 P0：**1.5 人日**。

---

## 6. ⑥ 未验证事项

1. **全部"能否编译/能否构建"结论未验证**：本会话禁止 `dotnet build`，§2.3/§5 中"csproj 断链""partial 组必须同目录"均为静态推断。**必须在允许编译的环境复验。**
2. **未运行任何冒烟测试**：147 个开关当前是否全部通过未知；`--vc14-dependency-log-replay` 等依赖 `Fixtures/` 与外部原包目录，可能无法在无资源环境跑。
3. **超大方法行号已用"括号配平 + 跳过 `=>`/`;` 结尾行"二次校验**，13 条清单逐条复核过起始行；`OfflineCpuSetScheduler.DiscoverCurrentTargets`（:34 体积仅 2 行表达式体）**不是**大方法，先前启发式误报已在正文剔除。`ShaderService.cs:160 ReadNvidiaDriver`（103 行）未达 150 行阈值，`ShaderService.cs:29 GetStatusAsync`（45 行）同样不达标。**仍建议用 Roslyn/IDE 复验这 13 条。**
4. **`GetTopologyAsync` 的调用频次未实测**：静态阅读确认 2 秒 tick（`ApplyCpuSchedulingTick`）走 `RuleAvailableMask` 缓存、不重读拓扑；但"用户连续点击时实际耗时"未测量（需 PerfView/ETW）。P2-2 已据此从"性能问题"降级为"轻微浪费"。
5. **CCD 静态状态在真实双 CCD 机器上的行为未验证**：本机 CPU 型号未知（未读取），双 CCD 路径（`ApplyDualCcdSchedulingAsync`）可能从未在审计环境执行。
6. **`Assets/Vc14FileRepair14.42` 目录在源码工作区不存在**（实测 `Assets/` 只有 `Acknowledgements`、`RuntimeHighFps`、`TweakDb`、`Vc14Baseline14.42`）。按 AGENTS.md"二进制不入 Git、构建时复制"，这是**预期行为**，但意味着动作 1 的"catalog ↔ 实际文件"断言只能在**发布布局**下跑，源码树只能跑"catalog ↔ BaselineHashes"的部分。
7. **`Assets/RuntimeHighFps` 只有 `Installer.cmd`(35048B) 与 `payload_manifest_sha256.csv`(63768B)**，未逐一核对清单内容与 `RepairPayloadManifestSha256` 是否匹配（需跑哈希，属"读文件"但量大，未做）。
8. **"TODO 2 处、XXX 1 处"来源不明**：当前快照 0 处。可能是其他快照，或把 `AutoDownload`/`Convert.ToDouble(saved)`/`VEN_xxxx` 计入。建议由 Lead 与数据提供方核对口径。
9. **前端 107 个 `async void` 是否逐个有 try/catch 未复核**（只抽查了 `ShaderViewModel.cs:159` 注释）。
10. **API 行为的平台差异未验证**：`GetLogicalProcessorInformationEx` 的 `EfficiencyClass` 语义在 Intel/AMD 上的差异依赖源码注释（`CpuTopologyService.cs:57-61`）与 `CcdModelChecks` 断言，未在真实 Intel 混合架构机器上验证。
11. **`RuntimeGuardService` 适配器（AlphaRestore）的 379 行是否仍有活调用路径未完全确认**：`RuntimeHighFpsMaintenance.cs:3/:63` 仍以 `AlphaService` 别名调用其 `RepairVcRedistAsync`；是否已计划下线未见文档。
12. **未审计**：`src/DeltaNFD.FileUpdater`、`src/DeltaNFD.UpdateCore`、`src/DeltaNFD.BuildTools`（若有）、`installer/`、`server/`——均在本任务范围外。

---

### 附：本报告使用的主要扫描命令口径（可复现）

```powershell
# 文件数 / 非空行（与任务给定口径一致）
Get-ChildItem src\DeltaNFD.Execution -Recurse -Include *.cs |
  Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
  ForEach-Object { (Get-Content $_.FullName | Where-Object { $_ -ne '' }).Count } |
  Measure-Object -Sum            # => Count 104, Sum 25390

# 空 catch 严格口径
(Get-Content $f -Raw) -match 'catch\s*\{\s*\}'

# 重复：非空行多重集交集（剔除 { } using 等琐碎行）
# 最长连续相同块：LCS 动态规划
```
