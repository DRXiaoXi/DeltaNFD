using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed class ApplicationPreLaunchControl(TweakBackupStore backups,
    Func<string, Task<(int Code, string? Output, string? Error)>> execute)
{
    internal const string Query = "$ErrorActionPreference='Stop'; $m=Get-MMAgent -ErrorAction Stop; if ($m.ApplicationPreLaunch -isnot [bool]) { throw 'ApplicationPreLaunch state unavailable' }; [int]$m.ApplicationPreLaunch";
    internal static string Command(bool enable) => "$ErrorActionPreference='Stop'; " + (enable ? "Enable" : "Disable") + "-MMAgent -ApplicationPreLaunch -ErrorAction Stop";

    internal static bool? Parse(int code, string? output) => code == 0 ? output?.Trim() switch
    { "0" => false, "1" => true, _ => null } : null;

    private async Task<bool> ReadAsync()
    {
        var result = await execute(Query);
        return Parse(result.Code, result.Output) ?? throw new IOException("ApplicationPreLaunch 状态无法读取：" + (result.Error ?? result.Output));
    }

    internal async Task<OperationResult> DisableAsync()
    {
        try
        {
            var before = await ReadAsync();
            var existing = backups.GetStrict("State", "MMAgent", "ApplicationPreLaunch");
            if (existing is not null && existing.Data is not ("0" or "1")) throw new InvalidDataException("ApplicationPreLaunch 原状态备份无效。");
            backups.SaveStrict(new RegistryValueBackup
            {
                Hive = "State", KeyPath = "MMAgent", ValueName = "ApplicationPreLaunch", ValueKind = RegistryValueKind.String,
                Data = before ? "1" : "0", CreatedAt = DateTimeOffset.Now,
            });
            Log.Info($"预读取：ApplicationPreLaunch 原状态={before}；仅操作此 MMAgent 参数，不停止或禁用 SysMain。");
            if (before)
            {
                var result = await execute(Command(false));
                if (result.Code != 0) throw new IOException("定向关闭失败：" + (result.Error ?? result.Output));
            }
            var after = await ReadAsync();
            Log.Info("预读取：ApplicationPreLaunch 关闭后复检=" + after);
            return after ? OperationResult.Fail("ApplicationPreLaunch 复检仍为开启，未宣称关闭；原状态备份保留。")
                : OperationResult.Ok("ApplicationPreLaunch 实际状态已关闭；SysMain 未执行停止或禁用。");
        }
        catch (Exception ex) { Log.Error("ApplicationPreLaunch 定向关闭失败", ex); return OperationResult.Fail(ex.Message + "；未执行 SysMain 停止/禁用，已有备份保留。"); }
    }

    internal async Task<OperationResult> RestoreAsync()
    {
        try
        {
            var original = backups.GetStrict("State", "MMAgent", "ApplicationPreLaunch");
            if (original is null) return OperationResult.Skip("没有 ApplicationPreLaunch 原状态记录，未猜测或改动该 MMAgent 开关。");
            if (original.Data is not ("0" or "1")) throw new InvalidDataException("ApplicationPreLaunch 原状态备份无效。");
            var expected = original.Data == "1";
            if (await ReadAsync() != expected)
            {
                var result = await execute(Command(expected));
                if (result.Code != 0) throw new IOException("还原失败：" + (result.Error ?? result.Output));
            }
            if (await ReadAsync() != expected) throw new IOException("ApplicationPreLaunch 还原后复检不匹配。");
            backups.RemoveStrict("State", "MMAgent", "ApplicationPreLaunch");
            Log.Info("预读取：ApplicationPreLaunch 已按原状态还原并复检：" + expected);
            return OperationResult.Ok("已恢复 ApplicationPreLaunch 修改前的状态；SysMain 未执行停止或禁用。");
        }
        catch (Exception ex) { Log.Error("ApplicationPreLaunch 还原失败", ex); return OperationResult.Fail(ex.Message + "；原状态记录保留，可重试。"); }
    }
}
