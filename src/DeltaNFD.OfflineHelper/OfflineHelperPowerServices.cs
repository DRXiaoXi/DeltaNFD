namespace DeltaNFD.Services;

// CpuTopologyService references the regular power service only for its online-mode
// hetero-policy path. The offline helper's scheduling path never calls that path;
// this fail-closed stub prevents accidental power-plan writes from the helper.
internal static class ServiceLocator
{
    internal static IPowerService Power { get; } = new OfflinePowerService();
}

internal static class PowerService
{
    internal static readonly (string Guid, string Title)[] HeteroPolicySettings =
    [
        ("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", "生效的异类策略"),
        ("93b8b6dc-0698-4d1c-9ee4-0644e900c85d", "异类线程调度策略"),
        ("bae08b81-2d5e-4688-ad6a-13243356654b", "异类短线程调度策略"),
    ];
}

internal sealed class OfflinePowerService : IPowerService
{
    private static OperationResult Denied() => OperationResult.Fail("脱机助手不执行电源计划或异类策略写入。");
    public Task<List<PowerSchemeInfo>> GetSchemesAsync() => Task.FromResult(new List<PowerSchemeInfo>());
    public Task<OperationResult> SetSchemeAsync(string schemeGuid) => Task.FromResult(Denied());
    public Task<OperationResult> ImportNoPowerSaveSchemeAsync() => Task.FromResult(Denied());
    public Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync() => Task.FromResult(Denied());
    public Task<bool> IsHibernateEnabledAsync() => Task.FromResult(false);
    public Task<OperationResult> SetHibernateAsync(bool enable) => Task.FromResult(Denied());
    public Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync() => Task.FromResult(new List<HeteroPolicyInfo>());
    public Task<OperationResult> SetHeteroPolicyAsync(string settingGuid, int value) => Task.FromResult(Denied());
    public Task<OperationResult> SetHeteroPolicyValuesAsync(string settingGuid, int acValue, int dcValue) => Task.FromResult(Denied());
}
