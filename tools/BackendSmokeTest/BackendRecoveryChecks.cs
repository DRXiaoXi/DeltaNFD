using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>
/// 恢复闭环集成检查（T1）：在隔离数据根内启动真实后端，验证只读核对命令可用，
/// 并验证恢复命令会拒绝未知范围（在触碰任何系统状态之前就返回失败）。
/// 绝不使用有效范围调用 recover——那会写真实系统。
/// </summary>
internal static class BackendRecoveryChecks
{
    public static async Task RunAsync()
    {
        var testId = Guid.NewGuid();
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-official-recovery", testId.ToString("N"));
        Directory.CreateDirectory(isolated);
        File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, testId);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", isolated);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", testId.ToString("D"));
        var passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
        try
        {
            // 只读核对命令：真实后端 EXE，隔离数据根，不写系统。
            foreach (var module in new[] { BackendModules.Ace, BackendModules.Gpu, BackendModules.System })
            {
                var command = BackendModules.ReconcileCommand(module);
                Check(command is not null, $"{module} 提供只读核对命令");
                await using var client = await BackendClient.StartAsync(module, CancellationToken.None);
                var result = await client.InvokeAsync(command!, new { }, null, CancellationToken.None);
                Check(result.Status == BackendResultStatuses.Ok, $"{module} 只读核对执行成功（{result.Code}）");
                Check(result.Data is not null, $"{module} 核对返回快照");
                var snapshot = BackendJson.Deserialize<ReconcileSnapshot>(result.Data!.Value);
                Check(snapshot.Module == module, $"{module} 核对快照模块一致");
                Check(snapshot.RecoveryScopes.Count > 0, $"{module} 提供恢复范围说明");
                Check(BackendModules.IsReadOnly(module, command!), $"{module} 核对命令被归类为只读");
            }

            // ACE 无可信备份恢复路径。
            Check(BackendModules.RecoverCommand(BackendModules.Ace) is null, "ACE 不提供按备份恢复路径");
            Check(BackendModules.RecoverCommand(BackendModules.Gpu) is not null, "显卡提供按备份恢复命令");

            // 恢复命令参数校验：未知范围必须在触碰系统前被拒绝。
            foreach (var module in new[] { BackendModules.Gpu, BackendModules.Runtime, BackendModules.System })
            {
                var command = BackendModules.RecoverCommand(module)!;
                Check(!BackendModules.IsReadOnly(module, command), $"{module} 恢复命令被归类为写入");
                Check(!BackendModules.RequiresWriteIntent(module, command), $"{module} 恢复命令不登记新意图");
                await using var client = await BackendClient.StartAsync(module, CancellationToken.None);
                var result = await client.InvokeAsync(command, new RecoverArgs { Scope = "__invalid__" }, null, CancellationToken.None);
                Check(result.Status == BackendResultStatuses.Failed && result.Code == module + ".recover",
                    $"{module} 拒绝未知恢复范围");
            }
            Check(BackendUnconfirmedStore.PendingModules().Count == 0, "只读核对与参数拒绝不留下未确认记录");

            Console.WriteLine($"Backend recovery checks passed ({passed} assertions): read-only reconcile for ace/gpu/system, " +
                              "recover command classification, unknown-scope refusal. Isolated data only; no real restore attempted.");
        }
        finally
        {
            try { Directory.Delete(isolated, true); } catch { }
        }
    }
}
