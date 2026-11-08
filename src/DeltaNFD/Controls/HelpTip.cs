using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD.Controls;

/// <summary>
/// 「ⓘ」说明：附加到标题 TextBlock 上，在标题末尾追加一个 Hyperlink 内联（文本元素，不新增可视元素）。
/// 浮层在第一次点击时才创建。仅展示文字，不调用服务、不触发任何操作。
/// 用法：&lt;TextBlock Text="标题" ui:HelpTip.Text="完整说明"/&gt;
/// </summary>
public static class HelpTip
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HelpTip), new PropertyMetadata("", OnTextChanged));
    private static readonly DependencyProperty LinkProperty = DependencyProperty.RegisterAttached(
        "Link", typeof(Hyperlink), typeof(HelpTip), new PropertyMetadata(null));
    private static readonly DependencyProperty FlyoutProperty = DependencyProperty.RegisterAttached(
        "Flyout", typeof(Flyout), typeof(HelpTip), new PropertyMetadata(null));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        if (block.GetValue(FlyoutProperty) is Flyout { Content: StackPanel panel } && panel.Children.LastOrDefault() is TextBlock body)
            body.Text = GetText(block);
        Attach(block);
        // Text 若在本属性之后才赋值会替换内联；Loaded 时补挂一次。
        block.Loaded -= OnLoaded;
        block.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((TextBlock)sender);

    private static void Attach(TextBlock block)
    {
        if (string.IsNullOrEmpty(GetText(block))) return;
        if (block.GetValue(LinkProperty) is Hyperlink existing && block.Inlines.Contains(existing)) return;
        var title = block.Text;
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
        var icon = new Run { Text = "\uE946" };
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var font) && font is FontFamily family) icon.FontFamily = family;
        link.Inlines.Add(icon);
        AutomationProperties.SetName(link, "说明：" + title);
        link.Click += (_, _) => Show(block, title);
        block.Inlines.Add(new Run { Text = "\u2002" });
        block.Inlines.Add(link);
        block.SetValue(LinkProperty, link);
    }

    private static void Show(TextBlock block, string title)
    {
        if (block.GetValue(FlyoutProperty) is not Flyout flyout)
        {
            var panel = new StackPanel { Spacing = 6, MaxWidth = 360 };
            panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Style = Application.Current.Resources["H3Style"] as Style });
            panel.Children.Add(new TextBlock { Text = GetText(block), IsTextSelectionEnabled = true, Style = Application.Current.Resources["NoteStyle"] as Style });
            flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            block.SetValue(FlyoutProperty, flyout);
        }
        flyout.ShowAt(block);
    }
}
