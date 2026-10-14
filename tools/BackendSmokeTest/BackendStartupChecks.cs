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
                    BackendModules.Runtime => [BackendCommands.RuntimeStatus, BackendCommands.Vc14Scan, BackendCommands.Vc14Preview],
                    BackendModules.CpuFrame => [BackendCommands.CpuFrameStatus],
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
                }
                if (spec.Id == BackendModules.Runtime)
                {
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
