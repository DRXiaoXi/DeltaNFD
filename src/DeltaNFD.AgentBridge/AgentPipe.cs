using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.AgentBridge;

public sealed record AgentEndpoint(int Protocol, string Pipe, string Token, int Pid, long StartTicks, string AppDirectory);
public sealed record AgentRequest(string Token, string Method, JsonElement Arguments);
public sealed record AgentResponse(bool Success, JsonElement? Data, string Error);

/// <summary>Bounded, local, same-user transport. No HTTP listener or system execution lives here.</summary>
public static class AgentPipe
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string EndpointPath(string appDirectory)
    {
        var canonical = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaNFD", "Mcp", key, "endpoint.json");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxBytes) throw new InvalidDataException("MCP 响应超过 2MiB，请缩小查询范围。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxBytes) throw new InvalidDataException("MCP 数据长度无效。");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("MCP 数据为空。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(Microsoft.Win32.SafeHandles.SafePipeHandle handle, int type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法确定本次 MCP 用户。");
        var security = new PipeSecurity();
        // User remains the same across UAC; token Owner may become Administrators.
        // Explicit medium label permits this user's ordinary Agent to reach an elevated UI.
        // Network and low-integrity clients remain excluded; all writes still require UI confirmation.
        security.SetSecurityDescriptorSddlForm($"O:{user.Value}D:P(D;;GA;;;NU)(A;;GA;;;{user.Value})");
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
        // Set only LABEL_SECURITY_INFORMATION, using WRITE_OWNER. Putting a SACL into
        // CreateNamedPipe would require SeSecurityPrivilege even for a medium label.
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;ME)", 1, out descriptor, out _)
                || !GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立 MCP 完整性标签。");
            var error = SetSecurityInfo(pipe.SafePipeHandle, 6, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (error != 0) throw new Win32Exception((int)error, "无法限定 MCP 接入的完整性级别。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyServer(NamedPipeClientStream pipe, AgentEndpoint endpoint)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User))
            throw new UnauthorizedAccessException("MCP 管道不属于当前用户。");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != endpoint.Pid)
            throw new InvalidDataException("MCP 主程序 PID 不匹配。");
        // QUERY_LIMITED_INFORMATION works without reading an elevated process's modules.
        using var process = OpenProcess(0x1000, false, endpoint.Pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对 MCP 主程序身份。");
        var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref size)
            || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 MCP 主程序映像或创建时间。");
        if (DateTime.FromFileTimeUtc(created).Ticks != endpoint.StartTicks
            || !string.Equals(image.ToString(), Path.Combine(endpoint.AppDirectory, "DeltaNFD.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MCP 主程序映像或创建时间不匹配。");
    }

    public static async Task<JsonElement> CallAsync(string appDirectory, string method, object arguments, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地 MCP 需要 Windows。");
        var endpointPath = EndpointPath(appDirectory);
        if (!File.Exists(endpointPath)) throw new InvalidOperationException("请打开同目录的三角帧不掉洲，在设置中开启本次 MCP 接入。");
        var endpointBytes = await File.ReadAllBytesAsync(endpointPath, token);
        if (endpointBytes.Length > 16384) throw new InvalidDataException("MCP 端点记录无效。");
        var endpoint = JsonSerializer.Deserialize<AgentEndpoint>(endpointBytes, Json) ?? throw new InvalidDataException("MCP 端点为空。");
        if (endpoint.Protocol != 1 || endpoint.Token.Length != 64 || !endpoint.Pipe.StartsWith("DeltaNFD.Mcp.", StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(endpoint.AppDirectory).TrimEnd('\\'), Path.GetFullPath(appDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MCP 端点身份不匹配。");
        await using var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        VerifyServer(pipe, endpoint);
        await WriteAsync(pipe, new AgentRequest(endpoint.Token, method, JsonSerializer.SerializeToElement(arguments, Json)), timeout.Token);
        var result = await ReadAsync<AgentResponse>(pipe, timeout.Token);
        if (!result.Success || result.Data is null) throw new InvalidOperationException(result.Error);
        return result.Data.Value;
    }
}

public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly string _endpointPath;
    private readonly byte[] _endpointBytes;
    private readonly AgentEndpoint _endpoint;
    private readonly Func<AgentRequest, Task<object>> _dispatch;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly NamedPipeServerStream _first;
    public AgentPipeServer(string appDirectory, Func<AgentRequest, Task<object>> dispatch)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地 MCP 需要 Windows。");
        _dispatch = dispatch;
        using var current = Process.GetCurrentProcess();
        _endpoint = new(1, "DeltaNFD.Mcp." + Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            current.Id, current.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(appDirectory));
        _endpointPath = AgentPipe.EndpointPath(appDirectory);
        _endpointBytes = JsonSerializer.SerializeToUtf8Bytes(_endpoint, AgentPipe.Json);
        _first = NewPipe(); // Claim the pipe before advertising it.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_endpointPath)!);
            var temp = _endpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, _endpointBytes); File.Move(temp, _endpointPath, true);
        }
        catch { _first.Dispose(); throw; }
        _loop = Task.Run(LoopAsync);
    }
    private NamedPipeServerStream NewPipe()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地 MCP 需要 Windows。");
        return AgentPipe.CreateServer(_endpoint.Pipe);
    }
    private async Task LoopAsync()
    {
        var pipe = _first;
        while (!_stop.IsCancellationRequested)
        {
            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var request = await AgentPipe.ReadAsync<AgentRequest>(pipe, timeout.Token);
                    object data;
                    if (request.Token is null || request.Token.Length != _endpoint.Token.Length
                        || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Token), Encoding.UTF8.GetBytes(_endpoint.Token)))
                        throw new InvalidDataException("MCP 本次接入凭据无效。");
                    data = await _dispatch(request).WaitAsync(_stop.Token);
                    await AgentPipe.WriteAsync(pipe, new AgentResponse(true, JsonSerializer.SerializeToElement(data, AgentPipe.Json), ""), timeout.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    try
                    {
                        using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        errorTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await AgentPipe.WriteAsync(pipe, new AgentResponse(false, null, ex.Message), errorTimeout.Token);
                    }
                    catch { }
                }
            }
            if (!_stop.IsCancellationRequested) pipe = NewPipe();
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (File.Exists(_endpointPath) && File.ReadAllBytes(_endpointPath).AsSpan().SequenceEqual(_endpointBytes)) File.Delete(_endpointPath); } catch { }
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
