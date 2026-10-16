using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>着色器维护（N 卡驱动体检 + PSOCache 清理 + 健康检测 + 系统着色器清理）的页面状态。</summary>
public partial class ShaderViewModel : ObservableObject
{
    private readonly IShaderService _shader = ServiceLocator.Shader;
    private readonly ICleanupService _cleanup = ServiceLocator.Cleanup;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isClearing;
    [ObservableProperty] private string driverAdvice = "正在体检驱动…";
    [ObservableProperty] private string cacheText = "正在扫描着色器缓存…";
    [ObservableProperty] private bool canClear;
    [ObservableProperty] private string diagText = "正在检测着色器缓存结构…";
    [ObservableProperty] private bool diagBusy;
    [ObservableProperty] private string sysCacheText = "系统级着色器缓存（D3DSCache / NVIDIA DXCache / AMD DxCache）";
    [ObservableProperty] private bool isCleaningSystem;
    [ObservableProperty] private bool shaderCacheNonDefault;
    [ObservableProperty] private bool shaderCacheCheckFailed;
    [ObservableProperty] private string shaderCacheAdvice = "";


    public bool ClearEnabled => !IsBusy && !IsClearing && !IsGameRunning && CanClear;

    public bool CanRefresh => !IsBusy;

    public bool CanDiagnose => !DiagBusy && !IsGameRunning;

    public bool CanCleanSystem => !IsCleaningSystem && !IsGameRunning;

    /// <summary>游戏运行状态（LoadAsync 回填；变更时联动刷新全部依赖按钮）。</summary>
    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value)
            {
                return;
            }

            _isGameRunning = value;
            OnPropertyChanged();
            RefreshFlags();
            OnPropertyChanged(nameof(CanDiagnose));
            OnPropertyChanged(nameof(CanCleanSystem));
        }
    }

    private bool _isGameRunning;

    public string ClearButtonText => IsClearing ? "清理中…" : IsGameRunning ? "游戏运行中" : "一键清除旧着色器";

    public string DiagButtonText => DiagBusy ? "检测中…" : "开始检测";

    public string CleanSystemButtonText => IsCleaningSystem ? "清理中…" : "清理系统着色器";

    partial void OnIsBusyChanged(bool value)
    {
        RefreshFlags();
        OnPropertyChanged(nameof(CanRefresh));
    }

    partial void OnIsClearingChanged(bool value) => RefreshFlags();

    partial void OnCanClearChanged(bool value) => RefreshFlags();

    partial void OnDiagBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(DiagButtonText));
        OnPropertyChanged(nameof(CanDiagnose));
    }

    partial void OnIsCleaningSystemChanged(bool value)
    {
        OnPropertyChanged(nameof(CleanSystemButtonText));
        OnPropertyChanged(nameof(CanCleanSystem));
    }

    private void RefreshFlags()
    {
        OnPropertyChanged(nameof(ClearEnabled));
        OnPropertyChanged(nameof(ClearButtonText));
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var status = await _shader.GetStatusAsync();
            DriverAdvice = status.DriverAdvice;
            ShaderCacheNonDefault = ShaderCacheCheckFailed = false;
            if (status.IsNvidia)
            {
                try
                {
                    var env = await ServiceLocator.NvProfile.GetEnvironmentAsync();
                    ShaderCacheNonDefault = env.ShaderCache?.HasNonDefault == true;
                    ShaderCacheCheckFailed = env.ShaderCache?.Complete != true && !ShaderCacheNonDefault;
                    ShaderCacheAdvice = env.ShaderCache?.Advice ?? "后端未返回缓存大小检测结果，无法确认是否为默认。请更新后端后重新检测。";
                }
                catch (Exception ex)
                {
                    ShaderCacheCheckFailed = true;
                    ShaderCacheAdvice = "无法确认全局及三角洲着色器缓存大小，请重新检测：" + ex.Message;
                }
            }
            else ShaderCacheAdvice = "";
            IsGameRunning = status.IsGameRunning;
            CanClear = status.PsoCachePath.Length > 0 && status.PsoCacheSizeMb > 0;

            CacheText = status.PsoCachePath.Length == 0
                ? "未找到 PSOCache 目录（游戏尚未生成过着色器缓存）"
                : $"PSOCache：{status.PsoCacheSizeMb:0.0} MB\n{status.PsoCachePath}";

            // 进页自动做一次健康检测
            await DiagnoseAsync();

        }
        finally
        {
            IsBusy = false;
        }
    }


    /// <summary>清除 PSOCache。返回后端结果供页面弹窗；完成后自动重扫。</summary>
    public async Task<OperationResult?> ClearAsync()
    {
        if (IsClearing)
        {
            return null;
        }

        IsClearing = true;
        try
        {
            var result = await _shader.ClearPsoCacheAsync();
            await LoadAsync();
            return result;
        }
        finally
        {
            IsClearing = false;
        }
    }

    /// <summary>着色器健康检测（GameVer 残留 + NVPH 结构判定）。检测全程兜底：
    /// 权限不足/目录刚被删除等异常不让 async void 调用方崩溃，页面给出失败文案。</summary>
    public async Task DiagnoseAsync()
    {
        if (DiagBusy)
        {
            return;
        }

        DiagBusy = true;
        try
        {
            var diag = await _shader.DiagnoseAsync();

            var text = new StringBuilder();
            text.AppendLine(DescribeLevel(diag.Level) + diag.Summary);
            foreach (var line in diag.Details)
            {
                text.AppendLine(line);
            }

            text.Append("👉 " + diag.Advice);
            DiagText = text.ToString();
        }
        catch (Exception ex)
        {
            DiagText = "检测失败：" + ex.Message + "\n👉 若因权限不足，请以管理员身份运行本工具后重试。";
        }
        finally
        {
            DiagBusy = false;
        }
    }

    private static string DescribeLevel(ShaderDiagLevel level) => level switch
    {
        ShaderDiagLevel.Normal => "✓ ",
        ShaderDiagLevel.Abnormal or ShaderDiagLevel.AbnormalDriverIssue => "⚠ ",
        ShaderDiagLevel.Info => "ℹ ",
        _ => "— ",
    };

    /// <summary>清理系统级着色器缓存（D3DSCache / NVIDIA DXCache / AMD DxCache）。返回结果供页面弹窗。</summary>
    public async Task<OperationResult?> CleanSystemShadersAsync()
    {
        if (IsCleaningSystem)
        {
            return null;
        }

        IsCleaningSystem = true;
        try
        {
            var (freedMb, skipped) = await _cleanup.CleanShaderCachesAsync();
            var skippedNote = skipped.Count > 0
                ? "\n被占用已跳过：\n" + string.Join("\n", skipped)
                : "";
            return OperationResult.Ok($"已清理系统级着色器缓存，释放 {freedMb:0.0} MB。{skippedNote}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"清理系统着色器缓存失败：{ex.Message}");
        }
        finally
        {
            IsCleaningSystem = false;
        }
    }
}
