using System.Text;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 插件运行时端到端检查（待做清单第六节第 4 项）。
/// 用真实测试后端子进程（冒烟程序自身按文件名切换模式的拷贝）走真实命名管道：
/// 启动/hello-ready 握手/身份核对/invoke/取消/超时/崩溃/stop 排空/BUSY 串行/越界拒绝。
/// 全部在临时目录，不触碰本机插件目录；测试子进程只做内存内应答，不修改系统。
/// </summary>
internal static class PluginRuntimeChecks
{
    private static int _failures;
    private static int _checks;
    private static string _fixtureRoot = "";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginRuntime_" + Guid.NewGuid().ToString("N"));
        _fixtureRoot = root;
        var pluginsRoot = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            await RunScenariosAsync(pluginsRoot);
        }
        finally
        {
            // 残留子进程会锁住临时目录里的 DLL：删除目录前先确认/清理全部测试后端。
            PluginTestCleanup.StopOwnedProcesses(root);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"插件运行时检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static async Task RunScenariosAsync(string pluginsRoot)
    {
        var index = new PluginPackageIndex(Path.Combine(pluginsRoot, "index.json"));
        const string pluginId = "org.example.runtime-probe";

        // 1. 未授权条目拒绝运行（不启动进程）
        var unauthorized = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: false);
        var runtime = new PluginRuntimeService(dataRoot: Path.Combine(Path.GetDirectoryName(pluginsRoot)!, "PluginData"));
        var refused = await runtime.InvokeOnceAsync(unauthorized, "report", new Dictionary<string, object?>());
        Check("未授权拒绝运行", !refused.Succeeded && refused.Code == "PROTOCOL_ERROR");
        Check("未授权未启动进程", !runtime.IsRunning(pluginId));

        // 2. 入口缺失拒绝
        var missing = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true) with
        {
            EntryExecutable = Path.Combine(pluginsRoot, "ghost", "backend", "PluginChildNormal.exe"),
        };
        var missingResult = await runtime.InvokeOnceAsync(missing, "report", new Dictionary<string, object?>());
        Check("入口缺失拒绝", !missingResult.Succeeded);

        // 3. 入口越界拒绝
        var escape = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true) with
        {
            EntryExecutable = Path.Combine(root2(pluginsRoot), "outside.exe"),
        };
        var escapeResult = await runtime.InvokeOnceAsync(escape, "report", new Dictionary<string, object?>());
        Check("入口越界拒绝", !escapeResult.Succeeded);

        // 4. 正常 invoke：握手 + result + 完成后停止
        var normal = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true);
        var ok = await runtime.InvokeOnceAsync(normal, "report", new Dictionary<string, object?> { ["detail"] = true });
        Check("正常 invoke 成功", ok.Succeeded && ok.Status == "success", ok.Code + " " + ok.Message);
        Check("result 只读项", ok.Items.Count == 1 && ok.Items[0].Message.Contains("未修改系统"));
        Check("完成后无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());
        var diagnosticRoot = Path.Combine(Path.GetDirectoryName(pluginsRoot)!, "PluginData", "diagnostics");
        using (PluginDiagnostics.UseDirectory(diagnosticRoot))
        {
            var records = PluginDiagnostics.ReadRecent();
            Check("真实子进程日志覆盖握手与停止", records.Any(e => e.Phase == "handshake" && e.Status == "ok") &&
                records.Any(e => e.Phase == "stop" && e.Status == "ok"));
            var resultRecord = records.FirstOrDefault(e => e.Phase == "invoke" && e.Status == "ok");
            Check("invoke 请求关联往返", resultRecord is not null && PluginDiagnostics.ReadByRequest(resultRecord.RequestId).Count >= 2);
        }
        Directory.Move(diagnosticRoot, diagnosticRoot + "-saved");
        File.WriteAllText(diagnosticRoot, "blocked fixture");
        var logFailureResult = await runtime.InvokeOnceAsync(normal, "report", new Dictionary<string, object?>());
        Check("日志写入失败不改变真实执行结果", logFailureResult.Succeeded && PluginDiagnostics.GetWriteError(diagnosticRoot).Length > 0, logFailureResult.Code);
        File.Delete(diagnosticRoot);
        Directory.Move(diagnosticRoot + "-saved", diagnosticRoot);

        // 5. 握手伪造（ready nonce 不匹配）→ 拒绝且清理
        var wrongReady = MakeEntry(pluginsRoot, "wrongready", pluginId, "1.0.0", authorized: true);
        var wrong = await runtime.InvokeOnceAsync(wrongReady, "report", new Dictionary<string, object?>());
        Check("伪造 ready 被拒", !wrong.Succeeded);
        Check("伪造 ready 无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 6. 崩溃（invoke 后立即退出）→ BACKEND_CRASHED
        var crash = MakeEntry(pluginsRoot, "crash", pluginId, "1.0.0", authorized: true);
        var crashed = await runtime.InvokeOnceAsync(crash, "report", new Dictionary<string, object?>());
        Check("崩溃判定 BACKEND_CRASHED", !crashed.Succeeded && crashed.Code == "BACKEND_CRASHED", crashed.Code);
        Check("崩溃后无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 7. 超时（1 秒）+ cancel 宽限 → RESULT_UNKNOWN
        var hang = MakeEntry(pluginsRoot, "hang", pluginId, "1.0.0", authorized: true, timeoutSeconds: 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = await runtime.InvokeOnceAsync(hang, "report", new Dictionary<string, object?>());
        sw.Stop();
        Check("超时判定 RESULT_UNKNOWN", !timedOut.Succeeded && timedOut.Code == "RESULT_UNKNOWN", timedOut.Code);
        Check("超时含 cancel 宽限（≥5 秒且 <20 秒）", sw.Elapsed.TotalSeconds is >= 5 and < 20, sw.Elapsed.TotalSeconds.ToString("0.0"));
        Check("超时后无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 8. cancel 感知：后端响应 cancel → cancelled
        var cancelAware = MakeEntry(pluginsRoot, "cancel", pluginId, "1.0.0", authorized: true, timeoutSeconds: 1);
        var cancelled = await runtime.InvokeOnceAsync(cancelAware, "report", new Dictionary<string, object?>());
        Check("取消感知返回 cancelled", cancelled.Status == "cancelled" && cancelled.Code == "CANCELLED", cancelled.Status + "/" + cancelled.Code);
        Check("取消后无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 9. BUSY：同一插件并发调用被拒
        var busyEntry = MakeEntry(pluginsRoot, "hang", pluginId, "1.0.0", authorized: true, timeoutSeconds: 1);
        var busyFirst = runtime.InvokeOnceAsync(
            busyEntry,
            "report", new Dictionary<string, object?>());
        await Task.Delay(700); // 等第一个请求进入执行
        var busySecond = await runtime.InvokeOnceAsync(
            busyEntry, "report", new Dictionary<string, object?>());
        Check("并发第二请求 BUSY", !busySecond.Succeeded && busySecond.Code == "BUSY", busySecond.Code);
        var firstResult = await busyFirst;
        Check("并发首请求仍收敛为 RESULT_UNKNOWN", firstResult.Code == "RESULT_UNKNOWN", firstResult.Code);
        Check("并发后无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        // 10. 未声明操作被拒（启动前校验 manifest）
        var undeclared = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true);
        var undeclaredResult = await runtime.InvokeOnceAsync(undeclared, "hack", new Dictionary<string, object?>());
        Check("未声明操作被拒", !undeclaredResult.Succeeded);
        Check("未声明操作无残留进程", !runtime.IsRunning(pluginId) && NoChildProcesses());

        var descendantEntry = MakeEntry(pluginsRoot, "descendant", pluginId, "1.0.0", authorized: true);
        var descendantWait = System.Diagnostics.Stopwatch.StartNew();
        var descendantResult = await runtime.InvokeOnceAsync(descendantEntry, "report", new Dictionary<string, object?>());
        Check("主进程退出后等待 Job 子进程", descendantResult.Succeeded && descendantWait.ElapsedMilliseconds >= 1500, descendantResult.Code);
        Check("子进程排空后才清除记录", !runtime.IsRunning(pluginId) && NoChildProcesses() &&
            runtime.Runs.TryList(out var runs, out _) && runs.Count == 0);

        // 11. StopAllAsync 收敛
        var stopAllFailed = await runtime.StopAllAsync();
        Check("StopAll 无失败", stopAllFailed.Count == 0);
        Check("StopAll 后无残留进程", NoChildProcesses());

        _ = index;
    }

    private static string root2(string pluginsRoot) => Path.GetDirectoryName(pluginsRoot) ?? pluginsRoot;

    /// <summary>构造临时安装目录 + 索引条目；把冒烟程序以模式命名复制为后端 exe。</summary>
    private static PluginIndexEntry MakeEntry(string pluginsRoot, string mode, string id, string version,
        bool authorized, int timeoutSeconds = 30)
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
            .Replace("PLUGIN_ID", id).Replace("VERSION", version).Replace("TIMEOUT", timeoutSeconds.ToString()));
return PluginTestPackage.Seal(new PluginIndexEntry
        {
            Id = id,
            Version = version,
            PackageSha256 = new string('a', 64),
            InstallDirectory = install,
            EntryExecutable = target,
            Name = "运行时探针",
            Author = "Smoke",
            State = authorized ? PluginPackageState.Authorized : PluginPackageState.ImportedDisabled,
            Files = [new PluginIndexFile("manifest.json", new string('b', 64), 1)],
            ImportedUtc = DateTimeOffset.UtcNow,
        });
    }

    private static bool NoChildProcesses()
    {
        try
        {
            return !PluginTestCleanup.HasLiveProcesses(_fixtureRoot);
        }
        catch { return true; }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private const string ManifestTemplate = """
        {
          "schemaVersion": 1,
          "id": "PLUGIN_ID",
          "name": "运行时探针",
          "author": "Smoke",
          "version": "VERSION",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/PluginChild.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "只读探针" }
          ],
          "capabilities": { "continuous": false, "offlineAutonomous": false },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": TIMEOUT }
          ]
        }
        """;
}
