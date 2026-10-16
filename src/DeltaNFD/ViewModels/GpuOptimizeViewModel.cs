using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Models;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

public partial class GpuOptimizeViewModel : ObservableObject
{
    private readonly IGpuOptimizer _optimizer = ServiceLocator.GpuOptimizer;

    public string[] LowLatencyOptions { get; } = ["关闭", "开启", "超极速"];
    public string[] PowerModeOptions { get; } = ["自动", "正常", "最高性能优先"];
    public string[] ShaderCacheOptions { get; } = ["驱动默认", "5 GB", "10 GB", "无限制"];
    public string[] ThreadOptOptions { get; } = ["自动", "开", "关"];

    [ObservableProperty] private string gpuModel = "检测中…";
    [ObservableProperty] private string gpuSpecText = "";
    [ObservableProperty] private string driverVersion = "";
    [ObservableProperty] private double temperature;
    [ObservableProperty] private string temperatureText = "--";
    [ObservableProperty] private double usage;
    [ObservableProperty] private string usageText = "--";

    [ObservableProperty] private int lowLatencyIndex = 2;
    [ObservableProperty] private int powerModeIndex = 2;
    [ObservableProperty] private int shaderCacheIndex = 2;
    [ObservableProperty] private int threadOptIndex = 0;
    [ObservableProperty] private bool verticalSync;
    [ObservableProperty] private double digitalVibrance = 55;
    [ObservableProperty] private string vibranceText = "55%";

    partial void OnDigitalVibranceChanged(double value)
    {
        VibranceText = $"{value:0}%";
    }

    [ObservableProperty] private bool isApplying;
    [ObservableProperty] private bool isApplyInfoVisible;
    [ObservableProperty] private string applyResultText = "";

    public GpuOptimizeViewModel()
    {
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var gpu = await _optimizer.GetGpuInfoAsync();

        GpuModel = $"{gpu.Vendor} {gpu.Model}";
        GpuSpecText = $"{gpu.VramGb:0} GB 显存 · 温度 {gpu.TemperatureC:0}°C";
        DriverVersion = gpu.DriverVersion;
        Temperature = gpu.TemperatureC;
        TemperatureText = $"{gpu.TemperatureC:0}°C";
        Usage = gpu.Usage;
        UsageText = $"{gpu.Usage:0}%";
    }

    [RelayCommand]
    private void ApplyRecommended()
    {
        LowLatencyIndex = 2;
        PowerModeIndex = 2;
        ShaderCacheIndex = 2;
        ThreadOptIndex = 0;
        VerticalSync = false;
        DigitalVibrance = 55;
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        if (IsApplying)
        {
            return;
        }

        IsApplying = true;
        IsApplyInfoVisible = false;

        var settings = new GpuSettings
        {
            LowLatencyMode = LowLatencyOptions[LowLatencyIndex],
            PowerManagementMode = PowerModeOptions[PowerModeIndex],
            ShaderCacheSize = ShaderCacheOptions[ShaderCacheIndex],
            ThreadedOptimization = ThreadOptOptions[ThreadOptIndex],
            VerticalSync = VerticalSync,
            DigitalVibrance = (int)DigitalVibrance,
        };

        await _optimizer.ApplySettingsAsync(settings);

        IsApplying = false;
        ApplyResultText = "已应用 6 项显卡设置（演示数据，未真实写入驱动）";
        IsApplyInfoVisible = true;
    }
}
