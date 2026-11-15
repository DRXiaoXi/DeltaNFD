using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface IPendulumPreparationEnvironment
{
    string? Blocker();
    string Sid { get; }
    string BootId { get; }
    void VerifyPackage();
    bool TestModeActive();
    bool TrustPresent(bool publisher);
    PendulumWin10DriverStatus Driver();
    PendulumPreparationRecord? Read();
    void Save(PendulumPreparationRecord record);
    void CheckPreviousChild(PendulumPreparationRecord record);
    void BackupBoot(PendulumPreparationRecord record);
    void EnableTestSigning(PendulumPreparationRecord record);
    void AddTrust(bool publisher);
    void InstallOrStart(PendulumPreparationRecord record);
}

internal static class PendulumPreparationEngine
{
    internal static OperationResult Run(IPendulumPreparationEnvironment env, PendulumPreparationArgs args)
    {
        PendulumPreparationRecord? record = null;
        try
        {
            if (env.Blocker() is { } blocked) return OperationResult.Fail(blocked);
            if (args.PackageId != PendulumPreparationPackage.Id) return OperationResult.Fail("驱动包身份不匹配。");
            if (!args.ResumeOnly && !args.ConfirmedSensitiveChanges) return OperationResult.Fail("尚未确认测试签名和证书信任改动，未执行准备。");
            env.VerifyPackage();
            if (!Guid.TryParse(env.BootId, out _)) return OperationResult.Fail("无法核对本次启动身份，未执行自动准备。");
            record = env.Read();
            if (record is not null)
            {
                PendulumPreparationInspection.Validate(record);
                if (record.Sid != env.Sid) return OperationResult.Fail("准备记录属于其他用户。");
                env.CheckPreviousChild(record);
            }
            if (args.ResumeOnly)
            {
                if (record is null) return OperationResult.Fail("没有受保护的授权记录，不会自动导入证书或修改启动项。");
                if (!env.TestModeActive())
                    return record.Stage == "AwaitRestart" && record.AuthorizedBootId == env.BootId
                        ? OperationResult.Ok("测试模式已配置，仍需完成 Windows 重启；重开软件不能代替重启。", requiresReboot: true)
                        : OperationResult.Fail("本次启动未启用测试签名，停止自动续办；如需再次开启，请重新确认一键准备。");
                if (!env.TrustPresent(false) || !env.TrustPresent(true))
                    return OperationResult.Fail("证书信任已变化，自动续办不会重新导入；请点击一键准备重新确认。");
                if (record.Stage == "Ready" && !env.Driver().DriverRegistered)
                    return OperationResult.Fail("已完成准备的驱动现已移除，自动续办不会重新安装；如需恢复，请重新确认一键准备。");
            }
            else
            {
                if (record is null)
                {
                    record = new() { Sid = env.Sid, AuthorizedBootId = env.BootId,
                        TestModeWasActive = env.TestModeActive(), RootWasPresent = env.TrustPresent(false), PublisherWasPresent = env.TrustPresent(true) };
                    env.Save(record); // authorization and original state before any system mutation
                }
                if (!env.TestModeActive())
                {
                    if (record.BootBackupSha.Length == 0)
                    { record.Stage = "BootBackup"; env.Save(record); env.BackupBoot(record); env.Save(record); }
                    if (env.Blocker() is { } beforeBoot) return OperationResult.Fail(beforeBoot);
                    record.Stage = "BootSetting"; record.BootEditAttempted = true; env.Save(record);
                    env.EnableTestSigning(record); // fixed TESTSIGNING only; never secure-boot/HVCI/BitLocker switches
                }
                foreach (var publisher in new[] { false, true })
                {
                    if (env.TrustPresent(publisher)) continue; // preserve pre-existing or ambiguous ownership
                    if (env.Blocker() is { } beforeTrust) return OperationResult.Fail(beforeTrust);
                    record.Stage = publisher ? "PublisherTrust" : "RootTrust";
                    if (publisher) record.PublisherAddAttempted = true; else record.RootAddAttempted = true;
                    env.Save(record); env.AddTrust(publisher);
                    if (!env.TrustPresent(publisher)) throw new IOException("证书信任回读未确认。");
                    if (publisher) record.PublisherAddedConfirmed = true; else record.RootAddedConfirmed = true;
                    env.Save(record);
                }
                if (!env.TestModeActive())
                {
                    record.Stage = "AwaitRestart"; record.LastError = ""; env.Save(record);
                    return OperationResult.Ok("已自动配置测试签名和证书信任。请重启电脑；再次打开软件后将自动安装、启动驱动并检测持有值。", requiresReboot: true);
                }
            }
            if (env.Blocker() is { } beforeDriver) return OperationResult.Fail(beforeDriver);
            if (!env.Driver().Ready)
            { record!.Stage = "Driver"; env.Save(record); env.InstallOrStart(record); }
            var ready = env.Driver();
            if (!ready.Ready) throw new IOException("驱动准备后核对未通过：" + ready.Message);
            record!.Stage = "Ready"; record.LastError = ""; env.Save(record);
            return OperationResult.Ok("自研驱动已准备并通过身份与运行核对，可以自动检测持有值。");
        }
        catch (Exception ex)
        {
            if (record is not null) { record.LastError = ex.Message; try { env.Save(record); } catch { } }
            return new() { Message = "自动准备未完成：" + ex.Message + "；准备记录与已有备份已保留。",
                IsPartial = record is not null, OutcomeUnknown = !string.IsNullOrEmpty(record?.ChildKind),
                RequiresReboot = record?.BootEditAttempted == true };
        }
    }
}

public static class PendulumDriverPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<OperationResult> RunAsync(PendulumPreparationArgs args, string applicationRoot, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) return OperationResult.Fail("另一个驱动准备操作正在执行。");
        try
        {
            return await Task.Run(() =>
            {
                using var machineLease = new Mutex(false, @"Global\DeltaNFD.PendulumDriverPreparation.v1");
                bool taken;
                try { taken = machineLease.WaitOne(0); } catch (AbandonedMutexException) { taken = true; }
                if (!taken) return OperationResult.Fail("另一会话正在准备钟摆驱动。");
                try
                {
                    using var environment = new WindowsPendulumPreparationEnvironment(applicationRoot);
                    return PendulumPreparationEngine.Run(environment, args);
                }
                finally { machineLease.ReleaseMutex(); }
            }, CancellationToken.None);
        }
        finally { Gate.Release(); }
    }
}
