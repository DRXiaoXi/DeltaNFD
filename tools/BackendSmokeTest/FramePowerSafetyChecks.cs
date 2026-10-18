using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

internal static class FramePowerSafetyChecks
{
    private static int _assertions;
    private const string Scheme = "11111111-1111-1111-1111-111111111111";
    private const string Other = "44444444-4444-4444-4444-444444444444";
    private const string Subgroup = "22222222-2222-2222-2222-222222222222";
    private const string Setting = "33333333-3333-3333-3333-333333333333";

    internal static async Task RunAsync()
    {
        _assertions = 0;
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_FramePower_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var testId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, testId);
        var fake = new FakePower();
        var service = new FrameTweaksService(root, fake.RunAsync, () => fake.Active);
        var snapshot = Path.Combine(root, "frame_power_scheme_snapshot.json");
        try
        {
            fake.QueryFails = true;
            var result = await service.ApplyPlanSettingAsync(Subgroup, Setting, "test-setting.json", "fixture", 0);
            Assert(!result.Success && fake.Writes.Count == 0 && !File.Exists(Path.Combine(root, "test-setting.json")), "原值查询失败不得写设置或创建空备份");
            fake.QueryFails = false;
            result = await service.ApplyPlanSettingAsync(Subgroup, Setting, "test-setting.json", "fixture", 0);
            Assert(result.Success, "完整原值方可应用");
            fake.Active = Other;
            fake.Writes.Clear();
            result = await service.RevertPlanSettingAsync(Subgroup, Setting, "test-setting.json", "fixture");
            Assert(result.Success && fake.Ac == 10 && fake.Dc == 20 && !fake.Writes.Any(w => w.StartsWith("/setactive")), "还原原值但不能劫持另一个活动计划");
            fake.Active = Scheme;
            File.WriteAllText(Path.Combine(root, "legacy.json"), $"{{\"Scheme\":\"{Scheme}\",\"Ac\":null,\"Dc\":null}}");
            fake.Writes.Clear();
            result = await service.RevertPlanSettingAsync(Subgroup, Setting, "legacy.json", "legacy");
            Assert(!result.Success && fake.Writes.Count == 0 && File.Exists(Path.Combine(root, "legacy.json")), "旧空原值备份不得猜默认值或删除");
            var oldPath = Path.Combine(root, "frame_usb_power.json");
            var oldJson = $"{{\"Scheme\":\"{Scheme}\",\"Ac\":null,\"Dc\":null}}";
            File.WriteAllText(oldPath, oldJson);
            var incomplete = await service.GetIncompletePowerBackupsAsync();
            Assert(incomplete.Count == 1 && incomplete[0].Reason.Contains("缺失"), "日志场景识别缺失的 USB 原值");
            File.WriteAllText(oldPath, oldJson + " ");
            Assert(!(await service.PreserveCurrentPowerValuesAsync(incomplete)).Success && File.Exists(oldPath) && fake.Writes.Count == 0,
                "确认后的外部改动阻止归档且不写电源");
            incomplete = await service.GetIncompletePowerBackupsAsync();
            Assert((await service.PreserveCurrentPowerValuesAsync(incomplete)).Success && !File.Exists(oldPath) && fake.Writes.Count == 0,
                "明确保留当前值后移出恢复队列，零电源写入");
            var evidence = Directory.GetFiles(Path.Combine(root, "unresolved-frame-power")).Single();
            Assert(File.ReadAllText(evidence) == oldJson + " ", "归档逐字保留原始证据");
            Assert((await service.RevertPlanSettingAsync(Subgroup, Setting, "frame_usb_power.json", "USB")).Success,
                "确认隔离后不再因空原值反复自锁");
            File.WriteAllText(oldPath, $"{{\"Scheme\":\"{Scheme}\",\"Ac\":10,\"Dc\":20}}");
            Assert((await service.GetIncompletePowerBackupsAsync()).Count == 0 &&
                !(await service.PreserveCurrentPowerValuesAsync(incomplete)).Success && File.Exists(oldPath),
                "完整备份必须精确还原，不能走隔离通道");
            Assert((await service.RevertPlanSettingAsync(Subgroup, Setting, "frame_usb_power.json", "USB")).Success,
                "有效备份保持原恢复行为");
            File.WriteAllText(oldPath, "{bad-json");
            incomplete = await service.GetIncompletePowerBackupsAsync();
            Assert(incomplete.Count == 1 && (await service.PreserveCurrentPowerValuesAsync(incomplete)).Success,
                "损坏 JSON 可确认留档而非删除");
            File.WriteAllText(oldPath, "");
            incomplete = await service.GetIncompletePowerBackupsAsync();
            Assert(incomplete.Count == 1 && (await service.PreserveCurrentPowerValuesAsync(incomplete)).Success,
                "历史零字节备份可确认留档");

            Assert((await service.SnapshotPowerSchemeContentAsync(Scheme)).Success, "首次快照");
            var original = File.ReadAllText(snapshot);
            fake.Ac = 99;
            Assert((await service.SnapshotPowerSchemeContentAsync(Scheme)).Success && File.ReadAllText(snapshot) == original, "重进/自愈不得覆盖首次基准");
            Assert(!(await service.SnapshotPowerSchemeContentAsync(Other)).Success && File.ReadAllText(snapshot) == original, "不能把已有快照改为其他计划");
            fake.WriteFails = true;
            result = await service.VerifyAndRestorePowerSchemeContentAsync(Scheme);
            Assert(!result.Success && File.Exists(snapshot), "内容恢复失败保留快照及失败状态");
            fake.WriteFails = false;
            fake.RefreshFails = true;
            result = await service.VerifyAndRestorePowerSchemeContentAsync(Scheme);
            Assert(!result.Success && File.Exists(snapshot), "计划刷新失败不能报告成功");
            fake.RefreshFails = false;
            fake.Ac = 99;
            Assert((await service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success && fake.Ac == 10, "失败后可重试精确还原");
            Assert(service.DiscardPowerSchemeContentSnapshot().Success && !File.Exists(snapshot), "成功后显式清理");
            Assert(Directory.GetFiles(Path.Combine(root, "restored-frame-power")).Length > 0, "恢复成功后归档原快照而不是销毁证据");
            await service.SnapshotPowerSchemeContentAsync(Scheme);
            fake.Ac = 99; fake.Active = Other; fake.Writes.Clear();
            Assert((await service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success
                && fake.Ac == 10 && !fake.Writes.Any(w => w.StartsWith("/setactive")), "恢复旧计划内容不会抢占当前活动计划");
            service.DiscardPowerSchemeContentSnapshot();
            fake.Active = Scheme;
            await service.SnapshotPowerSchemeContentAsync(Scheme);
            AppSettingsStore.Update(s => { s.FramePowerPreviousSchemeGuid = Other; s.FramePowerLockedSchemeGuid = Other; s.FramePowerRestorePending = true; });
            using (var engine = new DeltaNFD.Execution.CpuFrame.CpuFrameEngine(new FakePowerService(fake), service))
            {
                Assert((await engine.SetPowerLockAsync(false, "")).Success && !service.HasPowerSchemeContentSnapshot && !AppSettingsStore.Read().FramePowerRestorePending,
                    "历史计划元数据与快照不一致：按快照归属恢复再切原计划");
                fake.Active = Scheme;
                await service.SnapshotPowerSchemeContentAsync(Scheme);
                AppSettingsStore.Update(s => { s.FramePowerPreviousSchemeGuid = Other; s.FramePowerLockedSchemeGuid = Other; s.FramePowerRestorePending = true; });
                Assert((await engine.SetPowerLockAsync(true, Scheme)).Success && service.PowerSchemeSnapshotGuid == Scheme,
                    "带旧备份再次开启：先恢复旧会话，成功后新建而非覆盖");
                Assert((await engine.SetPowerLockAsync(false, "")).Success, "新会话可完整关闭恢复");
            }

            // 卓越性能模板 GUID 必须归一化为导入副本 GUID；旧设置里的模板 GUID 不能再造成目标/实际错位。
            AppSettingsStore.Update(s =>
            {
                s.FramePowerLockTargetGuid = PowerSchemes.UltimateSchemeTemplateGuid;
                s.UltimatePowerSchemeGuid = "";
                s.FramePowerPreviousSchemeGuid = "";
                s.FramePowerLockedSchemeGuid = "";
                s.FramePowerRestorePending = false;
            });
            fake.Active = Scheme;
            using (var engine = new DeltaNFD.Execution.CpuFrame.CpuFrameEngine(new FakePowerService(fake), service))
            {
                var locked = await engine.SetPowerLockAsync(true, PowerSchemes.UltimateSchemeTemplateGuid);
                Assert(locked.Success, "卓越性能模板导入后锁定成功");
                Assert(fake.Active == FakePowerService.ImportedScheme
                    && AppSettingsStore.Read().FramePowerLockTargetGuid == FakePowerService.ImportedScheme,
                    "锁定目标/实际计划都归一化为导入副本 GUID");
                Assert(service.PowerSchemeSnapshotGuid == FakePowerService.ImportedScheme, "内容快照使用真实导入计划 GUID");
                Assert((await engine.SetPowerLockAsync(false, "")).Success, "模板锁定可正常解除");
            }
            using (var engine = new DeltaNFD.Execution.CpuFrame.CpuFrameEngine(new FakePowerService(fake), service))
            {
                const string missing = "99999999-9999-9999-9999-999999999999";
                var before = fake.Active;
                var failedLock = await engine.SetPowerLockAsync(true, missing);
                Assert(!failedLock.Success && fake.Active == before,
                    "选择的计划不存在时明确失败，不得静默锁定当前计划");
            }
            Assert(!PowerService.ValidateActiveScheme(Scheme, Other).Success, "切换后活动计划不一致必须失败");
            Assert(PowerService.ValidateActiveScheme(Scheme, Scheme.ToUpperInvariant()).Success, "GUID 大小写不影响成功复核");
            await service.SnapshotPowerSchemeContentAsync(Scheme);
            fake.HideFromFull = true; fake.Ac = 99;
            Assert((await service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success && fake.Ac == 10,
                "全量明细缺少隐藏项时用单项查询恢复且核验");
            fake.DirectFails = true;
            Assert(!(await service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success && File.Exists(snapshot),
                "单项也不可读取时保留快照，不冒充成功");
            var snapshotReview = await service.GetIncompletePowerBackupsAsync();
            Assert(snapshotReview.Count == 1 && snapshotReview[0].FileName == "frame_power_scheme_snapshot.json",
                "永久缺项快照进入明确人工核对入口");
            File.AppendAllText(snapshot, " ");
            Assert(!(await service.PreserveCurrentPowerValuesAsync(snapshotReview)).Success && File.Exists(snapshot),
                "确认后快照变化不得归档");
            snapshotReview = await service.GetIncompletePowerBackupsAsync(); fake.Writes.Clear();
            Assert((await service.PreserveCurrentPowerValuesAsync(snapshotReview)).Success && !File.Exists(snapshot) && fake.Writes.Count == 0,
                "人工确认保留当前值后只归档证据，无电源写入");
            fake.HideFromFull = false; fake.DirectFails = false;
            fake.Empty = true;
            Assert(!(await service.SnapshotPowerSchemeContentAsync(Scheme)).Success && !File.Exists(snapshot), "空明细不能变成有效快照");
            fake.Empty = false;
            File.WriteAllText(snapshot, "{}");
            fake.Writes.Clear();
            Assert(!(await service.VerifyAndRestorePowerSchemeContentAsync(Scheme)).Success && File.Exists(snapshot) && fake.Writes.Count == 0, "无效元数据留档且不写电源");
            File.Delete(snapshot);
            File.WriteAllText(snapshot, "not json");
            Assert(!(await service.SnapshotPowerSchemeContentAsync(Scheme)).Success && File.ReadAllText(snapshot) == "not json", "损坏快照不得覆盖");
            File.Delete(snapshot);

            // 验证真实服务函数在查询中途被取消，恢复操作必须等其排空。
            Directory.CreateDirectory(snapshot);
            Assert(!(await service.SnapshotPowerSchemeContentAsync(Scheme)).Success
                && !service.DiscardPowerSchemeContentSnapshot().Success && Directory.Exists(snapshot),
                "备份路径异常不能被当作无备份或清理成功");
            Directory.Delete(snapshot);
            await service.SnapshotPowerSchemeContentAsync(Scheme);
            fake.Ac = 99;
            fake.Writes.Clear();
            fake.QueryEntered = NewSignal();
            fake.QueryRelease = NewSignal();
            var lifecycle = new FramePowerLifecycle();
            lifecycle.Start();
            var guard = lifecycle.CurrentAsync(token => service.VerifyAndRestorePowerSchemeContentAsync(Scheme, token));
            await fake.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var overlap = await lifecycle.CurrentAsync(_ => Task.FromResult(OperationResult.Ok("bad")), skipIfBusy: true);
            Assert(overlap is null, "守护忙时跳过，不能堆叠");
            lifecycle.Stop();
            var restored = false;
            var restore = lifecycle.ExclusiveAsync(() => { restored = true; return Task.FromResult(""); });
            Assert(!restored, "退出恢复必须等待在途守护");
            fake.QueryRelease.SetResult(true);
            Assert(await guard is null, "退出后取消旧守护");
            await restore;
            Assert(restored && fake.Writes.Count == 0, "取消后不能再写或激活锁定计划");
            fake.QueryEntered = null;
            fake.QueryRelease = null;

            lifecycle.Start();
            var entered = NewSignal();
            var release = NewSignal();
            var held = lifecycle.ExclusiveAsync(async () => { entered.SetResult(true); await release.Task; return ""; });
            await entered.Task;
            var ranOld = false;
            var queued = lifecycle.CurrentAsync(_ => { ranOld = true; return Task.FromResult(""); });
            var oldGeneration = lifecycle.Generation;
            lifecycle.Stop();
            lifecycle.Start();
            release.SetResult(true);
            await held;
            await queued;
            Assert(!ranOld, "新一轮激活后旧代次排队回调必须丢弃");
            Assert(await lifecycle.CurrentAsync(_ => Task.FromResult("bad"), expectedGeneration: oldGeneration) is null,
                "旧启动自愈任务不能在新一轮重新登记执行");
            lifecycle.Stop();

            var backend = new FakeDeepTweaks();
            var created = 0;
            var bx = new BxService(() => { created++; return backend; });
            Assert(created == 0, "完整 Hyper-V 后端按需创建");
            Assert((await bx.ApplyFullHyperVAsync(true)).Success && backend.DisableCount == 1, "新增项走共享备份后端");
            Assert(backend.GetBackedUpTweaks().Contains(SystemTweak.HyperVAndVbs), "一键恢复可发现完整 Hyper-V 记录");
            Assert((await bx.ApplyFullHyperVAsync(false)).Success && backend.RestoreCount == 1 && created == 1, "关闭项走原状态恢复，不执行 JSON 固定启用命令");
            backend.Fails = true;
            Assert(!(await bx.ApplyFullHyperVAsync(false)).Success, "恢复失败保留失败结果");
            var commandOn = SystemTweakService.BuildHyperVFeatureRestoreCommand("VirtualMachinePlatform", "Enabled");
            var commandOff = SystemTweakService.BuildHyperVFeatureRestoreCommand("VirtualMachinePlatform", "Disabled");
            Assert(commandOn.Contains("/Enable-Feature") && commandOff.Contains("/Disable-Feature"), "按备份启用/禁用，而非统一启用");
            var store = new TweakBackupStore(Path.Combine(root, "deep-backups.json"));
            store.SaveStrict(new RegistryValueBackup { Hive = "State", KeyPath = "HyperVFeatures", ValueName = "VirtualMachinePlatform",
                ValueKind = Microsoft.Win32.RegistryValueKind.String, Data = "Disabled" });
            store.SaveStrict(new RegistryValueBackup { Hive = "State", KeyPath = "HyperVFeatures", ValueName = "VirtualMachinePlatform",
                ValueKind = Microsoft.Win32.RegistryValueKind.String, Data = "Enabled" });
            Assert(store.Get("State", "HyperVFeatures", "VirtualMachinePlatform")!.Data == "Disabled"
                && new SystemTweakService(store).GetBackedUpTweaks().Contains(SystemTweak.HyperVAndVbs),
                "真实备份仓库保留首次原值，且真实一键恢复枚举发现 Hyper-V 记录");
            Assert(SystemTweakService.AssessHyperVFeatureStates("[{\"State\":\"Enabled\"}]") == false
                && SystemTweakService.AssessHyperVFeatureStates("[{\"State\":\"Disabled\"}]") == true
                && SystemTweakService.AssessHyperVFeatureStates("[{\"State\":\"DisablePending\"}]") == false
                && SystemTweakService.AssessHyperVFeatureStates("bad json") is null,
                "完整状态必须包含可选功能及重启等待状态，查询失败为未知");
            Console.WriteLine($"Frame power safety checks passed: {_assertions} assertions; failed reads, precise restore, persistent snapshots, failures, cancellation/drain, stale callbacks, shared Hyper-V backend. All power writes were mocked.");
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试清理路径越界");
            Directory.Delete(root, recursive: true);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); _assertions++; }

    private sealed class FakePower
    {
        public string Active = Scheme;
        public long Ac = 10, Dc = 20;
        public bool QueryFails, WriteFails, RefreshFails, Empty;
        public bool HideFromFull, DirectFails;
        public TaskCompletionSource<bool>? QueryEntered, QueryRelease;
        public List<string> Writes { get; } = new();
        public async Task<(int Code, string StdOut, string StdErr)> RunAsync(string arguments, TimeSpan timeout)
        {
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] is "/q" or "/qh")
            {
                QueryEntered?.TrySetResult(true);
                if (QueryRelease is not null) await QueryRelease.Task;
                if (QueryFails) return (5, "", "fixture access denied");
                if (parts.Length == 4 && DirectFails) return (87, "", "fixture setting unsupported");
                var output = $"Scheme {parts[1]}\nSubgroup {Subgroup}\n";
                if (!Empty) output += $"Setting {(HideFromFull && parts.Length == 2 ? "44444444-4444-4444-4444-444444444444" : Setting)}\nAC 0x{Ac:X8}\nDC 0x{Dc:X8}\n";
                return (0, output, "");
            }
            Writes.Add(arguments);
            if (WriteFails || (RefreshFails && parts[0] == "/setactive")) return (5, "", "fixture failure");
            if (parts[0] == "/setacvalueindex") Ac = long.Parse(parts[^1]);
            if (parts[0] == "/setdcvalueindex") Dc = long.Parse(parts[^1]);
            return (0, "", "");
        }
    }

    private sealed class FakePowerService(FakePower fake) : IPowerService
    {
        public const string ImportedScheme = "55555555-5555-5555-5555-555555555555";
        private bool _ultimateImported;

        public Task<List<PowerSchemeInfo>> GetSchemesAsync()
        {
            var list = new List<PowerSchemeInfo>
            {
                new() { Guid = Scheme, Name = "fixture", IsActive = fake.Active == Scheme },
                new() { Guid = Other, Name = "previous", IsActive = fake.Active == Other },
            };
            if (_ultimateImported)
                list.Add(new() { Guid = ImportedScheme, Name = "卓越性能", IsActive = fake.Active == ImportedScheme });
            return Task.FromResult(list);
        }

        public Task<OperationResult> SetSchemeAsync(string guid)
        {
            if (guid.Equals(PowerSchemes.UltimateSchemeTemplateGuid, StringComparison.OrdinalIgnoreCase))
            {
                _ultimateImported = true;
                fake.Active = ImportedScheme;
                AppSettingsStore.Update(s => s.UltimatePowerSchemeGuid = ImportedScheme);
                return Task.FromResult(OperationResult.Ok("fixture import"));
            }
            fake.Active = guid;
            return Task.FromResult(OperationResult.Ok("fixture"));
        }
        public Task<OperationResult> ImportNoPowerSaveSchemeAsync() => throw new InvalidOperationException("unexpected fixture operation");
        public Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync() => throw new InvalidOperationException("unexpected fixture operation");
        public Task<bool> IsHibernateEnabledAsync() => throw new InvalidOperationException("unexpected fixture operation");
        public Task<OperationResult> SetHibernateAsync(bool enable) => throw new InvalidOperationException("unexpected fixture operation");
        public Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync() => throw new InvalidOperationException("unexpected fixture operation");
        public Task<OperationResult> SetHeteroPolicyAsync(string setting, int value) => throw new InvalidOperationException("unexpected fixture operation");
        public Task<OperationResult> SetHeteroPolicyValuesAsync(string setting, int ac, int dc) => throw new InvalidOperationException("unexpected fixture operation");
    }

    private sealed class FakeDeepTweaks : ISystemTweakService
    {
        public int DisableCount, RestoreCount;
        public bool Fails;
        public Task<OperationResult> DisableAsync(SystemTweak tweak) { DisableCount++; return Task.FromResult(OperationResult.Ok("fixture", requiresReboot: true)); }
        public Task<OperationResult> RestoreAsync(SystemTweak tweak) { RestoreCount++; return Task.FromResult(Fails ? OperationResult.Fail("fixture") : OperationResult.Ok("fixture")); }
        public IReadOnlyList<SystemTweak> GetBackedUpTweaks() => DisableCount > 0 ? new[] { SystemTweak.HyperVAndVbs } : Array.Empty<SystemTweak>();
        public Task<List<TweakStatus>> GetStatusesAsync() => Task.FromResult(new List<TweakStatus>());
        public Task<bool?> IsHyperVFullyDisabledAsync() => Task.FromResult<bool?>(null);
        public Task<string> GetVbsRuntimeSummaryAsync() => Task.FromResult("fixture");
    }
}
