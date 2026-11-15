using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Execution.Procman;

internal static class GamePowerThrottlingChecks
{
    internal static async Task NativeChildAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeltaNFD-game-power-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--game-target-test-child")
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new Exception("测试子进程创建失败");
        var identity = ProcmanIdentity.TryIdentify(child.Id) ?? throw new Exception("测试子进程身份不可读");
        var power = new GamePowerThrottling(Path.Combine(dir, "backups.json"), new ProcessPropertyGate(Path.Combine(dir, "owners.json")));
        try
        {
            var before = ProcmanCapabilities.ReadEcoQos(child.Id);
            if (!before.Ok || !power.Sync([identity])) throw new Exception("测试子进程应用失败：" + power.Status);
            var after = ProcmanCapabilities.ReadEcoQos(child.Id);
            var parts = after.Value?.Split(':');
            if (!after.Ok || parts is not ["eco", var control, var state] || (uint.Parse(control) & 1) == 0 || (uint.Parse(state) & 1) != 0)
                throw new Exception("真实 Windows 接口未回读为 HighQoS");
            if (!power.Sync([]) || ProcmanCapabilities.ReadEcoQos(child.Id).Value != before.Value || power.PendingCount != 0)
                throw new Exception("测试子进程原值恢复失败：" + power.Status);
            Console.WriteLine($"真实 Windows 接口检查通过：仅自建测试子进程，{before.Value} → {after.Value} → {before.Value}。");
            // 使用产品真正的 NativeApi/NativeHandle，仅给读取结果附加模拟的系统自动状态位。
            // 写入、身份核对、回读与恢复都走实际 Windows 接口，目标始终是本测试创建的子进程。
            var nativeType = typeof(GamePowerThrottling).GetNestedType("NativeApi", System.Reflection.BindingFlags.NonPublic)!;
            var native = (IGamePowerApi)Activator.CreateInstance(nativeType, nonPublic: true)!;
            using var nativeHandle = native.Open(identity);
            var original = nativeHandle.Read();
            var cases = new[] { (new GamePowerState(0, 0), 4u), (new GamePowerState(0, 0), 1u),
                (new GamePowerState(0, 0), 5u), (new GamePowerState(4, 4), 1u), (new GamePowerState(5, 5), 0u) };
            var nativeChecks = 0;
            try
            {
                foreach (var (initial, automatic) in cases)
                {
                    nativeHandle.Write(initial);
                    var injected = new AutomaticReadApi(native, automatic);
                    var actual = new GamePowerThrottling(Path.Combine(dir, $"auto-{nativeChecks}.json"),
                        new ProcessPropertyGate(Path.Combine(dir, "owners.json")), injected);
                    if (!actual.Sync([identity]) || actual.AppliedCount != 1)
                        throw new Exception("真实写入处理自动状态位失败：" + actual.Status);
                    nativeChecks++;
                    var active = nativeHandle.Read();
                    if ((active.Control & 1) == 0 || (active.State & 1) != 0 || !actual.Sync([identity]))
                        throw new Exception("真实回读未确认关闭执行速度节能：" + actual.Status);
                    nativeChecks++;
                    if (!actual.Sync([]) || actual.PendingCount != 0 || nativeHandle.Read() != initial)
                        throw new Exception("真实恢复显式策略失败：" + actual.Status);
                    nativeChecks++;
                }
            }
            finally { nativeHandle.Write(original); }
            Console.WriteLine($"真实 Windows 接口 + 模拟系统自动位：{nativeChecks} 项通过，应用及恢复均为产品实际实现，仅操作测试子进程。");
        }
        finally
        {
            power.Sync([]);
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }

    private sealed class AutomaticReadApi(IGamePowerApi native, uint automatic) : IGamePowerApi
    {
        public string IdentityState(ProcmanProcessIdentity identity) => native.IdentityState(identity);
        public IGamePowerHandle Open(ProcmanProcessIdentity identity) => new Handle(native.Open(identity), automatic);
        private sealed class Handle(IGamePowerHandle native, uint automatic) : IGamePowerHandle
        {
            public GamePowerState Read()
            {
                var state = native.Read();
                return new(state.Control, state.State | (automatic & ~state.Control));
            }
            public void Write(GamePowerState state) => native.Write(state);
            public void Dispose() => native.Dispose();
        }
    }

    private sealed class FakeApi : IGamePowerApi
    {
        internal GamePowerState State = new(4, 4);
        internal string Identity = "same";
        internal bool RejectWrite, RejectOpen, ValidateWriteMasks;
        internal uint AutomaticState;
        internal int Writes;
        public string IdentityState(ProcmanProcessIdentity identity) => Identity;
        public IGamePowerHandle Open(ProcmanProcessIdentity identity)
        {
            if (RejectOpen || Identity != "same") throw new IOException("identity or access denied");
            return new Handle(this);
        }
        private sealed class Handle(FakeApi api) : IGamePowerHandle
        {
            public GamePowerState Read() => new(api.State.Control, api.State.State | (api.AutomaticState & ~api.State.Control));
            public void Write(GamePowerState state)
            {
                if (api.RejectWrite) throw new IOException("denied");
                if (api.ValidateWriteMasks && (state.State & ~state.Control) != 0)
                    throw new System.ComponentModel.Win32Exception(87);
                api.State = state; api.Writes++;
            }
            public void Dispose() { }
        }
    }

