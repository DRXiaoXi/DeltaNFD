using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeltaNFD.Views;

public sealed partial class SettingsPage
{
    private bool _mcpInitializing;
    private void InitializeMcpControls()
    {
        _mcpInitializing = true;
        McpAccessToggle.IsOn = App.MainWindow?.McpEnabled == true;
        _mcpInitializing = false;
        McpStatusText.Text = App.MainWindow?.McpStatus ?? "主窗口尚未就绪。";
        Loaded += (_, _) => { if (App.MainWindow is { } window) window.McpChanged += RefreshMcpStatus; RefreshMcpStatus(); };
        Unloaded += (_, _) => { if (App.MainWindow is { } window) window.McpChanged -= RefreshMcpStatus; };
    }
    private void RefreshMcpStatus()
    {
        _mcpInitializing = true;
        McpAccessToggle.IsOn = App.MainWindow?.McpEnabled == true;
        _mcpInitializing = false;
        McpStatusText.Text = App.MainWindow?.McpStatus ?? "主窗口尚未就绪。";
    }
    private async void McpAccess_Toggled(object sender, RoutedEventArgs e)
    {
        if (_mcpInitializing || McpStatusText is null || App.MainWindow is not { } window) return;
        McpAccessToggle.IsEnabled = false;
        var changed = false;
        try { await window.SetMcpEnabledAsync(McpAccessToggle.IsOn); changed = true; }
        catch (Exception ex) { McpStatusText.Text = "接入未更改：" + ex.Message; }
        finally
        {
            _mcpInitializing = true; McpAccessToggle.IsOn = window.McpEnabled; _mcpInitializing = false;
            McpAccessToggle.IsEnabled = true;
            if (changed) McpStatusText.Text = window.McpStatus;
        }
    }
    private void CopyMcpConfig_Click(object sender, RoutedEventArgs e)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Tools", "Mcp", "DeltaNFD.Mcp.exe");
        var text = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
            { ["delta-nfd"] = new { command = executable, args = Array.Empty<string>() } } }, new JsonSerializerOptions { WriteIndented = true });
        try { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); McpStatusText.Text = App.MainWindow?.McpEnabled == true
            ? "Agent 配置已复制，可在 Agent 客户端连接。" : "Agent 配置已复制；当前接入未开启，请查看上方状态并开启接入。"; }
        catch { McpStatusText.Text = "复制失败，可在下面选择并复制配置。"; }
        McpConfigText.Text = text;
        McpConfigText.Visibility = Visibility.Visible;
    }
}
