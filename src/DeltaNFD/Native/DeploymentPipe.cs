using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

internal static class DeploymentPipe
{
    internal static string NameFor(string root) => "DeltaNFD.Deployment." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..24];

    internal delegate Task<DeploymentHandover> PrepareDeployment(bool uninstall);

    internal static Func<Task<DeploymentHandover>>? SelectPreparation(string? request, PrepareDeployment prepare,
        Func<Task<DeploymentHandover>> prepareRemoval) => request switch
    {
        "INSTALL/1" => () => prepare(uninstall: false),
        "UNINSTALL/1" => () => prepare(uninstall: true),
        "UNINSTALL/2" => prepareRemoval,
        _ => null
    };

    public static void Start(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, PrepareDeployment prepare,
        Func<Task<DeploymentHandover>> prepareRemoval, Action exit)
    {
        // Do not propagate a trusted backend control lease into the listener.
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(async () =>
        {
            while (true)
            {
                DeploymentHandover? permit = null;
                try
                {
                    using var pipe = new NamedPipeServerStream(NameFor(AppContext.BaseDirectory), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    var request = await reader.ReadLineAsync(deadline.Token);
                    var preparation = SelectPreparation(request, prepare, prepareRemoval);
                    if (preparation is null) continue;
                    try
                    {
                        var completion = new TaskCompletionSource<DeploymentHandover>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!dispatcher.TryEnqueue(async () =>
                        {
                            try { completion.SetResult(await preparation()); }
                            catch (Exception ex) { completion.SetException(ex); }
                        })) throw new InvalidOperationException("主窗口已停止响应。");
                        // Preparation is never cancelled/force-killed when a client disconnects.
                        permit = await completion.Task;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("安装/卸载准备失败", ex);
                        await writer.WriteLineAsync("2|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ex.Message)));
                        continue;
                    }
                    try { await writer.WriteLineAsync(permit.ExitCode + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(permit.Detail))); }
                    catch (Exception ex) { Log.Warn("交接结果发送失败：" + ex.Message); }
                    if (permit.Ready)
                    {
                        // Keep both gates held until the UI and process have actually exited.
                        if (!dispatcher.TryEnqueue(() => exit())) throw new InvalidOperationException("无法提交安全退出。");
                        return;
                    }
                }
                catch (Exception ex) { Log.Error("安装/卸载交接通道失败", ex); }
                permit?.Dispose();
            }
        });
    }
}