    internal static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeltaNFD-game-power-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "backups.json");
        var gate = new ProcessPropertyGate(Path.Combine(dir, "owners.json"));
        var api = new FakeApi();
        var identity = new ProcmanProcessIdentity(12345, DateTime.UtcNow, "C:\\fixture\\game.exe");
        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        GamePowerThrottling New() => new(path, gate, api);
        try
        {
            var power = New();
            Check(power.Sync([identity]) && api.State == new GamePowerState(5, 4), "只关闭 EXECUTION_SPEED，保留计时器位：" + power.Status);
            Check(power.PendingCount == 1 && power.AppliedCount == 1, "真实生效与恢复义务分别记录");
            Check(power.Sync([identity]) && api.Writes == 1, "持续 tick 无需重复写入");
            api.State = new(7, 6); // 外部修改其他位。
            Check(power.Sync([]) && api.State == new GamePowerState(6, 6), "恢复原执行位并保留外部其他位");
            Check(power.PendingCount == 0, "恢复后才删除原值记录");
            var second = New();
            Check(second.PendingCount == 0, "第二个控制器先读取空快照");
            api.State = new(1, 1);
            Check(power.Sync([identity]), "原值显式 EcoQoS 开启时可以应用");
            Check(second.Sync([identity]), "第二个控制器按落盘原值接管");
            var stored = System.Text.Json.JsonSerializer.Deserialize<List<GamePowerThrottling.Backup>>(File.ReadAllText(path))!;
            Check(stored.Count == 1 && stored[0].Original == new GamePowerState(1, 1), "过期空快照不得覆盖第一份真实原值");
            power = New(); // 模拟后端退出后从磁盘接管恢复。
            Check(power.PendingCount == 1 && power.Sync([]) && api.State == new GamePowerState(1, 1), "重建控制器后仍恢复实际原值");
            Check(power.Sync([identity]), "再次开启");
            using (var lease = gate.Enter(identity, "ecoQos")) lease.SetOwner("manual");
            var writes = api.Writes;
            Check(!power.Sync([identity]) && api.Writes == writes, "进程管理手动设置优先，不被帧格覆盖");
            Check(!power.Sync([]) && power.PendingCount == 1, "手动接管期间保留恢复记录");
            using (var lease = gate.Enter(identity, "ecoQos")) lease.SetOwner(null);
            Check(power.Sync([]), "手动设置释放后可以恢复");
            Check(power.Sync([identity]), "旧进程应用");
            api.Identity = "missing";
            writes = api.Writes;
            Check(power.Sync([]) && power.PendingCount == 0 && writes == api.Writes, "PID 复用或已退出时只清理旧记录，不写新进程");
            api.Identity = "same";
            api.RejectOpen = true;
            Check(!power.Sync([identity]) && writes == api.Writes && power.PendingCount == 0, "权限/身份核对失败不写入且明确报错");
            api.RejectOpen = false;
            api.State = new(1, 1);
            api.RejectWrite = true;
            Check(!power.Sync([identity]) && power.PendingCount == 1, "写入失败保留写前原值");
            api.RejectWrite = false;
            Check(power.Sync([]) && power.PendingCount == 0, "未生效的原值记录可以正确收尾");
            // 系统管理的状态位不是可直接回写的显式控制位：真实 Windows 拒绝 StateMask 超出 ControlMask。
            api.ValidateWriteMasks = true;
            foreach (var original in new[] { new GamePowerState(0, 4), new(0, 1), new(0, 5), new(4, 5) })
            {
                api.State = new(original.Control, original.State & original.Control);
                api.AutomaticState = original.State & ~original.Control;
                power = New();
                Check(power.Sync([identity]) && power.AppliedCount == 1, $"系统自动位 {original} 不得被回写为非法参数：" + power.Status);
                Check((api.State.State & ~api.State.Control) == 0 && (api.State.Control & 1) != 0 && (api.State.State & 1) == 0,
                    "写入掩码合法且明确关闭执行速度节能");
                var originalSnapshot = System.Text.Json.JsonSerializer.Deserialize<List<GamePowerThrottling.Backup>>(File.ReadAllText(path))!;
                Check(originalSnapshot.Single().Original == original, "写前备份保留完整读取原值，不改写恢复证据");
                var once = api.Writes;
                Check(power.Sync([identity]) && api.Writes == once, "其他自动位变化不引起每 tick 重复写入");
                Check(power.Sync([]) && power.PendingCount == 0 && api.State.Control == original.Control
                    && api.State.State == (original.State & original.Control), "恢复显式原策略，自动位交还系统");
                Check(new GamePowerState(original.Control, original.State & original.Control).SameExecution(original),
                    "系统自动决定执行位时，不把有效状态变化误报成恢复失败");
            }
            api.AutomaticState = 0;
            api.ValidateWriteMasks = false;
            File.WriteAllText(path, "broken json");
            power = New();
            writes = api.Writes;
            Check(!power.Sync([identity]) && writes == api.Writes && power.PendingCount > 0, "损坏记录阻断新写入");
            File.Delete(path);
            Directory.CreateDirectory(path); // 无法原子写入日志。
            power = New();
            Check(!power.Sync([identity]) && writes == api.Writes, "原值保存失败绝不先写进程");
            Directory.Delete(path);
            Console.WriteLine($"游戏禁电源限制：{checks} 项隔离原生模拟检查通过；未修改本机任何进程属性。");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }
}
