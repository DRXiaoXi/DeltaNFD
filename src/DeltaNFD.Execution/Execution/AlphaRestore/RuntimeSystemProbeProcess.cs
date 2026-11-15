using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.AlphaRestore;

/// <summary>Read-only child entry point; it never initializes the repair service or touches its activity journal.</summary>
public static class RuntimeSystemProbeProcess
{
    private sealed record Reply(int Schema, Guid Nonce, int Pid, string Architecture, IReadOnlyList<RuntimeSystemProbe> Probes);
    public static int Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "--runtime-system-probe" || !Guid.TryParse(args[1], out var nonce)) return 2;
        try
        {
            var probes = RuntimeStrongRepairEnvironment.ProbeInProcess(default);
            Console.Write(JsonSerializer.Serialize(new Reply(1, nonce, Environment.ProcessId,
                Environment.Is64BitProcess ? "x64" : "x86", probes)));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("只读系统组件探测失败：" + ex.Message); return 3; }
    }
    internal static ProcessStartInfo Command(string executable, Guid nonce)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new InvalidDataException("探测程序路径必须为绝对路径。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--runtime-system-probe");
        info.ArgumentList.Add(nonce.ToString("D"));
        return info;
    }
    internal static async Task<IReadOnlyList<RuntimeSystemProbe>> ProbeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var nonce = Guid.NewGuid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // The executable is this trusted runtime backend, not a configurable DLL/tool path.
        using var child = Process.Start(Command(Environment.ProcessPath ?? throw new IOException("缺少当前后端路径。"), nonce))
            ?? throw new IOException("无法启动独立系统探测进程。");
        var output = ReadBoundedAsync(child.StandardOutput, timeout.Token);
        var error = ReadBoundedAsync(child.StandardError, timeout.Token);
        try
        {
            await child.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new IOException("独立探测未完成：" + await error);
            var reply = JsonSerializer.Deserialize<Reply>(await output) ?? throw new InvalidDataException("独立探测结果为空。");
            var expected = RuntimeStrongRepairEnvironment.Contracts.Select(c => "x64/" + c.Contract)
                .Concat(new[] { "x64/ucrtbase.dll", "x86/ucrtbase.dll" }).Order().ToArray();
            if (reply.Schema != 1 || reply.Nonce != nonce || reply.Pid != child.Id || reply.Pid == Environment.ProcessId
                || reply.Architecture != "x64" || reply.Probes is null
                || !reply.Probes.Select(p => p.Name).Order().SequenceEqual(expected))
                throw new InvalidDataException("独立探测的身份、架构或项目不完整。");
            return reply.Probes;
        }
        finally
        {
            // This child only reads files/loader contracts. Cancellation never kills a servicing worker.
            timeout.Cancel();
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 32768) throw new InvalidDataException("独立探测输出超出限制。");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
