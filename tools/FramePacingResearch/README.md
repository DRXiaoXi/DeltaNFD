# 三角洲原创稳帧研究工具

本工具实现的是**稳帧控制内核、自有 DX12 测试程序、只读帧数据采集与分析**。当前没有已验证的三角洲非注入逐帧控制入口，不会向主程序添加“已锁帧”开关，也不调用 NVIDIA 限帧设置。

不注入 DLL、不挂钩图形 API、不修改游戏内存、不暂停游戏线程、不改 CPU 调度、不修改游戏或驱动设置。本轮用户选择先完成工具与模拟，三角洲实机对比留待后续。

## 一次生成研究数据

需要本机已安装 Visual Studio 2022 C++ Build Tools（v143）、Windows SDK 与 .NET 8。脚本只构建工具，不安装依赖或修改系统设置。

在仓库根目录的 PowerShell 中运行：

```powershell
./tools/FramePacingResearch/run-research.ps1 -Dx12Smoke
```

输出到忽略的 `_buildcheck/frame-pacing/run-<GUID>/`，包括：

- 绝对截止时间、简单相对等待、不限速模拟三种模式 × 稳定负载、抖动、长帧、理想 2x 模拟生成帧，共 12 组逐帧 CSV 和分析。
- 等待超时夹具：预期明确失败并保留记录，不计入成功帧。
- 纯内核、真实 Windows 计时器与 CSV/统计/数据质量检查日志。
- 可选隐藏 WARP 软件渲染 DX12 运行、JSON 元数据、汇总 CSV 和 Markdown。

默认固定目标 144 FPS、原生帧数 8640、随机种子 20261010。`-TargetFps` 可用偶数 60–1000；`-Frames` 可用 100–200000。已有输出目录会被拒绝，避免覆盖研究证据。

