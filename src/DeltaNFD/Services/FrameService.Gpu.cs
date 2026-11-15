namespace DeltaNFD.Services;

public sealed partial class FrameService
{
    private bool _gameGpuPriorityEnabled, _nvidiaGpuShareEnabled;
    private FrameGpuPriority _gameGpuPriority = FrameGpuPriority.High;
    private int _nvidiaGpuBackgroundPercent = 10;
    public bool GameGpuPriorityEnabled
    {
        get => _gameGpuPriorityEnabled;
        set { if (!FeaturesEditable || value == _gameGpuPriorityEnabled) return; _gameGpuPriorityEnabled = value; SaveSettings(); OnPropertyChanged(); NotifyGpuPresentation(); }
    }
    public FrameGpuPriority GameGpuPriority
    {
        get => _gameGpuPriority;
        set { if (!FeaturesEditable || !Enum.IsDefined(value) || value == _gameGpuPriority) return; _gameGpuPriority = value; SaveSettings(); OnPropertyChanged(); }
    }
    public bool NvidiaGpuShareEnabled
    {
        get => _nvidiaGpuShareEnabled;
        set { if (!FeaturesEditable || value == _nvidiaGpuShareEnabled) return; _nvidiaGpuShareEnabled = value; SaveSettings(); OnPropertyChanged(); NotifyGpuPresentation(); }
    }
    public int NvidiaGpuBackgroundPercent
    {
        get => _nvidiaGpuBackgroundPercent;
        set { if (!FeaturesEditable || value is < 0 or > 100 || value == _nvidiaGpuBackgroundPercent) return; _nvidiaGpuBackgroundPercent = value; SaveSettings(); OnPropertyChanged(); NotifyGpuPresentation(); }
    }
    public string GameGpuPriorityStatusText
    {
        get
        {
            var backend = Backends.BackendProcmanService.Instance;
            return FrameGpuPresentation.Process(_gameGpuPriorityEnabled, _frameModeActive, backend.LatestStatus, DateTimeOffset.UtcNow, backend.StatusError);
        }
    }
    public string NvidiaGpuShareStatusText
    {
        get
        {
            var backend = Backends.BackendProcmanService.Instance;
            return FrameGpuPresentation.Nvidia(_nvidiaGpuShareEnabled, _frameModeActive, backend.LatestStatus, DateTimeOffset.UtcNow, backend.StatusError);
        }
    }
    public NvidiaGpuShareInfo? NvidiaGpuShareInfo => Backends.BackendProcmanService.Instance.LatestStatus?.FrameNvidiaGpuShare.Info;
    private void NotifyGpuPresentation()
    {
        OnPropertyChanged(nameof(GameGpuPriorityStatusText)); OnPropertyChanged(nameof(NvidiaGpuShareStatusText)); OnPropertyChanged(nameof(NvidiaGpuShareInfo));
    }
}
