using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class FramePage
{
    private static readonly FrameGpuPriority[] GpuPriorityChoices = [FrameGpuPriority.High, FrameGpuPriority.AboveNormal,
        FrameGpuPriority.Normal, FrameGpuPriority.BelowNormal, FrameGpuPriority.Idle];
    private static readonly int[] NvidiaGpuPresets = [10, 20, 50];
    public string GameGpuPriorityTitle => GameTargetService.Default.Current.DisplayName + " GPU 调度优先级";
    public bool GameGpuPriorityOptionsEditable => FrameSrv.FeaturesEditable && FrameSrv.GameGpuPriorityEnabled;
    public int GameGpuPriorityIndex => Array.IndexOf(GpuPriorityChoices, FrameSrv.GameGpuPriority);
    public string GameGpuPriorityStatusText => FrameSrv.GameGpuPriorityStatusText;
    public bool NvidiaGpuOptionsEditable => FrameSrv.FeaturesEditable && FrameSrv.NvidiaGpuShareEnabled;
    public int NvidiaGpuPresetIndex { get { var i = Array.IndexOf(NvidiaGpuPresets, FrameSrv.NvidiaGpuBackgroundPercent); return i < 0 ? 3 : i; } }
    public double NvidiaGpuPercent => FrameSrv.NvidiaGpuBackgroundPercent;
    public string NvidiaGpuRatioText => $"前台 {100 - FrameSrv.NvidiaGpuBackgroundPercent}% / 后台 {FrameSrv.NvidiaGpuBackgroundPercent}%";
    public string NvidiaGpuShareStatusText => FrameSrv.NvidiaGpuShareStatusText;
    public string NvidiaGpuRangeText => FrameSrv.NvidiaGpuShareInfo is { Supported: true } info
        ? $"最近检测到的后台比例范围：{info.Minimum}～{info.Maximum}%，步长 {info.Step}%；预设支持另由驱动核对。"
        : "开启帧格并运行目标游戏后查询驱动支持；数值需符合实际范围和步长，不支持时不写入。";
    private void GameGpuPriority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !GameGpuPriorityOptionsEditable || sender is not ComboBox { IsLoaded: true } box) return;
        if (box.SelectedIndex >= 0 && box.SelectedIndex < GpuPriorityChoices.Length) FrameSrv.GameGpuPriority = GpuPriorityChoices[box.SelectedIndex];
    }
    private void NvidiaGpuPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !NvidiaGpuOptionsEditable || sender is not ComboBox { IsLoaded: true } box) return;
        if (box.SelectedIndex >= 0 && box.SelectedIndex < NvidiaGpuPresets.Length) FrameSrv.NvidiaGpuBackgroundPercent = NvidiaGpuPresets[box.SelectedIndex];
        else if (box.SelectedIndex == 3) NvidiaGpuPercentBox.Focus(FocusState.Programmatic);
    }
    private void NvidiaGpuPercent_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded || !sender.IsLoaded || !NvidiaGpuOptionsEditable || !double.IsFinite(args.NewValue) ||
            args.NewValue != Math.Truncate(args.NewValue) || args.NewValue is < 0 or > 100) return;
        FrameSrv.NvidiaGpuBackgroundPercent = (int)args.NewValue;
    }
}
