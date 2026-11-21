using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using DeltaNFD.Native;
using DeltaNFD.Inspection;
using DeltaNFD.Backends;
using System.Text.Json;

internal static class Program
{
    private static int _checks;
    private static void Check(bool pass, string label)
    {
        _checks++;
        if (!pass) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private static (uint Id, uint? Value)[] Change(uint id, uint? value) => [(id, value)];
    private static Dictionary<uint, (bool HasOverride, uint Value)> Empty =>
        NvDriverSettingPolicy.ReadIds.ToDictionary(id => id, id => (false, NvDriverSettingPolicy.MissingValue(id)));
    private static async Task Main(string[] args)
    {
        if (args is ["--host-rebar-readonly"])
        {
            var ids = new uint[] { 0x000F00BA, 0x000BFA21, 0x000F00BB, 0x000F00BE, 0x00E942FC };
            var values = NvApiDrs.GetApplicationSettings("DeltaForceClient-Win64-Shipping.exe", ids);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Application = "DeltaForceClient-Win64-Shipping.exe",
                Driver = DeltaNFD.Inspection.NvShaderInspection.ReadDriverVersion(),
                Settings = values?.Select(row => new { Id = $"0x{row.Key:X8}", row.Value.HasOverride, row.Value.Value, Hex = $"0x{row.Value.Value:X8}" }),
                RebarProjection = values is null ? null : NvDriverSettingPolicy.Project(values)
                    .Select(row => new { Id = $"0x{row.Key:X8}", row.Value.HasOverride, row.Value.Value }),
            }));
            return;
        }
        if (args.Contains("--host-driver-readonly"))
        {
            // Only the official read-only driver-version API. No DRS session or settings operation.
            Console.WriteLine(JsonSerializer.Serialize(new { Driver = DeltaNFD.Inspection.NvShaderInspection.ReadDriverVersion(), Host = Environment.OSVersion.VersionString }));
            return;
        }
        var source = args is ["--source-root", var root] ? Path.GetFullPath(root) : SourceRoot();
        var testId = Guid.NewGuid();
        var testData = Path.Combine(AppContext.BaseDirectory, "fixtures", testId.ToString("N"));
        Directory.CreateDirectory(testData);
        File.WriteAllText(Path.Combine(testData, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(testData, testId);
        if (args is ["--host-nv-environment-readonly"])
        {
            var environment = await NvEnvironmentInspection.ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { environment.IsNvidia, environment.GpuName,
                environment.DriverVersion, environment.NvApiAvailable }));
            return;
        }
        await OriginalGpuNameChecksAsync();
        var defs = NvSettingsCatalog.Definitions;
        Check(defs.Count == 12 && defs.Select(d => d.Id).Distinct().Count() == defs.Count, "12 unique catalog settings including ReBAR V1 and V2");
        Check(defs.All(d => d.Options[0].Value is null), "all default options mean delete override");
        Check(defs.All(d => d.Options.Select(o => o.Value).Distinct().Count() == d.Options.Length), "distinct explicit and inherited values");
        var fg = defs.Single(d => d.Id == 0x10E41DF1);
        var sr = defs.Single(d => d.Id == 0x10E41DF3);
        Check(sr.Options.Select(o => o.Value).SequenceEqual(new uint?[] { null, 6, 10, 11, 13, 12 }), "SR contains only default/F/J/K/M/L in the requested order; driver values retained");
        Check(sr.Options.Skip(1).Select(o => o.Label).SequenceEqual(new[] { "F（末代CNN）", "J（第一代Transformer初版）", "K（第一代Transformer改进型）", "M（第二代Transformer低性能消耗）", "L（第二代Transformer高清晰度）" }), "SR model descriptions match requested generations");
        Check(fg.Options.Skip(1).Select(o => o.Label).SequenceEqual(new[] { "A（性能与画质适中）", "B（画质最高）", "C（帧数最高）" }), "FG A/B/C descriptions updated");
        foreach (var value in new uint[] { 1, 2, 3, 4, 5, 7, 8, 9, 14, 15, 0x00FFFFFF })
            Check(NvDriverSettingPolicy.Validate(Change(sr.Id, value), 62101, false) is not null, $"retired SR {value} cannot be newly applied");
        foreach (var value in new uint?[] { null, 6, 10, 11, 13, 12 })
            Check(NvDriverSettingPolicy.Validate(Change(sr.Id, value), 62101, false) is null, $"allowed SR {value} accepted");
        Check(fg.Options.Any(o => o.Value == 3 && o.Label.StartsWith("C")), "FG C maps to 3");
        Check(fg.CompanionEnableId == 0x10E41E03, "FG C uses FG enable companion");
        Check(defs.Single(d => d.Id == 0x1057EB71).Options.Any(o => o.Value == 0), "adaptive power is explicit zero");
        Check(defs.Single(d => d.Id == 0x0084CD70).Options.Any(o => o.Value == 0), "filter optimization off is explicit zero");
        Check(defs.Count(d => d.Title.Contains("各向异性") && d.Title.Contains("优化")) == 1 && defs.All(d => d.Id != 0x00E73211), "one filtering optimization row; extra sample optimization removed");
        Check(NvDriverSettingPolicy.Validate(Change(0x00E73211, null), 62101, false) is null, "retired sample optimization can still restore default");
        Check(NvDriverSettingPolicy.Validate(Change(0x00E73211, 1), 62101, false) is not null, "retired sample optimization cannot be newly enabled");
        Check(defs.Single(d => d.Id == NvDriverSettingPolicy.MonitorRequestedId).Options.Any(o => o.Value == 0), "G-SYNC is explicit zero");
        var v1 = defs.Single(d => d.Id == NvDriverSettingPolicy.RebarId);
        Check(v1.Id == 0x000F00BA && v1.Title == "Resizable BAR V1" && v1.Options.Single(o => o.Label == "开启").Value == 1,
            "original ReBAR is labelled V1 and retains the F00BA on value");
        var rebar = Change(NvDriverSettingPolicy.RebarId, 1);
        foreach (var version in new uint?[] { null, 59636, 61656, 62099, 62100 })
        {
            Check(NvDriverSettingPolicy.RebarWarning(version, rebar) is not null, $"warning for {version}");
            Check(NvDriverSettingPolicy.Validate(rebar, version, false) is not null, $"backend refuses unacknowledged {version}");
        }
        foreach (var version in new uint[] { 62101, 62102, 70000 })
            Check(NvDriverSettingPolicy.RebarWarning(version, rebar) is null, $"no old-driver warning for {version}");
        foreach (var v in new uint?[] { null, 0 })
            Check(NvDriverSettingPolicy.RebarWarning(59636, Change(NvDriverSettingPolicy.RebarId, v)) is null, $"no enable warning for default/off {v}");
        Check(NvDriverSettingPolicy.Validate(rebar, 61656, true) is null, "acknowledged old supported driver allowed");
        Check(NvDriverSettingPolicy.Validate(rebar, 61655, true) is null, "legacy activation is available without writing unsupported v2 policy");
        Check(NvDriverSettingPolicy.Validate(Change(NvDriverSettingPolicy.RebarId, null), 59636, false) is null, "default restore works on older driver");
        Check(NvDriverSettingPolicy.Validate(Change(0x0084CD70, 0), 62101, false) is null, "zero accepted as off");
        Check(NvDriverSettingPolicy.Validate(Change(0x0084CD70, 9), 62101, false) is not null, "out-of-catalog value rejected");
        Check(NvDriverSettingPolicy.Validate(Change(0x10E41DF5, 67), 62101, false) is null, "arbitrary legal SR percent preserved");
        Check(NvDriverSettingPolicy.Validate(Change(0x10E41DF5, 101), 62101, false) is not null, "out-of-range scale rejected");
        Check(NvDriverSettingPolicy.Validate([(0x1057EB71, 1), (0x1057EB71, 0)], 62101, false) is not null, "duplicate ID rejected");
        Check(NvDriverSettingPolicy.Validate(Change(123, 0), 62101, false) is not null, "unknown ID rejected");
        Check(NvDriverSettingPolicy.Validate(Change(NvDriverSettingPolicy.AnisoModeId, 1), 62101, false) is not null, "direct companion write rejected");
        Check(NvDriverSettingPolicy.FormatDriver(62101) == "621.01", "numeric driver formatting");
        RebarCompatibilityChecks();
        var state = Empty;
        foreach (var level in new uint?[] { null, 1, 2, 4, 8, 16 })
        {
            var plan = NvDriverSettingPolicy.Expand(Change(NvDriverSettingPolicy.AnisoLevelId, level), state).ToDictionary(c => c.Id, c => c.Value);
            Check(plan[NvDriverSettingPolicy.AnisoModeId] == (level is null ? null : 1u), $"AF {level} paired with mode or restore");
        }
        foreach (var mode in new uint?[] { null, 0, 3, 4 })
        {
            var plan = NvDriverSettingPolicy.Expand(Change(NvDriverSettingPolicy.MonitorRequestedId, mode), state).ToDictionary(c => c.Id, c => c.Value);
            Check(plan[NvDriverSettingPolicy.MonitorEffectiveId] == mode, $"monitor {mode} pairs request and effective state");
        }
        var fgPlan = NvDriverSettingPolicy.Expand(Change(fg.Id, 3), state);
        Check(fgPlan.Contains((0x10E41E03, 1)), "FG C enables companion");
        state[0x10E41DF5] = (true, 67);
        Check(NvDriverSettingPolicy.Expand(Change(0x10E41DF3, null), state).Contains((0x10E41E01, 1)), "removing SR model preserves active scale enable");
        Check(NvDriverSettingPolicy.Expand([(0x10E41DF3, null), (0x10E41DF5, null)], state).Contains((0x10E41E01, null)), "last SR dependent removed clears companion");
        var reset = NvDriverSettingPolicy.Expand(defs.Select(d => (d.Id, (uint?)null)).ToList(), state);
        Check(NvDriverSettingPolicy.CompanionIds.All(id => reset.Contains((id, null))) && reset.All(c => c.Value is null), "full reset includes every companion");
        state[NvDriverSettingPolicy.AnisoLevelId] = (true, 16);
        state[NvDriverSettingPolicy.AnisoModeId] = (true, 0);
        Check(NvDriverSettingPolicy.Project(state)[NvDriverSettingPolicy.AnisoLevelId].Value == uint.MaxValue, "half-applied AF is not claimed active");
        state[NvDriverSettingPolicy.AnisoModeId] = (true, 1);
        Check(NvDriverSettingPolicy.Project(state)[NvDriverSettingPolicy.AnisoLevelId].Value == 16, "consistent AF preserved");
        state[NvDriverSettingPolicy.MonitorRequestedId] = (true, 4);
        state[NvDriverSettingPolicy.MonitorEffectiveId] = (true, 0);
        Check(NvDriverSettingPolicy.Project(state)[NvDriverSettingPolicy.MonitorRequestedId].Value == uint.MaxValue, "half-applied monitor is not claimed active");
        var wire = JsonSerializer.Deserialize<NvDriverApplyArgs>(JsonSerializer.Serialize(new NvDriverApplyArgs { RebarWarningAccepted = true, Changes = [new() { Id = NvDriverSettingPolicy.RebarId, Value = 1 }, new() { Id = 0x00E73211, Value = 0 }, new() { Id = fg.Id, Value = null }] }))!;
        Check(wire.RebarWarningAccepted && wire.Changes[0].Value == 1 && wire.Changes[1].Value == 0 && wire.Changes[2].Value is null, "IPC retains warning acknowledgement, zero and null");
        Check(!JsonSerializer.Deserialize<NvDriverApplyArgs>("{}")!.RebarWarningAccepted, "old IPC cannot implicitly acknowledge warning");
        CommitChecks();
        await ViewModelChecks();
        await RetiredSrChecks();
        await ImmediateSelectionChecks();
        await RebarV2Checks();
        NvApplicationSaveChecks.Run();
        await NvIndicatorAndSaveViewChecks.RunAsync();
        await ProfileResetChecks.RunAsync();
        UiWiringChecks(source);
        await ShaderCacheViewChecks.RunAsync();
        Console.WriteLine($"TOTAL {_checks} NVIDIA settings checks passed; shader cache/DLSS UI checks also passed; no driver settings written.");
    }

    private static void RebarCompatibilityChecks()
    {
        const uint v1 = NvDriverSettingPolicy.RebarId, v2 = NvDriverSettingPolicy.RebarPolicyId;
        var raw = Empty;
        foreach (var value in new uint[] { 0, 1, 2, 99 })
        foreach (var hasOverride in new[] { false, true })
        {
            raw[v1] = (true, 1); raw[v2] = (hasOverride, value);
            Check(NvDriverSettingPolicy.Project(raw)[v1] == (true, 1)
                && NvDriverSettingPolicy.Project(raw)[v2] == (hasOverride, value), $"read both effective values, including v2 {value} / override={hasOverride}");
            var plan = NvDriverSettingPolicy.Expand(Change(v1, 1), raw);
            Check(value == 1 ? plan.SequenceEqual(Change(v1, 1)) : plan.Count == 2 && plan.Contains((v2, 1)),
                $"V1 enable preserves Auto and repairs V2={value} / override={hasOverride} to Auto when needed");
        }
        raw[v2] = (false, 1);
        Check(NvDriverSettingPolicy.Validate([(v1, 1), (v2, 1)], 62114, false) is null
            && NvDriverSettingPolicy.Expand([(v1, 1), (v2, 1)], raw).Contains((v2, 1)), "Delta Force permits V1 on with explicit V2 auto");
        Check(NvDriverSettingPolicy.Validate([(v1, 1), (v2, 0)], 62114, true) is not null,
            "a contradictory batch cannot claim it enabled V1 while explicitly disabling the policy");
        Check(NvDriverSettingPolicy.Expand(Change(v2, 0), raw).SequenceEqual(Change(v2, 0)),
            "independent V2 off remains intentional, without rewriting V1");
        Check(NvDriverSettingPolicy.Expand([(v1, 1), (v2, null)], raw).Contains((v2, null)),
            "an explicit request for inheritance remains a deletion");
        var disable = NvDriverSettingPolicy.Expand(Change(v1, 0), raw);
        Check(disable.SequenceEqual(Change(v1, 0)), "V1 off independently leaves V2 auto in place");
        Check(NvDriverSettingPolicy.Expand(Change(v1, null), raw).SequenceEqual(Change(v1, null)), "single-row default preserves the other override in the write plan");
        Check(NvDriverSettingPolicy.Validate(Change(v1, 2), 62114, true) is not null, "v2 on-value cannot be written to activation ID");
        Check(NvDriverSettingPolicy.Expand(Change(0x1057EB71, 0), raw).All(c => c.Id != v1 && c.Id != v2), "unrelated setting does not rewrite ReBAR");
    }

    private static async Task RebarV2Checks()
    {
        var primary = NvDriverSettingPolicy.RebarId; var v2 = NvDriverSettingPolicy.RebarPolicyId;
        var def = NvSettingsCatalog.Definitions.Single(d => d.Id == v2);
        Check(v2 == 0x000BFA21 && def.Title == "Resizable BAR V2"
            && def.Options.Select(o => o.Value).SequenceEqual(new uint?[] { null, 0, 1, 2 }), "v2 matches NVPI default/off/auto/on mappings");
        Check(def.Options.Single(o => o.Label == "开启").Value == 2 && def.Options.Single(o => o.Value == 1).Label.Contains("自动"),
            "v2 enabled is two; auto is explicitly labelled separately");
        Check(NvDriverSettingPolicy.ReadIds.Distinct().Count() == NvDriverSettingPolicy.ReadIds.Length
            && !NvDriverSettingPolicy.CompanionIds.Contains(v2), "v2 is an independent row with one native read ID");
        foreach (var version in new uint?[] { null, 59636, 61655, 61656, 62100, 62101, 62114 })
        {
            Check(NvDriverSettingPolicy.Validate(Change(v2, null), version, false) is null, "v2 default cleanup allowed on " + version);
            foreach (var value in new uint[] { 0, 1, 2 })
                Check((NvDriverSettingPolicy.Validate(Change(v2, value), version, true) is null) == (version >= 61656),
                    $"v2 {value} requires confirmed minimum driver: {version}");
            Check((NvDriverSettingPolicy.RebarWarning(version, Change(v2, 2)) is not null) == !(version >= 62101),
                "v2 enabled uses same recommended-driver warning boundary: " + version);
        }
        Check(NvDriverSettingPolicy.Validate(Change(v2, 2), 62100, false) is not null
            && NvDriverSettingPolicy.Validate(Change(v2, 2), 62100, true) is null, "supported older driver still requires explicit enable acknowledgement");
        Check(NvDriverSettingPolicy.RebarWarning(62000, Change(v2, 0)) is null
            && NvDriverSettingPolicy.RebarWarning(62000, Change(v2, 1)) is null, "off and auto are not force-enable warnings");
        foreach (var value in new uint[] { 3, 99, uint.MaxValue })
            Check(NvDriverSettingPolicy.Validate(Change(v2, value), 62114, true) is not null, "unknown v2 value rejected: " + value);
        Check(NvDriverSettingPolicy.Validate([(v2, 2), (v2, 1)], 62114, true) is not null, "duplicate v2 writes rejected");
        var raw = Empty; raw[primary] = (true, 1); raw[v2] = (true, 2);
        Check(NvDriverSettingPolicy.Validate([(primary, 1), (v2, 2)], 62114, true) is not null,
            "explicit dual enable rejected as a batch even after warning acknowledgement");
        foreach (var value in new uint?[] { null, 0, 1 })
            Check(NvDriverSettingPolicy.Expand(Change(v2, value), raw).SequenceEqual(Change(v2, value)),
                "non-enabling V2 change does not write V1: " + value);
        Check(NvDriverSettingPolicy.Expand(Change(v2, 2), raw).ToHashSet().SetEquals([(primary, 0), (v2, 2)]),
            "V2 on plans explicit V1 off and no other ReBAR writes");
        Check(NvDriverSettingPolicy.Expand([(primary, null), (v2, 2)], raw).Contains((primary, null)),
            "explicit removal stays in the plan and is checked against the inherited value before save");

        var fake = new FakeNv { Version = 62114 }; ServiceLocator.NvProfile = fake;
        fake.Values[primary] = (true, 1); fake.Values[v2] = (true, 2);
        var vm = new NvSettingsViewModel(); var row = vm.Rows.Single(r => r.Id == v2);
        var on = Array.FindIndex(def.Options, o => o.Value == 2); var auto = Array.FindIndex(def.Options, o => o.Value == 1);
        Check(row.SelectedIndex == on && row.StateText.Contains("开启") && !row.PendingChange
            && vm.StatusText.Contains("不能同时开启") && fake.Writes.Count == 0, "existing conflict is shown without writing or staging a fix");
        foreach (var value in new uint[] { 0, 1 })
        {
            fake.Values[v2] = (true, value); await vm.LoadAsync();
            Check(row.SelectedIndex == Array.FindIndex(def.Options, o => o.Value == value) && !row.PendingChange, "actual v2 row displays explicit " + value);
        }
        fake.Values[v2] = (false, 1); await vm.LoadAsync();
        Check(row.SelectedIndex == 0 && row.StateText.Contains("未覆盖") && !row.PendingChange, "inherited auto is displayed as default, not explicit auto");
        fake.Values[v2] = (true, 99); await vm.LoadAsync();
        Check(row.SelectedIndex == -1 && row.StateText.Contains("0x63") && !row.PendingChange
            && vm.Rows.Single(r => r.Id == primary).SelectedIndex == 2, "unknown v2 value remains visible without hiding primary activation");
        Check(await vm.SelectOptionAsync(row, 0) && (await vm.ApplyChangesAsync())?.Success == true
            && fake.Writes.Single().SequenceEqual(Change(v2, null)), "unknown v2 override can be explicitly removed by itself");

        fake = new FakeNv { Version = 62114 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2);
        var bar = vm.Rows.Single(r => r.Id == primary);
        Check(await vm.SelectOptionAsync(bar, 2) && await vm.SelectOptionAsync(row, on) && bar.SelectedIndex == 1
            && vm.PendingCount == 2 && fake.Writes.Count == 0, "selecting V2 on stages V1 off without writing GPU");
        await vm.ApplyChangesAsync();
        Check(fake.Writes.Single().ToHashSet().SetEquals([(primary, 0), (v2, 2)]) && row.SelectedIndex == on && !vm.HasPendingChanges,
            "viewmodel sends both changes together and reloads actual V2 on");
        Check(await vm.SelectOptionAsync(bar, 2) && row.SelectedIndex == auto && vm.PendingCount == 2
            && (await vm.ApplyChangesAsync())?.Success == true && fake.Writes.Last().ToHashSet().SetEquals([(primary, 1), (v2, 1)]),
            "switching back to V1 atomically stages V2 Auto instead of overriding V1 with Off");
        Check(await vm.SelectOptionAsync(row, 1) && bar.SelectedIndex == 2 && vm.PendingCount == 1
            && (await vm.ApplyChangesAsync())?.Success == true && fake.Writes.Last().SequenceEqual(Change(v2, 0))
            && vm.StatusText.Contains("覆盖了 V1"), "intentionally switching V2 off shuts off the policy while retaining the stored V1 flag");
        Check(await vm.SelectOptionAsync(row, auto) && bar.SelectedIndex == 2 && vm.PendingCount == 1
            && (await vm.ApplyChangesAsync())?.Success == true && fake.Writes.Last().SequenceEqual(Change(v2, 1)),
            "setting V2 to Auto independently restores the existing V1 choice");
        fake.Values[primary] = (true, 0); await vm.LoadAsync();
        Check(await vm.SelectOptionAsync(bar, 2) && row.SelectedIndex == auto && vm.PendingCount == 1
            && (await vm.ApplyChangesAsync())?.Success == true && fake.Writes.Last().SequenceEqual(Change(primary, 1)),
            "enabling Delta Force V1 likewise preserves previously selected V2 auto");
        fake.Values[primary] = (false, 1); fake.Values[v2] = (false, 0); await vm.LoadAsync();
        Check(bar.SelectedIndex == 0 && bar.StateText.Contains("继承驱动默认 → 开启")
            && await vm.SelectOptionAsync(row, on) && bar.SelectedIndex == 1 && vm.PendingCount == 2,
            "inherited V1 on is visible and staged off when V2 is enabled");

        fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        fake.Values[primary] = (true, 1);
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2); bar = vm.Rows.Single(r => r.Id == primary);
        var decision = new TaskCompletionSource<bool>(); var calls = 0;
        var select = vm.SelectOptionAsync(row, on, _ => { calls++; return decision.Task; });
        Check(calls == 1 && !select.IsCompleted && row.SelectedIndex == 0 && !vm.ApplyEnabled && fake.Writes.Count == 0,
            "v2 old-driver warning waits before staging or applying");
        decision.SetResult(false);
        Check(!await select && row.SelectedIndex == 0 && bar.SelectedIndex == 2 && !vm.HasPendingChanges && !vm.IsSelectingOption,
            "cancelled V2 warning preserves the previously enabled V1 and stages nothing");
        Check(await vm.SelectOptionAsync(row, on, _ => Task.FromResult(true)), "v2 acknowledged selection can become pending");
        await vm.ApplyChangesAsync(_ => throw new Exception("unchanged v2 acknowledgement should not repeat"));
        Check(fake.Writes.Count == 1 && fake.LastAcknowledged && fake.Writes.Single().Contains((v2, 2)), "v2 acknowledgement is passed to backend on unchanged driver");

        foreach (var version in new uint?[] { null, 61655 })
        {
            fake = new FakeNv { Version = version }; ServiceLocator.NvProfile = fake;
            fake.Values[v2] = (true, 2); vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2);
            Check(row.StateText.Contains("仅可恢复驱动默认"), "unsupported/unknown driver explains v2 cleanup-only state: " + version);
            Check(!await vm.SelectOptionAsync(row, auto) && row.SelectedIndex == on && fake.Writes.Count == 0,
                "unsupported v2 selection is refused before changing dropdown: " + version);
            Check(await vm.SelectOptionAsync(row, 0) && (await vm.ApplyChangesAsync())?.Success == true
                && fake.Writes.Single().SequenceEqual(Change(v2, null)), "unsupported v2 override can still be removed: " + version);
        }
        fake = new FakeNv { Version = 62114 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2);
        await vm.SelectOptionAsync(row, on); fake.Version = 61655;
        Check((await vm.ApplyChangesAsync(_ => Task.FromResult(true))) is { Success: false } && fake.Writes.Count == 0 && row.PendingChange,
            "driver downgrade below v2 support is rechecked immediately before submitting");
        fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); bar = vm.Rows.Single(r => r.Id == primary); row = vm.Rows.Single(r => r.Id == v2);
        await vm.SelectOptionAsync(bar, 2, _ => Task.FromResult(true)); row.SelectedIndex = on; calls = 0;
        await vm.ApplyChangesAsync(_ => { calls++; return Task.FromResult(false); });
        Check(calls == 0 && fake.Writes.Count == 0 && vm.StatusText.Contains("不能同时开启"),
            "contradictory staged values are refused before any prompt or write");
        fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2);
        await vm.SelectOptionAsync(row, on, _ => Task.FromResult(true)); fake.Version = 62001; calls = 0;
        await vm.ApplyChangesAsync(_ => { calls++; return Task.FromResult(false); });
        Check(calls == 1 && fake.Writes.Count == 0 && row.PendingChange, "v2 changed-driver acknowledgement expires");

        fake = new FakeNv { Version = 62114 }; fake.Values[v2] = (true, 2); ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2);
        fake.ThrowValues = true; await vm.LoadAsync();
        Check(row.SelectedIndex == -1 && row.StateText == "读取失败" && !row.PendingChange, "failed refresh clears stale v2 enabled display");
        fake.ThrowValues = false; await vm.LoadAsync();
        Check(row.SelectedIndex == on && !row.PendingChange, "successful retry restores actual v2 state");
        fake.ThrowEnvironment = true; await vm.LoadAsync();
        Check(row.SelectedIndex == -1 && !await vm.SelectOptionAsync(row, on) && fake.Writes.Count == 0,
            "environment read failure cannot reuse previous driver version to approve v2");
        fake.ThrowEnvironment = false; await vm.LoadAsync(); await vm.ResetAllAsync();
        Check(fake.Writes.Last().Contains((primary, null)) && fake.Writes.Last().Contains((v2, null))
            && fake.Writes.Last().All(c => c.Value is null), "full reset deletes both overrides without writing auto or zero");
        fake = new FakeNv { Locked = true }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); row = vm.Rows.Single(r => r.Id == v2); await vm.SelectOptionAsync(row, on);
        Check(!vm.ApplyEnabled && await vm.ApplyChangesAsync() is null && fake.Writes.Count == 0, "locked DRS database cannot apply staged v2");

        RebarCommitChecks();
    }

    private static void RebarCommitChecks()
    {
        const uint v1 = NvDriverSettingPolicy.RebarId, v2 = NvDriverSettingPolicy.RebarPolicyId;
        var saves = 0;
        uint? inheritedV2 = null;
        var values = new Dictionary<uint, uint?> { [v1] = 0, [v2] = 2 };
        var staged = new List<(uint, uint?)>();
        int Stage(uint id, uint? v) { staged.Add((id, v)); values[id] = v; return 0; }
        int Save() { saves++; return 0; }
        (int, bool, bool, uint) Read(uint id) => values[id] is uint present ? (0, true, true, present)
            : id == v2 && inheritedV2 is uint inherited ? (0, true, false, inherited) : (-160, false, false, 0u);
        // Pure model of the sampled driver's initial decision, not a hardware enable claim.
        bool EffectivePolicyOn() => (values[v2] ?? inheritedV2 ?? 1) switch { 0 => false, 2 => true, _ => values[v1] == 1 };
        var plan = NvDriverSettingPolicy.Expand(Change(v1, 1), new Dictionary<uint, (bool, uint)> { [v1] = (true, 0), [v2] = (true, 2) });
        var result = NvApiDrs.CommitApplicationWrites(plan, Stage, Save, () => 0, Read);
        Check(result.Success && saves == 1 && staged.SequenceEqual(new (uint, uint?)[] { (v2, 1), (v1, 1) })
            && EffectivePolicyOn(), "V2 to V1 saves Auto first and actually leaves the policy enabled");
        result = NvApiDrs.CommitApplicationWrites(Change(v2, 0), Stage, Save, () => 0, Read);
        Check(result.Success && saves == 2 && values[v1] == 1 && !EffectivePolicyOn(), "explicit V2 off after V1 does not rewrite V1 or report an effective enable");
        result = NvApiDrs.CommitApplicationWrites([(v1, 1), (v2, 0)], Stage, Save, () => 0, Read);
        Check(!result.Success && !result.OutcomeUnknown && saves == 2, "effective pair refuses V1 enable overridden by V2 off");
        result = NvApiDrs.CommitApplicationWrites([(v1, 1), (v2, 2)], Stage, Save, () => 0, Read);
        Check(!result.Success && !result.OutcomeUnknown && saves == 2, "effective pair refuses explicit dual enable even if validation was bypassed");
        foreach (var inherited in new uint[] { 0, 2 })
        {
            inheritedV2 = inherited;
            result = NvApiDrs.CommitApplicationWrites([(v1, 1), (v2, null)], Stage, Save, () => 0, Read);
            Check(!result.Success && !result.OutcomeUnknown && saves == 2 && result.Message.Contains("本次更改未保存"),
                "refuse enable when deletion exposes incompatible inherited V2 " + inherited);
        }
        inheritedV2 = null;
        result = NvApiDrs.CommitApplicationWrites([(v1, 1), (v2, null)], Stage, Save, () => 0, Read);
        Check(result.Success && saves == 3 && values[v2] is null && EffectivePolicyOn(),
            "the truly missing V2 setting takes Auto without fabricating an explicit override");
        result = NvApiDrs.CommitApplicationWrites(Change(v1, 1), Stage, Save, () => 0,
            id => id == v2 ? (-1, false, false, 0u) : Read(id));
        Check(!result.Success && !result.OutcomeUnknown && saves == 3 && result.Message.Contains("无法确认"), "unreadable effective pair fails before save");
        result = NvApiDrs.CommitApplicationWrites(Change(v1, 1), Stage, Save, () => { inheritedV2 = 0; return 0; }, Read);
        Check(!result.Success && result.OutcomeUnknown && saves == 4 && result.Message.Contains("ReBAR 回读未通过"),
            "an unchanged companion going Off after save cannot falsely verify the V1 policy");
    }

    private static void CommitChecks()
    {
        var saveCount = 0;
        var readCount = 0;
        var changes = new (uint Id, uint? Value)[] { (1, 0), (2, null) };
        var result = NvApiDrs.CommitApplicationWrites(changes, (_, _) => 0, () => { saveCount++; return 0; }, () => 0,
            id => { readCount++; return (0, true, id == 1, 0); });
        Check(result.Success && saveCount == 1 && readCount == 2, "actual native commit verifies explicit zero and default deletion");
        saveCount = 0;
        result = NvApiDrs.CommitApplicationWrites(changes, (id, _) => id == 2 ? -5 : 0, () => { saveCount++; return 0; }, () => 0, _ => throw new Exception());
        Check(!result.Success && !result.OutcomeUnknown && saveCount == 0, "companion set failure never saves partial batch");
        result = NvApiDrs.CommitApplicationWrites(changes, (_, _) => 0, () => -1, () => throw new Exception(), _ => throw new Exception());
        Check(!result.Success && result.OutcomeUnknown, "save failure returned as unconfirmed");
        result = NvApiDrs.CommitApplicationWrites(changes, (_, _) => 0, () => 0, () => -1, _ => throw new Exception());
        Check(!result.Success && result.OutcomeUnknown, "reload failure cannot claim success");
        result = NvApiDrs.CommitApplicationWrites(changes, (_, _) => 0, () => 0, () => 0, _ => (0, true, true, 4));
        Check(!result.Success, "wrong value cannot claim success");
        result = NvApiDrs.CommitApplicationWrites(Change(1, 2), (_, _) => 0, () => 0, () => 0, _ => (0, true, false, 2));
        Check(!result.Success, "inherited value cannot confirm explicit override");
        result = NvApiDrs.CommitApplicationWrites(Change(1, null), (_, _) => -160, () => 0, () => 0, _ => (-160, true, false, 0));
        Check(result.Success, "already absent restore is successful");
        result = NvApiDrs.CommitApplicationWrites(Change(1, null), (_, _) => 0, () => 0, () => 0, _ => (-5, true, false, 0));
        Check(!result.Success, "read error is not default");
        result = NvApiDrs.CommitApplicationWrites(Change(1, 2), (_, _) => 0, () => 0, () => 0, _ => (0, false, true, 2));
        Check(!result.Success, "wrong value type cannot verify");
    }

    private static async Task OriginalGpuNameChecksAsync()
    {
        const string display = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const string deviceRoot = @"SYSTEM\CurrentControlSet\Enum\";
        const string driverRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
        const string chip = @"PCI\VEN_10DE&DEV_2820&SUBSYS_12345678";
        const string chip2 = @"PCI\VEN_10DE&DEV_2820&SUBSYS_87654321";
        const string first = chip + @"\4&AB12&0&0010";
        const string second = chip2 + @"\4&CD34&0&0010";
        const string realName = "NVIDIA GeForce RTX 4070 Laptop GPU";
        const string alternateName = "NVIDIA GeForce RTX 4060 Laptop GPU";
        const string spoof = "NVIDIA GeForce RTX 2050 Laptop GPU";
        const string binding = display + @"\0007";
        const string binding2 = display + @"\0011";
        var firstDevice = new Dictionary<string, object?> { ["ClassGUID"] = display, ["Driver"] = binding,
            ["HardwareID"] = new[] { chip }, ["DeviceDesc"] = spoof };
        var firstDriver = new Dictionary<string, object?> { ["ProviderName"] = "NVIDIA", ["MatchingDeviceId"] = chip, ["DriverDesc"] = realName };
        var keys = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            [deviceRoot + first] = firstDevice,
            [driverRoot + binding] = firstDriver,
            [deviceRoot + second] = new() { ["ClassGUID"] = display, ["Driver"] = binding2, ["HardwareID"] = new[] { chip2 } },
            [driverRoot + binding2] = new() { ["ProviderName"] = "NVIDIA Corporation", ["MatchingDeviceId"] = chip2, ["DriverDesc"] = alternateName },
        };
        var selected = new ShaderDriverReading(ShaderGpuDetectionState.Nvidia, "621.24", "32.0.16.2124", "WMI", spoof, first);
        var queries = new List<string>();
        string Resolve(ShaderDriverReading? reading) => NvEnvironmentInspection.ReadOriginalGpuName(reading, (path, names) =>
        {
            if (names.Any(name => name is "DeviceDesc" or "FriendlyName")) throw new InvalidOperationException("spoofable name requested");
            queries.Add(path);
            return keys.TryGetValue(path, out var values) ? names.ToDictionary(n => n, n => values.GetValueOrDefault(n)) : null;
        });
        Check(Resolve(selected) == realName && queries.SequenceEqual(new[] { deviceRoot + first, driverRoot + binding }),
            "spoofed WMI name is ignored and selected physical adapter uses its own driver");
        Check(Resolve(selected with { Adapter = realName }) == realName, "unmodified adapter shows its original model");
        Check(Resolve(selected with { PnpDeviceId = second.ToLowerInvariant() }) == alternateName, "second NVIDIA adapter uses its own binding even with the same PCI device code");
        firstDevice["Driver"] = binding2;
        Check(Resolve(selected) == "", "another NVIDIA adapter's unmatched SUBSYS cannot supply the name");
        firstDevice["Driver"] = binding;
        firstDriver["MatchingDeviceId"] = @"PCI\VEN_1002&DEV_2820";
        firstDevice["HardwareID"] = new[] { @"PCI\VEN_1002&DEV_2820" };
        Check(Resolve(selected) == "", "conflicting PCI vendor is rejected even if the hardware list claims a match");
        firstDevice["HardwareID"] = new[] { chip.ToLowerInvariant() };
        firstDriver["MatchingDeviceId"] = chip;
        Check(Resolve(selected) == realName, "hardware ID comparison is case insensitive");
        foreach (var id in new[] { "", @"ROOT\DISPLAY\0001", @"PCI\VEN_1002&DEV_2820\0001", first + "\\malformed" })
        {
            queries.Clear();
            Check(Resolve(selected with { PnpDeviceId = id }) == "" && queries.Count == 0, "missing/foreign/invalid instance does not read a guessed driver: " + id);
        }
        queries.Clear();
        Check(Resolve(null) == "" && Resolve(selected with { State = ShaderGpuDetectionState.NotPresent }) == "" && queries.Count == 0,
            "custom target or absent NVIDIA adapter does not read a model");
        firstDevice["ClassGUID"] = "wrong-display-class";
        Check(Resolve(selected) == "", "non-display PCI instance cannot supply the GPU name");
        firstDevice["ClassGUID"] = display;
        firstDevice["Driver"] = display + @"\0008";
        Check(Resolve(selected) == "", "missing driver key leaves the original model unconfirmed");
        firstDevice["Driver"] = binding;
        firstDriver["ProviderName"] = "Microsoft";
        Check(Resolve(selected) == "", "generic driver cannot supply an original model");
        firstDriver["ProviderName"] = "NVIDIA";
        foreach (var name in new object?[] { null, "", "  ", "@oem12.inf,%device%", "A\nB", 123 })
        {
            firstDriver["DriverDesc"] = name;
            Check(Resolve(selected) == "", "missing or invalid DriverDesc does not fall back to spoofed text");
        }
        Check(NvEnvironmentInspection.ReadOriginalGpuName(selected, (_, _) => throw new UnauthorizedAccessException()) == "",
            "inaccessible registry is an unconfirmed name, not an exception");
        firstDriver["DriverDesc"] = realName;
        var fake = new FakeNv { Version = 62124, GpuName = Resolve(selected) };
        ServiceLocator.NvProfile = fake;
        var vm = new NvSettingsViewModel();
        await vm.LoadAsync();
        Check(vm.GpuSummaryText == $"显卡：{realName} · 驱动：621.24 · NVAPI：可用", "page shows original model with unchanged driver/NVAPI indicators");
        fake.GpuName = Resolve(selected with { PnpDeviceId = "" });
        await vm.LoadAsync();
        Check(vm.IsNvidia && vm.NvApiAvailable && vm.CanEditOptions
            && vm.GpuSummaryText == "显卡：原型号未能确认 · 驱动：621.24 · NVAPI：可用" && fake.Writes.Count == 0,
            "unconfirmed model leaves settings available and does not write or reuse the previous model");
        fake.IsNvidia = false;
        await vm.LoadAsync();
        Check(!vm.IsNvidia && vm.GpuSummaryText == "显卡：未检测到 NVIDIA 设备", "absence remains distinct from an unconfirmed model");
    }

    private static async Task ViewModelChecks()
    {
        foreach (var version in new uint?[] { null, 62000, 62101, 62102 })
        {
            var fake = new FakeNv { Version = version };
            ServiceLocator.NvProfile = fake;
            var vm = new NvSettingsViewModel();
            var row = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
            row.SelectedIndex = Array.FindIndex(row.Def.Options, o => o.Value == 1);
            vm.OnRowSelectionChanged();
            var asked = 0;
            await vm.ApplyChangesAsync(_ => { asked++; return Task.FromResult(false); });
            var needsWarning = version is null || version < 62101;
            Check(asked == (needsWarning ? 1 : 0), $"VM warning threshold {version}");
            Check(needsWarning ? fake.Writes.Count == 0 && row.PendingChange : fake.Writes.Count == 1, $"cancel protects entire batch and retains intent {version}");
            if (needsWarning)
            {
                await vm.ApplyChangesAsync(_ => Task.FromResult(true));
                Check(fake.Writes.Count == 1 && fake.LastAcknowledged && fake.Writes[0].Contains((NvDriverSettingPolicy.RebarId, 1)), $"confirmed warning sent to backend {version}");
            }
        }
        var f = new FakeNv(); ServiceLocator.NvProfile = f;
        var model = new NvSettingsViewModel();
        f.Version = 62000; // installed version changed after page opened
        var bar = model.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        bar.SelectedIndex = 2;
        var warned = false;
        await model.ApplyChangesAsync(_ => { warned = true; return Task.FromResult(false); });
        Check(warned && f.Writes.Count == 0, "driver refreshed before enable warning");
        await model.ApplyChangesAsync();
        Check(f.Writes.Count == 0, "missing confirmation callback cannot bypass warning");
        var power = model.Rows.Single(r => r.Id == 0x1057EB71);
        bar.SelectedIndex = 0;
        power.SelectedIndex = 1; // explicit adaptive=0
        await model.ApplyChangesAsync();
        Check(f.Writes.Last().Contains((0x1057EB71, 0)), "VM sends explicit adaptive zero");
        power.SelectedIndex = 0;
        await model.ApplyChangesAsync();
        Check(f.Writes.Last().Contains((0x1057EB71, null)), "VM sends null for default, not zero");
        await model.ResetAllAsync();
        var full = NvDriverSettingPolicy.Expand(f.Writes.Last(), Empty);
        Check(NvDriverSettingPolicy.CompanionIds.All(id => full.Contains((id, null))), "VM reset reaches all companion deletions");
        Check(NvDriverSettingPolicy.RetiredManagedIds.All(id => full.Contains((id, null))), "VM reset cleans retired optimization override only when user resets");
        f.Values[0x1057EB71] = (false, 5);
        await model.LoadAsync();
        Check(power.SelectedIndex == 0 && !power.PendingChange, "inherited nonzero value still selects default");
        f.Values[NvDriverSettingPolicy.RebarId] = (true, 999);
        await model.LoadAsync();
        Check(bar.SelectedIndex == -1 && !bar.PendingChange, "unknown override is retained without silently choosing default");
        bar.SelectedIndex = 0;
        Check(bar.PendingChange, "unknown override can explicitly restore default");
        await model.ApplyChangesAsync();
        Check(f.Writes.Last().Contains((NvDriverSettingPolicy.RebarId, null)), "unknown override removal is sent");
        bar.SelectedIndex = 2;
        f.ThrowEnvironment = true;
        var previousWrites = f.Writes.Count;
        var failed = await model.ApplyChangesAsync(_ => Task.FromResult(true));
        Check(failed is { Success: false, OutcomeUnknown: false } && f.Writes.Count == previousWrites && !model.IsApplying, "environment failure catches exception and performs no write");
    }

    private static async Task RetiredSrChecks()
    {
        foreach (var value in new uint[] { 1, 2, 3, 4, 5, 7, 8, 9, 14, 15, 0x00FFFFFF })
        {
            var fake = new FakeNv();
            fake.Values[0x10E41DF3] = (true, value);
            ServiceLocator.NvProfile = fake;
            var vm = new NvSettingsViewModel();
            await vm.LoadAsync();
            var sr = vm.Rows.Single(r => r.Id == 0x10E41DF3);
            Check(sr.SelectedIndex == -1 && !sr.PendingChange && fake.Writes.Count == 0, $"retired SR {value} is shown as an existing unlisted override without auto migration");
            vm.Rows.Single(r => r.Id == 0x1057EB71).SelectedIndex = 1;
            await vm.ApplyChangesAsync();
            Check(fake.Writes.Count == 1 && fake.Writes[0].All(c => c.Id != sr.Id) && fake.Values[sr.Id] == (true, value), $"applying another setting preserves retired SR {value}");
            sr.SelectedIndex = 0;
            await vm.ApplyChangesAsync();
            Check(fake.Writes.Last().Contains((sr.Id, null)), $"retired SR {value} can explicitly return to default");
        }
    }

    private static async Task ImmediateSelectionChecks()
    {
        var fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        var vm = new NvSettingsViewModel();
        var bar = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        var power = vm.Rows.Single(r => r.Id == 0x1057EB71);
        var enabledIndex = Array.FindIndex(bar.Def.Options, o => o.Value == 1);
        var decision = new TaskCompletionSource<bool>();
        var calls = 0;
        var selection = vm.SelectOptionAsync(bar, enabledIndex, _ => { calls++; return decision.Task; });
        Check(calls == 1 && !selection.IsCompleted, "old-driver warning starts immediately on selection, before apply");
        Check(bar.SelectedIndex == 0 && !bar.PendingChange && fake.Writes.Count == 0, "unconfirmed enable never enters pending write state");
        Check(vm.IsSelectingOption && !vm.CanEditOptions && !vm.ApplyEnabled, "pending modal disables edits and apply");
        Check(await vm.ApplyChangesAsync() is null && fake.Writes.Count == 0, "apply cannot run while selection decision pending");
        Check(!await vm.SelectOptionAsync(power, 1) && power.SelectedIndex == 0, "another selection cannot race pending confirmation");
        Check(await vm.ResetAllAsync() is null && fake.Writes.Count == 0, "reset cannot race pending confirmation");
        decision.SetResult(false);
        Check(!await selection && bar.SelectedIndex == 0 && !bar.PendingChange && !vm.IsSelectingOption, "cancel immediately retains original selection");
        Check(await vm.SelectOptionAsync(bar, enabledIndex, _ => { calls++; return Task.FromResult(true); }) && bar.PendingChange, "confirmed selection becomes pending only");
        Check(fake.Writes.Count == 0, "selecting enable never writes the GPU");
        var repeated = 0;
        await vm.ApplyChangesAsync(_ => { repeated++; return Task.FromResult(false); });
        Check(repeated == 0 && fake.Writes.Count == 1 && fake.LastAcknowledged, "unchanged driver uses selection acknowledgement without second dialog");

        await vm.SelectOptionAsync(bar, 1); // off, not applied
        Check(await vm.SelectOptionAsync(bar, enabledIndex, _ => { calls++; return Task.FromResult(true); }) && calls == 3, "selecting off then enable warns again");

        fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); bar = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        await vm.SelectOptionAsync(bar, enabledIndex, _ => Task.FromResult(true));
        fake.Version = 62001;
        var changedVersionWarnings = 0;
        await vm.ApplyChangesAsync(_ => { changedVersionWarnings++; return Task.FromResult(false); });
        Check(changedVersionWarnings == 1 && fake.Writes.Count == 0 && bar.PendingChange, "changed driver invalidates old selection acknowledgement");

        fake = new FakeNv { Version = 62101 }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); bar = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        var modernWarnings = 0;
        await vm.SelectOptionAsync(bar, enabledIndex, _ => { modernWarnings++; return Task.FromResult(false); });
        Check(modernWarnings == 0 && bar.PendingChange && fake.Writes.Count == 0, "621.01 selection has no old-driver warning or write");
        await vm.ApplyChangesAsync();
        Check(fake.Writes.Count == 1 && !fake.LastAcknowledged, "621.01 apply needs no warning acknowledgement");

        fake = new FakeNv { Version = null }; ServiceLocator.NvProfile = fake;
        vm = new NvSettingsViewModel(); bar = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        var unknownWarnings = 0;
        await vm.SelectOptionAsync(bar, enabledIndex, _ => { unknownWarnings++; return Task.FromResult(true); });
        await vm.ApplyChangesAsync(_ => throw new Exception("must not ask twice"));
        Check(unknownWarnings == 1 && fake.Writes.Count == 1 && fake.LastAcknowledged, "unknown version warns immediately and confirmation is not duplicated");

        fake = new FakeNv { Version = 62000 }; ServiceLocator.NvProfile = fake;
        fake.Values[NvDriverSettingPolicy.RebarId] = (true, 1);
        vm = new NvSettingsViewModel(); bar = vm.Rows.Single(r => r.Id == NvDriverSettingPolicy.RebarId);
        var initialWarnings = 0;
        Check(await vm.SelectOptionAsync(bar, enabledIndex, _ => { initialWarnings++; return Task.FromResult(false); }) && initialWarnings == 0,
            "re-filling current enabled state does not trigger a user-action warning");
        await vm.SelectOptionAsync(bar, 0);
        Check(!await vm.SelectOptionAsync(bar, enabledIndex, _ => throw new IOException("test dialog error")) && bar.SelectedIndex == 0 && !vm.IsSelectingOption,
            "dialog error retains prior option and releases UI busy state");
        Check(!await vm.SelectOptionAsync(bar, -1) && !await vm.SelectOptionAsync(bar, 999), "invalid option indices cannot mutate selection");
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../.."));

    private static void UiWiringChecks(string source)
    {
        Console.WriteLine("Verified source tree: " + source);
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(source, "src/DeltaNFD/Views/NvSettingsPage.xaml"));
        var combo = xaml.Descendants().Single(e => e.Name.LocalName == "PageScrollComboBox" && e.Attribute("Tag") is not null);
        Check((string?)combo.Attribute("SelectedIndex") == "{x:Bind SelectedIndex, Mode=OneWay}" && (string?)combo.Attribute("Tag") == "{x:Bind Id}", "actual XAML waits for confirmation instead of TwoWay committing selection");
        Check((string?)combo.Attribute("AutomationProperties.Name") == "{x:Bind Title}", "each settings dropdown including v2 has the matching accessible name");
        var indicator = xaml.Descendants().Single(e => e.Name.LocalName == "PageScrollComboBox" && (string?)e.Attribute("AutomationProperties.Name") == "游戏内 DLSS 模型指示器");
        Check((string?)indicator.Attribute("SelectedIndex") == "{x:Bind ViewModel.DlssIndicatorSelectedIndex, Mode=TwoWay}"
            && !indicator.Ancestors().Any(e => e.Name.LocalName == "ItemsControl"), "global indicator has independent accessible UI, not a DRS row");
        var indicatorSave = xaml.Descendants().Single(e => (string?)e.Attribute("Click") == "SaveDlssIndicator_Click");
        Check((string?)indicatorSave.Attribute("IsEnabled") == "{x:Bind ViewModel.CanSaveDlssIndicator, Mode=OneWay}", "explicit indicator save does not use game pending state");
        var containerButton = xaml.Descendants().Single(e => (string?)e.Attribute("Click") == "DisplayContainer_Click");
        var containerCopy = containerButton.Parent!.Elements().Where(e => e.Name.LocalName == "TextBlock").ToArray();
        Check(containerCopy.Any(e => (string?)e.Attribute("TextWrapping") == "Wrap"
            && (string?)e.Attribute("Text") == "使用 621.14 或 621.24 时，若 nvlog.nvlgstg 持续写入，停用显示容器可避免这项额外磁盘写入。请先退出游戏。"),
            "container note names both driver versions and scopes the mitigation to the target log");
        var containerText = string.Join("\n", containerCopy.Select(e => (string?)e.Attribute("Text")));
        Check(containerText.Contains("NVIDIA 控制面板可能打不开") && !containerText.Contains("不保证提升帧"),
            "container note keeps the control-panel warning but removes the FPS disclaimer");
        var page = File.ReadAllText(Path.Combine(source, "src/DeltaNFD/Views/NvSettingsPage.xaml.cs"));
        var handler = page[page.IndexOf("private async void RowCombo_SelectionChanged")..page.IndexOf("private void OpenShaderRecovery_Click")];
        Check(handler.Contains("await ViewModel.SelectOptionAsync") && handler.Contains("combo.SelectedIndex = row.SelectedIndex"), "actual selection handler awaits warning and restores cancelled UI choice");
        Check(page.Contains("DefaultButton = ContentDialogButton.Close"), "warning defaults to cancellation");
    }

    private sealed class FakeNv : INvProfileService
    {
        public uint? Version = 62101;
        public bool ThrowEnvironment;
        public bool ThrowValues;
        public bool Locked;
        public bool LastAcknowledged;
        public string GpuName = "fixture";
        public bool IsNvidia = true;
        public Dictionary<uint, (bool HasOverride, uint Value)> Values = NvSettingsCatalog.Definitions.ToDictionary(d => d.Id,
            d => (false, NvDriverSettingPolicy.MissingValue(d.Id)));
        public List<List<(uint Id, uint? Value)>> Writes = [];
        public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync() => ThrowEnvironment ? throw new IOException("test read error") : Task.FromResult(new NvGpuEnvironmentInfo
        { IsNvidia = IsNvidia, GpuName = GpuName, NvApiAvailable = true, DrsFilesExist = true, DrsFilesLocked = Locked, DriverVersion = Version });
        public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;
        public Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync() => ThrowValues ? throw new IOException("fixture settings read failure")
            : Task.FromResult<Dictionary<uint, (bool, uint)>?>(new(Values));
        public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes, bool rebarWarningAccepted = false)
        {
            Writes.Add(changes.ToList()); LastAcknowledged = rebarWarningAccepted;
            foreach (var c in changes) Values[c.Id] = (c.Value is not null, c.Value ?? NvDriverSettingPolicy.MissingValue(c.Id));
            return Task.FromResult(OperationResult.Ok("fixture success"));
        }
        public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => throw new Exception("unexpected lock");
    }
}
