using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class BackendStartupChecks
{
    public static async Task RunAsync(string layout)
    {
        int passed = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            passed++;
            Console.WriteLine("PASS: " + message);
        }
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-startup-checks", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var names = new[] { BackendPaths.BackendRootEnvironmentVariable, "DELTANFD_BACKEND_ISOLATED_ROOT",
            "DELTANFD_BACKEND_ISOLATED_ID", "DOTNET_ROOT", "DOTNET_ROOT_X64", "PATH" };
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", root);
            Environment.SetEnvironmentVariable("PATH", "");
            var backends = Path.Combine(Path.GetFullPath(layout), "Backends");
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, backends);
            foreach (var spec in BackendModules.All.Values)
            {
                var exe = BackendPaths.Resolve(spec.Id);
                Check(Path.GetFullPath(exe).StartsWith(backends + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), spec.Id + " resolves from the tested layout");
                await using var client = await BackendClient.StartAsync(spec.Id, CancellationToken.None);
                Check(client.IsUsable && client.SessionId != Guid.Empty,
                    spec.Id + " real pipe handshake and image identity passed without system .NET");
                string[] commands = spec.Id switch
                {
                    BackendModules.Ace => [BackendCommands.AceScan, BackendCommands.AceCoreCheck],
                    BackendModules.Gpu => [BackendCommands.GpuDriverStatus],
                    BackendModules.Runtime => [BackendCommands.RuntimeStatus, BackendCommands.Vc14Scan, BackendCommands.Vc14Preview, BackendCommands.RuntimeFileUseScan],
                    BackendModules.CpuFrame => [BackendCommands.CpuFrameStatus, BackendCommands.CpuFrameTopology],
                    BackendModules.Procman => [BackendCommands.ProcmanStatus],
                    BackendModules.System => [BackendCommands.SystemShaderStatus, BackendCommands.SystemShaderDiagnose],
                    _ => throw new InvalidOperationException("No read-only startup check for " + spec.Id),
                };
                foreach (var command in commands)
                {
                    Check(!BackendModules.IsMutating(spec.Id, command), command + " is read-only");
                    using var scanTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var scan = await client.InvokeAsync(command, new { }, null, scanTimeout.Token);
                    Check(scan.Status == BackendResultStatuses.Ok && scan.Data is not null,
                        command + " completed with actual response data");
                    if (command == BackendCommands.Vc14Scan)
                    {
                        var runtimeScan = scan.Data?.Deserialize<Vc14Scan>(BackendJson.SerializerOptions);
                        Check(runtimeScan is not null && runtimeScan.MsiStateDiagnostics.Count <= Vc14MsiDiagnosticSession.MaximumProducts,
                            "MSI state diagnostics survive the actual backend protocol with bounded product count");
                        Check(runtimeScan!.MsiStateDiagnostics.All(d => Guid.TryParseExact(d.ProductCode, "B", out _) &&
                            d.StateName == Vc14MsiStateDiagnostics.State(d.StateCode).Name && d.Contexts.Count is 0 or 5),
                            "native state codes and context scopes retain structured meanings");
                        var reportPath = Path.Combine(root, "logs", "vc14-detection-latest.json");
                        Check(File.Exists(reportPath), "MSI diagnostics report written only in the isolated test directory");
                        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
                        Check(report.RootElement.GetProperty("MsiStateDiagnosticsVersion").GetInt32() == 1 &&
                            report.RootElement.GetProperty("Scan").GetProperty("MsiStateDiagnostics").GetArrayLength() == runtimeScan.MsiStateDiagnostics.Count,
                            "saved report contains the same structured state diagnostics as the scan response");
                        Console.WriteLine($"MSI read-only: {runtimeScan.Products.Count} verified products; {runtimeScan.Issues.Count} issues; {runtimeScan.MsiStateDiagnostics.Count} state diagnostics.");
                    }
                    if (command == BackendCommands.CpuFrameTopology)
                    {
                        var topology = scan.Data?.Deserialize<CpuTopology>(BackendJson.SerializerOptions);
                        Check(topology is not null, "CPU topology deserialized from the actual backend");
                        var expectedModel = topology!.IsAmd ? AmdCcdModelCatalog.Find(topology.CpuName) : null;
                        Check(topology.ModelCcdCount == (expectedModel?.CcdCount ?? 0), "model CCD design count survives the backend protocol");
                        Check(topology.CcdCount == topology.Ccds.Count && topology.IsAmdMultiCcd == (topology.IsAmd && topology.CcdCount >= 2),
                            "usable CCD count remains separate from model metadata");
                        Console.WriteLine($"CPU read-only: {topology.CpuName}; model CCD={topology.ModelCcdCount}; actual CCD={topology.CcdCount}; source={topology.CcdDetectionSource}; " +
                            string.Join(", ", topology.Ccds.Select(c => $"CCD{c.Index}=g{c.Group}:0x{c.Mask:X}")));
                    }
                }
                if (spec.Id == BackendModules.Runtime)
                {
                    using (var fileUseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    {
                        Check(!BackendModules.IsMutating(spec.Id, BackendCommands.RuntimeFileUseScan), "full file-use scan remains read-only");
                        var response = await client.InvokeAsync(BackendCommands.RuntimeFileUseScan,
                            new RuntimeFileUseScanArgs { Scope = RuntimeFileUseScope.FullRepair }, null, fileUseTimeout.Token);
                        var fullFileUse = response.Data?.Deserialize<RuntimeFileUseScan>(BackendJson.SerializerOptions);
                        Check(response.Status == BackendResultStatuses.Ok && fullFileUse is { Success: true, Scope: RuntimeFileUseScope.FullRepair },
                            "full file-use scan succeeds through actual backend protocol");
                        Check(fullFileUse!.Message.Contains("2005–2013") && fullFileUse.SnapshotId.Length > 0,
                            "full scan reports extended fixed scope and supplies approval snapshot");
                    }
                    foreach (var legacyStrong in new[] { false, true })
                    {
                        Check(!BackendModules.IsMutating(spec.Id, BackendCommands.RuntimeHighFpsPreview), "full repair preview remains read-only");
                        using var fullPreviewTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        var response = await client.InvokeAsync(BackendCommands.RuntimeHighFpsPreview,
                            new RuntimeHighFpsArgs { StrongMode = legacyStrong }, null, fullPreviewTimeout.Token);
                        var full = response.Data?.Deserialize<RuntimeHighFpsPreview>(BackendJson.SerializerOptions);
                        Check(response.Status == BackendResultStatuses.Ok && full is not null, "full repair preview passed actual protocol");
                        Check(legacyStrong ? !full!.CanRepair && full.Blocker.Contains("已移除")
                            : full!.StrongMode == false && !full.Blocker.Contains("必须在安全模式"), "full repair rejects legacy strong and allows optional normal-mode uninstall");
                    }
                    foreach (var mode in new[] { Vc14RepairMode.UninstallOnly, Vc14RepairMode.InstallOnly, Vc14RepairMode.RegistrationCleanup })
                    {
                        using var previewTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        var stage = await client.InvokeAsync(BackendCommands.Vc14Preview,
                            new Vc14RepairArgs { Mode = mode, AllowTemporaryInstaller = mode == Vc14RepairMode.UninstallOnly }, null, previewTimeout.Token);
                        Check(stage.Status == BackendResultStatuses.Ok && stage.Data is not null,
                            "stage " + mode + " passed real read-only preview protocol");
                    }
                    foreach (var retired in new[] { BackendCommands.RuntimeRepair, BackendCommands.RuntimeCompatibilityRepair,
                        BackendCommands.RuntimeRedistUninstall, BackendCommands.RuntimeRedistUninstallAll })
                    {
                        Check(!spec.Commands.Contains(retired), "retired runtime command absent: " + retired);
                        var refused = false;
                        try { await client.InvokeAsync(retired, new { }, null, CancellationToken.None); }
                        catch (BackendContractException) { refused = true; }
                        Check(refused, "retired runtime request rejected before sending: " + retired);
                    }
                }
                await client.DrainAsync(TimeSpan.FromSeconds(10));
                Check(client.ExitConfirmed, spec.Id + " drained and exited after the read-only check");
            }

            // Reproduce the screenshot: app-local hostfxr + framework-dependent config.
            // Only our temporary apphost copy is started, with native GUI errors disabled by the client.
            var bad = Path.Combine(root, "mixed");
            Directory.CreateDirectory(bad);
            var ace = BackendModules.Require(BackendModules.Ace);
            var source = BackendPaths.Resolve(ace.Id);
            foreach (var name in new[] { ace.ExecutableName, Path.ChangeExtension(ace.ExecutableName, ".dll"), "hostfxr.dll" })
                File.Copy(Path.Combine(Path.GetDirectoryName(source)!, name), Path.Combine(bad, name));
            File.WriteAllText(Path.Combine(bad, Path.ChangeExtension(ace.ExecutableName, ".runtimeconfig.json")),
                JsonSerializer.Serialize(new { runtimeOptions = new { framework = new { name = "Microsoft.NETCore.App", version = "8.0.0" } } }));
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, bad);
            var watch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await using var unexpected = await BackendClient.StartAsync(ace.Id, timeout.Token);
                throw new Exception("Mixed apphost unexpectedly started");
            }
            catch (BackendContractException ex)
            {
                Check(ex.Message.Contains("在建立连接前已退出") && ex.Message.Contains("0x80008096")
                    && ex.Message.Contains(Path.Combine(bad, ace.ExecutableName)),
                    "native framework resolution failure reports actual exit code and executable path");
                Check(watch.Elapsed < TimeSpan.FromSeconds(10), "early process exit does not wait for the 15-second pipe timeout");
            }
            Console.WriteLine($"Backend startup checks: {passed} passed. Isolated state; no optimize, clean, install or other modifying commands.");
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            Directory.Delete(root, true);
        }
    }
}
