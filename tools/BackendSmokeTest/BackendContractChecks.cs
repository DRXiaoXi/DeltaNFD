using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace BackendSmokeTest;

/// <summary>
/// 官方后端合同与调用器检查（C1/C2）：
/// 1) 长度前缀帧编解码、严格 JSON、协议字段校验；
/// 2) 模块命令白名单、超时、退出语义；
/// 3) 真实 ACE 后端 EXE 的握手 / 身份 / 扫描 / 排空（只读，绝不触发 ace.clean）。
/// 全部为只读或纯逻辑，不对真实系统做任何修改。
/// </summary>
internal static class BackendContractChecks
{
    private static int _passed;

    public static async Task RunAsync()
    {
        _passed = 0;
        var testId = Guid.NewGuid();
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-official-contract", testId.ToString("N"));
        Directory.CreateDirectory(isolated);
        File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, testId);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", isolated);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", testId.ToString("D"));
        LogicChecks();
        MessageChecks();
        ModuleChecks();
        BackendPathsChecks();
        var contradictory = BackendResult.Ok("fixture", "", OperationResult.Ok("fixture")) with { Status = BackendResultStatuses.Failed };
        Check(!BackendResults.Map(contradictory, "fixture").Success, "失败状态不能被 data.Success 覆盖成成功");
        await ProcessChecksAsync();
        Console.WriteLine($"官方后端合同检查全部通过（{_passed} 项断言）。");
    }

    private static void LogicChecks()
    {
        var session = Guid.NewGuid();
        var request = Guid.NewGuid();
        var frame = BackendMessages.EncodeProgress(BackendModules.Ace, session, request, new BackendProgress(42, "进行中", "scan"));

        // 1) 长度前缀为 4 字节小端，且与 payload 长度一致。
        var declared = (uint)(frame[0] | (frame[1] << 8) | (frame[2] << 16) | (frame[3] << 24));
        Check(declared == frame.Length - 4, "帧长度前缀与 payload 长度一致");
        Check(frame.Length > 4, "帧包含 payload");

        // 2) 流式读取：半帧 + 多帧不丢帧。
        var buffer = new ReadOnlySequence<byte>(frame);
        Check(BackendProtocol.TryReadFrame(ref buffer, out var parsed), "可从缓冲区解析完整帧");
        Check(parsed.Type == BackendFrameType.Progress && parsed.Module == BackendModules.Ace &&
              parsed.SessionId == session && parsed.RequestId == request, "解析出的帧字段与编码一致");
        Check(BackendMessages.ParseProgress(parsed.Payload).Percent == 42, "progress 百分比回读一致");

        var half = frame[..(frame.Length / 2)];
        var rest = frame[(frame.Length / 2)..];
        var partial = new ReadOnlySequence<byte>(half);
        Check(!BackendProtocol.TryReadFrame(ref partial, out _), "半帧不消费（返回 false）");
        var combined = new ReadOnlySequence<byte>(half.Concat(rest).ToArray());
        Check(BackendProtocol.TryReadFrame(ref combined, out _) && combined.IsEmpty, "半帧补齐后可完整解析");

        // 3) 非法长度前缀被拒绝。
        Check(Throws<BackendContractException>(() =>
        {
            var bad = new byte[4];
            var seq = new ReadOnlySequence<byte>(bad);
            BackendProtocol.TryReadFrame(ref seq, out _);
        }), "长度为 0 的帧被拒绝");

        // 4) 超长帧被拒绝。
        Check(Throws<BackendContractException>(() => BackendProtocol.EncodeFrame(new string('a', BackendProtocol.MaxFrameBytes + 1))),
            "超长 payload 被拒绝");

        // 5) 重复属性被拒绝。
        var duplicate = """{"protocol":"1.0","protocol":"1.0","type":"ping","module":"ace","sessionId":"x","requestId":"y","payload":{}}""";
        Check(Throws<BackendContractException>(() =>
        {
            var seq = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(duplicate));
            BackendProtocol.TryParseFrame(seq, out _);
        }), "重复属性被拒绝");

        // 6) 未知消息类型被拒绝。
        Check(Throws<BackendContractException>(() => ParseRaw(session, request, "run-shell", BackendModules.Ace, "{}")),
            "未知消息类型被拒绝");

        // 7) 未知模块被拒绝。
        Check(Throws<BackendContractException>(() => ParseRaw(session, request, "ping", "evil", "{}")),
            "未知模块被拒绝");

        // 8) 非 UUID 会话被拒绝。
        Check(Throws<BackendContractException>(() => ParseRaw(Guid.Empty, request, "ping", BackendModules.Ace, "{}")),
            "空 GUID 会话被拒绝");
    }

    private static void MessageChecks()
    {
        var nonce = Convert.ToBase64String(new byte[32]);

        Check(BackendMessages.IsThreePartVersion("0.90.1"), "三段版本被接受");
        Check(!BackendMessages.IsThreePartVersion("Beta0.9") && !BackendMessages.IsThreePartVersion("0.90"),
            "非三段数字版本被拒绝");
        Check(BackendMessages.IsNonce(nonce) && !BackendMessages.IsNonce("short"), "nonce 长度校验正确");
        Check(BackendMessages.IsSha256(new string('a', 64)) && !BackendMessages.IsSha256("xyz"), "SHA256 十六进制校验正确");

        Check(Throws<BackendContractException>(() => BackendMessages.ParseHello(Obj("""{"nonce":"bad","hostVersion":"0.90.1","dataDirectory":"C:\\x"}"""))),
            "hello：坏 nonce 被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseHello(Obj($$"""{"nonce":"{{nonce}}","hostVersion":"0.90.1","dataDirectory":"relative"}"""))),
            "hello：相对数据目录被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseHello(Obj($$"""{"nonce":"{{nonce}}","hostVersion":"Beta","dataDirectory":"C:\\x"}"""))),
            "hello：非三段宿主版本被拒绝");

        Check(Throws<BackendContractException>(() => BackendMessages.ParseReady(Obj($$"""
            {"nonce":"{{nonce}}","module":"ace","moduleVersion":"0.90.1","protocolMinor":0,"pid":1,"startTimeUtcTicks":1,"imagePath":"C:\\a.exe","imageSha256":"zz"}
            """))), "ready：坏哈希被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseReady(Obj($$"""
            {"nonce":"{{nonce}}","module":"ace","moduleVersion":"0.90.1","protocolMinor":99,"pid":1,"startTimeUtcTicks":1,"imagePath":"C:\\a.exe","imageSha256":"{{new string('a', 64)}}"}
            """))), "ready：协议次版本越界被拒绝");

        Check(Throws<BackendContractException>(() => BackendMessages.ParseProgress(Obj("""{"percent":101,"message":"x","stage":""}"""))),
            "progress：百分比越界被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseResult(Obj("""{"status":"maybe","code":"x","message":"y","pendingRestore":false}"""))),
            "result：未知状态被拒绝");
        var knownPartial = BackendMessages.ParseResult(Obj("""{"status":"partial","code":"x","message":"y","pendingRestore":false,"requiresReview":false}"""));
        Check(!knownPartial.RequiresReview, "result：已知 partial 的 requiresReview=false 被保留");
        var legacyPartial = BackendMessages.ParseResult(Obj("""{"status":"partial","code":"x","message":"y","pendingRestore":false}"""));
        Check(legacyPartial.RequiresReview, "result：旧 partial 缺少 requiresReview 时按需要复核处理");
        var knownOk = BackendMessages.ParseResult(Obj("""{"status":"ok","code":"x","message":"y","pendingRestore":false,"requiresReview":false}"""));
        Check(!knownOk.RequiresReview, "result：成功结果不要求复核");
        var itemStatuses = BackendMessages.ParseResult(Obj("""{"status":"partial","code":"x","message":"y","pendingRestore":false,"requiresReview":false,"items":[{"id":"priority","status":"applied","message":""},{"id":"cpuSet","status":"rejected","message":"需要接管"},{"id":"x","status":"exited","message":""}]}"""));
        Check(itemStatuses.Items.Select(i => i.Status).SequenceEqual(["applied", "rejected", "exited"]),
            "result：逐项 applied/rejected/exited 等命令专用状态被接受");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseResult(
                Obj("""{"status":"partial","code":"x","message":"y","pendingRestore":false,"items":[{"id":"a","status":"made-up","message":""}]}"""))),
            "result：未知逐项状态仍被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseResult(
                Obj("""{"status":"ok","code":"x","message":"y","pendingRestore":false,"requiresReview":"no"}"""))),
            "result：requiresReview 非布尔被拒绝");
        Check(Throws<BackendContractException>(() => BackendMessages.ParseCancel(Obj("""{"referencedRequestId":"nope"}"""))),
            "cancel：非 UUID 被拒绝");
    }

    private static void ModuleChecks()
    {
        Check(BackendModules.All.Count == 6, "注册了 6 个官方后端模块（ace/gpu/runtime/cpuframe/procman/system）");
        Check(!BackendModules.All.ContainsKey("shaderstutter"), "Beta0.922 公测排除未发布卡顿采集后端");
        Check(!BackendModules.All.ContainsKey("diagnostics"), "已撤下的卡顿诊断后端不再注册");
        Check(BackendModules.IsAllowed(BackendModules.Procman, BackendCommands.ProcmanSet), "procman.process.set 在白名单内");
        Check(BackendModules.IsReadOnly(BackendModules.Procman, BackendCommands.ProcmanList), "procman.process.list 为只读");
        Check(BackendModules.IsMutating(BackendModules.Procman, BackendCommands.ProcmanSet), "procman.process.set 视为写入");
        Check(!BackendModules.RequiresWriteIntent(BackendModules.Procman, BackendCommands.ProcmanRecover),
            "procman.recover 属于恢复核对，不写新的执行意图");
        Check(BackendModules.ReconcileCommand(BackendModules.Procman) == BackendCommands.ProcmanReconcile, "procman 核对命令已登记");
        Check(BackendModules.RecoverCommand(BackendModules.Procman) == BackendCommands.ProcmanRecover, "procman 恢复命令已登记");
        foreach (var spec in BackendModules.All.Values)
        {
            Check(spec.Commands.Count > 0, $"{spec.Id} 有非空命令白名单");
            foreach (var command in spec.ExitAfterCommands)
                Check(spec.Commands.Contains(command), $"{spec.Id} 的退出命令 {command} 位于白名单内");
            Check(spec.ExecutableName.EndsWith(".exe", StringComparison.Ordinal), $"{spec.Id} 的 EXE 名合法");
        }

        Check(BackendModules.IsAllowed(BackendModules.Ace, BackendCommands.AceClean), "ace.clean 在白名单内");
        Check(!BackendModules.IsAllowed(BackendModules.Ace, "cmd.exe /c whoami"), "任意 shell 命令不在白名单内");
        Check(!BackendModules.IsAllowed(BackendModules.Ace, BackendCommands.GpuDriverApply), "跨模块命令不被允许");
        Check(Throws<BackendContractException>(() => BackendModules.TimeoutFor(BackendModules.Ace, "nope")),
            "非白名单命令取超时被拒绝");
        Check(BackendModules.TimeoutFor(BackendModules.Ace, BackendCommands.AceClean) >= TimeSpan.FromMinutes(10),
            "ACE 清理超时足够长");
    }

    private static void BackendPathsChecks()
    {
        var ace = BackendPaths.Resolve(BackendModules.Ace);
        Check(File.Exists(ace) && ace.EndsWith("DeltaNFD.Backend.Ace.exe", StringComparison.OrdinalIgnoreCase),
            "能解析出 ACE 后端 EXE");
        var gpu = BackendPaths.Resolve(BackendModules.Gpu);
        Check(File.Exists(gpu) && gpu.EndsWith("DeltaNFD.Backend.Gpu.exe", StringComparison.OrdinalIgnoreCase),
            "能解析出显卡后端 EXE");
        var runtime = BackendPaths.Resolve(BackendModules.Runtime);
        Check(File.Exists(runtime) && runtime.EndsWith("DeltaNFD.Backend.Runtime.exe", StringComparison.OrdinalIgnoreCase),
            "能解析出运行库后端 EXE");

        // T4：CPU/帧格后端已落地，必须能解析出 EXE（不再回退前端执行）。
        var cpuframe = BackendPaths.Resolve(BackendModules.CpuFrame);
        Check(File.Exists(cpuframe) && cpuframe.EndsWith("DeltaNFD.Backend.CpuFrame.exe", StringComparison.OrdinalIgnoreCase),
            "能解析出 CPU/帧格后端 EXE");
    }

    private static async Task ProcessChecksAsync()
    {
        var exe = BackendPaths.Resolve(BackendModules.Ace);

        // 描述模式与错误参数。
        Check(await RunExitCodeAsync(exe, ["--describe"]) == 0, "后端 --describe 正常");
        Check(await RunExitCodeAsync(exe, ["--backend", "gpu", "--pipe", "x"]) == 2, "模块参数不匹配时拒绝启动");

        // 真实握手 + 只读扫描 + 排空。
        await using var client = await BackendClient.StartAsync(BackendModules.Ace, CancellationToken.None);

        var scan = await client.InvokeAsync(BackendCommands.AceScan, new { }, null, CancellationToken.None);
        Check(scan.Status == BackendResultStatuses.Ok, "ace.scan 返回 ok");
        Check(scan.Data is not null, "ace.scan 返回数据");
        var result = BackendJson.Deserialize<AceScanResult>(scan.Data!.Value);
        Check(result.Services.Count == 6, "ace.scan 枚举 6 个 ACE 服务");
        Check(result.RunningProcesses is not null && result.Paths is not null, "ace.scan 结果字段完整");

        // 白名单外命令必须在发送前被客户端拒绝。
        var rejected = false;
        try { await client.InvokeAsync("ace.rm-rf", new { }, null, CancellationToken.None); }
        catch (BackendContractException) { rejected = true; }
        Check(rejected, "客户端拒绝白名单外命令");

        // 排空后进程必须退出。
        using var owned = CaptureOwnedProcess(client);
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Check(owned.HasExited, "排空后本次 ACE 后端进程实际退出");

        await GpuProcessChecksAsync();
    }

    /// <summary>显卡后端：握手 + 只读枚举/环境读取 + 排空（绝不触发伪装写入或驱动写入）。</summary>
    private static async Task GpuProcessChecksAsync()
    {
        var exe = BackendPaths.Resolve(BackendModules.Gpu);
        Check(await RunExitCodeAsync(exe, ["--describe"]) == 0, "显卡后端 --describe 正常（白名单与处理器一致）");
        Check(BackendModules.IsAllowed(BackendModules.Gpu, BackendCommands.GpuSpoofReload),
            "显卡设备重载命令已在白名单内（T4 迁移执行到显卡后端）");

        await using var client = await BackendClient.StartAsync(BackendModules.Gpu, CancellationToken.None);

        var adapters = await client.InvokeAsync(BackendCommands.GpuSpoofStatus, new { }, null, CancellationToken.None);
        Check(adapters.Status == BackendResultStatuses.Ok, "gpu.spoof.status 返回 ok");
        Check(adapters.Data is not null, "gpu.spoof.status 返回数据");
        var adapterList = BackendJson.Deserialize<List<DisplayAdapterInfo>>(adapters.Data!.Value);
        Check(adapterList.All(a => a.RegistryPath.Length > 0), "适配器列表路径非空");

        var environment = await client.InvokeAsync(BackendCommands.GpuDriverStatus, new { }, null, CancellationToken.None);
        Check(environment.Status == BackendResultStatuses.Ok && environment.Data is not null, "gpu.driver.status 返回 ok");
        var env = BackendJson.Deserialize<NvGpuEnvironmentInfo>(environment.Data!.Value);
        Check(env.GpuName is not null, "驱动环境对象可反序列化");

        // 伪造路径必须被后端拒绝（不触碰任何注册表写）。
        var badPath = await client.InvokeAsync(BackendCommands.GpuSpoofApply,
            new GpuSpoofApplyArgs { RegistryPath = "", FakeName = "x" }, null, CancellationToken.None);
        Check(badPath.Status == BackendResultStatuses.Failed, "gpu.spoof.apply 缺少 registryPath 时被拒绝");

        using var owned = CaptureOwnedProcess(client);
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Check(owned.HasExited, "本次显卡后端排空后实际退出");

        await RuntimeProcessChecksAsync();
        await SystemProcessChecksAsync();
    }

    /// <summary>系统维护后端：握手 + 只读枚举/状态读取 + 排空（绝不执行清理、改电源或改虚拟内存）。</summary>
    private static async Task SystemProcessChecksAsync()
    {
        var exe = BackendPaths.Resolve(BackendModules.System);
        Check(await RunExitCodeAsync(exe, ["--describe"]) == 0, "系统维护后端 --describe 正常（白名单与处理器一致）");
        // T3 迁移后：扩展库 / Defender / 内存命令进入白名单；伪造命令仍被拒绝。
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemBxStatus),
            "system.bx.status 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemBxApply),
            "system.bx.apply 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemMemoryPurge),
            "system.memory.purge 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.System, BackendCommands.SystemDefenderPreflight),
            "system.defender.preflight 在白名单内");
        Check(!BackendModules.IsAllowed(BackendModules.System, "system.bx.apply-raw-script"),
            "伪造的扩展库命令不在白名单内");

        await using var client = await BackendClient.StartAsync(BackendModules.System, CancellationToken.None);
        var backups = await client.InvokeAsync(BackendCommands.SystemTweaksBackups, new { }, null, CancellationToken.None);
        Check(backups.Status == BackendResultStatuses.Ok && BackendJson.Deserialize<List<SystemTweak>>(backups.Data!.Value).Count == 0,
            "系统恢复清单由隔离后端读取，不是前端猜测");
        var missingTweak = await client.InvokeAsync(BackendCommands.SystemTweaksApply, new { }, null, CancellationToken.None);
        Check(missingTweak.Status == BackendResultStatuses.Failed, "缺失优化项不能默认执行 Hyper-V 写入");
        var invalidTweak = await client.InvokeAsync(BackendCommands.SystemTweaksRestore, new { tweak = 999 }, null, CancellationToken.None);
        Check(invalidTweak.Status == BackendResultStatuses.Failed, "非法优化项恢复被拒绝");
        var missingGroup = await client.InvokeAsync(BackendCommands.SystemAdvancedApply, new { }, null, CancellationToken.None);
        Check(missingGroup.Status == BackendResultStatuses.Failed, "高级优化缺失分组不执行写入");

        var schemes = await client.InvokeAsync(BackendCommands.SystemPowerSchemes, new { }, null, CancellationToken.None);
        Check(schemes.Status == BackendResultStatuses.Ok && schemes.Data is not null, "system.power.schemes 返回 ok");
        Check(BackendJson.Deserialize<List<PowerSchemeInfo>>(schemes.Data!.Value).Count > 0, "电源计划列表非空");

        var scan = await client.InvokeAsync(BackendCommands.SystemCleanupScan, new { }, null, CancellationToken.None);
        Check(scan.Status == BackendResultStatuses.Ok && scan.Data is not null, "system.cleanup.scan 返回 ok");
        Check(BackendJson.Deserialize<List<CleanupTarget>>(scan.Data!.Value) is not null, "清理目标列表可反序列化");

        var shader = await client.InvokeAsync(BackendCommands.SystemShaderStatus, new { }, null, CancellationToken.None);
        Check(shader.Status == BackendResultStatuses.Ok && shader.Data is not null, "system.shader.status 返回 ok");
        Check(BackendJson.Deserialize<ShaderStatus>(shader.Data!.Value).DriverAdvice is not null, "着色器状态对象可反序列化");

        var pagefile = await client.InvokeAsync(BackendCommands.SystemPagefileStatus, new { }, null, CancellationToken.None);
        Check(pagefile.Status == BackendResultStatuses.Ok && pagefile.Data is not null, "system.pagefile.status 返回 ok");
        Check(BackendJson.Deserialize<PagefileStatusDto>(pagefile.Data!.Value).Info is not null, "虚拟内存状态对象可反序列化");

        var hibernate = await client.InvokeAsync(BackendCommands.SystemPowerHibernateGet, new { }, null, CancellationToken.None);
        Check(hibernate.Status == BackendResultStatuses.Ok, "system.power.hibernate.get 返回 ok");

        // 缺少盘符必须被后端拒绝（不触碰注册表写）。
        var badPagefile = await client.InvokeAsync(BackendCommands.SystemPagefileSet,
            new PagefileSetArgs { MinMb = 16384, MaxMb = 16384, DriveLetter = "" }, null, CancellationToken.None);
        Check(badPagefile.Status == BackendResultStatuses.Failed, "system.pagefile.set 缺少盘符时被拒绝");

        using var owned = CaptureOwnedProcess(client);
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Check(owned.HasExited, "本次系统维护后端排空后实际退出");
        await using var first = new BackendCaller(BackendModules.System);
        await using var second = new BackendCaller(BackendModules.System);
        Check((await first.InvokeAsync(BackendCommands.SystemTweaksBackups, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
            "系统调用器可读取后端备份清单");
        Check((await second.InvokeAsync(BackendCommands.SystemTweaksBackups, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok &&
            BackendSessions.Snapshot().Count(x => x.ModuleId == BackendModules.System) == 1,
            "不同系统服务共享一个后端，不重复创建进程");
        Check((await BackendSessions.DrainAllAsync(TimeSpan.FromSeconds(10))).Count == 0,
            "共享后端读取停止应答后正常排空");

        await CpuFrameProcessChecksAsync();
    }

    /// <summary>
    /// CPU/帧格后端：握手 + 状态快照 + 反复启停监控 + 只读核对/恢复分类 + 排空退出。
    /// 不开启任一真实优化（默认设置全关、无目标游戏），只验证只读/状态/生命周期路径。
    /// </summary>
    private static async Task CpuFrameProcessChecksAsync()
    {
        var exe = BackendPaths.Resolve(BackendModules.CpuFrame);
        Check(await RunExitCodeAsync(exe, ["--describe"]) == 0, "CPU/帧格后端 --describe 正常（白名单与处理器一致）");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameMonitorStart),
            "cpuframe.monitor.start 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameReconcile), "cpuframe.reconcile 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameBoostApply), "cpuframe.boost.apply 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameTopology), "cpuframe.topology 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameAffinityCheck), "cpuframe.affinity.check 在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.CpuFrame, BackendCommands.CpuFrameMemoryLimitDetect), "cpuframe.memorylimit.detect 在白名单内");
        Check(BackendModules.IsReadOnly(BackendModules.CpuFrame, BackendCommands.CpuFrameTopology), "cpuframe.topology 为只读");
        Check(BackendModules.IsMutating(BackendModules.CpuFrame, BackendCommands.CpuFrameBoostApply), "cpuframe.boost.apply 视为写入");
        Check(!BackendModules.RequiresWriteIntent(BackendModules.CpuFrame, BackendCommands.CpuFrameRecover),
            "cpuframe.recover 不登记新执行意图");
        Check(BackendModules.RecoverCommand(BackendModules.CpuFrame) == BackendCommands.CpuFrameRecover, "cpuframe 恢复命令已登记");

        await using var client = await BackendClient.StartAsync(BackendModules.CpuFrame, CancellationToken.None);
        var status = await client.InvokeAsync(BackendCommands.CpuFrameStatus, new { }, null, CancellationToken.None);
        Check(status.Status == BackendResultStatuses.Ok && status.Data is not null, "cpuframe.status 返回 ok");
        var snapshot = BackendJson.Deserialize<CpuFrameStatusSnapshot>(status.Data!.Value);
        Check(snapshot.Module == BackendModules.CpuFrame, "状态快照模块为 cpuframe");
        Check(!snapshot.MonitorRunning, "未显式启动时监控为停止（不自动跑真实优化）");

        var start = await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, CancellationToken.None);
        Check(start.Status == BackendResultStatuses.Ok, "cpuframe.monitor.start 成功");
        var stop = await client.InvokeAsync(BackendCommands.CpuFrameMonitorStop, new { }, null, CancellationToken.None);
        Check(stop.Status == BackendResultStatuses.Ok, "cpuframe.monitor.stop 成功");
        Check((await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok
            && (await client.InvokeAsync(BackendCommands.CpuFrameMonitorStop, new { }, null, CancellationToken.None)).Status == BackendResultStatuses.Ok,
            "监控可重复启停");

        var reconcile = await client.InvokeAsync(BackendCommands.CpuFrameReconcile, new { }, null, CancellationToken.None);
        Check(reconcile.Status == BackendResultStatuses.Ok && reconcile.Data is not null, "cpuframe.reconcile 返回 ok");
        var reconcileSnapshot = BackendJson.Deserialize<ReconcileSnapshot>(reconcile.Data!.Value);
        Check(reconcileSnapshot.Module == BackendModules.CpuFrame && reconcileSnapshot.RecoveryScopes.Count > 0,
            "核对返回帧格恢复范围");

        var badScope = await client.InvokeAsync(BackendCommands.CpuFrameRecover, new RecoverArgs { Scope = "nope" }, null, CancellationToken.None);
        Check(badScope.Status == BackendResultStatuses.Failed, "未知恢复范围被拒绝");

        using var owned = CaptureOwnedProcess(client);
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Check(owned.HasExited, "本次 CPU/帧格后端排空后实际退出");
    }

    /// <summary>
    /// 运行库后端：在隔离数据根下握手 + 只读状态/扫描 + 排空
    /// （绝不开启/关闭拦截，绝不重装或卸载运行库；不触碰真实防护状态）。
    /// </summary>
    private static async Task RuntimeProcessChecksAsync()
    {
        var exe = BackendPaths.Resolve(BackendModules.Runtime);

        // 静态白名单/超时检查（不启动进程、不写入）。
        Check(!BackendModules.IsAllowed(BackendModules.Runtime, "runtime.redist.install"),
            "已废弃的运行库安装命令不在白名单内");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeGuardEnable) &&
              BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeGuardDisable),
            "运行库拦截开关在白名单内");
        Check(!BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeRepair),
            "运行库修复已从命令白名单移除");

        using var isolation = BackendIsolation.Create();
        Check(await RunExitCodeAsync(exe, ["--describe"]) == 0, "运行库后端 --describe 正常（白名单与处理器一致）");

        await using var client = await BackendClient.StartAsync(BackendModules.Runtime, CancellationToken.None);

        // 读取机器级防护状态需要对 ProgramData 强化目录的 ACL 读取权限（需管理员）。
        // 未提权时如实记为 SKIP，不伪造成功；判定逻辑本身由 RuntimeGuardSafetyChecks 用注入夹具覆盖。
        var status = await client.InvokeAsync(BackendCommands.RuntimeStatus, new { }, null, CancellationToken.None);
        if (status.Status == BackendResultStatuses.Ok && status.Data is not null)
        {
            var guardStatus = BackendJson.Deserialize<RuntimeGuardStatus>(status.Data.Value);
            Check(guardStatus.IfeoTotal > 0, "运行库防护状态含 IFEO 名单总数");
            Check(guardStatus.InstalledRedists is not null, "运行库防护状态含已装运行库列表");
        }
        else if (IsElevationLimited(status.Message))
        {
            Console.WriteLine("  [SKIP] runtime.status 需管理员读取机器级防护状态（未提权）；已按失败如实返回，不折算成功。");
        }
        else
        {
            throw new Exception($"官方后端合同检查失败：runtime.status 未返回 ok 且非权限受限｜status={status.Status}｜message={status.Message}");
        }

        using var owned = CaptureOwnedProcess(client);
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Check(owned.HasExited, "本次运行库后端排空后实际退出");
    }

    private static Process CaptureOwnedProcess(BackendClient client)
    {
        var process = (Process)typeof(BackendClient).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        return Process.GetProcessById(process.Id);
    }

    private static bool IsElevationLimited(string message) =>
        message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("拒绝访问", StringComparison.Ordinal) ||
        message.Contains("无权", StringComparison.Ordinal);

    /// <summary>为后端进程设置隔离数据根（临时目录 + 标记文件 + 环境变量），Dispose 时恢复。</summary>
    private sealed class BackendIsolation : IDisposable
    {
        private readonly string _root;
        private readonly string? _previousRoot;
        private readonly string? _previousId;

        private BackendIsolation(string root, Guid id)
        {
            _root = root;
            _previousRoot = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT");
            _previousId = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID");
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        }

        public static BackendIsolation Create()
        {
            var id = Guid.NewGuid();
            var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_BackendIsolation_" + id.ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
            return new BackendIsolation(root, id);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", _previousRoot);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", _previousId);
            try
            {
                var full = Path.GetFullPath(_root);
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    private static void ParseRaw(Guid session, Guid request, string type, string module, string payload)
    {
        var json = $$"""{"protocol":"{{BackendProtocol.ProtocolVersion}}","type":"{{type}}","module":"{{module}}","sessionId":"{{session:D}}","requestId":"{{request:D}}","payload":{{payload}}}""";
        var seq = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json));
        BackendProtocol.TryParseFrame(seq, out _);
    }

    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<int> RunExitCodeAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("官方后端合同检查失败：" + description);
        _passed++;
    }
}