生成标准图表（Windows 自带 .NET Charting，不需要安装 Python 包）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/FramePacingResearch/plot-results.ps1 -ResearchDirectory <本次研究目录>
```

`-ExecutionPolicy Bypass` 仅用于当前绘图子进程，不更改机器或用户执行策略。图中所有数据明确标为 synthetic，不能作为与 NVIDIA 或游戏原生限帧比较的证据。

## 内核与自有 DX12 程序

```powershell
./tools/FramePacingLab/build.ps1
./_buildcheck/frame-pacing/native/FramePacingLab.exe --self-test
./_buildcheck/frame-pacing/native/FramePacingLab.exe --clock-test
./_buildcheck/frame-pacing/native/FramePacingLab.exe --fps 144 --seconds 60 --scenario long-frame --output _buildcheck/frame-pacing/my-dx12.csv
```

最后一条会显示自有 DX12 动画窗口，关闭窗口或 Ctrl+C 可取消。该程序完全独立于三角洲。`--hidden` 隐藏测试窗口，`--warp` 显式选择软件渲染，`--simulate` 使用确定性虚拟时钟。隐藏/WARP 结果仅用于执行链路检查，不用于认定物理显示帧率或实际游戏性能。

核心接口位于 `../FramePacingLab/FramePacer.h`：调用者提供单调时钟及可取消的等待函数，`FramePacer::pace()` 返回截止时间、就绪时间、提交点、等待状态、重建时间基准及丢弃过期槽数量。

算法使用整数有理数槽位 `epoch + slot * frequency / fps`，避免逐帧取整累积漂移；超过截止时间 200 微秒后重建下一帧时间基准，避免长帧之后持续追赶。等待误差 EWMA 上限 2 毫秒，忙等尾段最多 200 微秒；不调用 `timeBeginPeriod`，不改变全局计时器或线程优先级。

**内核只控制调用它的程序，不能控制其他游戏进程。** 理想生成帧夹具是在两个已知原生帧间离线插入中点，使用了未来信息；它不是实时插帧器，不实现 DLSS，不证明真实补帧节奏可控。

## 逐帧数据含义

- `PresentSubmitMs`：自有程序在 CPU 侧调用呈现的时间点，或明确标注的模拟时间点；不是物理显示时间。
- `RenderWorkMs`：本测试程序的工作就绪耗时，包含人工负载与 GPU fence 等待；不是单独的 GPU 渲染时间。
- `QueueWaitMs`、`PresentCallMs`：呈现队列等待与 Present 调用耗时，用于解释测试环境的限制。
- `RenderLateMs`：进入限速器前已经超过截止时间的部分。
- `WaitOvershootMs`：等待返回相对实际可等待截止点的额外迟到。
- `LatenessMs`：总迟到；失败等待通过 `WaitStatus` 标识，不能视为成功提交。

统计提供 P50/P95/P99、标准差、最大值、长帧数量及 1% low。1% low 明确定义为 `1000 / 最慢 1% 帧耗时的均值`。长帧定义为耗时大于 `max(目标帧间隔 × 2, 20ms)`；P99 之外的稀有卡顿仍必须检查最大值与长帧数量。

PresentMon 的 Native FPS/间隔使用已识别 Application 帧的应用起始时间线（或旧格式的 Present 起始时间），不代表 GPU 渲染完成时间。长帧阈值统一以最终显示目标定义；渲染、等待等非显示指标的超阈值数量不能直接当作最终画面卡顿数量。

## 后续三角洲只读采集

下载锁定版本的控制台采集器，**不是 MSI 或常驻服务**：

```powershell
./tools/FramePacingResearch/download-presentmon.ps1
dotnet build tools/FramePacingResearch/FramePacingResearch.csproj
```

PresentMon 固定为官方 2.6.0 x64，大小 980320 字节，SHA256：

```text
b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af
```

采集程序同时固定此哈希并在运行期间持有只读文件句柄；修改 lock JSON 不能让执行器接受其他 EXE。

**ETW 需要管理员或 Performance Log Users 等有效权限。** 请使用已有相应权限的终端运行；工具不会自动提权、修改用户组或弹出安装器。本轮实际连接自有程序时收到 Windows `access denied`，失败记录已保留，未宣称 ETW 实机采集通过。

手动进入三角洲同一场景，确认目标 PID 与游戏 EXE 完整路径，然后运行：

```powershell
dotnet tools/FramePacingResearch/bin/Debug/net8.0-windows/FramePacingResearch.dll capture --presentmon _buildcheck/frame-pacing/dependencies/PresentMon-2.6.0-x64.exe --exe "D:/实际游戏目录/DeltaForce/Binaries/Win64/DeltaForceClient-Win64-Shipping.exe" --pid 12345 --seconds 60 --target 144 --condition uncapped --fg off --scene "training-route-A-1080p-fixed-settings" --output _buildcheck/frame-pacing/captures/uncapped-off-1
```

- `condition` 使用 `uncapped`、`native`、`nvidia`；设置必须由用户手动切换，标签是用户声明，工具不会自动验证或改写设置。
- `fg` 使用 `off` 或 `on`；不知道时可写 `unknown`，但不能进入正式对比。
- 同一场景、画质、分辨率、刷新率、VRR/VSync/Reflex、补帧配置保持一致；每个条件至少 3 轮各 60 秒。FG 关闭与开启应分别形成完整三组。
- 路径、PID、启动时间均核对。游戏退出、采集超时或取消会保留失败证据。
- 每次采集使用唯一 ETW session 名称，不终止其他 PresentMon 会话；取消时只清理自己创建的会话与采集器，不结束游戏。

只读分析：

```powershell
dotnet tools/FramePacingResearch/bin/Debug/net8.0-windows/FramePacingResearch.dll analyze --csv <capture目录>/frames.csv --pid 12345 --target 144 --output <全新分析目录>
```

多进程或多交换链 CSV 必须显式选择 `--pid` / `--swapchain`，不拼接不同流。缺失字段保持未知；原始 QPC 计数没有频率信息时不会被误当毫秒；重复帧不计作新生成帧；数据损坏、非单调时间、丢失显示指标均不宣称锁帧成功。

`--track_frame_type` 依赖应用/驱动提供事件。当前锁定版本的输出类型包括 Application、Intel XeSS-FG、AMD AFMF、NotSet、Repeated；不能据此承诺三角洲 DLSS/MFG 已能正确分离。缺少可信类型时最终“原生+生成 FPS”保持未确认，不能用显示 FPS 除以 2 猜原生帧率。可用输入/应用延迟指标分别按原字段保存，不互相替代，不把呈现耗时当输入延迟。

## 三组正式对比

建立 JSON 数组文件，列出至少 9 个采集目录（相对路径以 JSON 所在目录为基准）：

```json
["captures/uncapped-off-1", "captures/uncapped-off-2", "captures/uncapped-off-3", "captures/native-off-1", "captures/native-off-2", "captures/native-off-3", "captures/nvidia-off-1", "captures/nvidia-off-2", "captures/nvidia-off-3"]
```

```powershell
dotnet tools/FramePacingResearch/bin/Debug/net8.0-windows/FramePacingResearch.dll compare --manifest <目录列表.json> --output <全新对比目录>
```

校验 CSV 哈希、采集成功状态、不同会话、重复次数、时长、游戏版本、驱动、系统、场景、目标和帧类型。拒绝复制一份数据充当三次采样，拒绝把实验程序/模拟数据混入游戏比较。补帧开启组必须存在可信生成帧证据，否则该组尚未满足对比条件。

输出每种条件的多轮 P95/P99、波动及长帧结果，不自动断言哪种方案必然更优。当前没有三角洲实测数据，不能声称自研内核优于 NVIDIA 限帧。

## 许可与集成状态

PresentMon 为 Intel 等贡献者的 MIT 项目，全文见 `PresentMon-LICENSE.txt`，上游：https://github.com/GameTechDev/PresentMon 。采集器仅下载到忽略目录，不进入主程序构建、安装包或自动更新。

主程序、版本号和安装器未修改。没有已验证的三角洲控制入口时，仅交付研究与分析工具；具体结论见 `notes/三角洲原创稳帧研究报告-20261010.md`。
