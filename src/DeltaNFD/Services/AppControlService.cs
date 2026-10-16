using System.ComponentModel;
namespace DeltaNFD.Services;

/// <summary>应用级控制：开机自动启动、关闭后最小化到托盘。</summary>
public interface IAppControlService : INotifyPropertyChanged
{
    /// <summary>关闭窗口后是否驻留托盘（即时生效，持久化）。</summary>
    bool CloseToTrayEnabled { get; }

    /// <summary>开机自动启动当前是否已启用（计划任务存在 = 已启用）。</summary>
    Task<bool> IsAutoStartEnabledAsync();

    /// <summary>开启/关闭开机自动启动（计划任务方式，登录自启且不弹 UAC）。</summary>
    Task<OperationResult> SetAutoStartAsync(bool enable);

    /// <summary>设置关闭后最小化到托盘（持久化，UI 层订阅 PropertyChanged 即时应用）。</summary>
    void SetCloseToTray(bool enable);
}

/// <summary>应用级控制的真实实现。</summary>
public sealed class AppControlService : IAppControlService
{
    private const string AutoStartTaskName = "DeltaNFD_AutoStart";
    private const string LegacyAutoStartTaskName = "DeltaOptimizer_AutoStart";

    private bool _closeToTrayEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppControlService()
    {
        _closeToTrayEnabled = AppSettingsStore.Read().CloseToTrayEnabled;
    }

    public bool CloseToTrayEnabled
    {
        get => _closeToTrayEnabled;
        private set
        {
            if (_closeToTrayEnabled == value)
            {
                return;
            }

            _closeToTrayEnabled = value;
            AppSettingsStore.Update(s => s.CloseToTrayEnabled = value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseToTrayEnabled)));
        }
    }

    public void SetCloseToTray(bool enable)
    {
        if (enable && OfflineModeGuard.BlocksNormalAutomation)
        {
            Log.Warn("脱机模式或恢复流程中，拒绝开启托盘驻留。");
            CloseToTrayEnabled = false;
            return;
        }
        CloseToTrayEnabled = enable;
    }

    public async Task<bool> IsAutoStartEnabledAsync()
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return false;
        var current = await OfflineTaskOwnership.InspectAsync(AutoStartTaskName);
        var legacy = await OfflineTaskOwnership.InspectAsync(LegacyAutoStartTaskName);
        if (current.State == OfflineScheduledTaskState.ForeignOrUnknown || legacy.State == OfflineScheduledTaskState.ForeignOrUnknown)
        {
            Log.Warn("自启动状态查询发现归属未知的同名计划任务，未将其当作本工具自启动。");
            return false;
        }
        if (current.State == OfflineScheduledTaskState.Owned) return current.Enabled;
        if (legacy.State != OfflineScheduledTaskState.Owned || !legacy.Enabled) return false;

        var migration = await SetAutoStartAsync(enable: true);
        if (!migration.Success) Log.Warn($"旧版自启动任务迁移失败：{migration.Message}");
        var migrated = await OfflineTaskOwnership.InspectAsync(AutoStartTaskName);
        return migrated.State == OfflineScheduledTaskState.Owned && migrated.Enabled;
    }

    public async Task<OperationResult> SetAutoStartAsync(bool enable)
    {
        if (enable && OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("脱机模式或恢复流程正在运行，已禁止开机自动启动。");
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return OperationResult.Fail("无法确定本程序路径。");
        }

        if (enable)
        {
            var create = await OfflineTaskOwnership.EnsureOwnedTaskAsync(AutoStartTaskName, exePath);
            if (!create.Success) return OperationResult.Fail("创建开机自启动任务失败：" + create.Message);
            var legacy = await OfflineTaskOwnership.RemoveOwnedTaskAsync(LegacyAutoStartTaskName, exePath);
            if (!legacy.Success)
            {
                await OfflineTaskOwnership.RemoveOwnedTaskAsync(AutoStartTaskName, exePath);
                return OperationResult.Fail("新任务已创建，但旧任务无法安全移除；已尝试回滚新任务：" + legacy.Message);
            }
            return OperationResult.Ok("已开启开机自动启动（登录后自动运行，不弹 UAC）。");
        }

        var currentRemoved = await OfflineTaskOwnership.RemoveOwnedTaskAsync(AutoStartTaskName, exePath);
        if (!currentRemoved.Success) return currentRemoved;
        var legacyRemoved = await OfflineTaskOwnership.RemoveOwnedTaskAsync(LegacyAutoStartTaskName, exePath);
        return legacyRemoved.Success ? OperationResult.Ok("已关闭本工具的开机自动启动。") : legacyRemoved;
    }
}
