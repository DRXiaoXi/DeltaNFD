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

    private string gpuModel = "检测中…";
    public string GpuModel
    {
        get => gpuModel;
        set => SetProperty(ref gpuModel, value);
    }

    private string gpuSpecText = "";
    public string GpuSpecText
    {
        get => gpuSpecText;
        set => SetProperty(ref gpuSpecText, value);
    }

    private string driverVersion = "";
    public string DriverVersion
    {
        get => driverVersion;
        set => SetProperty(ref driverVersion, value);
    }

    private double temperature;
    public double Temperature
    {
        get => temperature;
        set => SetProperty(ref temperature, value);
    }

    private string temperatureText = "--";
    public string TemperatureText
    {
        get => temperatureText;
        set => SetProperty(ref temperatureText, value);
    }

    private double usage;
    public double Usage
    {
        get => usage;
        set => SetProperty(ref usage, value);
    }

    private string usageText = "--";
    public string UsageText
    {
        get => usageText;
        set => SetProperty(ref usageText, value);
    }

    private int lowLatencyIndex = 2;
    public int LowLatencyIndex
    {
        get => lowLatencyIndex;
        set => SetProperty(ref lowLatencyIndex, value);
    }

    private int powerModeIndex = 2;
    public int PowerModeIndex
    {
        get => powerModeIndex;
        set => SetProperty(ref powerModeIndex, value);
    }

    private int shaderCacheIndex = 2;
    public int ShaderCacheIndex
    {
        get => shaderCacheIndex;
        set => SetProperty(ref shaderCacheIndex, value);
    }

    private int threadOptIndex = 0;
    public int ThreadOptIndex
    {
        get => threadOptIndex;
        set => SetProperty(ref threadOptIndex, value);
    }

    private bool verticalSync;
    public bool VerticalSync
    {
        get => verticalSync;
        set => SetProperty(ref verticalSync, value);
    }

    private double digitalVibrance = 55;
    public double DigitalVibrance
    {
        get => digitalVibrance;
        set
        {
            if (EqualityComparer<double>.Default.Equals(digitalVibrance, value))
            {
                return;
            }

            OnPropertyChanging(nameof(DigitalVibrance));
            digitalVibrance = value;
            OnDigitalVibranceChanged(value);
            OnPropertyChanged(nameof(DigitalVibrance));
        }
    }
    partial void OnDigitalVibranceChanged(double value);

    private string vibranceText = "55%";
    public string VibranceText
    {
        get => vibranceText;
        set => SetProperty(ref vibranceText, value);
    }

    partial void OnDigitalVibranceChanged(double value)
    {
        VibranceText = $"{value:0}%";
    }

    private bool isApplying;
    public bool IsApplying
    {
        get => isApplying;
        set => SetProperty(ref isApplying, value);
    }

    private bool isApplyInfoVisible;
    public bool IsApplyInfoVisible
    {
        get => isApplyInfoVisible;
        set => SetProperty(ref isApplyInfoVisible, value);
    }

    private string applyResultText = "";
    public string ApplyResultText
    {
        get => applyResultText;
        set => SetProperty(ref applyResultText, value);
    }

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
