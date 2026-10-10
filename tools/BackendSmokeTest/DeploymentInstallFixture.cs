using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

internal static class DeploymentInstallFixture
{
    internal static async Task<int> RunAsync(string root, string codeText, string nonce, int listenerDelay = 0)
    {
        if (!Path.IsPathFullyQualified(root) || !Guid.TryParse(nonce, out _) || !int.TryParse(codeText, out var code)
            || code is not (0 or 2 or 10) || File.ReadAllText(Path.Combine(root, "isolated-test.marker")) != nonce) return 2;
        var name = "DeltaNFD.Deployment." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..24];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        File.WriteAllText(Path.Combine(root, "fixture-ready.txt"), Environment.ProcessId.ToString());
        await Task.Delay(listenerDelay, deadline.Token);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        var request = await reader.ReadLineAsync(deadline.Token);
        File.WriteAllText(Path.Combine(root, "fixture-request.txt"), request);
        if (request != "INSTALL/1") return 3;
        var detail = code == 2 ? "隔离测试：当前事务未结束，保留恢复证据。" : "隔离测试：安全交接完成，中文路径与报告正常。";
        await writer.WriteLineAsync(code + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(detail)));
        if (code == 2)
            while (!File.Exists(Path.Combine(root, "fixture-finish.txt"))) await Task.Delay(50, deadline.Token);
        return 0;
    }
}
