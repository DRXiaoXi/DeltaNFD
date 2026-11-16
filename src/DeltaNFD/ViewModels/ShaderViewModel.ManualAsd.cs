using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.ViewModels;

public partial class ShaderViewModel
{
    private readonly BackendManualAsdService _manualAsd = new();
    private ManualAsdSession? _manualSession;
    private bool _manualAsdBusy, _manualSupported;
    private string _manualAsdText = "第 1 步：开始判定将清理 PSOCache 和系统 D3DSCache；普通健康检测不会自动判断 ASD。";
    private InfoBarSeverity _manualSeverity = InfoBarSeverity.Informational;
    public bool ManualAsdBusy { get => _manualAsdBusy; private set { SetProperty(ref _manualAsdBusy, value); RefreshManualFlags(); } }
    public string ManualAsdText { get => _manualAsdText; private set => SetProperty(ref _manualAsdText, value); }
    public InfoBarSeverity ManualAsdSeverity { get => _manualSeverity; private set => SetProperty(ref _manualSeverity, value); }
    public bool CanStartManualAsd => _manualSupported && !ManualAsdBusy && !IsBusy && !IsClearing && !IsCleaningSystem && !DiagBusy && !IsGameRunning;
    public bool CanFinishManualAsd => !ManualAsdBusy && _manualSession?.Stage is ManualAsdStage.Warmup or ManualAsdStage.NotDetected;
    public bool CanCancelManualAsd => !ManualAsdBusy && _manualSession?.Stage is ManualAsdStage.Warmup or ManualAsdStage.NotDetected or ManualAsdStage.Activated;
    private void RefreshManualFlags()
    {
        OnPropertyChanged(nameof(CanStartManualAsd)); OnPropertyChanged(nameof(CanFinishManualAsd)); OnPropertyChanged(nameof(CanCancelManualAsd));
        OnPropertyChanged(nameof(ClearEnabled)); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanDiagnose)); OnPropertyChanged(nameof(CanCleanSystem));
    }
    private void SetManualSession(ManualAsdSession? session)
    {
        _manualSession = session;
        ManualAsdText = session is null ? (_manualSupported
            ? "第 1 步：点击“开始判定”，清理缓存后按提示进入游戏预热。"
            : "手动 ASD 判定仅适用于已识别三角洲安装的 NVIDIA 621 及以上驱动。")
            : session.Stage == ManualAsdStage.Activated
                ? "上次手动判定（" + session.CheckedUtc?.ToLocalTime().ToString("g") + "）：" + session.Message
                : session.Stage == ManualAsdStage.Cleaning ? "上次清理尚未确认完成，请退出游戏后重新开始判定。" : session.Message;
        ManualAsdSeverity = session?.Stage switch
        { ManualAsdStage.Activated => InfoBarSeverity.Success, ManualAsdStage.Interrupted or ManualAsdStage.NotDetected or ManualAsdStage.Cleaning => InfoBarSeverity.Warning, _ => InfoBarSeverity.Informational };
        RefreshManualFlags();
    }
    public async Task LoadManualAsdAsync()
    {
        if (ManualAsdBusy) return;
        try
        {
            var state = await _manualAsd.ReadAsync(); _manualSupported = state.Supported; SetManualSession(state.Session);
            if (state.Error.Length > 0) { ManualAsdText = state.Error; ManualAsdSeverity = InfoBarSeverity.Warning; }
        }
        catch (Exception ex) { _manualSession = null; _manualSupported = false; ManualAsdText = "手动 ASD 进度读取未完成：" + ex.Message; ManualAsdSeverity = InfoBarSeverity.Warning; RefreshManualFlags(); }
    }
    public async Task StartManualAsdAsync(GameInstallationSelection selection)
    {
        if (!CanStartManualAsd) return;
        ManualAsdBusy = true; ManualAsdText = "第 1 步：正在清理缓存并核对空目录，请勿启动游戏…";
        try
        {
            SetManualSession(await _manualAsd.StartAsync(selection));
            CacheText = "本次 PSOCache 已清理，预热后可重新检测缓存大小。";
            DiagLevel = ShaderDiagLevel.Info; DiagText = "本次缓存已清理，等待进入游戏预热；手动 ASD 判定请按上方步骤完成。";
        }
        catch (Exception ex)
        {
            _manualSession = null; ManualAsdText = "清理与判定未开始：" + ex.Message + " 请退出游戏、关闭占用缓存的应用后重新开始。"; ManualAsdSeverity = InfoBarSeverity.Warning;
            DiagLevel = ShaderDiagLevel.NotApplicable; DiagText = "本次清理未确认完成，请重新检测缓存状态。";
        }
        finally { ManualAsdBusy = false; }
    }
    public async Task FinishManualAsdAsync()
    {
        if (!CanFinishManualAsd || _manualSession is null) return;
        ManualAsdBusy = true;
        try
        {
            SetManualSession(await _manualAsd.FinishAsync(_manualSession.Id));
            await DiagnoseAsync(); // Read-only file metadata; safe while the game is still open.
        }
        catch (Exception ex) { ManualAsdText = "本次检测未完成，未判定 ASD 激活：" + ex.Message + " 可稍后重试或重新开始判定。"; ManualAsdSeverity = InfoBarSeverity.Warning; }
        finally { ManualAsdBusy = false; }
    }
    public async Task CancelManualAsdAsync()
    {
        if (!CanCancelManualAsd || _manualSession is null) return;
        ManualAsdBusy = true;
        try { SetManualSession(await _manualAsd.CancelAsync(_manualSession.Id)); }
        catch (Exception ex) { ManualAsdText = "取消未完成：" + ex.Message; ManualAsdSeverity = InfoBarSeverity.Warning; }
        finally { ManualAsdBusy = false; }
    }
}
