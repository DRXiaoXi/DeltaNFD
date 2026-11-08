using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Execution.Procman;

/// <summary>
/// 隔离检查：帧格「让系统忽略三角洲定时器分辨率请求」的位语义、落盘顺序、恢复与失败路径。
/// 全部使用假 API，不触碰任何真实进程或系统定时器。
/// </summary>
internal static class FrameTimerIgnoreChecks
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }
    private static readonly ProcmanProcessIdentity Id = new(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
    private static readonly ProcmanProcessIdentity Other = new(54321, DateTime.UtcNow, @"C:\Windows\System32\notepad.exe");

    private sealed class Lab : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-timer-ignore-" + Guid.NewGuid().ToString("N"));
        internal string Journal => Path.Combine(Root, "frame-game-timer-ignore.json");
        internal readonly ProcessPropertyGate Owners;
        internal readonly List<string> Order = [];
        internal readonly FakeProbe ProbeApi = new();
        internal readonly Api Api;
        internal readonly FrameTimerLegacyProbe Probe;
        internal readonly FrameGameTimerIgnore Controller;
        internal Lab()
        {
            Directory.CreateDirectory(Root);
            Owners = new(Path.Combine(Root, "owners.json"));
            ProbeApi.Log = name => Order.Add(name);
            Api = new Api { Log = name => Order.Add(name) };
            Probe = new FrameTimerLegacyProbe(ProbeApi);
            Controller = new(Journal, Owners, Api, Probe);
        }
        internal bool Sync(IReadOnlyList<ProcmanProcessIdentity>? ids = null, bool supported = true, Func<ProcmanProcessIdentity, bool>? allow = null)
            => Controller.Sync(ids ?? [Id], allow ?? (_ => true), "等待三角洲启动，未施加定时器请求忽略", supported);
        internal bool Restore(bool supported = true) => Controller.Sync([], _ => false, "已恢复", supported);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class Api : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(0, 0);
        internal readonly List<FrameTimerPowerState> Writes = [];
        internal Action<string>? Log;
        internal bool DenyWrite;
        internal bool WrongReadback;
        internal string Identity = "same";
        internal Action<FrameTimerPowerState>? BeforeWrite;
        public string IdentityState(ProcmanProcessIdentity id) => Identity;
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id)
        {
            if (Identity != "same") throw new IOException("fake identity is not current");
            return new Handle(this);
        }
        private sealed class Handle(Api api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                api.Log?.Invoke("bit.write");
                api.BeforeWrite?.Invoke(state);
                if (api.DenyWrite) throw new IOException("fake access denied");
                api.Writes.Add(state);
                api.State = api.WrongReadback ? new(state.Control, state.State & ~FrameTimerPowerState.IgnoreTimer) : state;
            }
            public void Dispose() { }
        }
    }

    private sealed class FakeProbe : IFrameTimerReachabilityApi
    {
        internal double? Value = 1.0;
        internal int Calls;
        internal Action<string>? Log;
        public double? MeasureRequestlessGranularityMs()
        {
            Calls++;
            Log?.Invoke("probe");
            return Value;
        }
    }

    internal static void Run()
    {
        _checks = 0;
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!defaults.FrameGameTimerIgnoreEnabled, "新功能默认关闭，升级不自动开启");

        // 1) 非 Windows 11：不写、不报成功、状态明确。
        using (var lab = new Lab())
        {
            Check(lab.Sync(supported: false), "不支持时无记录应安全返回");
            Check(lab.Api.Writes.Count == 0, "不支持时不写入任何进程");
            Check(!lab.Controller.Snapshot.Supported && lab.Controller.Snapshot.Message.Contains("Windows 11"),
                "不支持时快照标记不支持并说明原因");
        }

        // 2) 身份门禁：只允许三角洲主进程。
        using (var lab = new Lab())
        {
            Check(!lab.Sync([Other]), "非三角洲身份必须整体拒绝");
            Check(lab.Api.Writes.Count == 0, "身份不符时零写入");
            Check(lab.Controller.Snapshot.HasError, "身份不符必须报错而不是静默");
        }

        // 3) 正常施加：落盘先于写入、位语义、位 0x1 保全。
        using (var lab = new Lab())
        {
            lab.Api.State = new(1, 1); // 预先存在 EcoQoS(0x1) 控制位
            lab.Api.BeforeWrite = state =>
            {
                var records = JsonSerializer.Deserialize<List<FrameGameTimerIgnore.Backup>>(File.ReadAllText(lab.Journal))!;
                Check(records.Count == 1 && records[0].Identity == Id, "原生写入前必须已落盘原值记录");
                Check((records[0].AppliedControl & 4u) != 0 && (records[0].AppliedState & 4u) != 0,
                    "落盘记录应保存本功能写入值");
                Check(records[0].OriginalControl == 1 && records[0].OriginalState == 1, "落盘记录应保存原值");
            };
            Check(lab.Sync(), "正常施加应成功");
            Check(lab.Api.Writes.Count == 1, "首次施加写一次");
            Check(lab.Api.State.Control == 5 && lab.Api.State.State == 5, "只应额外设置 0x4 位");
            Check((lab.Api.State.State & ~lab.Api.State.Control) == 0, "写入的 StateMask 必须落在 ControlMask 内");
            Check((lab.Api.State.Control & 1u) == 1 && (lab.Api.State.State & 1u) == 1, "既有 EcoQoS 位不得被清除");
            Check(lab.Controller.Snapshot.AppliedCount == 1 && lab.Controller.Snapshot.Maintaining, "快照应记录实际生效数");
            Check(lab.Controller.Snapshot.Message.Contains("1/1"), "状态文字应含实际生效计数");

            // 4) 幂等：已生效不重复写。
            Check(lab.Sync(), "重复同步应成功");
            Check(lab.Api.Writes.Count == 1, "已生效时不得重复写入");

            // 5) 关闭后恢复原值，且只还原 0x4 位。
            Check(lab.Restore(), "关闭后恢复应成功");
            Check(lab.Api.Writes.Count == 2, "恢复写一次");
            Check(lab.Api.State.Control == 1 && lab.Api.State.State == 1, "恢复后应回到原值并保留 EcoQoS 位");
            Check(!File.Exists(lab.Journal) || File.ReadAllText(lab.Journal) == "[]", "恢复完成后不得残留原值记录");
            Check(lab.Controller.Snapshot.RestorePending == false, "恢复后不应再显示待恢复");
        }

        // 6) 外部改动：保留记录、报冲突、不覆盖。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "施加成功");
            lab.Api.State = new(4, 0); // 外部把 0x4 的控制位保留、状态位清掉
            Check(!lab.Restore(), "外部变更时应如实失败");
            Check(lab.Controller.Snapshot.RestorePending && lab.Controller.Snapshot.HasError, "外部变更后保留记录并标记错误");
            Check(lab.Api.State.Control == 4 && lab.Api.State.State == 0, "不得覆盖外部变更");
        }

        // 7) 进程已退出：清除记录、无恢复义务。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "施加成功");
            lab.Api.Identity = "missing";
            Check(lab.Restore(), "进程消失时应安全清除记录");
            Check(!File.Exists(lab.Journal) || File.ReadAllText(lab.Journal) == "[]", "进程消失后应清除记录");
        }

        // 8) 身份无法确认：保留记录并报错。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "施加成功");
            lab.Api.Identity = "other";
            Check(!lab.Restore(), "身份无法确认时应失败并保留记录");
            Check(lab.Controller.Snapshot.RestorePending, "身份无法确认时保留原值记录");
        }

        // 9) 写入被拒：如实失败、不报成功、不重复重试。
        using (var lab = new Lab())
        {
            lab.Api.DenyWrite = true;
            Check(!lab.Sync(), "写入被拒必须如实失败");
            Check(lab.Api.Writes.Count == 0, "被拒时不得记录成功写入");
            Check(lab.Controller.Snapshot.HasError, "被拒后快照应报错");
            Check(!lab.Sync(), "同一进程被拒后本次不再重试");
            Check(lab.Api.Writes.Count == 0, "重试被阻断时仍不得写入");
        }

        // 10) 回读不符：报错并保留记录。
        using (var lab = new Lab())
        {
            lab.Api.WrongReadback = true;
            Check(!lab.Sync(), "回读未确认必须失败");
            Check(lab.Controller.Snapshot.HasError, "回读未确认应报错");
            Check(File.Exists(lab.Journal), "回读失败后应保留原值记录供恢复");
        }

        // 11) 未授权（门禁变化）：不写。
        using (var lab = new Lab())
        {
            Check(!lab.Sync(allow: _ => false), "门禁拒绝时必须失败");
            Check(lab.Api.Writes.Count == 0, "门禁拒绝时零写入");
        }

        // 12) 记录损坏/非法：不写、报错。
        using (var lab = new Lab())
        {
            File.WriteAllText(lab.Journal, "{not json");
            Check(!lab.Sync(), "损坏记录必须拒绝");
            Check(lab.Api.Writes.Count == 0, "损坏记录时零写入");
            Check(lab.Controller.Snapshot.HasError, "损坏记录应报错");
        }
        using (var lab = new Lab())
        {
            var bad = new FrameGameTimerIgnore.Backup(Id, 0, 0, 0, 0); // 写入值缺 0x4
            File.WriteAllText(lab.Journal, JsonSerializer.Serialize(new[] { bad }));
            Check(!lab.Sync(), "非法记录必须拒绝");
            Check(lab.Api.Writes.Count == 0, "非法记录时零写入");
        }

        // 13) 100ns→ms 换算（原 OfflineTimerResolutionChecks 的断言并入此处，删除后仍覆盖）。
        Check(Math.Abs(DeltaNFD.Native.ProcessPerf.ConvertTimerResolution100nsToMilliseconds(5_000) - 0.5) < 1e-9, "5000×100ns = 0.5ms");
        Check(Math.Abs(DeltaNFD.Native.ProcessPerf.ConvertTimerResolution100nsToMilliseconds(10_000) - 1.0) < 1e-9, "10000×100ns = 1.0ms");
        Check(Math.Abs(DeltaNFD.Native.ProcessPerf.ConvertTimerResolution100nsToMilliseconds(156_250) - 15.625) < 1e-9, "156250×100ns = 15.625ms");

        // 14) 无目标时的等待文案。
        using (var lab = new Lab())
        {
            Check(lab.Sync([]), "无目标时应安全返回");
            Check(lab.Controller.Snapshot.Message.Contains("等待三角洲启动"), "无目标时状态应说明等待三角洲启动");
            Check(lab.Controller.Snapshot.InstanceCount == 0 && lab.Controller.Snapshot.AppliedCount == 0, "无目标时生效计数为零");
        }

        // 15) 第二实例互斥交接：共享日志的两个控制器不丢原值。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "第一实例施加成功");
            var second = new FrameGameTimerIgnore(lab.Journal, lab.Owners, lab.Api,
                new FrameTimerLegacyProbe(lab.ProbeApi));
            Check(second.PendingCount == 1, "第二实例能读到第一实例落盘的原值");
            Check(second.Sync([], _ => false, "已恢复", true), "第二实例可完成恢复交接");
            Check(lab.Api.State.Control == 0 && lab.Api.State.State == 0, "交接后原值已恢复");
            Check(second.PendingCount == 0, "交接后不再残留记录");
        }

        // 16) 页面状态文字分支（只读展示层）。
        var now = DateTimeOffset.UtcNow;
        Check(FrameStatusPresentation.TimerIgnore(false, true, null, now, "") == "未启用（当前系统定时器读数不可用）", "未选择时仍保留只读读数入口");
        Check(FrameStatusPresentation.TimerIgnore(true, false, null, now, "") == "已选择 · 等待开启一键帧格模式（当前系统定时器读数不可用）", "已选择但帧格未激活时如实说明与显示未知读数");
        var unsupported = new ProcmanStatusSnapshot
        {
            Module = BackendModules.Procman,
            UpdatedUtc = now,
            FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = false, UpdatedUtc = now },
        };
        Check(FrameStatusPresentation.TimerIgnore(true, true, unsupported, now, "").Contains("Windows 11"), "不支持的快照显示原因");
        var stale = new ProcmanStatusSnapshot
        {
            Module = BackendModules.Procman,
            UpdatedUtc = now.AddMinutes(-1),
            FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = true, UpdatedUtc = now.AddMinutes(-1) },
        };
        Check(FrameStatusPresentation.TimerIgnore(true, true, stale, now, "").Contains("等待后端核对"), "过期快照不冒充生效");
        var fresh = new ProcmanStatusSnapshot
        {
            Module = BackendModules.Procman,
            UpdatedUtc = now,
            RulesEngineRunning = true,
            FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = true, Maintaining = true, AppliedCount = 1, Message = "已让系统忽略 1/1 个三角洲进程的定时器分辨率请求（回读确认）", UpdatedUtc = now },
        };
        Check(FrameStatusPresentation.TimerIgnore(true, true, fresh, now, "").Contains(fresh.FrameTimerIgnore.Message), "新鲜快照直接展示后端实际结论");
        var readable = new ProcmanStatusSnapshot
        {
            Module = BackendModules.Procman,
            UpdatedUtc = now,
            RulesEngineRunning = true,
            FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = true, Maintaining = true, AppliedCount = 1, Message = "已忽略", SystemCurrentMilliseconds = 1.0, UpdatedUtc = now },
        };
        Check(FrameStatusPresentation.TimerIgnore(true, true, readable, now, "").Contains("当前系统定时器读数 1 ms"),
            "保留只读系统定时器读数展示");
        ProcmanStatusSnapshot Present(FrameTimerIgnoreSnapshot state, string module = BackendModules.Procman) =>
            new() { Module = module, UpdatedUtc = now, RulesEngineRunning = true, FrameTimerIgnore = state };
        var appliedClock = Present(readable.FrameTimerIgnore with
        {
            InstanceCount = 1, IgnoreApplied = true, LegacyGlobalReachable = true,
        });
        var appliedText = FrameStatusPresentation.TimerIgnore(true, true, appliedClock, now, "");
        Check(appliedText.Contains("已忽略三角洲的精度请求") && appliedText.Contains("最佳时钟"),
            "已施加时须说明三角洲改用系统当前最佳时钟");
        Check(appliedText.Contains("当前系统定时器读数 1 ms"), "已施加时仍须保留只读系统读数");
        Check(!FrameStatusPresentation.TimerIgnore(true, true, appliedClock, now.AddSeconds(11), "").Contains("已忽略三角洲的精度请求"),
            "过期的已施加快照不能冒充当前生效");
        var skipped = Present(readable.FrameTimerIgnore with
        {
            InstanceCount = 1, IgnoreApplied = false, LegacyGlobalReachable = false,
        });
        Check(FrameStatusPresentation.TimerIgnore(true, true, skipped, now, "").Contains("已跳过忽略位"),
            "时钟过粗时应明确说明已跳过忽略位");
        Check(FrameStatusPresentation.TimerIgnore(true, true, Present(readable.FrameTimerIgnore with { InstanceCount = 1, LegacyGlobalReachable = null }), now, "").Contains("未确认"),
            "可达性未确认时须说明未施加");
        foreach (var selected in new[] { false, true })
        foreach (var active in new[] { false, true })
        {
            Check(FrameStatusPresentation.TimerIgnore(selected, active, readable, now, "").Contains("当前系统定时器读数 1 ms"),
                "只读读数不依赖功能或帧格开关");
            Check(FrameStatusPresentation.TimerIgnore(selected, active, readable, now.AddSeconds(11), "").Contains("读数不可用"),
                "所有开关组合均不把过期读数当作当前值");
            Check(FrameStatusPresentation.TimerIgnore(selected, active, readable, now, "lost").Contains("读数不可用"),
                "连接未知时不显示旧读数");
        }
        var unsupportedReadable = Present(readable.FrameTimerIgnore with { Supported = false });
        Check(FrameStatusPresentation.TimerIgnore(true, true, unsupportedReadable, now, "").Contains("当前系统定时器读数 1 ms"),
            "系统不支持进程策略时仍可展示独立的只读读数");
        var wrongModule = Present(readable.FrameTimerIgnore, BackendModules.System);
        Check(FrameStatusPresentation.TimerIgnore(false, false, wrongModule, now, "").Contains("读数不可用"),
            "不接受错误模块的读数");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
        {
            var invalid = Present(readable.FrameTimerIgnore with { SystemCurrentMilliseconds = value });
            Check(FrameStatusPresentation.TimerIgnore(false, false, invalid, now, "").Contains("读数不可用"),
                "非有效正数不能冒充实际读数");
        }
        var unreadable = new ProcmanStatusSnapshot
        {
            Module = BackendModules.Procman,
            UpdatedUtc = now,
            RulesEngineRunning = true,
            FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = true, Maintaining = true, AppliedCount = 1, Message = "已忽略", SystemCurrentMilliseconds = null, UpdatedUtc = now },
        };
        Check(FrameStatusPresentation.TimerIgnore(true, true, unreadable, now, "").Contains("当前系统定时器读数不可用"),
            "读数不可读时如实说明");

        // 18) 时钟可达门禁：先实测「无请求进程的等待粒度」，只有够细才写忽略位。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "可达时应成功施加");
            Check(string.Join(">", lab.Order) == "probe>bit.write",
                "顺序必须是 先实测时钟可达性 → 最后写忽略位，实际：" + string.Join(">", lab.Order));
            Check(lab.Controller.Snapshot.LegacyGlobalReachable == true && lab.Controller.Snapshot.IgnoreApplied,
                "可达时才标记忽略位已施加");
            Check(lab.Controller.Snapshot.Message.Contains("已忽略"), "状态文字应说明已忽略");
        }

        // 19) 时钟过粗（按进程语义）：拒绝施加、不写位、如实说明。
        using (var lab = new Lab())
        {
            lab.ProbeApi.Value = 15.6;
            Check(lab.Sync(), "不可达时应安全返回而不是报错");
            Check(lab.Api.Writes.Count == 0, "时钟过粗时绝不能写忽略位（否则三角洲回落到 15.625ms）");
            Check(lab.Controller.Snapshot.IgnoreApplied == false, "不可达时快照不得声称已忽略");
            Check(lab.Controller.Snapshot.LegacyGlobalReachable == false, "快照应记录可达性实测结论");
            Check(lab.Controller.Snapshot.Message.Contains("未施加"),
                "状态文字应说明未施加，实际：" + lab.Controller.Snapshot.Message);
            Check(!File.Exists(lab.Journal) || File.ReadAllText(lab.Journal) == "[]", "未施加时不得留下原值记录");
        }

        // 20) 探针失败（无法判定）：同样不施加。
        using (var lab = new Lab())
        {
            lab.ProbeApi.Value = null;
            Check(lab.Sync(), "探针失败时应安全返回");
            Check(lab.Api.Writes.Count == 0, "无法判定时钟是否够细时不得写忽略位");
            Check(lab.Controller.Snapshot.LegacyGlobalReachable is null, "无法判定时快照记录为未知");
            Check(lab.Controller.Snapshot.Message.Contains("未施加"), "无法判定时状态文字应说明未施加");
        }

        // 21) 三角洲退出（无目标）：不写位、状态回到待机。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "先施加成功");
            Check(lab.Sync([]), "目标消失时应安全恢复");
            Check(lab.Controller.Snapshot.InstanceCount == 0 && !lab.Controller.Snapshot.IgnoreApplied,
                "无目标时不得声称已忽略");
            Check(lab.Api.State.Control == 0 && lab.Api.State.State == 0, "无目标时应恢复原值");
        }

        // 22) 探针结果在新鲜期内复用（不每次 tick 都起子进程）。
        using (var lab = new Lab())
        {
            Check(lab.Sync(), "第一次施加成功");
            Check(lab.Sync(), "第二次同步成功");
            Check(lab.ProbeApi.Calls == 1, "新鲜期内只实测一次，实际次数：" + lab.ProbeApi.Calls);
        }

        // 23) 可达性从「可达」变为「不可达」：必须撤销已施加的位。
        using (var lab = new Lab())
        {
            lab.Api.State = new(0, 0);
            Check(lab.Sync(), "可达时施加成功");
            Check(lab.Api.State.Control == 4 && lab.Api.State.State == 4, "位已置位");
            lab.ProbeApi.Value = 15.6;
            lab.Probe.ResetForTests();
            Check(lab.Sync(), "变为不可达时应能安全撤销");
            Check(lab.Api.State.Control == 0 && lab.Api.State.State == 0, "不可达时必须撤销忽略位，恢复三角洲自身精度");
            Check(!lab.Controller.Snapshot.IgnoreApplied, "撤销后不得再声称已忽略");
            Check(!File.Exists(lab.Journal) || File.ReadAllText(lab.Journal) == "[]", "撤销后不得残留原值记录");
        }

        // 26) 契约：状态快照可序列化并包含本功能字段。
        var status = new ProcmanStatusSnapshot { FrameTimerIgnore = new FrameTimerIgnoreSnapshot { Supported = true, AppliedCount = 2, Message = "m" } };
        var json = JsonSerializer.Serialize(status);
        Check(json.Contains("FrameTimerIgnore") && json.Contains("AppliedCount"), "进程管理状态快照应包含定时器忽略状态");

        Console.WriteLine($"定时器请求忽略检查通过（{_checks} 项）：默认关闭、Win11 门禁、身份门禁、落盘先于写入、位 0x4 独立与位 0x1 保全、幂等、恢复、外部变更、进程消失、身份变化、写入被拒、回读不符、门禁拒绝、记录损坏、100ns 换算、等待文案、双实例交接、页面状态分支、实测先于写位、时钟过粗拒绝施加、探针失败拒绝、目标消失恢复、探针新鲜期复用、可达性变化撤销、契约。未触碰真实进程。");
    }
}
