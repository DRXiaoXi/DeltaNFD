using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Backends;

/// <summary>后端一次命令执行的上下文：命令名、参数、会话/请求标识、取消令牌与进度上报。</summary>
public sealed class BackendCommandContext
{
    private readonly Action<BackendProgress> _report;

    internal BackendCommandContext(
        string command, JsonElement args, Guid sessionId, Guid requestId,
        CancellationToken cancellationToken, Action<BackendProgress> report)
    {
        Command = command;
        Args = args;
        SessionId = sessionId;
        RequestId = requestId;
        CancellationToken = cancellationToken;
        _report = report;
    }

    public string Command { get; }
    public JsonElement Args { get; }
    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>上报进度（0..100 或 null 百分比 + 纯文本 + 阶段标识）。</summary>
    public void ReportProgress(string message, int? percent = null, string stage = "") =>
        _report(new BackendProgress(percent, message, stage));

    /// <summary>把 args 反序列化为强类型参数对象；失败抛 <see cref="BackendContractException"/>。</summary>
    public T ArgsAs<T>() where T : notnull => BackendJson.Deserialize<T>(Args);

    /// <summary>可选字符串参数。</summary>
    public string ArgString(string name) =>
        BackendJson.TryGetString(Args, name, out var value) ? value : "";

    public bool ArgBool(string name, bool fallback = false) =>
        BackendJson.TryGetBool(Args, name, out var value) ? value : fallback;

    public int ArgInt(string name, int fallback = 0) =>
        BackendJson.TryGetInt(Args, name, out var value) ? value : fallback;
}

/// <summary>后端启动配置：模块规格 + 命令处理器表（即白名单的实际实现）。</summary>
public sealed class BackendHostOptions
{
    public required BackendModuleSpec Module { get; init; }

    /// <summary>命令 → 处理器。键必须全部位于 <see cref="BackendModuleSpec.Commands"/> 白名单内。</summary>
    public required IReadOnlyDictionary<string, Func<BackendCommandContext, Task<BackendResult>>> Handlers { get; init; }

    /// <summary>握手等待上限（宿主未按时发送 hello 即退出）。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>可选的后端自身启动优先级，不修改宿主或目标进程。</summary>
    public ProcessPriorityClass? InitialPriorityClass { get; init; }

    /// <summary>执行命令时，超过模块超时后附加的宽限（超过即由后端自行取消）。</summary>
    public TimeSpan CommandGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 可选的状态生产者：后端空闲时由宿主持续轮询并把返回值写入 status 帧（携带可选状态快照）。
    /// 仅在无在途请求、未收到停止请求时调用，绝不与 result 帧的写入竞争；返回 null 表示本次无状态。
    /// </summary>
    public Func<BackendStatus?>? StatusSource { get; init; }

    /// <summary>状态帧推送间隔（仅空闲时）。</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>进程内自检入口（供契约测试调用，不连接管道）。</summary>
    public Func<Task<int>>? SelfCheck { get; init; }

    /// <summary>在途操作结束后执行收尾；停止、断线与协议退出均走此入口，结果写入 stopped。</summary>
    public Func<Task<OperationResult>>? OnStopping { get; init; }
}

