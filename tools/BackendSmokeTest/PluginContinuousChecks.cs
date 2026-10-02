using System.Diagnostics;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件持续运行验证（待做清单第六节第 6 项）。
/// 覆盖：Job Object 不设 kill-on-close（关闭句柄后子进程自主存活——规范要求先验证）、
/// continuous 驻留与心跳、15 秒失联标记（不自动强杀）、崩溃后不自动重启、
/// 未声明 continuous 拒绝驻留、未授权拒绝驻留、stop 排空驻留会话。
/// 真实子进程 + 临时目录；不修改系统。
/// </summary>
internal static class PluginContinuousChecks
{
    private static int _failures;
    private static int _checks;
    private static string _fixtureRoot = "";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginContinuous_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        _fixtureRoot = root;
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            await RunJobSurvivalChecksAsync(Path.Combine(root, "jobcheck", "backend"));
            await RunPersistentFlowAsync(pluginsRoot);
        }
        finally
        {
            PluginTestCleanup.StopOwnedProcesses(root);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"插件持续运行检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    /// <summary>规范第 5 节前置验证：Job 内进程在关闭宿主句柄后必须自主存活。</summary>
    private static async Task RunJobSurvivalChecksAsync(string dir)
    {
        Console.WriteLine("== Job Object 自主存活 ==");
        var source = Environment.ProcessPath!;
        var exeName = "PluginChildNormal.exe";
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, exeName);
        File.Copy(source, target);
        foreach (var sibling in new[] { "BackendSmokeTest.dll", "BackendSmokeTest.deps.json", "BackendSmokeTest.runtimeconfig.json" })
        {
            var from = Path.Combine(Path.GetDirectoryName(source)!, sibling);
            if (File.Exists(from)) File.Copy(from, Path.Combine(dir, sibling));
        }
        var start = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 用无效管道名：子进程连接失败即退出（exit 0 via catch）——这验证不了存活。
        // 改用持续驻留模式：正常握手后长时间读管道，宿主关句柄后它保持存活。
        var pipeName = "DeltaNFD_JobCheck_" + Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        start.ArgumentList.Add("--dnfd-pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--dnfd-session");
        start.ArgumentList.Add(session.ToString("D"));
        start.ArgumentList.Add("--dnfd-host-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName,
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var job = PluginJobHandle.TryCreateFor(process);
        Check("Job 创建成功", job is not null);
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Console.WriteLine("  [child] " + line); };
        process.BeginErrorReadLine();
        server.WaitForConnectionAsync(CancellationTokenSource
            .CreateLinkedTokenSource(new CancellationTokenSource(10_000).Token).Token).GetAwaiter().GetResult();
        // 握手（hello 的 sessionId 必须与命令行 --dnfd-session 一致，child 会核对）
        WriteHostFrame(server, "hello", session, Guid.NewGuid(),
            BuildHelloJson(dir));
        await Task.Delay(1500);
        Check("Job 成员进程存活", !process.HasExited);

        // 关闭 Job 句柄：不设 kill-on-close → 进程必须存活（规范前置验证）。
        job?.Dispose();
        await Task.Delay(800);
        Check("关闭 Job 句柄后进程自主存活（未设 kill-on-close）", !process.HasExited);

        // 清理：停掉管道让子进程退出（不能留残留）。
        server.Disconnect();
        if (!process.WaitForExit(8000))
        {
            process.Kill();
            process.WaitForExit(5000);
        }
        Check("测试子进程已清理", process.HasExited);
        Console.WriteLine("  Job 检查完成。");
    }

    private static string BuildHelloJson(string dataDirectory)
    {
        var nonce = Convert.ToBase64String(new byte[32]);
        var json = "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"0.83.0\",\"dataDirectory\":\"" +
            dataDirectory.Replace("\\", "\\\\") + "\"}";
        return json;
    }

    private static void WriteHostFrame(System.IO.Pipes.NamedPipeServerStream pipe, string type, Guid session,
        Guid requestId, string payloadJson)
    {
        var envelope = "{\"protocol\":\"1.0\",\"type\":\"" + type + "\",\"sessionId\":\"" + session.ToString("D") +
            "\",\"requestId\":\"" + requestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";
        var payload = System.Text.Encoding.UTF8.GetBytes(envelope);
        var frame = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private static async Task RunPersistentFlowAsync(string pluginsRoot)
    {
        Console.WriteLine("== continuous 驻留 ==");
        var runtime = new PluginRuntimeService(dataRoot: Path.Combine(Path.GetDirectoryName(pluginsRoot)!, "PluginData"));
        const string pluginId = "org.example.continuous-probe";

        // 1. 未声明 continuous 的插件拒绝驻留
        var notContinuous = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true, continuous: false);
        var reject = await runtime.StartPersistentAsync(notContinuous);
        Check("未声明 continuous 拒绝驻留", reject.Contains("未声明"), reject);
        Check("拒绝后无残留", !runtime.IsRunning(pluginId));

        // 2. 未授权拒绝驻留
        var unauthorized = MakeEntry(pluginsRoot, "normal", pluginId, "1.1.0", authorized: false, continuous: true);
        var rejectAuth = await runtime.StartPersistentAsync(unauthorized);
        Check("未授权拒绝驻留", rejectAuth.Contains("未授权"), rejectAuth);

        // 3. 正常驻留：心跳 pong、状态查询、stop 排空
        var entry = MakeEntry(pluginsRoot, "normal", pluginId, "2.0.0", authorized: true, continuous: true);
        var started = await runtime.StartPersistentAsync(entry);
        Check("驻留启动成功", started.Length == 0, started);
        Check("状态显示持续运行", runtime.QueryStatus(pluginId) is { IsRunningFlag: true, Persistent: true, ProcessAlive: true });
        await Task.Delay(6500); // ≥1 个心跳周期
        var status = runtime.QueryStatus(pluginId);
        Check("心跳维持不失联", !status.LostContact, status.Detail);
        var stopped = await runtime.StopAsync(pluginId);
        Check("stop 排空驻留会话", stopped);
        await Task.Delay(500);
        Check("停止后无残留", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 4. 崩溃后不自动重启（驻留中后端退出 → 状态如实呈现，不重新拉起）
        var crashEntry = MakeEntry(pluginsRoot, "crash", pluginId, "2.0.1", authorized: true, continuous: true);
        // crash 模式在收到第一条 invoke 时退出；驻留启动只握手——先驻留成功，再发一个会致其退出的场景不可行，
        // 直接验证：kill 子进程后 QueryStatus 呈现退出且 IsRunning 仍标记（记录保留）直到 StopAsync 清理。
        var crashStarted = await runtime.StartPersistentAsync(crashEntry);
        Check("crash 模式驻留启动成功", crashStarted.Length == 0, crashStarted);
        // 手动终止子进程模拟崩溃
        KillChild("PluginChildCrash");
        await Task.Delay(1200);
        var crashedStatus = runtime.QueryStatus(pluginId);
        Check("崩溃后状态如实呈现", !crashedStatus.ProcessAlive && crashedStatus.Detail.Contains("退出"), crashedStatus.Detail);
        Check("崩溃后不自动重启（运行表保留待人工 Stop）", crashedStatus.IsRunningFlag);
        await runtime.StopAsync(pluginId);
        Check("崩溃清理后无残留", NoChildProcesses());

        // 5. 失联：noheartbeat 模式不回 pong → 15 秒标记失联（不自动强杀）
        var silent = MakeEntry(pluginsRoot, "noheartbeat", pluginId, "2.0.2", authorized: true, continuous: true);
        var silentStarted = await runtime.StartPersistentAsync(silent);
        Check("失联场景驻留启动成功", silentStarted.Length == 0, silentStarted);
        await Task.Delay(17000); // >15 秒无 pong
        var lostStatus = runtime.QueryStatus(pluginId);
        Check("15 秒无响应标记失联", lostStatus.LostContact, lostStatus.Detail);
        Check("失联不自动强杀（进程仍在）", lostStatus.ProcessAlive);
        await runtime.StopAsync(pluginId);
        Check("失联处理后代清理", NoChildProcesses());

        Console.WriteLine("  驻留检查完成。");
    }

    private static void KillChild(string processName)
    {
        PluginTestCleanup.StopOwnedProcesses(_fixtureRoot);
    }

    private static bool NoChildProcesses() =>
        !PluginTestCleanup.HasLiveProcesses(_fixtureRoot);

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static PluginIndexEntry MakeEntry(string pluginsRoot, string mode, string id, string version,
        bool authorized, bool continuous)
    {
        var install = Path.Combine(pluginsRoot, id, version);
        var backend = Path.Combine(install, "backend");
        Directory.CreateDirectory(backend);
        var source = Environment.ProcessPath!;
        var exeName = "PluginChild" + (mode == "normal" ? "" : char.ToUpperInvariant(mode[0]) + mode[1..]) + ".exe";
        var target = Path.Combine(backend, exeName);
        if (!File.Exists(target)) File.Copy(source, target);
        foreach (var sibling in new[] { "BackendSmokeTest.dll", "BackendSmokeTest.deps.json", "BackendSmokeTest.runtimeconfig.json" })
        {
            var from = Path.Combine(Path.GetDirectoryName(source)!, sibling);
            var to = Path.Combine(backend, sibling);
            if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
        }
        File.WriteAllText(Path.Combine(install, "manifest.json"), ManifestTemplate
            .Replace("PLUGIN_ID", id).Replace("VERSION", version)
            .Replace("CONTINUOUS", continuous ? "true" : "false"));
return PluginTestPackage.Seal(new PluginIndexEntry
        {
            Id = id,
            Version = version,
            PackageSha256 = new string('a', 64),
            InstallDirectory = install,
            EntryExecutable = target,
            Name = "持续探针",
            Author = "Smoke",
            State = authorized ? PluginPackageState.Authorized : PluginPackageState.ImportedDisabled,
            Files = [new PluginIndexFile("manifest.json", new string('b', 64), 1)],
            ImportedUtc = DateTimeOffset.UtcNow,
        });
    }

    private const string ManifestTemplate = """
        {
          "schemaVersion": 1,
          "id": "PLUGIN_ID",
          "name": "持续探针",
          "author": "Smoke",
          "version": "VERSION",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/PluginChild.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "只读探针" }
          ],
          "capabilities": { "continuous": CONTINUOUS, "offlineAutonomous": false },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": 30 }
          ]
        }
        """;
}
