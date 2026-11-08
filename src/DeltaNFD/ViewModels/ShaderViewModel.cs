using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>着色器维护（N 卡驱动体检 + PSOCache 清理 + 健康检测 + 系统着色器清理）的页面状态。</summary>
public partial class ShaderViewModel : ObservableObject
{
    private readonly IShaderService _shader = ServiceLocator.Shader;
    private readonly ICleanupService _cleanup = ServiceLocator.Cleanup;

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isBusy, value))
            {
                return;
            }

            OnPropertyChanging(nameof(IsBusy));
            isBusy = value;
            OnIsBusyChanged(value);
            OnPropertyChanged(nameof(IsBusy));
        }
    }
    partial void OnIsBusyChanged(bool value);

    private bool isClearing;
    public bool IsClearing
    {
        get => isClearing;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isClearing, value))
            {
                return;
            }

            OnPropertyChanging(nameof(IsClearing));
            isClearing = value;
            OnIsClearingChanged(value);
            OnPropertyChanged(nameof(IsClearing));
        }
    }
    partial void OnIsClearingChanged(bool value);

    private string driverAdvice = "正在体检驱动…";
    public string DriverAdvice
    {
        get => driverAdvice;
        set => SetProperty(ref driverAdvice, value);
    }

    private string cacheText = "正在扫描着色器缓存…";
    public string CacheText
    {
        get => cacheText;
        set => SetProperty(ref cacheText, value);
    }

    private bool canClear;
    public bool CanClear
    {
        get => canClear;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(canClear, value))
            {
                return;
            }

            OnPropertyChanging(nameof(CanClear));
            canClear = value;
            OnCanClearChanged(value);
            OnPropertyChanged(nameof(CanClear));
        }
    }
    partial void OnCanClearChanged(bool value);

    private string diagText = "正在检测着色器缓存结构…";
    public string DiagText
    {
        get => diagText;
        set => SetProperty(ref diagText, value);
    }
    private ShaderDiagLevel diagLevel = ShaderDiagLevel.NotApplicable;
    public ShaderDiagLevel DiagLevel { get => diagLevel; set => SetProperty(ref diagLevel, value); }

    private bool diagBusy;
    public bool DiagBusy
    {
        get => diagBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(diagBusy, value))
            {
                return;
            }

            OnPropertyChanging(nameof(DiagBusy));
            diagBusy = value;
            OnDiagBusyChanged(value);
            OnPropertyChanged(nameof(DiagBusy));
        }
    }
    partial void OnDiagBusyChanged(bool value);

    private string sysCacheText = "系统级着色器缓存（D3DSCache / NVIDIA DXCache+GLCache / AMD DxCache+DxcCache+GLCache / Intel ShaderCache）";
    public string SysCacheText
    {
        get => sysCacheText;
        set => SetProperty(ref sysCacheText, value);
    }

    private bool isCleaningSystem;
    public bool IsCleaningSystem
    {
        get => isCleaningSystem;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(isCleaningSystem, value))
            {
                return;
            }

            OnPropertyChanging(nameof(IsCleaningSystem));
            isCleaningSystem = value;
            OnIsCleaningSystemChanged(value);
            OnPropertyChanged(nameof(IsCleaningSystem));
        }
    }
    partial void OnIsCleaningSystemChanged(bool value);

    private bool shaderCacheNonDefault;
    public bool ShaderCacheNonDefault
    {
        get => shaderCacheNonDefault;
        set => SetProperty(ref shaderCacheNonDefault, value);
    }

    private bool shaderCacheCheckFailed;
    public bool ShaderCacheCheckFailed
    {
        get => shaderCacheCheckFailed;
        set => SetProperty(ref shaderCacheCheckFailed, value);
    }

    private string shaderCacheAdvice = "";
    public string ShaderCacheAdvice
    {
        get => shaderCacheAdvice;
        set => SetProperty(ref shaderCacheAdvice, value);
    }

    private string shaderCacheGlobalText = "等待检测…";
    public string ShaderCacheGlobalText
    {
        get => shaderCacheGlobalText;
        private set => SetProperty(ref shaderCacheGlobalText, value);
    }
    private string shaderCacheGameText = "等待检测…";
    public string ShaderCacheGameText
    {
        get => shaderCacheGameText;
        private set => SetProperty(ref shaderCacheGameText, value);
    }


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
        ShaderCacheGlobalText = ShaderCacheGameText = "等待检测…";
        ShaderCacheNonDefault = ShaderCacheCheckFailed = false;
        try
        {
            var status = await _shader.GetStatusAsync();
            DriverAdvice = status.DriverAdvice;
            if (status.IsNvidia)
            {
                try
                {
                    var env = await ServiceLocator.NvProfile.GetEnvironmentAsync();
                    ShaderCacheGlobalText = ShaderCacheStatusPresentation.Scope(env.ShaderCache, "全局设置");
                    ShaderCacheGameText = ShaderCacheStatusPresentation.Scope(env.ShaderCache, "三角洲程序设置");
                    ShaderCacheNonDefault = env.ShaderCache?.HasNonDefault == true;
                    var complete = ShaderCacheStatusPresentation.Complete(env.ShaderCache);
                    ShaderCacheCheckFailed = !complete && !ShaderCacheNonDefault;
                    ShaderCacheAdvice = env.ShaderCache is null
                        ? "后端未返回缓存大小检测结果，无法确认是否为默认。请更新后端后重新检测。"
                        : complete ? env.ShaderCache.Advice
                        : ShaderCacheNonDefault ? env.ShaderCache.Advice + "\n部分缓存范围未确认，请重新检测。"
                        : "缓存范围缺失、重复或未读全，无法确认全局及三角洲设置。\n" + env.ShaderCache.Details;
                }
                catch (Exception ex)
                {
                    ShaderCacheCheckFailed = true;
                    ShaderCacheGlobalText = ShaderCacheGameText = "读取失败，无法确认当前设置。";
                    ShaderCacheAdvice = "无法确认全局及三角洲着色器缓存大小，请重新检测：" + ex.Message;
                }
            }
            else if (status.GpuDetectionState == ShaderGpuDetectionState.Unknown)
            {
                ShaderCacheCheckFailed = true;
                ShaderCacheGlobalText = ShaderCacheGameText = "显卡状态未确认，暂无法核对。";
                ShaderCacheAdvice = "显卡信息未读全，无法确认缓存大小是否适用，请重新检测。";
            }
            else
            {
                ShaderCacheAdvice = "";
                ShaderCacheGlobalText = ShaderCacheGameText = "未检测到 NVIDIA 显卡，本项不适用。";
            }
            IsGameRunning = status.IsGameRunning;
            // 0 字节或舍入为 0.0 MB 的残留也必须允许再次清理。
            CanClear = status.PsoCachePaths.Count > 0 || status.PsoCachePath.Length > 0;

            CacheText = !CanClear
                ? "未找到 PSOCache 目录（游戏尚未生成过着色器缓存）"
                : $"PSOCache：{status.PsoCacheSizeMb:0.0} MB\n"
                    + string.Join("\n", status.PsoCachePaths.Count > 0 ? status.PsoCachePaths : [status.PsoCachePath]);

            // 进页自动做一次健康检测
            await DiagnoseAsync();

        }
        catch (Exception ex)
        {
            DriverAdvice = "检测未完成：" + ex.Message;
            CacheText = "PSOCache 状态未确认，请重新检测。";
            DiagText = "检测未完成，请重新检测：" + ex.Message;
            CanClear = false;
            ShaderCacheCheckFailed = true;
            ShaderCacheGlobalText = ShaderCacheGameText = "读取失败，无法确认当前设置。";
            ShaderCacheAdvice = "无法确认当前驱动及缓存状态，请重新检测：" + ex.Message;
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
            try { await LoadAsync(); }
            catch (Exception ex) { CacheText = "清理后的状态刷新失败，请重新检测：" + ex.Message; }
            return result;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("PSOCache 清理未完成：" + ex.Message + "\n请核对原因后再次清理。");
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
        DiagLevel = ShaderDiagLevel.NotApplicable;
        try
        {
            var diag = await _shader.DiagnoseAsync();
            DiagLevel = diag.Level;

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
        ShaderDiagLevel.Warning or ShaderDiagLevel.Abnormal or ShaderDiagLevel.AbnormalDriverIssue => "⚠ ",
        ShaderDiagLevel.Info => "ℹ ",
        _ => "— ",
    };

    /// <summary>
    /// 清理系统级着色器缓存（D3DSCache / NVIDIA DXCache+GLCache / AMD DxCache+DxcCache+GLCache / Intel ShaderCache）。
    /// 返回结果供页面弹窗；链接、权限、占用等未完成项不能被包装成完整成功。
    /// </summary>
    public async Task<OperationResult?> CleanSystemShadersAsync()
    {
        if (IsCleaningSystem)
        {
            return null;
        }

        IsCleaningSystem = true;
        try
        {
            var report = await _cleanup.CleanShaderCachesAsync();
            var skippedNote = report.Skipped.Count > 0
                ? "\n未完成/已跳过：\n" + string.Join("\n", report.Skipped.Take(12))
                : "";

            if (report.GameStarted && report.DeletedFiles == 0)
                return OperationResult.Fail("清理已停止：三角洲正在运行。" + skippedNote);
            if (report.Cancelled && report.DeletedFiles == 0)
                return OperationResult.Fail("清理已取消。" + skippedNote);
            if (report.FailedItems > 0 && report.DeletedFiles == 0)
                return OperationResult.Fail(report.Message + skippedNote);

            if (report.Partial || report.GameStarted || report.Cancelled)
            {
                return new OperationResult
                {
                    Success = true,
                    IsPartial = true,
                    Message = report.Message + skippedNote,
                };
            }

            return OperationResult.Ok(report.Message + skippedNote);
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