/// <summary>
/// 官方后端宿主：连接宿主管道、完成双向握手、按白名单串行执行命令，
/// 支持进度、取消、停止排空与部分成功；超时/断线一律不得折算为成功。
/// 后端不信任前端预检：处理器必须自行复核权限与状态（见各模块实现）。
/// </summary>
public static class BackendHost
{
    private const int ExitUsage = 2;
    private const int ExitHandshake = 3;
    private const int ExitPipe = 4;
    private const int ExitProtocol = 5;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    public static async Task<int> RunAsync(BackendHostOptions options, string[] args, CancellationToken hostToken = default)
    {
        // 隔离测试数据根（后端 E2E 夹具）：必须在任何服务读取 AppDataPaths 之前配置。
        var isolatedRoot = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT");
        var isolatedId = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID");
        if (!string.IsNullOrWhiteSpace(isolatedRoot) || !string.IsNullOrWhiteSpace(isolatedId))
        {
            if (string.IsNullOrWhiteSpace(isolatedRoot) || string.IsNullOrWhiteSpace(isolatedId) ||
                !Guid.TryParse(isolatedId, out var testId))
                return ExitUsage;
            try
            {
                AppDataPaths.ConfigureIsolatedDataRoot(isolatedRoot, testId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("隔离测试数据根拒绝：" + ex.Message);
                return ExitUsage;
            }
        }

        // 处理器必须与白名单严格一致：既不挂白名单外的命令，也不留未实现的空命令（--describe 也会校验）。
        foreach (var command in options.Handlers.Keys)
        {
            if (!options.Module.Commands.Contains(command))
                throw new BackendContractException($"处理器命令“{command}”不在模块 {options.Module.Id} 白名单内，拒绝启动。");
        }
        foreach (var command in options.Module.Commands)
        {
            if (!options.Handlers.ContainsKey(command))
                throw new BackendContractException($"模块 {options.Module.Id} 的白名单命令“{command}”未注册处理器，拒绝启动。");
        }

        if (args.Contains("--describe", StringComparer.Ordinal))
        {
            var spec = options.Module;
            Console.WriteLine($"DeltaNFD official backend: {spec.Id} ({spec.DisplayName}) exe={spec.ExecutableName}");
            Console.WriteLine("commands=" + string.Join(",", spec.Commands.OrderBy(c => c, StringComparer.Ordinal)));
            return 0;
        }

        if (args.Contains("--self-check", StringComparer.Ordinal) && options.SelfCheck is not null)
            return await options.SelfCheck();

        var module = GetArgument(args, "--backend");
        var pipeName = GetArgument(args, "--pipe");
        if (!int.TryParse(GetArgument(args, "--host-pid"), out var hostPid) || hostPid <= 0 ||
            !string.Equals(module, options.Module.Id, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(pipeName))
            return ExitUsage;

        Log.Info($"官方后端启动：module={options.Module.Id}, pipe={pipeName}");
        if (options.InitialPriorityClass is { } priority)
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                current.PriorityClass = priority;
                current.Refresh();
                Log.Info($"官方后端自身优先级：module={options.Module.Id}；请求={priority}；回读={current.PriorityClass}");
            }
            catch (Exception ex) { Log.Warn($"后端自身优先级设置未完成：module={options.Module.Id}；{ex.Message}"); }
        }
        try
        {
            return await ServeAsync(options, pipeName, hostPid, hostToken);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("官方后端异常退出", ex);
            return ExitPipe;
        }
    }

