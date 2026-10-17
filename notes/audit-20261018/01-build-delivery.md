# DeltaNFD 构建与交付审计（只读）— 2026-10-18

> 审计员 **A（只读）**。范围：安装包体积构成、自包含发布布局、Inno Setup 压缩、assets 载荷策略、CI 与解决方案结构。
> 硬约束：**未修改 src/ 与 installer/ 下任何文件；未执行 dotnet build / ISCC / 安装器 / 发布脚本；未联网**。
> 版本基准：Alpha0.9210 / 0.9210（`update.json:3-4` 与 `live-files-0.9210.json` 一致）。
>
> **主要证据**
> - `notes/audit-20261018/live-files-0.9210.json`（814,719 B，= 线上 files.json 原件，1227 项，schemaVersion=1，remove=0）
> - `update.json:14/19`（安装包 248,944,034 B）、`update.json:25`（files.json 814,719 B）
> - `installer/DeltaNFD.iss:66-67/91`、`installer/publish-selfcontained.ps1`、`installer/merge-shared-backends.ps1`
> - `src/DeltaNFD/DeltaNFD.csproj:26/39`、`src/DeltaNFD/obj/project.assets.json`、`%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk*`
> - `.github/workflows/build.yml`（250 行 / 50 step）、`DeltaNFD.sln`（15 条 `Project(` = 2 解决方案文件夹 + 13 个 C# 项目）
>
> **口径**：files.json 的 `sizeBytes` = 解压后（安装目录）字节；`downloadSize` = 增量更新对象 gzip 字节。
> 安装包 248,944,034 B ÷ 布局 546,918,669 B = **45.5%** 的等效压缩比（含 Inno 框架约 1.5–2 MB 固定开销）。
> 本报告的"安装包收益"用**实测 lzma2 比率**推算（见 ③-表 F 的校准实验），标注为估算，误差约 ±10%。

---

## ① 一页摘要（Top5）

### 1）最大单块可削减收益：WindowsAppSDK 1.8 顺带打进来的 AI/ML 载荷 ≈ **15.5 MB 安装包**
根目录 44 个 AI/ML 文件，**解压 45,441,778 B（43.34 MiB）**，其中 `onnxruntime.dll` 20.73 MiB、`DirectML.dll` 17.84 MiB。
**全仓 src 对 `Microsoft.Windows.AI|onnxruntime|DirectML|WindowsAppSDKML|Microsoft.ML.Onnx` 的引用 = 0 条**（`src/**/*.cs,*.csproj,*.xaml,*.json` grep 命中 0），
唯一的 `using Windows.*` 是 `src/DeltaNFD/Views/SettingsPage.xaml.cs:5 using Windows.Storage.Pickers;`（选择文件夹，与 AI 无关）。
我用 python lzma2（preset 9e / 64 MB 字典）实测这组文件：**lzma2 = 15,452,195 B（14.74 MiB）**。
→ 从安装包里删掉这组文件，**预计省 15.5 MB（= 15,452,195 B = 14.74 MiB）**。
**没有任何官方 csproj 开关能只关 AI/ML**（见 ②-断言 2 与 ⑤-A2 的可行替代路径）。

### 2）Backends 不是"6 份运行时"，而是"根目录 1 份 + Backends 1 份"共 2 份
`installer/merge-shared-backends.ps1:11-29` 已把 7 个组件（6 后端 + FileUpdater）的运行时去重合并为**一份**。
Backends/ 233 文件中 **192 个与根目录同名**（合计 75,942,932 B / 72.42 MiB），其中 **186 个 SHA256 逐字节相同**（71.88 MiB），
6 个不同（`DeltaNFD.Contracts/Shared/UpdateCore` 及其 pdb，共 542,656 B，因为它们是同一程序集的两次独立构建产物）。
Backends 真正独有的只有 **41 文件 3.77 MiB**（7 个后端 exe + `DeltaNFD.Execution.dll` + Contracts/Shared/UpdateCore + 少量私有依赖）。
→ 若改成"单宿主进程承载全部后端"或"后端 framework-dependent 共享根目录运行时"，理论上限 **-72.42 MiB 解压 / ≈ -29 MB 安装包**，
但这是**架构级改动**（后端 exe 启动协议、崩溃隔离、`DotNetLayoutCheck` 门禁全部要改），不建议本轮做（P1）。

### 3）Assets 232.03 MiB 里 228.13 MiB（98.32%）是 5 个离线运行库修复载荷目录
`RuntimeCppOfficial` 108.16 MiB ＋ `RuntimeHighFps` 45.09 MiB ＋ `Vc14Baseline14.42` 37.76 MiB ＋ `Vc14OwnershipOriginals` 24.16 MiB ＋ `Vc14FileRepair14.42` 12.95 MiB = **228.13 MiB**。
这 5 个目录全部被 `installer/publish-selfcontained.ps1:104-131` 逐文件哈希**强校验**（缺失即 publish 失败），是刻意的离线修复能力。
关键是它们的**压缩性差异极大**（③-表 B）：`RuntimeCppOfficial` gzip/raw = 97.3%、`Vc14Baseline14.42` 98.7%、`Vc14OwnershipOriginals` 99.0%——这些是微软已签名的 EXE/包，几乎压不动。
→ 想让安装包真正变小，**只能少装/改为按需下载**，不可能靠换压缩算法（见 ②-断言 5）。

