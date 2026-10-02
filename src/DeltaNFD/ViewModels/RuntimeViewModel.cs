using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>运行库页（14.51.36247 检测 / AIO 修复 / 防护模式）的页面状态。</summary>
public partial class RuntimeViewModel : ObservableObject
{
    private readonly IRuntimeGuardService _guard = ServiceLocator.Guard;

    // ---------------- 检测 ----------------

    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private string checkText = "正在检测运行库（v14 问题版本 + 分支最适版本）…";
    [ObservableProperty] private bool hasIssue;

    /// <summary>存在非最适版本运行库（低于推荐版本或未安装，黄色提醒）。</summary>
    [ObservableProperty] private bool hasSuboptimal;

    public bool CanCheck => !IsChecking && !IsRepairing && !UninstallV14Busy;

    public string CheckButtonText => IsChecking ? "检测中…" : "重新检测";

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CheckButtonText));
        // RepairEnabled = !IsRepairing && !IsChecking：IsChecking 变化时必须同步通知，
        // 否则进页自动检测把按钮判灰后，检测完成按钮永远不会再亮（x:Bind 不会自动追踪依赖属性）
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(UninstallV14Enabled));
    }

    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(RepairButtonText));

    partial void OnHasSuboptimalChanged(bool value) => OnPropertyChanged(nameof(RepairButtonText));

    public async Task LoadAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var report = await _guard.ScanVcRedistsAsync();
            HasIssue = report.HasAbnormal;
            HasSuboptimal = report.HasSuboptimal;

            var text = new StringBuilder();
            if (report.HasAbnormal)
            {
                text.AppendLine($"⚠ 检测到 {report.Abnormal.Count} 个 v14 运行库（不完善版本，可能导致部分游戏/软件异常）：");
                foreach (var e in report.Abnormal)
                {
                    text.AppendLine($"· {e.Architecture}  {e.DisplayVersion}  （{e.DisplayName}）");
                }
            }
            else
            {
                text.AppendLine("✓ 未检测到 v14 运行库。");
            }

            if (report.HasSuboptimal)
            {
                text.AppendLine($"⚠ 检测到 {report.Suboptimal.Count} 项运行库非最适版本（低于推荐版本或未安装）：");
                foreach (var s in report.Suboptimal)
                {
                    text.AppendLine(string.IsNullOrEmpty(s.InstalledVersion)
                        ? $"· {s.Branch}：未安装（推荐 {s.RecommendedVersion}）"
                        : $"· {s.Branch}：{s.InstalledVersion}（推荐 ≥ {s.RecommendedVersion}）");
                }
            }
            else
            {
                text.AppendLine("✓ 各分支运行库均为最适版本。");
            }

            text.Append("👉 " + report.Advice);
            CheckText = text.ToString();
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ---------------- 修复 / 重装 ----------------

    [ObservableProperty] private bool isRepairing;
    [ObservableProperty] private string repairStatusText = "";

    public bool RepairEnabled => !IsRepairing && !IsChecking && !UninstallV14Busy;

    /// <summary>按钮三态：检测到 v14 → 修复运行库；非最适版本 → 重装运行库（升级到最适）；全部正常 → 重装运行库（确保纯净）。</summary>
    public string RepairButtonText => IsRepairing
        ? "处理中…"
        : HasIssue ? "修复运行库" : "重装运行库";

    partial void OnIsRepairingChanged(bool value)
    {
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(RepairButtonText));
        OnPropertyChanged(nameof(CanCheck));
        // 修复期间必须同时禁用防护开关（否则 IFEO 会拦截修复包自身的安装器）
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(UninstallV14Enabled));
    }

    /// <summary>静默重装运行库，逐包记录安装结果，并在完成后复检。</summary>
    public async Task<OperationResult?> RepairAsync()
    {
        if (IsRepairing)
        {
            return null;
        }

        IsRepairing = true;
        try
        {
            var progress = new Progress<string>(s => RepairStatusText = s);
            var result = await _guard.RepairVcRedistAsync(progress);
            RepairStatusText = result.Success ? "修复流程及分支复检通过。" : "修复未完成，请查看失败明细及诊断目录。";
            try { await LoadAsync(); }
            catch (Exception ex)
            {
                Log.Error("运行库修复：页面刷新检测失败", ex);
                CheckText = "页面检测刷新失败：" + ex.Message;
                return new OperationResult { Success = false, RequiresReboot = result.RequiresReboot,
                    Message = result.Message + "\n页面检测刷新失败：" + ex.Message };
            }
            return result;
        }
        finally
        {
            IsRepairing = false;
        }
    }

    // ---------------- 单独卸载 V14 运行库 ----------------

    [ObservableProperty] private bool uninstallV14Busy;
    [ObservableProperty] private string uninstallV14StatusText = "";

    public bool UninstallV14Enabled => !UninstallV14Busy && !IsRepairing && !IsChecking;

    partial void OnUninstallV14BusyChanged(bool value)
    {
        OnPropertyChanged(nameof(UninstallV14Enabled));
        OnPropertyChanged(nameof(RepairEnabled));
        OnPropertyChanged(nameof(CanCheck));
        // 卸载期间禁用防护开关：中途开启 IFEO 会拦截尚未执行的卸载器
        OnPropertyChanged(nameof(GuardToggleEnabled));
    }

    /// <summary>确认弹窗用的 v14 条目清单（空串 = 无条目）。</summary>
    public async Task<string> BuildV14ListTextAsync()
    {
        var entries = await _guard.GetV14EntriesAsync();
        return string.Join("\n", entries.Select(e =>
            $"· {e.Architecture}  {e.DisplayVersion}  （{e.DisplayName}）"));
    }

    /// <summary>单独卸载名字带「v14」的运行库（年份命名条目不动）；完成后自动重跑检测。</summary>
    public async Task<OperationResult?> UninstallV14Async()
    {
        if (UninstallV14Busy)
        {
            return null;
        }

        UninstallV14Busy = true;
        try
        {
            var progress = new Progress<string>(s => UninstallV14StatusText = s);
            var result = await _guard.UninstallV14Async(progress);
            await LoadAsync();
            return result;
        }
        finally
        {
            UninstallV14Busy = false;
        }
    }

    // ---------------- 防护模式 ----------------

    [ObservableProperty] private bool guardIsEnabled;
    [ObservableProperty] private bool guardIsBusy;
    [ObservableProperty] private bool guardIsPartial;
    [ObservableProperty] private string guardStatusText = "正在读取防护状态…";
    private bool _guardStateKnown;

    public bool GuardToggleEnabled => _guardStateKnown && !GuardIsBusy && !IsRepairing && !UninstallV14Busy;

    public string GuardToggleText => GuardIsBusy ? "切换中…" : GuardIsPartial ? "防护部分生效"
        : GuardIsEnabled ? "防护已开启" : "防护未开启";

    partial void OnGuardIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(GuardToggleEnabled));
        OnPropertyChanged(nameof(GuardToggleText));
    }

    partial void OnGuardIsEnabledChanged(bool value) => OnPropertyChanged(nameof(GuardToggleText));
    partial void OnGuardIsPartialChanged(bool value) => OnPropertyChanged(nameof(GuardToggleText));

    /// <summary>读取防护模式状态（busy 守卫版，供进页/手动刷新调用）。</summary>
    public async Task LoadGuardAsync()
    {
        if (GuardIsBusy)
        {
            return;
        }

        GuardIsBusy = true;
        try
        {
            await RefreshGuardCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：读取实际状态失败", ex);
            _guardStateKnown = false;
            GuardStatusText = $"防护状态读取失败：{ex.Message}；请重新进入此页重试。";
            OnPropertyChanged(nameof(GuardToggleEnabled));
        }
        finally
        {
            GuardIsBusy = false;
        }
    }

    /// <summary>重读防护状态核心逻辑（无 busy 守卫——SetGuardAsync 切换后必须重读，
    /// 否则状态/开关文字停留在旧值，用户回拨开关会被一致性守卫静默吞掉）。</summary>
    private async Task RefreshGuardCoreAsync()
    {
        var status = await _guard.GetStatusAsync();
        _guardStateKnown = true;
        GuardIsEnabled = status.IfeoManagedCount > 0 || status.Ue4PrereqDenied || status.Ue4RestorePending;
        GuardIsPartial = GuardIsEnabled && (!status.IfeoApplied ||
            (status.Ue4PrereqFound && !status.Ue4PrereqDenied));
        OnPropertyChanged(nameof(GuardToggleEnabled));
        GuardStatusText = status.IfeoApplied
            ? $"{(GuardIsPartial ? "防护部分生效" : "防护已开启")}：IFEO 已拦截 {status.IfeoCount}/{status.IfeoTotal} 个运行库安装器"
              + (status.Ue4PrereqFound
                  ? (status.Ue4PrereqDenied ? "，UE4 前置包已拒绝执行" : "，UE4 前置包未拦截")
                  : "")
            : GuardIsEnabled
                ? $"防护部分生效：IFEO {status.IfeoCount}/{status.IfeoTotal}，部分安装器可能仍被拦截；可关闭以清理残留"
                : $"本工具防护未开启（IFEO {status.IfeoCount}/{status.IfeoTotal}）";
        if (status.IfeoExternalCount > 0)
            GuardStatusText += $"；另有 {status.IfeoExternalCount} 项外部/归属不明拦截，请在创建它的工具中处理。";
        if (status.Ue4RestorePending) GuardStatusText += "；UE4 原文件权限恢复记录仍保留。";
    }

    /// <summary>切换防护模式。返回后端结果供页面弹窗；完成后重读真实状态。</summary>
    public async Task<OperationResult?> SetGuardAsync(bool enable)
    {
        if (GuardIsBusy)
        {
            return null;
        }

        GuardIsBusy = true;
        try
        {
            var result = enable
                ? await _guard.EnableAsync()
                : await _guard.DisableAsync();
            try
            {
                await RefreshGuardCoreAsync();
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("运行库防护：切换后读取实际状态失败", ex);
                _guardStateKnown = false;
                GuardStatusText = $"切换后无法读取实际防护状态：{ex.Message}";
                OnPropertyChanged(nameof(GuardToggleEnabled));
                return OperationResult.Fail(result.Message + "；" + GuardStatusText);
            }
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：切换过程异常", ex);
            GuardStatusText = $"防护切换异常：{ex.Message}";
            return OperationResult.Fail(GuardStatusText);
        }
        finally
        {
            GuardIsBusy = false;
        }
    }
}