    private static async Task<int> ServeAsync(BackendHostOptions options, string pipeName, int hostPid, CancellationToken hostToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await pipe.ConnectAsync(connectTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("官方后端：连接宿主管道超时。");
                return ExitPipe;
            }
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != hostPid)
            return ExitHandshake;
        var writer = new BackendPipeWriter(pipe);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);

        // ---- 握手：等待宿主 hello ----
        BackendFrame? hello;
        using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token))
        {
            helloTimeout.CancelAfter(options.HandshakeTimeout);
            try
            {
                hello = await BackendFrameStream.ReadFrameAsync(pipe, helloTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                hello = null;
            }
        }
        if (hello is null) { Log.Warn("官方后端：未在超时内收到 hello。"); return ExitHandshake; }
        if (hello.Type != BackendFrameType.Hello || !string.Equals(hello.Module, options.Module.Id, StringComparison.Ordinal))
        {
            Log.Warn($"官方后端：hello 非法（type={hello.Type}, module={hello.Module}）。");
            return ExitHandshake;
        }

        BackendHello helloPayload;
        try
        {
            helloPayload = BackendMessages.ParseHello(hello.Payload);
        }
        catch (BackendContractException ex)
        {
            Log.Warn("官方后端：hello 校验失败：" + ex.Message);
            return ExitHandshake;
        }

        var sessionId = hello.SessionId;
        var ready = new BackendReady(
            helloPayload.Nonce,
            options.Module.Id,
            AssemblyVersionText(),
            BackendProtocol.ProtocolMinor,
            Environment.ProcessId,
            ProcessStartTicks(),
            Environment.ProcessPath ?? "",
            SelfSha256());
        await writer.WriteAsync(BackendMessages.EncodeReady(options.Module.Id, sessionId, hello.RequestId, ready));
        Log.Info($"官方后端就绪：pid={ready.Pid}, version={ready.ModuleVersion}, session={sessionId:D}");

        // ---- 主循环：串行执行，随时响应 cancel/stop/ping ----
        var processedRequests = new HashSet<Guid>();
        CancellationTokenSource? activeRequestCts = null;
        void CancelActive()
        {
            try { activeRequestCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        Task<BackendResult>? inFlight = null;
        Task<BackendFrame?>? pendingRead = null;
        Guid inFlightRequest = Guid.Empty;
        string inFlightCommand = "";
        var exitAfterCurrent = false;
        var stopRequested = false;

        // 在途请求完成即回写结果；返回 true 表示该命令属于「完成即退出」的按需任务。
        async Task<bool> TrySendCompletedInFlightAsync()
        {
            if (inFlight is null || !inFlight.IsCompleted) return false;
            await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, inFlightRequest, await inFlight));
            exitAfterCurrent = options.Module.ExitAfterCommands.Contains(inFlightCommand);
            inFlight = null;
            activeRequestCts = null;
            inFlightRequest = Guid.Empty;
            inFlightCommand = "";
            return exitAfterCurrent;
        }

        try
        {
        while (!hostToken.IsCancellationRequested)
        {
            pendingRead ??= BackendFrameStream.ReadFrameAsync(pipe, sessionCts.Token);
            BackendFrame? frame = null;

            if (inFlight is not null)
            {
                var winner = await Task.WhenAny(pendingRead, inFlight);
                if (winner == inFlight)
                {
                    if (await TrySendCompletedInFlightAsync())
                    {
                        Log.Info("官方后端：按需任务已完成，进程退出。");
                        break;
                    }
                    continue;
                }
            }

            // 空闲时推送状态快照（有在途请求时不推送，保证 result 与 status 的写入不交错竞争）。
            if (inFlight is null && options.StatusSource is not null)
            {
                var statusDelay = Task.Delay(options.StatusInterval, sessionCts.Token);
                if (await Task.WhenAny(pendingRead, statusDelay) == statusDelay)
                {
                    try
                    {
                        var status = options.StatusSource();
                        if (status is not null)
                            await writer.SafeWriteAsync(BackendMessages.EncodeStatus(
                                options.Module.Id, sessionId, Guid.NewGuid(), status));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("官方后端：状态快照推送失败：" + ex.Message);
                    }
                    continue; // pendingRead 仍然有效，下一轮继续等待宿主帧。
                }
            }

            try
            {
                frame = await pendingRead;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
            {
                Log.Info("官方后端：宿主连接结束（" + ex.GetType().Name + "）。");
                CancelActive();
                break;
            }
            pendingRead = null;

            if (frame is null)
            {
                CancelActive();
                break;
            }

            if (frame.SessionId != sessionId)
            {
                Log.Warn("官方后端：收到会话不一致的帧，断开连接。");
                return ExitProtocol;
            }

            switch (frame.Type)
            {
                case BackendFrameType.Invoke:
                {
                    BackendInvoke invoke;
                    try { invoke = BackendMessages.ParseInvoke(frame.Payload); }
                    catch (BackendContractException ex)
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("invoke.malformed", ex.Message)));
                        continue;
                    }

                    if (inFlight is not null)
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("backend.busy", "后端正在执行上一请求，已拒绝并发调用。")));
                        continue;
                    }

                    if (!options.Module.Commands.Contains(invoke.Command))
                    {
                        Log.Warn($"官方后端：拒绝白名单外命令“{invoke.Command}”。");
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("command.not-allowed", $"命令“{invoke.Command}”不在模块 {options.Module.Id} 白名单内。")));
                        continue;
                    }

                    if (!processedRequests.Add(frame.RequestId))
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("request.duplicate", "重复的 requestId，已拒绝。")));
                        continue;
                    }

                    if (!options.Handlers.TryGetValue(invoke.Command, out var handler))
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("command.not-implemented", $"命令“{invoke.Command}”尚未实现。")));
                        continue;
                    }

                    // 在执行和写前日志之前拒绝；恢复/停止命令不属于可禁用能力。
                    if (EmergencyPolicies.BlockReason(invoke.Command) is { } emergencyReason)
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("emergency-policy.blocked", emergencyReason)));
                        continue;
                    }

                    inFlightRequest = frame.RequestId;
                    inFlightCommand = invoke.Command;
                    var requestId = frame.RequestId;
                    var requestCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                    activeRequestCts = requestCts;
                    var timeout = BackendModules.TimeoutFor(options.Module.Id, invoke.Command) + options.CommandGrace;
                    requestCts.CancelAfter(timeout);
                    var context = new BackendCommandContext(invoke.Command, invoke.Args, sessionId, requestId,
                        requestCts.Token, p => writer.SafeWriteProgress(options.Module.Id, sessionId, requestId, p));
                    inFlight = RunHandlerAsync(handler, context, requestCts);
                    break;
                }

                case BackendFrameType.Cancel:
                {
                    Guid referenced;
                    try { referenced = BackendMessages.ParseCancel(frame.Payload); }
                    catch (BackendContractException ex)
                    {
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, frame.RequestId,
                            BackendResult.Failed("cancel.malformed", ex.Message)));
                        continue;
                    }

                    if (inFlight is not null && referenced == inFlightRequest)
                    {
                        Log.Info($"官方后端：收到取消请求 {referenced:D}。");
                        CancelActive();
                    }
                    break;
                }

                case BackendFrameType.Stop:
                {
                    stopRequested = true;
                    Log.Info("官方后端：收到停止请求，开始排空。");
                    CancelActive();
                    break;
                }

                case BackendFrameType.Ping:
                    await writer.SafeWriteAsync(BackendMessages.EncodePong(options.Module.Id, sessionId, frame.RequestId));
                    break;

                case BackendFrameType.Hello:
                case BackendFrameType.Ready:
                case BackendFrameType.Progress:
                case BackendFrameType.Result:
                case BackendFrameType.Status:
                case BackendFrameType.Pong:
                default:
                    Log.Warn($"官方后端：忽略不适用帧 {frame.Type}。");
                    break;
            }

            if (await TrySendCompletedInFlightAsync())
            {
                Log.Info("官方后端：按需任务已完成，进程退出。");
                break;
            }

            if (stopRequested)
            {
                if (inFlight is not null)
                {
                    // 有界等待在途请求收尾。
                    try
                    {
                        using var drain = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
                        drain.CancelAfter(TimeSpan.FromSeconds(15));
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, inFlightRequest,
                            await inFlight.WaitAsync(drain.Token)));
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Warn("官方后端：在途请求未能在排空窗口内收尾。");
                        // Do not report stopped or exit while a non-cancellable system operation is still running.
                        await writer.SafeWriteAsync(BackendMessages.EncodeResult(options.Module.Id, sessionId, inFlightRequest,
                            await inFlight));
                    }
                    inFlight = null;
                }

                break;
            }
        }

        }
        finally
        {
            CancelActive();
            if (inFlight is not null) await inFlight;
            OperationResult? cleanup = null;
            if (options.OnStopping is not null)
            {
                try { cleanup = await options.OnStopping(); }
                catch (Exception ex) { cleanup = OperationResult.Fail("后端收尾恢复未确认：" + ex.Message); }
            }
            await writer.SafeWriteAsync(BackendMessages.EncodeStatus(options.Module.Id, sessionId, Guid.NewGuid(),
                new BackendStatus(BackendMessages.StateStopped, null, cleanup is null ? null : JsonSerializer.SerializeToElement(cleanup, BackendJson.SerializerOptions))));
            try { await writer.FlushAsync(); } catch { /* 宿主可能已断开 */ }
        }
        Log.Info("官方后端退出。");
        return 0;
    }

    private static async Task<BackendResult> RunHandlerAsync(
        Func<BackendCommandContext, Task<BackendResult>> handler,
        BackendCommandContext context,
        CancellationTokenSource requestCts)
    {
        try
        {
            return await handler(context);
        }
        catch (OperationCanceledException)
        {
            return BackendResult.Cancelled("command.cancelled", "命令已取消。");
        }
        catch (BackendContractException ex)
        {
            return BackendResult.Failed("command.contract", "参数校验失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"官方后端：命令 {context.Command} 执行异常", ex);
            return BackendResult.Failed("command.failed", "执行异常：" + ex.Message);
        }
        finally
        {
            requestCts.Dispose();
        }
    }

    private static string AssemblyVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private static long ProcessStartTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string SelfSha256()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定后端自身路径。");
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>后端→宿主的帧写入器：串行化写入，断线后静默失败（由主循环负责退出）。</summary>
internal sealed class BackendPipeWriter(Stream stream)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _broken;

    public async Task WriteAsync(byte[] frame)
    {
        await _gate.WaitAsync();
        try
        {
            await stream.WriteAsync(frame);
            await stream.FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SafeWriteAsync(byte[] frame)
    {
        if (_broken) return;
        try
        {
            await WriteAsync(frame);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _broken = true;
        }
    }

    /// <summary>同步写入进度帧以保证与后续 result 的先后顺序（后端线程执行，阻塞可接受）。</summary>
    public void SafeWriteProgress(string moduleId, Guid sessionId, Guid requestId, BackendProgress progress)
    {
        if (_broken) return;
        try
        {
            WriteAsync(BackendMessages.EncodeProgress(moduleId, sessionId, requestId, progress)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _broken = true;
        }
    }

    public async Task FlushAsync()
    {
        try { await stream.FlushAsync(); } catch { /* 忽略 */ }
    }
}
