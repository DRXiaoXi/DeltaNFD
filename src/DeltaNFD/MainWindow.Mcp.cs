using System.Text.Json;
using DeltaNFD.AgentBridge;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private AgentPipeServer? _mcpServer;
    private AgentOperations? _mcpOperations;
    private string? _mcpStartupNote;
    private (string Arguments, uint? Driver)? _mcpRebarConfirmation;
    internal event Action? McpChanged;
    internal bool McpEnabled => _mcpServer is not null;
    internal string McpStatus => (McpEnabled ? "本次接入已开启。" : _mcpOperations?.HasActive == true ? "接入已关闭，已有任务仍在收尾。" : _mcpStartupNote ?? "本次接入已关闭；下次正常启动时会自动开启。") + (_mcpOperations?.LastMessage ?? "");

    private void InitializeMcpBridge()
    {
        Closed += async (_, _) => { try { await SetMcpEnabledAsync(false); } catch (Exception ex) { Log.Warn("MCP 退出：" + ex.Message); } };
    }

    internal async Task StartMcpOnStartupAsync()
    {
        // Start only after App has registered and presented the fully constructed main window.
        if (OfflineModeGuard.BlocksNormalAutomation || App.PendingFileFallback is not null)
        {
            _mcpStartupNote = "当前为维护或恢复模式，未自动开启接入。";
            McpChanged?.Invoke();
            return;
        }
        try { await SetMcpEnabledAsync(true); }
        catch (Exception ex)
        {
            _mcpStartupNote = "自动开启接入失败，可手动重试：" + ex.Message;
            Log.Warn("MCP 自动开启失败，主程序继续运行：" + ex.Message);
            McpChanged?.Invoke();
        }
    }

    internal async Task SetMcpEnabledAsync(bool enabled)
    {
        if (enabled == McpEnabled) return;
        if (!enabled)
        {
            _mcpOperations?.Close();
            var server = _mcpServer; _mcpServer = null;
            if (server is not null) await server.DisposeAsync();
            McpChanged?.Invoke();
            Log.Info("MCP 本次接入已关闭；在途任务沿原后端收尾。");
            return;
        }
        if (_exitRequested || _deploymentInProgress || _factoryResetInProgress)
            throw new InvalidOperationException("程序正在退出或维护，请稍后开启 MCP。");
        if (_mcpOperations?.HasActive == true) throw new InvalidOperationException("上次 MCP 操作仍在执行，请等待完成后重新开启。");
        var operations = new AgentOperations(
            (action, args, token) => McpOnUiAsync(() => ConfirmMcpAsync(action, args, token)),
            (action, args, progress) => McpOnUiAsync(() => ExecuteMcpAsync(action, args, progress)));
        // Force catalog validation before accepting connections.
        _ = AgentCatalog.Actions.Count;
        operations.Changed += () => DispatcherQueue.TryEnqueue(() => McpChanged?.Invoke());
        var newServer = new AgentPipeServer(AppContext.BaseDirectory, request => DispatchMcpAsync(operations, request));
        _mcpOperations = operations;
        _mcpServer = newServer;
        _mcpStartupNote = null;
        McpChanged?.Invoke();
        Log.Info("MCP 本次接入开启；仅本机同用户，写入由主窗口确认。");
    }

    private Task<T> McpOnUiAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("主窗口不可用，MCP 操作未调度。"));
        return completion.Task;
    }

    private async Task<object> DispatchMcpAsync(AgentOperations operations, AgentRequest request)
    {
        var args = request.Arguments;
        return request.Method switch
        {
            "status" => await McpOnUiAsync(() => Task.FromResult<object>(ReadMcpStatus())),
            "submit" => operations.Submit(args.GetProperty("action").GetString()!, args.GetProperty("arguments"), args.GetProperty("requestId").GetString()!),
            "operation" => operations.Get(Guid.Parse(args.GetProperty("operationId").GetString()!)),
            "cancel" => operations.Cancel(Guid.Parse(args.GetProperty("operationId").GetString()!)),
            _ => throw new ArgumentException("未知 MCP 方法。"),
        };
    }

    private object ReadMcpStatus() => new
    {
        version = AppVersion.Text, enabled = McpEnabled, elevated = ElevationHelper.IsElevated,
        deltaRunning = PageBackendSessions.Manager.DeltaRunning, pendingModules = BackendUnconfirmedStore.PendingModules(),
        frameActive = ServiceLocator.Frame.FrameModeActive,
        message = McpStatus, actions = AgentCatalog.Actions.Count,
    };

    private void CheckMcpAdmission(AgentAction action)
    {
        bool? running = null;
        if (action.Module is "system" or "gpu" or "runtime" or "ace" || action.Name == "system.low-memory.status")
        { try { running = DeltaForceLocator.IsGameRunning(); } catch { } }
        AgentAdmission.Check(action, McpEnabled, _exitRequested || _deploymentInProgress || _factoryResetInProgress,
            ElevationHelper.IsElevated, running);
        BackendOperationGate.Check();
        if (BackendReviewFlow.IsReviewing && action.Name != "app.review") throw new InvalidOperationException("正在核对未确认操作，请先完成核对。");
        if (action.Module != "app") BackendUnconfirmedStore.Check(action.Module);
        if (ServiceLocator.GameTarget.IsCustom && (action.Module == "ace" || action.Name.StartsWith("system.shader.", StringComparison.Ordinal)
            || action.Name.StartsWith("system.game-recording.", StringComparison.Ordinal)))
            throw new InvalidOperationException("该功能仅限三角洲目标，未执行操作。");
        if (action.Module == "runtime" && !ReleaseFeatures.RuntimeUiEnabled) throw new InvalidOperationException("当前版本未开放运行库功能。");
    }

    private async Task<bool> ConfirmMcpAsync(AgentAction action, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CheckMcpAdmission(action);
        CheckMcpTweak(action, args);
        if (action.Name is "frame.activate" or "frame.deactivate" or "app.review") return true; // Original full UI flow owns its confirmations.
        ActivateFromSecondLaunch();
        _mcpRebarConfirmation = null;
        uint? driver = null;
        var note = "";
        if (action.Name == BackendCommands.GpuDriverApply)
        {
            driver = (await ServiceLocator.NvProfile.GetEnvironmentAsync()).DriverVersion;
            var nv = BackendJson.Deserialize<NvDriverApplyArgs>(args);
            note = "\n当前驱动：" + NvDriverSettingPolicy.FormatDriver(driver) + "。\n"
                + (NvDriverSettingPolicy.RebarWarning(driver, nv.Changes.Select(c => (c.Id, c.Value))) ?? "") + "\n";
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Agent 请求修改配置",
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock
            { Text = action.Description + note + "\n\n操作：" + action.Name + "\n参数：\n" + JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true })
                + "\n\n继续后按现有备份与复检流程执行；取消则不开始本次操作。", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            PrimaryButtonText = "确认执行", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        using var cancelled = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        // Use the shared dialog gate, but do not borrow the currently visible page's lease.
        using var dialogLease = await BackendDialogExtensions.EnterReviewAsync();
        token.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();
        token.ThrowIfCancellationRequested();
        if (result == ContentDialogResult.Primary && action.Name == BackendCommands.GpuDriverApply)
            _mcpRebarConfirmation = (args.GetRawText(), driver);
        return result == ContentDialogResult.Primary;
    }

    private async Task<BackendResult> ExecuteMcpAsync(AgentAction action, JsonElement args, IProgress<string> progress)
    {
        CheckMcpAdmission(action);
        CheckMcpTweak(action, args);
        Log.Info("MCP 执行：" + action.Name); // Never log raw arguments, paths or endpoint credentials.
        if (action.Module == "app")
        {
            switch (action.Name)
            {
                case "app.status": return BackendResult.Ok(action.Name, "应用状态。", ReadMcpStatus());
                case "gpu.settings.catalog": return BackendResult.Ok(action.Name, "NVIDIA设置目录。", NvSettingsCatalog.Definitions);
                case "system.low-memory.status": return BackendResult.Ok(action.Name, "低内存配置与本次状态指纹；未知项不能当作正常。", await LowMemoryHealthCache.ReadAsync(force: true));
                case "system.optimizations.catalog":
                    var installedMemory = Inspection.PhysicalMemoryInspection.InstalledBytes();
                    return BackendResult.Ok(action.Name, "内置优化目录；执行时仍按各项条件复核，不接受目录外脚本。", new
                    {
                        sections = Services.TweakDb.BxCatalog.Sections,
                        items = Services.TweakDb.BxCatalog.Database.ToDictionary(p => p.Key,
                            p => p.Value.Select(item => new { item.Name, item.Description, item.Risk, item.NoSupport, item.ValidationError })),
                        serviceGroups = Services.TweakDb.BxCatalog.ServiceGroups,
                        deepTweaks = Enum.GetValues<SystemTweak>().Where(t => LowMemoryPolicy.ShowUpTweak(t, installedMemory)).Select(t => t.ToString()).ToArray(),
                    });
                case "frame.status": return BackendResult.Ok(action.Name, "偏好与实际快照分别返回；空快照为未知。", new { active = ServiceLocator.Frame.FrameModeActive, snapshot = BackendCpuFrameService.Instance.Latest });
                case "frame.activate":
                case "frame.deactivate":
                    ActivateFromSecondLaunch();
                    var target = action.Name == "frame.activate";
                    if (target) await FrameModeFlow.RunActivateAsync(Content.XamlRoot, ServiceLocator.Frame);
                    else await FrameModeFlow.RunDeactivateAsync(Content.XamlRoot, ServiceLocator.Frame);
                    if (BackendUnconfirmedStore.PendingModules().Count > 0)
                        return BackendResult.PartialResult(action.Name, "帧格操作后存在待核对记录，请在主程序核对。", []);
                    return ServiceLocator.Frame.FrameModeActive == target
                        ? BackendResult.PartialResult(action.Name, "帧格界面流程已结束，总开关已达到请求状态；各子项结果请查看页面，未据此认定全部功能成功。", [], new { active = ServiceLocator.Frame.FrameModeActive, snapshot = BackendCpuFrameService.Instance.Latest }, requiresReview: false)
                        : BackendResult.Cancelled(action.Name, "帧格总开关未达到请求状态；请查看主程序的取消或失败原因。");
                case "app.review":
                    ActivateFromSecondLaunch(); await BackendReviewFlow.RunAsync(Content.XamlRoot);
                    return BackendResult.Ok(action.Name, "核对界面流程已结束；剩余记录不等于已解决。", new { pendingModules = BackendUnconfirmedStore.PendingModules() });
            }
            throw new InvalidOperationException("未实现的 MCP 应用动作。");
        }
        object wireArgs = args;
        if (action.Name == BackendCommands.GpuDriverApply)
        {
            var confirmation = _mcpRebarConfirmation; _mcpRebarConfirmation = null;
            var currentDriver = (await ServiceLocator.NvProfile.GetEnvironmentAsync()).DriverVersion;
            if (confirmation is null || confirmation.Value.Arguments != args.GetRawText() || confirmation.Value.Driver != currentDriver)
                return BackendResult.Cancelled(action.Name, "驱动版本或本次确认已变化，请重新核对；未提交写入。");
            var nv = BackendJson.Deserialize<NvDriverApplyArgs>(args);
            nv.RebarWarningAccepted = true; // Only reached after this exact operation was locally confirmed.
            wireArgs = nv;
        }
        using var unowned = PageBackendSessions.ConstructFor(null);
        using var targetLease = ServiceLocator.GameTarget.BeginOperation();
        var route = action.Module switch { "gpu" => "spoof", "system" => "system", "runtime" => "runtime", "ace" => "ace", _ => null };
        var owner = route is null ? null : new PageBackendLease(route);
        var caller = new BackendCaller(action.Module, owner);
        BackendResult result;
        try { result = await caller.InvokeAsync(action.Name, wireArgs, progress, CancellationToken.None); }
        finally
        {
            if (owner is not null)
            {
                owner.Suspend("MCP 本次操作已结束。", leaving: true);
                await owner.Stopping;
            }
        }
        if (caller.SharedClient is { } client && owner is not null && (!client.ExitConfirmed || client.RequiresAttention))
            return BackendResult.PartialResult(action.Name, result.Message + "；后端收尾未确认，请在设置核对原记录。", result.Items, result.Data);
        if (action.Module == "gpu" && result.Status == BackendResultStatuses.Ok && !result.RequiresReview && !result.PendingRestore
            && result.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("suggestShaderMaintenance", out var suggest) && suggest.ValueKind == JsonValueKind.True)
            GpuShaderGuidanceSession.Shared.Observe(BackendJson.Deserialize<OperationResult>(data));
        return result;
    }

    private static void CheckMcpTweak(AgentAction action, JsonElement args)
    {
        if (action.Name == BackendCommands.SystemTweaksApply)
            AgentAdmission.CheckTweak(action.Name, BackendJson.Deserialize<AgentTweakArgs>(args).Tweak, Inspection.PhysicalMemoryInspection.InstalledBytes());
    }
}
