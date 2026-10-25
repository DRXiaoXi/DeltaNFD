using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Controls;

/// <summary>
/// 功能页统一标题：左侧「‹」+ 标题文本，整块可点，点击返回主页。
/// 返回复用既有导航入口 <see cref="App.MainWindow"/>.<c>NavigateByTag("dashboard")</c>。
/// </summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, OnTitleChanged));

    public PageHeader()
    {
        InitializeComponent();
    }

    /// <summary>标题文本（同时作为返回按钮的无障碍名称）。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PageHeader header) return;
        var text = e.NewValue as string ?? string.Empty;
        header.TitleText.Text = text;
        AutomationProperties.SetName(header.HeaderButton, "返回主页（当前：" + text + "）");
    }

    private void OnBackToHome(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateByTag("dashboard");
}