### 4）"免费"的零风险瘦身：卫星语言 + PDB + 调试工具链 ≈ **6.7 MB 安装包**（叠加 ①-1 后"保守档"合计 ≈ 22.1 MB）
- 卫星语言：**85 个纯语言目录 + 1 个变体目录（ca-Es-VALENCIA）= 86 目录 / 172 个 `.mui`**，解压 3,530,488 B（3.37 MiB）/ gzip 1,819,376 B。
  只留 `zh-CN + en-us` 可删 **168 文件 3,455,472 B（3.30 MiB）**。这些 `.mui` 来自 `microsoft.windowsappsdk.winui` 包的 `runtimes-framework\win-x64\native\<locale>\`（该包有 **86 个语言目录、688 个 mui**，自包含 targets 按 `**\*.mui` 全量收进来）。
  **全仓 `SatelliteResourceLanguages` 命中 0**——现在根本没设。
- PDB：**15 个文件 1,325,636 B（1.26 MiB）**（根 4 个 = 788,540 B，含 `DeltaNFD.pdb` 657,080 B；Backends 11 个 = 537,096 B）。`[Files] Excludes`（`DeltaNFD.iss:91`）**未排除任何 pdb**。
- 调试/转储工具链：`mscordaccore.dll`、`mscordaccore_amd64_*.dll`、`mscordbi.dll`、`Microsoft.DiaSymReader.Native.amd64.dll`、`createdump.exe`，根目录与 Backends 各一份，**10 文件 12,760,496 B（12.17 MiB）**。
  （另 `System.Diagnostics.EventLog.Messages.dll` 2 份 1,602,160 B **不能删**——`RuntimeRepairDiagnostics.cs:197` 用 `EventLogReader` 读 Application 日志；含它的小计是 12 文件 14,362,656 B / 13.70 MiB。）
- 三项合计 lzma2 ≈ **6.70 MB 安装包**（= 1,661,131 + 575,366 + 4,456,190 B；校准系数见 ③-表 F：卫星 ×0.934、PDB ×0.83、调试工具 ×0.80）；与 ①-1 合并即"保守档" **≈ 22.14 MB / 248.94 → 226.80 MB（216.29 MiB）**。

### 5）压缩层面已经到顶；而且"减小安装包"与"减少日常更新流量"是两个目标
`installer/DeltaNFD.iss:66-67` 已是 `Compression=lzma2/max` + `SolidCompression=yes`。
`ultra64` 与 `max` 的差别只在词典大小，对这些**已压缩载荷（exe/cab/msi，lzma2 只比 gzip 小 8–9%）**收益经验值 **<0.5%（≈1 MB 量级）**，代价是安装期解压内存 +32 MB。
`LZMAUseSeparateProcess` 只是把压缩放到独立进程、绕开 32 位编译器的地址空间限制，**不改变产出字节**。
更重要的区分：站点日常增量流量 = **变化对象的 gzip 之和**，与安装包体积只有弱相关。
本版**最坏情况全量文件更新 = 333,209,453 B（317.79 MiB），比安装包 248,944,034 B 还大 34%**；files.json 自身仅 814,719 B。
→ "瘦身安装包"主要省**首次下载/更新带宽峰值**；"减少日常流量"靠**少改文件 + 内容寻址去重**，两者优化动作不同（见 ②-断言 7）。

---

## ② 断言核实表

| # | 断言 | 判定 | 证据 |
|---|------|------|------|
| **1** | 删除卫星语言目录（`SatelliteResourceLanguages=zh-Hans`）能省多少 MB/多少文件？PDB 能省多少？ | **真（量级小但零风险）** | 卫星：**86 目录 / 172 `.mui` / 3,530,488 B（3.37 MiB）/ gzip 1,819,376 B**；只留 zh-CN+en-us 可删 **168 文件 3,455,472 B（3.30 MiB）/ gzip 1,778,727 B**。PDB：**15 文件 1,325,636 B（1.26 MiB）/ gzip 693,212 B**（根 4 = 788,540 B，Backends 11 = 537,096 B）。`.xml`：**布局内 0 个**，无需处理。全仓 `SatelliteResourceLanguages` 命中 **0**（`src/**` + installer + tools 全 grep）。注意：`.mui` 来源是 `microsoft.windowsappsdk.winui/1.8.260803003/runtimes-framework/win-x64/native/<locale>/`，由 `Microsoft.WindowsAppSDK.SelfContained.targets:147` 的 `**\*.mui` 收进来，**该 targets 不检查 `SatelliteResourceLanguages`**——所以设这个属性对它**无效**，必须在 publish 阶段过滤（见 ⑤-A1）。 |
| **2** | Microsoft.WindowsAppSDK 1.8 自包含引入 AI/ML 二进制（onnxruntime 20.7MB、DirectML 17.8MB、Microsoft.Windows.AI.* 等，合计 38 文件 45.3MB），src 对它们的引用为 0；能否用 csproj 属性排除？ | **部分真**（数字需修正；"不能靠单一属性"） | **数字**：实测 **44 文件 45,441,778 B（43.34 MiB）**解压 / gzip 18,639,376 B，不是 38 文件。核心 5 文件 41,663,416 B（39.73 MiB）：`onnxruntime.dll` 21,734,352 B、`DirectML.dll` 18,710,680 B、`Microsoft.Windows.AI.MachineLearning.dll` 922,880 B、`...Imaging.dll` 765,600 B、`...Text.dll` 734,000 B。引用为 0 **确认为真**：`src/**` grep `Microsoft\.Windows\.AI|onnxruntime|DirectML|WindowsAppSDKML|Microsoft\.ML\.Onnx` = **0 条**。**排除可行性**：<br>① `WindowsAppSDKSelfContained` 只有 true/false 两态，无"只自包含 WinUI"档（`Microsoft.WindowsAppSDK.Base.targets:5-17`）。<br>② `WindowsAppSDKMLPassthroughOnnxRuntime` 与自包含**互斥**：`Microsoft.WindowsAppSDK.ML.targets:34-35` 明写 `WindowsAppSDKSelfContained and WindowsAppSDKMLPassthroughOnnxRuntime cannot both be true`，会直接 Error。<br>③ 组件枚举是 `WindowsAppSdkComponentPackages`（每个组件包的 `build\*.props` 各加一条），AI/ML 是 `Microsoft.WindowsAppSDK` 元包的**直接依赖**（`microsoft.windowsappsdk.nuspec` 明确列 `AI 1.8.79`、`ML 1.8.2197`），**不能被 `ExcludeAssets` 摘掉**（ExcludeAssets 只影响编译/运行时资产选择，不影响该 MetaPackage 的 nuspec 依赖解析；且移除后 `WindowsAppSDKVerifyTransitiveDependencies` 可能报错）。<br>④ **存在官方过滤钩子**：`Microsoft.WindowsAppSDK.SelfContained.targets:154` `<MicrosoftWindowsAppSDKFiles Remove="@(MicrosoftWindowsAppSDKFilesExcluded)"/>`——**MicrosoftWindowsAppSDKFilesExcluded 由本仓自己定义即可**（当前用法命中 0），约定在 `AddMicrosoftWindowsAppSDKPayloadFilesFromComponents` 之后追加 Remove。**但注意**：同目录 `WindowsAppSDK.manifest`/`AppxManifest.xml` 已生成 AI 类注册（`src/DeltaNFD/obj/x64/Release/net8.0-windows10.0.19041.0/win-x64/Manifests/WindowsAppSDK.manifest:963-998` 有 `Microsoft.Windows.AI.Imaging.dll` 等 8 条 AI/ML `<asmv3:file>` + 23 条 AI 激活类），若删 DLL 不同时清 manifest 条目，**sxstrace/激活注册会指向不存在的文件**（Unpackaged 应用的 WinRT 激活依赖 manifest，风险不明，必须实测启动）。→ **可行方案与风险见 ⑤-A2** |
| **3** | Backends 76.2MB 中 6 后端共用同一份 .NET 自包含运行时；核算"单 exe 多实例宿主"或"后端改 framework-dependent + 共享运行时"能省多少 | **真（但已是"合并后的一份"）** | `installer/merge-shared-backends.ps1:11-29` 遍历 `Backends/{ace,gpu,runtime,system,cpuframe,procman,updater}` 7 个组件目录，按相对路径去重并**校验哈希冲突**（`throw "Shared dependency conflict"`），最终写出一份共享布局 → **当前已经是 1 份共享运行时，不是 6 份**。剩余重复是"根目录（WinUI 前端）1 份 + Backends 1 份"：<br>• 同名 192 文件 75,942,932 B（72.42 MiB）<br>• SHA256 完全相同 **186 文件 75,400,276 B（71.88 MiB）**<br>• 不同 **6 文件 542,656 B**（`DeltaNFD.Contracts.dll/.pdb`、`DeltaNFD.Shared.dll/.pdb`、`DeltaNFD.UpdateCore.dll/.pdb`）<br>• Backends 独有 **41 文件 3,957,942 B（3.77 MiB）**<br>• 单看 `System.Private.CoreLib.dll` 就是 12,557,400 B × 2<br>→ 上限 **-72.42 MiB 解压 ≈ -29 MB 安装包**（lzma2 按 0.85×gzip 35,209,381 估）；实际能省的是"删掉 Backends 下那份、让后端从 `..\` 加载"或"改多实例宿主"。**风险**：后端崩溃隔离（当前 6 个独立 exe 是进程级隔离）、deps.json/runtimeconfig 解析（`Backends/*.deps.json` 14 个）、`tools/DotNetLayoutCheck` 会按 `Backends/<name>.runtimeconfig.json` 逐项目验运行时完整性（`tools/DotNetLayoutCheck/Program.cs:15-17`），`Backends/DeltaNFD.FileUpdater.exe` 还要能被复制成独立 worker 运行（`installer/FILE-UPDATES.md:52`）→ **不建议本轮动** |
| **4** | Assets 232MB 里 228MB 是 5 个运行库修复载荷目录；各自代码使用条件、能否按需下载、分档方案 | **真** | 5 目录合计 **239,208,667 B（228.13 MiB）**，占 Assets 232.03 MiB 的 **98.32%**。代码使用条件（全部是**离线修复时读取**，不是启动必需）：<br>• `RuntimeCppOfficial` → `src/DeltaNFD.Execution/Execution/AlphaRestore/RuntimeCppOverwrite.cs:9` + `RuntimeHighFpsMaintenance.cs:25`「全量修复预览即 VerifyCppPayload」，`RuntimeCppOfficial.cs` 加载嵌入 catalog（10 包）<br>• `RuntimeHighFps` → `RuntimeBetaRepair.cs:142/143`、`RuntimeRepairEnvironment.cs:10`，`RuntimeHighFpsMaintenance.cs:22-24` 预览前 VerifyRepairPackage（要求 `payload_manifest_sha256.csv` 375 行 + `Installer.cmd`）<br>• `Vc14Baseline14.42` → `Vc14Reinstall.cs:456 Baseline(arch)`，安装/修复 14.42 基线时使用<br>• `Vc14OwnershipOriginals` → `Vc14DependencyPolicy.cs:213` + `Vc14OwnershipOriginals.cs:22-41`，**仅作"归属证据"，从不安装/执行**（注释明写 `These historical packages are never installed or executed`）；缺失时 `Resolve()` 返回原 owners、**不抛异常**（`Vc14OwnershipOriginals.cs:39-40,48-50`）→ **唯一可以"降级但不断功能"的载荷**<br>• `Vc14FileRepair14.42` → `Vc14FileRepair.cs:37-72` + `Vc14Reinstall.cs:572-591`，14.42 普通修复失败后的强制文件重装备份路径<br>分档与预计体积见 ⑤-A3/表 F |
| **5** | 安装包压缩：`lzma2/max` 已是最高档，改 `ultra64` 与 `LZMAUseSeparateProcess` 是否有实际收益；`[Files] Excludes` 是否还需补 pdb/xml | **部分真** | `DeltaNFD.iss:66` `Compression=lzma2/max`、`:67` `SolidCompression=yes` **已是最强组合**，正确。<br>• `ultra64` vs `max`：差别只在 64 MB 词典。本包 521.63 MiB 整体固实块，**词典 32→64 MB 的增量收益经验上 <0.5%**（≈1 MB），安装期解压内存 +≈32 MB；**非零但需实测**（本机未装 ISCC，无法编译验证 → 列入⑥）。<br>• `LZMAUseSeparateProcess`：把 LZMA 压缩放到独立 `compil32` 子进程，仅解决**编译期**内存/地址空间，**不改变产出字节**。若当前本地/CI 编译不 OOM，则收益为 0。<br>• PDB：**需补**（`DeltaNFD.iss:91` Excludes 只列 6 条退役 Assets）。`.xml`：**布局内 0 个**，无需补。注意 Inno `Excludes` 的通配符对 `recursesubdirs` 的路径匹配语义易踩坑（`*.pdb` 是否覆盖子目录需实测），**更稳的做法是在 publish 阶段剔除**（见 ⑤-A1）。 |
| **6** | `DeltaNFD.sln` 项目数能否从 13 精简；tools/ 与 web/ 的维护成本；根目录脚本/笔记归档风险（CI 硬编码引用 = 0，需复核 installer 与 docs） | **部分真（给定数据有 3 处需修正）** | **sln 实际内容**（`DeltaNFD.sln:6-34`，15 条 `Project(` = 2 个解决方案文件夹 `src`/`tools` + **13 个 C# 项目**）：<br>DeltaNFD、BackendSmokeTest、GpuBackupTool、OfflineHelper、Contracts、Execution、Backend.Ace、Backend.Gpu、Backend.Runtime、Backend.SystemMaintenance、Backend.CpuFrame、Backend.Procman、Shared。<br>**修正 1**：**UpdateCore、FileUpdater、2 个插件样例都不在 sln 里**（给定数据"13 = 1 主 + 6 后端 + Contracts/Execution/Shared/UpdateCore/FileUpdater/OfflineHelper + 2 tools + 2 插件"不成立）。正确拆解 = 1 主 + 6 后端 + Contracts/Execution/Shared + OfflineHelper + 2 tools。<br>**修正 2**：`tools/` 下是 **9 个 csproj**，其中 **8 个无版本**（正确），但 `BackendSmokeTest` **有版本 0.9210/Beta0.921** → "tools 下 8 个项目无版本"应表述为"9 个中 8 个无版本"。<br>**修正 3**：全仓 **24 个 csproj**，`<Version>0.9210</Version>` 共 **14 个**（13 src 产品 + BackendSmokeTest）；2 个插件样例是 `1.0.0`。<br>**是否可精简**：sln 里 13 个**都是实际参与者**（`BackendSmokeTest` 50 个 step 里被调用 40+ 次；`GpuBackupTool` 仅 `backup` 功能），**真正"游离"的是 sln 外未纳入的 7 个 tools 项目**（BxStateCheck、DotNetLayoutCheck、EmergencyPolicyChecks、EmergencyPolicyPublisher、FramePacingResearch、JsonVerify、NvProbe）——其中 **DotNetLayoutCheck 是 publish 的强制门禁**（`publish-selfcontained.ps1:133-137`）却不在 sln，属结构不一致（P2）。<br>**维护成本**：`web/` = VitePress 文档站（10 文件 / 157,317 B / `sync-assets.mjs`），**不参与 C# 构建**，成本低；`tools/` = 9 csproj + 40 个 ps1，仅 `BackendSmokeTest`/`DotNetLayoutCheck`/`EmergencyPolicyChecks`/`EmergencyPolicyPublisher` 有 CI/发布引用，其余（JsonVerify、NvProbe、BxStateCheck、FramePacingResearch、FramePacingLab、GpuBackupTool）引用为 0 或仅 README。<br>**归档风险**：根目录 6 个调试脚本（acecore-check/diag/experiment/loadtest/modlist/monitor）**全仓引用 = 0**（对 `*.ps1,*.cmd,*.md,*.yml,*.iss,*.csproj,*.sln` 全量 grep，排除 notes/_buildcheck/retired）→ **确认可安全归档**；`notes/` **23 文件 / 932,707 B**，无 CI/installer/docs 引用。 |
| **7** | 安装包体积与 GitHub 更新/自建服务器带宽是两个不同目标 | **真** | 增量更新只下载 `downloadSize` 之和的最小变化集；**最坏情况（全量文件更新）333,209,453 B = 317.79 MiB > 安装包 248,944,034 B（+34%）**。日常典型场景是若干变化对象（例：只改主程序 = `DeltaNFD.dll` 844,625 B + `DeltaNFD.exe` 251,080 B + `DeltaNFD.pri` 400,645 B ≈ 1.4 MiB）。files.json 自身 814,719 B（`update.json:25`）**每次检查都可能被下载**（`Cache-Control: no-store`，见 `installer/FILE-UPDATES.md:44`）。→ 两个目标正交：<br>• 减安装包 = 少装（删载荷/去重），影响**新装用户首次下载**与整包回退带宽<br>• 减日常流量 = 少改文件 + 内容寻址去重（对象按原始文件 SHA256 命名，天然去重）+ 不要让 files.json 膨胀<br>注意**反向效应**：删掉 PDB/卫星语言反而会**减小**日常流量（files.json 少 180+ 条对象），但**老用户不会自动删除已装的这些文件**，必须靠 `--previous-files` 生成 remove 条目（当前 `remove:0`）。 |

---

## ③ 实测数据表

### 表 A：布局总览（源：`live-files-0.9210.json`）

```text
version=0.9210  displayVersion=Beta0.921  channel=beta  schemaVersion=1  remove(条目数)=0
```

| 顶层 | 文件数 | 解压字节 | 解压 MiB | gzip 字节 | gzip MiB | 占比(解压) |
|------|--------|----------|----------|-----------|----------|-----------|
| `Assets/` | 455 | 243,300,554 | 232.03 | 210,813,444 | 201.05 | 44.5% |
| （根目录，无 `/`） | 345 | 219,716,743 | 209.54 | 85,156,427 | 81.21 | 40.2% |
| `Backends/` | 233 | 79,900,874 | 76.20 | 35,209,381 | 33.58 | 14.6% |
| 其余（86 语言目录 + NpuDetect + Views + licenses + Tools + `Microsoft.UI.Xaml/`） | 194 | 4,000,498 | 3.82 | 2,030,201 | 1.94 | 0.7% |
| **合计** | **1,227** | **546,918,669** | **521.63** | **333,209,453** | **317.79** | 100% |

> 安装包 **248,944,034 B（237.41 MiB）**（`update.json:14/19`）→ 对 521.63 MiB 布局等效压缩比 **45.5%**。

### 表 B：Assets 明细（455 文件 / 232.03 MiB / 228.13 MiB 是修复载荷）

| 子目录 | 文件数 | 解压字节 | 解压 MiB | gzip 字节 | gzip MiB | gzip/raw | 代码使用点 |
|--------|--------|----------|----------|-----------|----------|----------|-----------|
| `RuntimeCppOfficial` | 38 | 113,410,631 | 108.16 | 110,348,593 | 105.24 | **97.3%** | `RuntimeCppOverwrite.cs:9`、`RuntimeHighFpsMaintenance.cs:25`、catalog 10 包（publish:123-131 校验） |
| `RuntimeHighFps` | 376 | 47,285,412 | 45.09 | 19,697,405 | 18.78 | 41.7% | `RuntimeBetaRepair.cs:142`、`RuntimeRepairEnvironment.cs:10`；375 payload + 1 csv（publish:88 强制 375 行） |
| `Vc14Baseline14.42` | 2 | 39,597,656 | 37.76 | 39,093,516 | 37.28 | **98.7%** | `Vc14Reinstall.cs:456`；`DeltaNFD.csproj:97` 直接 Content |
| `Vc14OwnershipOriginals` | 1 | 25,337,776 | 24.16 | 25,094,401 | 23.93 | **99.0%** | `Vc14DependencyPolicy.cs:213`、`Vc14OwnershipOriginals.cs:22`（**仅证据，不执行**；缺失可降级） |
| `Vc14FileRepair14.42` | 8 | 13,577,192 | 12.95 | 13,138,383 | 12.53 | **96.8%** | `Vc14FileRepair.cs:37`、`Vc14Reinstall.cs:572`（publish:114-122 校验 4 包） |
| Assets 根文件（5 个） | 5 | 2,525,517 | 2.41 | 2,291,732 | 2.19 | 90.7% | `background.png` 1,722,681、`FeedbackGroupQr.png` 672,222、`DeltaNFD.png` 120,268、`NoPowerSave-Scheme.pow` 8,192、`UpdateConfig.json` 2,154（`DeltaNFD.csproj:78-95/124`） |
| `Acknowledgements` | 9 | 1,122,026 | 1.07 | 1,103,357 | 1.05 | 98.3% | `DeltaNFD.csproj:89-90` |
| `TweakDb` | 16 | 444,344 | 0.42 | 46,057 | 0.04 | 10.4% | `DeltaNFD.csproj:72` |
| **小计（5 载荷）** | **425** | **239,208,667** | **228.13** | — | — | — | 占 Assets **98.32%** |

### 表 C：卫星语言 / PDB / XML / winmd（"可直接删"候选）

| 项 | 文件数 | 解压字节 | 解压 MiB | gzip 字节 | gzip MiB | 备注 |
|----|--------|----------|----------|-----------|----------|------|
| 语言目录 `.mui`（**86 目录**） | 172 | 3,530,488 | 3.37 | 1,819,376 | 1.74 | 85 个纯 locale + `ca-Es-VALENCIA`（变体名，正则易误判） |
| ├ 保留 `zh-CN` + `en-us` | 4 | 75,016 | 0.07 | 40,649 | 0.04 | 每目录只有 `Microsoft.ui.xaml.dll.mui` + `Microsoft.UI.Xaml.Phone.dll.mui` |
| └ **可删除** | **168** | **3,455,472** | **3.30** | **1,778,727** | **1.70** | |
| `.pdb` | 15 | 1,325,636 | 1.26 | 693,212 | 0.66 | 根 4 = 788,540 B（`DeltaNFD.pdb` 657,080、`Contracts.pdb` 67,500、`Shared.pdb` 45,508、`UpdateCore.pdb` 18,452）；Backends 11 = 537,096 B |
| `.xml` | **0** | 0 | 0 | 0 | 0 | 布局内不存在 → `[Files] Excludes` **不需要补 xml** |
| `.winmd`（WinRT 元数据） | 46 | 2,726,976 | 2.60 | 1,021,523 | 0.97 | 自包含激活注册必需（`WindowsAppSDK.manifest:963-998`），**不建议删** |

> 卫星语言来源核实：`%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk.winui\1.8.260803003\runtimes-framework\win-x64\native\` 下确认 **86 个语言目录 / 688 个 `.mui`**；
> 自包含 targets `Microsoft.WindowsAppSDK.SelfContained.targets:147` 按 `**\*.mui` 收集，**不读 `SatelliteResourceLanguages`**。

### 表 D：Backends 与根目录的两份运行时

| 项 | 文件数 | 字节 | MiB | gzip 字节 |
|----|--------|------|-----|-----------|
| Backends 全部 | 233 | 79,900,874 | 76.20 | 35,209,381 |
| ├ 与根目录**同名** | 192 | 75,942,932 | 72.42 | 33,648,879 |
| │　├ SHA256 **完全相同** | 186 | 75,400,276 | **71.88** | — |
| │　└ 不同（自研程序集的两次构建） | 6 | 542,656 | 0.52 | — |
| └ Backends **独有** | 41 | 3,957,942 | 3.77 | 1,560,502 |
| `System.Private.CoreLib.dll` | 2 份（13,174,608 × 2） | 26,349,216 | 25.13 | — |
| 7 后端 exe + `FileUpdater.exe`（各 152,064 B） | 7 | 1,064,448 | 1.02 | — |

> 6 个不同项：`DeltaNFD.Contracts.dll/.pdb`、`DeltaNFD.Shared.dll/.pdb`、`DeltaNFD.UpdateCore.dll/.pdb`（同源不同构建，尺寸相同、哈希不同）。

### 表 E：WindowsAppSDK 1.8 组件归因（根目录 345 文件）

| NuGet 组件包 | 版本 | win-x64 native 文件 | 命中布局 | 布局字节 | MiB | gzip MiB |
|--------------|------|---------------------|----------|----------|-----|----------|
| `Microsoft.WindowsAppSDK.ML` | 1.8.2197 | 4 | **4** | 41,418,928 | **39.50** | 16.29 |
| `Microsoft.WindowsAppSDK.WinUI` | 1.8.260803003 | 182 | 8（+86 语言目录另计） | 29,006,864 | 27.66 | 10.65 |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 1.8.260708001 | 17 | 17 | 14,303,024 | 13.64 | 5.16 |
| `Microsoft.WindowsAppSDK.AI` | 1.8.79 | 31 | 27 | 3,295,346 | 3.14 | 1.18 |
| `Microsoft.WindowsAppSDK.DWrite` | 1.8.25122902 | 1 | 1 | 3,098,184 | 2.95 | 1.24 |
| `Microsoft.WindowsAppSDK.Foundation` | 1.8.260803002 | 7 | 6 | 2,945,112 | 2.81 | 1.05 |
| `Microsoft.WindowsAppSDK.Widgets` | 1.8.251231004 | 1 | 1 | 2,404,896 | 2.29 | 0.61 |
| `Microsoft.WindowsAppSDK.Base / .Runtime` | — | 0（仅 build targets） | 0 | 0 | 0 | 0 |

> 包体量参考（缓存实测）：`microsoft.windowsappsdk.runtime` **287.47 MiB**、`microsoft.windowsappsdk.winui` **221.61 MiB**、`microsoft.windowsappsdk.ml` **165.81 MiB**、`...foundation` 21.02 MiB、`...ai` 14.55 MiB——1.8 的组件化拆分**没有**"只要 WinUI"的元包档位。
> 另有 281 个根目录文件（123,244,389 B / 117.5 MiB）不属于任何组件 native 目录，主要是 `Microsoft.Windows.SDK.NET.dll`（24,877,600 B，来自 `microsoft.windows.sdk.net.ref`）、`Microsoft.WinUI.dll`（7.3 MiB）、.NET BCL 与 `DeltaNFD.dll`。

### 表 F：AI/ML 载荷与 lzma2 校准实测（python 3.12.3 + `lzma` preset 9e / 64 MB 字典，gzip level 9）

| 样本 | raw 字节 | gzip | lzma2 | lzma2/raw | lzma2/gzip |
|------|----------|------|-------|-----------|-----------|
| `onnxruntime.dll` | 21,734,352 | 7,108,881 | **4,942,131** | 22.7% | 69.5% |
| `DirectML.dll` | 18,710,680 | 9,462,191 | **8,925,819** | 47.7% | 94.3% |
| `Microsoft.WindowsAppRuntime.dll` | 2,360,160 | 816,653 | 603,700 | 25.6% | 73.9% |
| `Microsoft.ui.xaml.dll`（WinUI，15.08 MB） | 15,083,832 | 5,631,243 | 4,148,211 | 27.5% | 73.7% |
| `vc_redist.x64.exe`（14.42 基线，已签名压缩包） | 25,640,112 | 25,389,199 | 23,280,858 | **90.8%** | 91.7% |
| **AI/ML 整组（从 NuGet 包取 43/44 文件，缺 1 个 57,384 B 的小文件）** | 45,384,394 | 18,521,476 | **15,452,195** | **34.0%** | **83.4%** |
| 卫星 mui 样本（每目录 2 个） | — | — | — | — | **93.4%** |
| PDB 样本（4 个，661,320 B） | 661,320 | 307,239 | 255,090 | — | **83.0%** |
| 调试工具样本（3 个，2,658,800 B） | 2,658,800 | 1,135,451 | 908,554 | — | **80.0%** |

> **关键结论**：exe/cab/msi 类"已压缩"载荷的 lzma2 **仅比 gzip 小 8–9%**（`vc_redist.x64.exe` 91.7%），
> 所以 5 个修复载荷（占解压 228 MiB）在安装包里**本来就压不动**——它们贡献的 105+37+24+13 MiB gzip 几乎 1:1 进安装包。
> 反之 DLL/程序集的 lzma2 比 gzip 再小 17–30%，这是"删 DLL 类文件"收益高于直觉的原因。

### 表 G：分档瘦身估算（用上表 lzma2 比率，相对 248,944,034 B = 248.9 MB / 237.4 MiB）

| 档位 | 动作 | 减掉的解压字节 | 预计省安装包 | 预计安装包 |
|------|------|----------------|--------------|-----------|
| **保守** | 删 AI/ML（15,452,195 B 实测）+ 卫星语言（1,778,727×0.934=1,661,131）+ PDB（693,212×0.83=575,366）+ 调试工具 10 文件（5,570,238×0.80=4,456,190） | 62,983,382 B / 60.06 MiB | **22,144,882 B ≈ 22.1 MB** | **≈ 226.8 MB**（216.3 MiB） |
| **中档** | 保守 + `Vc14OwnershipOriginals`（gzip 25,094,401×0.917≈23,011,566）改为按需下载 | 88,321,158 B / 84.23 MiB | **45,156,448 B ≈ 45.2 MB** | **≈ 203.8 MB**（194.3 MiB） |
| **激进** | 中档 + `RuntimeCppOfficial`（110,348,593×0.90≈99,313,734）+ `RuntimeHighFps`（19,697,405×0.85≈16,742,794）改按需下载 | 249,017,201 B / 237.48 MiB | **161,212,976 B ≈ 161.2 MB** | **≈ 87.7 MB**（83.7 MiB） |
| 结构档（P1，不进本表） | Backends 共享根目录运行时 | -75,942,932 B / -72.42 MiB | ≈ -29 MB | — |

> 估算不确定性：solid 压缩下移除中等体积文件的实际收益通常**略低于**线性相加（共享字典损失），误差约 ±10%。
> 保守档的 22.14 MB 中，**15.45 MB 来自一处纯"无用载荷"删除**，几乎无争议。

### 表 H：增量更新流量基线（`downloadSize`）

| 项 | 字节 | MiB |
|----|------|-----|
| 全部 1,227 个对象之和（**最坏全量文件更新**） | 333,209,453 | 317.79 |
| ├ Assets | 210,813,444 | 201.05 |
| ├ 根目录 | 85,156,427 | 81.21 |
| ├ Backends | 35,209,381 | 33.58 |
| └ 其余 | 2,030,201 | 1.94 |
| files.json 自身（`update.json:25`） | 814,719 | 0.78 |
| 安装包（整包路径，`update.json:14`） | 248,944,034 | 237.41 |
| 典型小改（只换主程序 3 件） | 1,496,350 | 1.43 |

---

## ④ 问题清单

> 收益 = 预计安装包减小；风险 = 回归/兼容风险；成本 = 改动面与验收成本。

### P0（本轮就该做，收益明确、风险低）

| ID | 问题 | 收益 | 风险 | 成本 |
|----|------|------|------|------|
| **P0-1** | **AI/ML 载荷 44 文件 43.34 MiB 随包但零引用**（`onnxruntime.dll` + `DirectML.dll` = 38.57 MiB）。当前**无任何排除机制**：`MicrosoftWindowsAppSDKFilesExcluded` 全仓命中 0；`WindowsAppSDKSelfContained` 无 AI/ML 档；ML 的 Passthrough 与自包含互斥。 | **-15.45 MB**（lzma2 实测 15,452,195 B = 14.74 MiB） | **中**：`WindowsAppSDK.manifest`（111,980 B，25 条 `<asmv3:file>`）里 **8 条是 AI/ML/Workloads/ProxyStub**、23 条 `Microsoft.Windows.AI.*` 激活类（`src/DeltaNFD/obj/x64/Release/net8.0-windows10.0.19041.0/win-x64/Manifests/WindowsAppSDK.manifest:963-998`）；`AppxManifest.xml` 同源，删除 DLL 时若不清理会指向不存在文件。Unpackaged + `WindowsPackageType=None` 下该 manifest 会嵌入 exe（`app.manifest` + 合并），**必须实测启动/设置页/文件选择器**。 | 小：1 个 csproj ItemGroup + publish 断言；验收 = 冒烟启动 + 6 后端只读 + DotNetLayoutCheck |
| **P0-2** | **PDB 15 文件 1.26 MiB 随包**，`[Files] Excludes`（`DeltaNFD.iss:91`）未排除；同时它们还进了 files.json（15 条对象 / 693,212 B 日常更新流量）。 | **-0.58 MB** 安装包；-0.66 MiB 更新流量 | **低**：无运行时依赖（`DotNetLayoutCheck` 只查 `.runtimeconfig.json`/`.deps.json`；`DeploymentLayoutChecks.cs` 不查 pdb）。唯一注意：老用户已装的 pdb 需 remove 条目。 | 小：publish 脚本加一个 Remove/Delete 循环 + `--previous-files` 迁移 |
| **P0-3** | **86 个卫星语言目录 172 文件 3.37 MiB 随包**，其中 168 文件（3.30 MiB）对中文用户无用。`SatelliteResourceLanguages` 未设，且**对该来源无效**（见 ②-断言 1）。 | **-1.66 MB** 安装包；-1.70 MiB 更新流量 | **低**：WinUI 资源找不到对应语言时回退到 `zh-CN`/`en-us`；需验证非中英文系统下 UI 不回退异常（仅影响非中文用户）。 | 小：publish 阶段白名单过滤 + 断言 |
| **P0-4** | **调试/转储工具链 10 文件 12.17 MiB × 2 份**（根 + Backends）：`mscordaccore*`、`mscordbi.dll`、`Microsoft.DiaSymReader.Native.amd64.dll`、`createdump.exe`。产品运行不需要 crash dump 调试器（`createdump.exe` 仅在进程崩溃写 dump 时由运行时调用，可选）。 | **-4.46 MB** 安装包；-5.31 MiB 更新流量 | **中低**：丢失的是"崩溃时可产出/读取 minidump"的运维能力，产品功能无影响；`System.Diagnostics.EventLog.Messages.dll` **不能删**（`RuntimeRepairDiagnostics.cs:197` 用 `EventLogReader` 读 Application 日志做运行库诊断）。 | 小：publish 排除清单 + 保留 EventLog.Messages |

### P1（结构性问题，需单独排期）

| ID | 问题 | 收益 | 风险 | 成本 |
|----|------|------|------|------|
| **P1-1** | **Backends 与根目录各带一份 .NET 运行时**（同名 192 文件 72.42 MiB，186 个哈希相同）。 | **≈ -29 MB** | **高**：6 后端当前是独立进程（崩溃隔离），改共享运行时需动 `Backends/*.runtimeconfig.json`、`DotNetLayoutCheck` 逐项目门禁（`Program.cs:15-17`）、`FileUpdater` 复制自身依赖成 worker（`FILE-UPDATES.md:52`）、以及 `DeploymentLayoutChecks.cs:52-56` 的布局断言。 | 大：架构提案 + 全量后端只读通信回归 |
| **P1-2** | **5 个修复载荷 228.13 MiB 全量随包**，其中 `Vc14OwnershipOriginals`（24.16 MiB）**仅作证据、从不执行**，缺失可降级（`Vc14OwnershipOriginals.cs:39-40/48-50` 返回不带证据而非抛错）。 | **-23.0 MB**（23,011,566 B / 21.95 MiB） | **中**：失去"自带归属原包"能力后，`2d507699-...` 这类孤立引用的核验会退回"无法证明归属"（历史上是用户反馈的真实痛点）。适合做成"首次使用时按需下载 + 固定 SHA256 校验"。 | 中：需新增下载器 + 缓存目录 + 离线提示；`publish-selfcontained.ps1:106-113` 强制校验要改成"可选" |
| **P1-3** | **`RuntimeCppOfficial` 108.16 MiB + `RuntimeHighFps` 45.09 MiB 全量随包**，但两者压缩性天差地别（97.3% vs 41.7%）。 | 激进档合计 **≈ -110.7 MB** | **高**：全量修复是核心卖点，改为按需下载会引入"没网就修不了"。可行折中：只把 `RuntimeCppOfficial` 里 2005–2013 的 10 个原包按需化（用户点"全量修复"时才下），并保留 14.42 双架构基线。 | 大：产品交互 + 下载/校验/断网降级 + 发布材料体积 |
| **P1-4** | **打包链路无 CI 覆盖**：`.github/workflows/build.yml` 250 行 / 50 step 中 **`ISCC`、`make-installer.cmd`、`publish-selfcontained.ps1` 命中 0**；CI 只 build 主程序与各工具，**从不生成 publish 布局与安装包**。同时本机 **Inno Setup 6 未安装**（`%LOCALAPPDATA%\Programs`、`Program Files (x86)`、`Program Files` 三处均无 `ISCC.exe`）→ 安装包可编译性**仅靠人工**。 | 非体积，属交付风险 | **高**：布局/压缩/Excludes 改动无法自动回归，只能靠发布时手测。 | 中：新增非阻塞 job（publish + `ISCC /DValidateOnly`）+ 上传布局体积报告 |

### P2（清理与一致性，低成本高整洁度）

| ID | 问题 | 收益 | 风险 | 成本 |
|----|------|------|------|------|
| **P2-1** | **sln 与真实项目集不一致**：`UpdateCore`、`FileUpdater`、2 个插件样例、7 个 tools 项目均不在 `DeltaNFD.sln`（其中 `DotNetLayoutCheck` 是 publish 的**强制门禁**）。`dotnet build DeltaNFD.sln` 不会构建它们。 | 无（一致性） | **低**：补齐 sln 或明确"按路径构建"的约定；补 sln 后 CI 可多一个"全 sln 可构建"检查 | 小 |
| **P2-2** | **根目录 6 个调试脚本 + `notes/` 23 文件 932,707 B 无任何引用**（全仓 grep 命中 0，含 installer/docs/CI）。 | 无（仓库整洁） | **低**：归档到 `retired/` 或 `tools/dev-scratch/`；`notes/` 是决策记录，建议保留但加 README 索引 | 小 |
| **P2-3** | **`[Files] Excludes` 语义脆弱**：`DeltaNFD.iss:91` 的 6 条 `Assets\...` 通配对 `recursesubdirs` 行为正确，但若新增 `*.pdb` 这类"跨层通配"，Inno 的 `*` 是否跨越反斜杠需实测；一旦写错会静默漏排或误排。 | 无（防错） | **低**：推荐改为 publish 阶段剔除，iss 保持"不排除" | 小 |
| **P2-4** | **`remove:0`**：删文件类瘦身（PDB/卫星/调试工具）若不发 remove 条目，**已装用户磁盘上会永久残留**，且下次 files.json 比对时会因"清单里没有、本地有"而被忽略。 | 影响瘦身实效 | **低**：`installer/FILE-UPDATES.md:25` 明确支持 `--previous-files`，需在下次发布生成 remove 条目 | 小 |
| **P2-5** | **版本号需改 14 处**（13 src 产品 + `tools/BackendSmokeTest`），且 DocFX 版本在 `update.json`、`DeltaNFD.iss:17-18`、各 csproj 中分别硬编码，无单一真源。 | 无 | **低**：可引入 `Directory.Build.props` 顶层版本变量（当前 `src/` 下唯一的 `Directory.Build.props` 是 `src/DeltaNFD.OfflineHelper/Directory.Build.props`，只重定向输出目录，无语言/PDB 裁剪）。 | 中 |

---

## ⑤ 建议动作

> 每条：**文件 → 改动要点 → 验收 → 回滚 → 预计体积收益**。
> 全部为**建议**，审计期内未执行任何修改。

### A1（P0-2 + P0-3）在 publish 阶段剔除 PDB 与卫星语言，而不是改 iss
- **文件**：`installer/publish-selfcontained.ps1`（在 `[3/3] Copy WinUI resources` 之后、`merge-shared-backends.ps1` 之前插入）
- **改动要点**：
  1. 遍历 `$componentLayout`，删除所有 `*.pdb`（根 + `Backends/`），并计数断言 `-eq 15`（当前实测 15 个，若数量变化说明构建配置漂移，应失败）。
  2. 只保留 `zh-CN`、`en-us` 两个语言目录；删除其余 84 个（白名单而非黑名单，避免 `ca-Es-VALENCIA` 这类变体名误判），断言删除文件数 `-eq 168`。
  3. 在 `$required` 里新增"`DeltaNFD.pdb` 不存在"与"`de-DE` 不存在"的反向断言。
  4. 同步在 `installer/DeltaNFD.iss:91` **保持现状**（不新增 Excludes），把过滤集中在一处。
- **验收**：publish 成功且 `DotNetLayoutCheck` 通过；`files.json` 少 183 条对象；安装后启动 + 设置页切换语言（zh-CN/en-US）UI 正常；`BackendSmokeTest` 六后端只读通信通过。
- **回滚**：单文件回退 `publish-selfcontained.ps1`（无其他依赖），重跑 publish。
- **收益**：**≈ -2.24 MB 安装包**（卫星 1,661,131 + PDB 575,366 = 2,236,497 B）；更新流量 **-2.36 MiB**。

### A2（P0-1）用官方过滤钩子摘除 AI/ML，并同步清理 manifest
- **文件**：`src/DeltaNFD/DeltaNFD.csproj` + 新增一个 `Directory.Build.targets`（或直接在同 csproj 内联 Target）+ `installer/publish-selfcontained.ps1`
- **改动要点**：
  1. `DeltaNFD.csproj` 显式 `<PackageReference Include="Microsoft.WindowsAppSDK.AI" ... ExcludeAssets="all" />` 与 `...ML` 双保险（即使元包仍拉取，也让编译资产不进来——注意：这**不能**阻止 native 自包含复制，仅减少依赖图噪声）。
  2. 新增 Target，`BeforeTargets="AddMicrosoftWindowsAppSDKPayloadFilesFromComponents;AssignTargetPaths"`：
     ```xml
     <ItemGroup>
       <MicrosoftWindowsAppSDKFilesExcluded Include="$(MicrosoftWindowsAppSDKPackageDir)../microsoft.windowsappsdk.ml/**/*.dll" />
       <MicrosoftWindowsAppSDKFilesExcluded Include="$(MicrosoftWindowsAppSDKPackageDir)../microsoft.windowsappsdk.ai/**/*.dll" />
     </ItemGroup>
     ```
     利用 `Microsoft.WindowsAppSDK.SelfContained.targets:154` 的 `<MicrosoftWindowsAppSDKFiles Remove="@(MicrosoftWindowsAppSDKFilesExcluded)"/>`（该钩子当前全仓未使用）。
  3. **关键**：`WindowsAppSDK.manifest`/`AppxManifest.xml` 由同一 targets 通过 `MicrosoftWindowsAppSDKAppxFragmentFiles` 生成；需在生成后对 manifest 做 XML 级清理（删 `Microsoft.Windows.AI.*`/`Microsoft.Graphics.Imaging`/`Workloads`/`SessionHandleIPCProxyStub` 的 `<asmv3:file>` 块），或改用"先删 DLL 再让 targets 从现存文件生成 manifest"的顺序。
  4. publish 脚本新增断言：`onnxruntime.dll`/`DirectML.dll`/`Microsoft.Windows.AI.*` 不存在，且 `WindowsAppSDK.manifest` 不含 `Windows.AI`。
- **验收**：`dotnet publish` 成功；启动到主窗口（WinUI 激活走 manifest，**这是最大风险点**）；设置页"选择文件夹"（`Windows.Storage.Pickers`）；6 后端只读通信；`DotNetLayoutCheck` 通过；体积报告 = 根目录少 44 文件 / 43.34 MiB。
- **回滚**：删除该 Target + PackageReference 两行即可完整恢复（无外溢）。
- **收益**：**≈ -15.45 MB 安装包**（lzma2 实测 15,452,195 B = 14.74 MiB）；更新流量 **-17.78 MiB**。
- **风险提示**：**若 WinUI/AppSDK 在启动时静态链接 onnxruntime（例如 Windows ML 自动初始化），删除会导致启动失败**。`Microsoft.WindowsAppSDK.ML.targets:10-21` 显示 `WindowsAppSDKMLAutoInitialize` 默认 true，且 `SelfContained` 模式会走 `WindowsMLAutoInitializerSelfContained`——**这是纯 C++ 自动初始化，与 .NET 项目无关**（找不到 `ClCompile` 项即不生效），但仍建议**先做一次隔离构建 + 启动实测**，并保留"如果不能删就只删 `DirectML.dll`（17.84 MiB，仅 GPU EP 需要）"的降级方案。

### A3（P0-4）剔除调试/转储工具链（保留 EventLog.Messages）
- **文件**：`installer/publish-selfcontained.ps1`
- **改动要点**：删除根 + Backends 下 `mscordaccore.dll`、`mscordaccore_amd64_*.dll`、`mscordbi.dll`、`Microsoft.DiaSymReader.Native.amd64.dll`、`createdump.exe`（**保留** `System.Diagnostics.EventLog.Messages.dll`，`RuntimeRepairDiagnostics.cs:197` 依赖 `EventLogReader` 的 message 资源）。断言删除 10 文件（每项 2 份，共 10；`createdump.exe` 2 份）。
- **验收**：启动 + 一次后端调用 + 一次运行库诊断（`vc14-dependencies-*.json` 能正常产出）；`DotNetLayoutCheck` 通过。
- **回滚**：单文件回退。
- **收益**：**≈ -4.27 MB 安装包**；流量 **-5.33 MiB**。

### A4（P1-4）给打包链路加一个非阻塞 CI job
- **文件**：`.github/workflows/build.yml`
- **改动要点**：新增 job（`continue-on-error: true` 或独立 workflow_dispatch）：`publish-selfcontained.ps1` → 输出布局文件数/字节到 job summary → `ISCC /DValidateOnly`（不产出安装包，避免每次 PR 编译 237 MiB）。同时补一条 **files.json/update.json 版本一致性校验**（当前 CI 完全没有：`update.json` 未出现在任何 step 中）。
- **验收**：PR 上能看到"布局 = 1227 文件 / 521.63 MiB"与"安装包可编译"两项红绿。
- **回滚**：删除该 job。
- **收益**：非体积；把 A1/A2/A3 的回归从"发布时手测"变成"PR 自动"。

### A5（P1-2，需产品决策）`Vc14OwnershipOriginals` 按需下载
- **文件**：`installer/publish-selfcontained.ps1:106-113`、`src/DeltaNFD.Execution/Execution/Vc14OwnershipOriginals.cs:22-41`、新增下载器
- **改动要点**：把 publish 的"必须存在"改为"存在则校验、缺失则跳过"；`Read()` 在本地缺失时走固定 URL（微软官方固定链接已在 catalog 中）下载到 `%ProgramData%\DeltaNFD-FileUpdates\originals\`，校验 `Bytes`+`Sha256` 后使用；离线时返回"证据缺失，无法证明归属"（现有 `Vc14OwnershipOriginals.cs:39-40` 已能优雅降级）。
- **验收**：断网时修复流程仍能给出明确拒绝原因（不崩）；联网时 `2d507699-...` 类孤立引用仍能被核验。
- **回滚**：恢复 publish 强校验 + 删除下载器（`Read()` 变回只读本地）。
- **收益**：**≈ -23.0 MB 安装包**（24.16 MiB 解压 / 23.93 MiB gzip × 0.917）。

### A6（P2，随时可做）sln / 脚本 / 笔记清理
- **文件**：`DeltaNFD.sln`、根目录 6 个 `.ps1`、`notes/README.md`（新建）
- **改动要点**：sln 补入 `UpdateCore`、`FileUpdater`、`DotNetLayoutCheck`（publish 门禁必须可构建）；2 个插件样例与 7 个研究类 tools 维持"按路径构建"并在 README 说明；6 个根脚本移入 `tools/dev-scratch/`（0 引用，已核实）；`notes/` 加索引 README。
- **验收**：`dotnet build DeltaNFD.sln` 成功；CI 全绿。
- **回滚**：`git revert` 单提交。
- **收益**：0 MB（整洁度）。

### A7（不做，仅记录）压缩参数调整
- **结论**：`DeltaNFD.iss:66-67` 保持 `lzma2/max` + `SolidCompression=yes`。**不建议**改 `ultra64`（词典 32→64 MB，预期 <1 MB 收益、安装期内存 +32 MB），**不建议**加 `LZMAUseSeparateProcess`（只影响编译期内存，不改字节；除非未来编译 OOM 才需要）。
- **若仍想试**：只能在有 ISCC 的机器上做 A/B（`Compression=lzma2/ultra64` 单变量），并把"安装期峰值内存"一起测（`LZMAUseSeparateProcess=no` 时解压内存与词典同阶）。

---

## ⑥ 未验证事项

1. **`lzma2/ultra64` vs `lzma2/max` 的真实字节差**——本机 **Inno Setup 6 未安装**（三处标准路径均无 `ISCC.exe`），且任务禁止编译安装包，故只能用"实测 lzma2 压缩率 + 载荷构成"做推断（预期 <0.5%）。需在有 ISCC 的环境做单变量 A/B。
2. **删除 AI/ML 后 WinUI 能否正常启动**——`WindowsAppSDK.manifest` 已含 23 条 `Microsoft.Windows.AI.*` 激活类 + 8 条 AI/ML/Workloads/ProxyStub `<asmv3:file>`，`WindowsAppSDKMLAutoInitialize` 默认 true（`Microsoft.WindowsAppSDK.ML.targets:10`）。理论上 .NET 项目不触发 C++ 自动初始化，但 manifest 的激活注册是进程级事实，**必须隔离构建 + 启动实测**才能定论。这是 A2 的**前置验证项**。
3. **卫星语言删除对非中英文系统的影响**——未在 `de-DE`/`ja-JP` 系统验证 WinUI 的 MUI 回退行为（预期回退 `en-us`，需确认不会显示空白/异常布局）。
4. **删 `mscordaccore`/`mscordbi`/`createdump` 后崩溃转储能力**——未验证 .NET 8 在缺失 `createdump.exe` 时是否会打印警告或行为变化（预期仅失去自动 dump，不影响运行）。
5. **`[Files] Excludes` 的通配符跨层语义**——未编译 iss 验证 `*.pdb` 是否覆盖 `Backends\*.pdb`；因此建议路线 A1 绕开该问题（改为 publish 剔除）。若将来必须用 iss Excludes，需实测。
6. **安装包实测体积**——所有"预计 MB"都是基于 lzma2 校准的**估算**（solid 压缩下非线性，误差 ±10%）；只有真正 `ISCC` 编译后才能给出确定值。
7. **Backends 共享运行时的实际可运行性**——未验证"删除 Backends 下运行时、让后端从 `..\` 加载"是否能通过 .NET 的 `deps.json` 解析（hostpolicy 会按 app 目录查找，理论上需要 `AppContext.BaseDirectory` 一致或 `additionalProbingPaths`），A6/P1-1 必须先做隔离原型。
8. **`remove` 条目的实际删除行为**——本版 `remove:0`，未验证 `--previous-files` 生成的 remove 条目在用户机器上删除旧的 168 个 mui / 15 个 pdb 是否成功（受文件占用、只读属性影响）。
9. **`web/`（VitePress）与 `docs/`（1 文件 393 B）的实际发布路径**——未验证它们是否被 GitHub Pages 或自建站使用；仅确认不参与 C# 构建。
10. **本机当前无法完整复现发布链路**：`notes/audit-20261018/build-release.log:34-48` 记录了一次 **NuGet 还原失败**（`microsoft.windowsdesktop.app.runtime.win-x64/8.0.28`、`microsoft.aspnetcore.app.runtime.win-x64/8.0.28`、`microsoft.netcore.app.runtime.win-x64/8.0.28` 下载 EOF / `Central Directory corrupt`，3 errors），发生在 `src/DeltaNFD.FileUpdater` 项目，SDK 为 `10.0.301`。该日志说明"当前工作区不能直接重跑 publish"，任何体积实测都需先修复还原缓存。
11. **`Microsoft.Windows.SDK.NET.dll`（24,877,600 B，23.73 MiB）能否裁剪**——它来自 `microsoft.windows.sdk.net.ref` 的 `lib\net8.0`，是 CSWinRT 生成的投影程序集。理论上可用 `CsWinRT` 的"仅引用用到的命名空间"裁剪（`WindowsSdkPackageVersion` + `CsWinRTAotOptimizer`），但**未验证**，且属于高风险 AOT/trim 领域，故未列入 P0/P1。

---

## 附：本次审计使用的关键命令与可复现口径

```powershell
# 总计与分组
$j = Get-Content notes\audit-20261018\live-files-0.9210.json -Raw | ConvertFrom-Json
$j.files.Count                                        # 1227
($j.files | Measure-Object sizeBytes -Sum).Sum        # 546918669
($j.files | Measure-Object downloadSize -Sum).Sum     # 333209453
# 卫星语言
($j.files | Where-Object { $_.path -match '\.mui$' }).Count                       # 172
($j.files | Where-Object { $_.path -match '\.mui$' } | Measure-Object sizeBytes -Sum).Sum  # 3530488
# PDB
($j.files | Where-Object { $_.path -match '\.pdb$' } | Measure-Object sizeBytes -Sum).Sum  # 1325636
# Backends 与根目录同名去重
$root = @{}; $j.files | Where-Object { $_.path -notmatch '/' } | ForEach-Object { $root[$_.path] = $_.sha256 }
# → 同名 192 / 哈希相同 186 / 不同 6
# AI/ML
($j.files | Where-Object { $_.path -match '(?i)^(onnxruntime|DirectML|Microsoft\.Windows\.AI\.|...)' } | Measure-Object sizeBytes -Sum).Sum  # 45441778
# lzma2 校准（仓库外临时目录，未写入工作区）
python -c "import lzma; ..."   # preset 9|PRESET_EXTREME, dict_size 64MB, FORMAT_RAW
```

> 审计期内对 `H:\coding\DeltaNFD` 的唯一写入 = 本文件。未创建/修改任何其他文件；临时压缩实验全部在 `%TEMP%` 下完成并已删除。
