using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>前端侧电源管理服务：电源计划读取/切换/导入、休眠开关、异构策略全部经系统维护后端 EXE 完成。</summary>
internal sealed class BackendPowerService : IPowerService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    public async Task<List<PowerSchemeInfo>> GetSchemesAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerSchemes, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<List<PowerSchemeInfo>>(result, "读取电源计划");
    }

    public async Task<OperationResult> SetSchemeAsync(string schemeGuid)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerSetScheme,
            new PowerSetSchemeArgs { SchemeGuid = schemeGuid }, null, CancellationToken.None);
        return BackendResults.Map(result, "切换电源计划");
    }

    public async Task<OperationResult> ImportNoPowerSaveSchemeAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerImportNoPowerSave, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "导入无省电电源计划");
    }

    public async Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerEnsureName, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "迁移电源计划名称");
    }

    public async Task<bool> IsHibernateEnabledAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerHibernateGet, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<HibernateStateDto>(result, "查询休眠状态").Enabled;
    }

    public async Task<OperationResult> SetHibernateAsync(bool enable)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerHibernateSet,
            new PowerHibernateSetArgs { Enable = enable }, null, CancellationToken.None);
        return BackendResults.Map(result, enable ? "开启休眠" : "关闭休眠");
    }

    public async Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerHeteroList, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<List<HeteroPolicyInfo>>(result, "读取异构调度策略");
    }

    public async Task<OperationResult> SetHeteroPolicyAsync(string settingGuid, int value)
        => await SetHeteroPolicyValuesAsync(settingGuid, value, value);

    public async Task<OperationResult> SetHeteroPolicyValuesAsync(string settingGuid, int acValue, int dcValue)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPowerHeteroSet,
            new PowerHeteroSetArgs { SettingGuid = settingGuid, AcValue = acValue, DcValue = dcValue }, null, CancellationToken.None);
        return BackendResults.Map(result, "设置异构调度策略");
    }
}
