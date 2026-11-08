using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Execution.Procman;

/// <summary>
/// 隔离检查：「定时器精度请求忽略列表」的名称解析、安全边界、引擎施加/恢复与快照。
/// 全部使用假 API + 隔离数据目录，不触碰任何真实进程。
/// </summary>
internal static class TimerIgnoreListChecks
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }

    private static readonly ProcmanProcessIdentity App = new(2468, DateTime.UtcNow, @"C:\Users\XiaoXi\Documents\timer-precision\TimerPrecision.App.exe");
    private static readonly ProcmanProcessIdentity Browser = new(1357, DateTime.UtcNow, @"C:\Program Files\Browser\browser.exe");
    private static readonly ProcmanProcessIdentity SystemApp = new(99, DateTime.UtcNow, @"C:\Windows\System32\notepad.exe");
    private static readonly ProcmanProcessIdentity Ace = new(77, DateTime.UtcNow, @"C:\Program Files\AntiCheatExpert\ACE-Service64.exe");
    private static readonly ProcmanProcessIdentity Tool = new(11, DateTime.UtcNow, @"C:\Users\XiaoXi\AppData\Local\DeltaNFD\DeltaNFD.exe");
    private static readonly ProcmanProcessIdentity Relative = new(12, DateTime.UtcNow, @"notepad.exe");

    private sealed class FakeProbe : IFrameTimerReachabilityApi
    {
        internal double? Value = 1.0;
        public double? MeasureRequestlessGranularityMs() => Value;
    }

    private sealed class FakeApi : IFrameTimerIgnoreApi
    {
        internal FrameTimerPowerState State = new(0, 0);
        internal readonly List<FrameTimerPowerState> Writes = [];
        internal string Identity = "same";
        internal Func<ProcmanProcessIdentity, bool> Admission = _ => true;
        internal bool FailRestore;
        public string IdentityState(ProcmanProcessIdentity id) => Identity;
        public IFrameTimerIgnoreHandle OpenForApply(ProcmanProcessIdentity id)
        {
            if (!Admission(id)) throw new IOException("fake current-user/session/service admission denied");
            return Open(id);
        }
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id)
        {
            if (Identity != "same") throw new IOException("fake identity is not current");
            return new Handle(this);
        }
        private sealed class Handle(FakeApi api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.FailRestore && !state.Forced) throw new IOException("fake restore write denied");
                api.Writes.Add(state); api.State = state;
            }
            public void Dispose() { }
        }
    }

    internal static void Run()
    {
        _checks = 0;
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(defaults.TimerIgnoreExtraProcesses is { Count: 0 }, "忽略列表默认应为空");

        // 1) 名称解析：去空、去重、忽略大小写。
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-timer-ignore-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
            AppDataPaths.ConfigureIsolatedDataRoot(root, token);
            CheckAdmissionFacts();
            CheckSharedBitResponsibility(root);

            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses =
                new List<string> { "  TimerPrecision.App.exe  ", "", "timerprecision.app.EXE", "browser.exe" });
            var names = ProcmanEngine.TimerIgnoreListNames();
            Check(names.Count == 2, "名称解析应去空去重并忽略大小写，实际 " + names.Count + "：" + string.Join("/", names));
            Check(names.Contains("TimerPrecision.App.exe") && names.Contains("browser.exe"), "名称解析结果应保留原始大小写：" + string.Join("/", names));

            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = null!);
            Check(ProcmanEngine.TimerIgnoreListNames().Count == 0, "设置里显式 null 应按空列表处理");

            // 2) 安全边界：系统/Windows/反作弊/本工具自身/相对路径一律不允许。
            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = new List<string> { "TimerPrecision.App.exe" });
            Check(ProcmanEngine.IsTimerIgnoreListTarget(App), "普通用户进程应可作为列表目标");
            Check(!ProcmanEngine.IsTimerIgnoreListIdentityAllowed(SystemApp), "Windows 目录进程不得操作");
            Check(!ProcmanEngine.IsTimerIgnoreListIdentityAllowed(Ace), "反作弊进程不得操作");
            Check(!ProcmanEngine.IsTimerIgnoreListIdentityAllowed(Tool), "本工具自身不得操作");
            Check(!ProcmanEngine.IsTimerIgnoreListIdentityAllowed(Relative), "相对路径身份不得操作");
            Check(!ProcmanEngine.IsTimerIgnoreListTarget(Browser), "不在列表里的进程不是目标");
            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = new List<string> { "browser.exe", "notepad.exe" });
            Check(ProcmanEngine.IsTimerIgnoreListTarget(Browser), "列表里的浏览器应成为目标");
            Check(!ProcmanEngine.IsTimerIgnoreListTarget(SystemApp), "即便名称命中，Windows 目录进程仍不是目标");

            // 3) 引擎接线：施加 → 快照/记录 → 移除名称后仍能恢复。
            var probeApi = new FakeProbe();
            var api = new FakeApi();
            var probe = new FrameTimerLegacyProbe(probeApi);
            using (var engine = new ProcmanEngine(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
                timerIgnoreApi: api, timerProbe: probe))
            {
                engine.BackgroundLoopDisabledForTests = true;
                engine.TimerIgnoreListTargetsForTests = () => [App];
                AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = new List<string> { "TimerPrecision.App.exe" });
                Check(engine.TimerIgnoreListOnceForTests(), "列表有名称且探针可达时应施加忽略位：" + engine.Snapshot().TimerIgnoreList.Message);
                Check((api.State.Control & FrameTimerPowerState.IgnoreTimer) != 0
                    && (api.State.State & FrameTimerPowerState.IgnoreTimer) != 0, "忽略位应已置位");
                var snap = engine.Snapshot().TimerIgnoreList;
                Check(snap.IgnoreApplied && snap.AppliedCount == 1, "快照应记录已施加：" + snap.Message);
                Check(snap.Message.Contains("忽略列表"), "状态文字应写明是忽略列表：" + snap.Message);
                var journal = Path.Combine(root, "timer-ignore-list.json");
                Check(File.Exists(journal), "施加前应先落盘原值记录");

                // 移除名称：没有目标 → 走恢复路径；记录必须仍可校验（validate 与 accept 分开）。
                AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = new List<string>());
                Check(engine.TimerIgnoreListOnceForTests(), "移除名称后应恢复成功：" + engine.Snapshot().TimerIgnoreList.Message);
                Check(api.State.Control == 0 && api.State.State == 0, "移除名称后应恢复原始值，实际 " + api.State.Control + "/" + api.State.State);
                Check(!File.Exists(journal) || JsonSerializer.Deserialize<List<object>>(File.ReadAllText(journal))?.Count == 0,
                    "恢复后原值记录应清空");
                Check(engine.Snapshot().TimerIgnoreList.BackupCount == 0, "恢复后不得再有未恢复记录");

                // 探针不可达（系统 tick 过粗）时拒绝施加，避免把目标进程压到 15.625ms。
                probeApi.Value = 15.6;
                probe.Invalidate(); // 探针有缓存：模拟系统 tick 变粗后必须重新实测
                AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = new List<string> { "TimerPrecision.App.exe" });
                var writesBefore = api.Writes.Count;
                engine.TimerIgnoreListOnceForTests();
                Check(api.Writes.Count == writesBefore, "探针不可达时不得写忽略位");
                Check(!engine.Snapshot().TimerIgnoreList.IgnoreApplied, "探针不可达时不得显示已施加：" + engine.Snapshot().TimerIgnoreList.Message);

                // 契约：状态快照带独立字段。
                var status = engine.Snapshot();
                Check(status.TimerIgnoreList is not null, "进程管理快照应包含忽略列表状态");
                Check(!status.FrameTimerIgnore.IgnoreApplied, "忽略列表不得影响三角洲那项的状态");
            }
            CheckSyncFailurePropagation(root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine($"定时器忽略列表检查通过（{_checks} 项）：默认空、名称去空去重、显式 null 安全、系统/Windows/反作弊/本工具自身拒绝、名称命中、引擎施加与快照、移除名称后仍可恢复、探针不可达拒绝施加、契约字段。未触碰任何真实进程。");
    }

    private static void CheckAdmissionFacts()
    {
        Check(ProcmanIdentity.OrdinaryUserFactsAllowed(1, "user", false, 1, "user"), "当前用户当前会话普通进程允许");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(2, "user", false, 1, "user"), "其他会话拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(1, "other", false, 1, "user"), "其他用户拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(1, "user", true, 1, "user"), "当前用户的服务仍拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(null, "user", false, 1, "user"), "未知会话拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(1, null, false, 1, "user"), "未知用户拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(1, "user", null, 1, "user"), "未知服务状态拒绝");
        Check(!ProcmanIdentity.OrdinaryUserFactsAllowed(0, "user", false, 0, "user"), "服务会话零拒绝");
        bool Admit(IReadOnlyList<string>? groups, bool? scmService = false) =>
            ProcmanIdentity.OrdinaryUserAdmissionAllowed(1, "user", scmService, 1, "user", groups);
        Check(Admit(["S-1-5-4", "S-1-5-11"]), "完整交互登录token和已核验非服务状态允许");
        Check(!Admit(null), "TokenGroups未知拒绝");
        Check(!Admit([]), "TokenGroups空拒绝");
        Check(!Admit(["S-1-5-11"]), "没有交互登录组拒绝");
        Check(!Admit(["S-1-5-4", "S-1-5-6"]), "即使SCM无PID命中，服务登录组仍必须拒绝");
        Check(!Admit(["S-1-5-4", "S-1-5-80-123-456"]), "per-service SID身份保守拒绝");
        Check(!Admit(["S-1-5-4", "S-1-5-80-0"]), "ALL SERVICES组保守拒绝");
        Check(!Admit(["S-1-5-4"], true), "交互token也不得绕过SCM的服务PID命中");
        Check(!Admit(["S-1-5-4"], null), "SCM遗漏或注册服务状态无法完整核验时拒绝");
        Check(!Admit(["S-1-5-4", ""]), "组数据不完整拒绝");
    }

    private static void CheckSyncFailurePropagation(string root)
    {
        var dir = Path.Combine(root, "sync-failure-propagation");
        Directory.CreateDirectory(dir);
        AppSettingsStore.Update(s =>
        {
            s.FrameModeActive = false;
            s.FrameAutomaticMemoryEnabled = false;
            s.FrameGamePriorityEnabled = false;
            s.FrameGameTimerIgnoreEnabled = false;
            s.OptimalClockEnabled = false;
            s.TimerIgnoreExtraProcesses = ["TimerPrecision.App.exe"];
        });
        var api = new FakeApi { Admission = _ => false };
        using var engine = new ProcmanEngine(Path.Combine(dir, "changes.json"), Path.Combine(dir, "rules.json"),
            timerIgnoreApi: api, timerProbe: new FrameTimerLegacyProbe(new FakeProbe()));
        engine.BackgroundLoopDisabledForTests = true;
        engine.TimerIgnoreListTargetsForTests = () => [App];
        try
        {
            var rejected = engine.SyncFrameBackground();
            var rejectedState = engine.Snapshot().TimerIgnoreList;
            Check(!rejected.Success && rejectedState.HasError, "SyncFrameBackground应传播列表新施加被拒绝的失败");
            Check(rejected.Message.Contains(rejectedState.Message) && rejected.Message.Contains("fake current-user/session/service admission denied"),
                "新施加拒绝返回消息必须包含忽略列表的权威失败状态：" + rejected.Message);
            Check(api.Writes.Count == 0 && rejectedState.BackupCount == 0, "同步拒绝施加不得产生写入或虚假恢复责任");

            api.Admission = _ => true;
            var applied = engine.SyncFrameBackground();
            Check(applied.Success && engine.Snapshot().TimerIgnoreList.IgnoreApplied,
                "同步对照应成功施加列表：" + applied.Message);
            Check(applied.Message.Contains(engine.Snapshot().TimerIgnoreList.Message), "成功同步反馈也应包含列表状态");

            api.FailRestore = true;
            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = []);
            var failedRestore = engine.SyncFrameBackground();
            var pending = engine.Snapshot().TimerIgnoreList;
            Check(!failedRestore.Success && pending.HasError && pending.RestorePending && pending.BackupCount == 1,
                "移除列表后的恢复写入失败必须传播且保留责任");
            Check(failedRestore.Message.Contains(pending.Message) && failedRestore.Message.Contains("fake restore write denied"),
                "恢复失败返回消息必须包含忽略列表的权威恢复状态：" + failedRestore.Message);
            Check(api.State.Forced, "恢复写入拒绝时假进程仍保留施加位，不得冒称已恢复");

            api.FailRestore = false;
            var retried = engine.SyncFrameBackground();
            Check(retried.Success && engine.Snapshot().TimerIgnoreList.BackupCount == 0
                && api.State == new FrameTimerPowerState(0, 0), "恢复重试成功后应释放原值责任：" + retried.Message);
        }
        finally
        {
            api.Admission = _ => true;
            api.FailRestore = false;
            AppSettingsStore.Update(s => s.TimerIgnoreExtraProcesses = []);
        }
    }

    private static void CheckSharedBitResponsibility(string root)
    {
        var id = new ProcmanProcessIdentity(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
        foreach (var listFirst in new[] { false, true })
        {
            var dir = Path.Combine(root, "overlap-" + listFirst);
            Directory.CreateDirectory(dir);
            var owners = new ProcessPropertyGate(Path.Combine(dir, "owners.json"));
            var api = new FakeApi();
            var probe = new FrameTimerLegacyProbe(new FakeProbe());
            var framePath = Path.Combine(dir, "frame-game-timer-ignore.json");
            var listPath = Path.Combine(dir, "timer-ignore-list.json");
            var frame = new FrameGameTimerIgnore(framePath, owners, api, probe);
            var list = new FrameGameTimerIgnore(listPath, owners, api, probe,
                accept: _ => true, validate: _ => true, capability: "timer-ignore-list");
            var first = listFirst ? list : frame;
            var second = listFirst ? frame : list;
            Check(first.Sync([id], _ => true, "idle", true), "第一功能施加");
            var writes = api.Writes.Count;
            Check(!second.Sync([id], _ => true, "idle", true), "另一功能不得重叠施加");
            Check(second.PendingCount == 0 && api.Writes.Count == writes, "拒绝重叠不得新增备份/写入");
            Check(first.Sync([], _ => false, "idle", true), "最早原值持有者可恢复");
            Check(api.State == new FrameTimerPowerState(0, 0), "恢复最早0/0原值");
            Check(second.Sync([id], _ => true, "idle", true), "原功能释放后另一功能可接管");
            Check(second.Sync([], _ => false, "idle", true), "另一功能按自身能力恢复");
            using (var lease = owners.Enter(id, "timer-ignore-list"))
                Check(lease.Owner == "system", "恢复应清除共享owner，不得残留列表owner");

            // Simulate pre-fix dual journals, including one original that already contains 0x4.
            File.WriteAllText(framePath, JsonSerializer.Serialize(new[] { new FrameGameTimerIgnore.Backup(id, 0, 0, 4, 4) }));
            File.WriteAllText(listPath, JsonSerializer.Serialize(new[] { new FrameGameTimerIgnore.Backup(id, 4, 4, 4, 4) }));
            api.State = new(4, 4);
            writes = api.Writes.Count;
            Check(!frame.Sync([], _ => false, "idle", true) && !list.Sync([], _ => false, "idle", true), "旧双journal不得猜测原值恢复");
            Check(frame.PendingCount == 1 && list.PendingCount == 1 && api.Writes.Count == writes, "旧双journal应保留双方记录且不写入");
            Check(frame.Snapshot.Message.Contains("最早原值"), "双journal冲突应解释原值不确定");
            Check(!frame.Sync([id], _ => true, "idle", true) && frame.Snapshot.RestorePending
                && !frame.Snapshot.Maintaining, "双journal即使仍选中目标也必须显示未完成恢复责任");

            // Upgrade-era separate owner keys must not prevent cleanup of a confirmed
            // exited identity. Preserve the peer journal and a reused-PID identity.
            string OwnerKey(ProcmanProcessIdentity target, string capability) => ProcmanChangeLog.ChangeKey(
                target.Pid, target.StartTimeUtcTicks, target.ExecutablePath, capability);
            var reused = id with { StartTimeUtc = id.StartTimeUtc.AddTicks(1) };
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            ProcessPropertyGate.Claim Claim(string role) => new()
            {
                GamePid = self.Id, GameStart = self.StartTime.ToUniversalTime().Ticks, GameRole = role,
            };
            var legacyOwners = new Dictionary<string, ProcessPropertyGate.Claim>
            {
                [OwnerKey(id, "timer-ignore")] = Claim("timer-ignore"),
                [OwnerKey(id, "timer-ignore-list")] = Claim("timer-ignore-list"),
                [OwnerKey(reused, "timer-ignore-bit")] = Claim("timer-ignore"),
            };
            var ownerPath = Path.Combine(dir, "owners.json");
            File.WriteAllText(ownerPath, JsonSerializer.Serialize(legacyOwners));
            api.Identity = "unknown";
            Check(!frame.Sync([], _ => false, "idle", true) && frame.PendingCount == 1,
                "旧双owner身份未知不得清理责任");
            Check(JsonSerializer.Deserialize<Dictionary<string, ProcessPropertyGate.Claim>>(File.ReadAllText(ownerPath))!.Count == 3,
                "身份未知不得删除历史owner");
            api.Identity = "missing";
            var peerBefore = File.ReadAllText(listPath);
            Check(frame.Sync([], _ => false, "idle", true) && frame.PendingCount == 0,
                "确认旧身份退出后本功能应可清理双journal中的自身记录");
            var remaining = JsonSerializer.Deserialize<Dictionary<string, ProcessPropertyGate.Claim>>(File.ReadAllText(ownerPath))!;
            Check(!remaining.ContainsKey(OwnerKey(id, "timer-ignore")) && remaining.ContainsKey(OwnerKey(id, "timer-ignore-list"))
                && remaining.ContainsKey(OwnerKey(reused, "timer-ignore-bit")), "退出清理只删除本功能旧identity的owner");
            Check(File.ReadAllText(listPath) == peerBefore && list.PendingCount == 1,
                "清理本功能不得修改另一功能journal");
            Check(list.Sync([], _ => false, "idle", true) && list.PendingCount == 0,
                "另一功能也可独立清理自己已退出的责任");
            remaining = JsonSerializer.Deserialize<Dictionary<string, ProcessPropertyGate.Claim>>(File.ReadAllText(ownerPath))!;
            Check(remaining.Count == 1 && remaining.ContainsKey(OwnerKey(reused, "timer-ignore-bit")) && api.Writes.Count == writes,
                "双功能退出清理无原生写入且保留复用PID的新identity归属");
        }

        var policyDir = Path.Combine(root, "admission");
        Directory.CreateDirectory(policyDir);
        var fake = new FakeApi { Admission = _ => false };
        var controller = new FrameGameTimerIgnore(Path.Combine(policyDir, "timer-ignore-list.json"),
            new ProcessPropertyGate(Path.Combine(policyDir, "owners.json")), fake, new FrameTimerLegacyProbe(new FakeProbe()),
            accept: _ => true, validate: _ => true, capability: "timer-ignore-list");
        Check(!controller.Sync([App], _ => true, "idle", true) && fake.Writes.Count == 0 && controller.PendingCount == 0,
            "注入准入策略拒绝不得写入或备份");
        fake.Admission = _ => true;
        Check(controller.Sync([App], _ => true, "idle", true), "注入准入允许后可施加");
        fake.Admission = _ => false;
        Check(controller.Sync([], _ => false, "idle", true) && fake.State == new FrameTimerPowerState(0, 0),
            "恢复与新施加准入分离，策略变化仍按固定身份恢复");
    }
}
