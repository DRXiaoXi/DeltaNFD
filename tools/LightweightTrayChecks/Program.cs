using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const string ProductKey = "DELTA_TRAY_TEST_PRODUCT";
    private const string DataKey = "DELTA_TRAY_TEST_DATA";
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["parent"] or ["activate"] || args.FirstOrDefault() == "--lightweight-tray-handoff") return RunFixture(args);
            if (args is not [var product, var evidence]) throw new ArgumentException("Usage: LightweightTrayChecks.exe <product layout> <evidence dir>");
            if (!Environment.UserInteractive) throw new InvalidOperationException("An interactive Explorer session is required.");
            RequireNoOtherInstance();
            Check(!IsPrimaryPresent(), "session has no existing primary before the probe");
            Directory.CreateDirectory(evidence);
            var observations = new List<object>();
            foreach (var mode in new[] { "cancel", "open", "open-after-taskbar-rebuild", "open-from-shortcut" })
            {
                var id = Guid.NewGuid();
                var data = Path.Combine(evidence, id.ToString("N"));
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "isolated-test.marker"), id.ToString("D"));
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.Environment[ProductKey] = Path.GetFullPath(product);
                start.Environment[DataKey] = Path.GetFullPath(data);
                start.ArgumentList.Add("parent");
                using var parent = Process.Start(start)!;
                Eventually(() => File.Exists(Path.Combine(data, "ready")), "native icon registered before commit");
                using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(data, "child.pid"))));
                Check(child.MainModule!.FileName == Environment.ProcessPath, "fixture owns the candidate");
                var window = TrayWindow(child.Id);
                Check(window != 0, "candidate owns a native hidden window");
                Check(IsPrimaryPresent(), "the parent still owns the session before the decision");
                File.WriteAllText(Path.Combine(data, "decision"), mode);
                try
                {
                    Check(parent.WaitForExit(8000) && parent.ExitCode == 0, "parent completed controlled decision");
                    if (mode != "cancel")
                    {
                        Eventually(IsPrimaryPresent, "only after parent exit did the candidate become primary");
                        if (mode == "open-after-taskbar-rebuild")
                        {
                            Check(PostMessageW(window, RegisterWindowMessageW("TaskbarCreated"), 0, 0), "inject rebuild into the fixture window only");
                            Eventually(() => Logs(data).Contains("检测到任务栏重建") && Logs(data).Split("图标注册成功").Length >= 3,
                                "the tray re-registers after the simulated taskbar rebuild");
                        }
                        child.Refresh();
                        var modules = child.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).Order().ToArray();
                        Check(!modules.Any(n => n.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Microsoft.WinUI", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Windows.UI.Xaml", StringComparison.OrdinalIgnoreCase)), "idle candidate has no native XAML/WinUI module");
                        observations.Add(new { mode, workingSetBytes = child.WorkingSet64, privateBytes = child.PrivateMemorySize64, modules });
                        if (mode == "open-from-shortcut")
                        {
                            RequireNoOtherInstance();
                            start.ArgumentList[0] = "activate";
                            using var duplicate = Process.Start(start)!;
                            Check(duplicate.WaitForExit(8000) && duplicate.ExitCode == 0, "second launch signals the existing tray rather than becoming primary");
                        }
                        else Check(PostMessageW(window, 0x8001, 0, 0x0202), "simulate click on the fixture window only");
                    }
                    Check(child.WaitForExit(10000), "candidate completed without a forced exit");
                    Check(File.ReadAllText(Path.Combine(data, "result")) == (mode == "cancel" ? "False" : "True"), "correct decision to enter the UI");
                    Check(TrayWindow(child.Id) == 0, "native message window is gone");
                    if (mode == "cancel") Check(!IsPrimaryPresent(), "cancelled handoff never became primary");
                }
                finally
                {
                    var owned = !child.HasExited ? TrayWindow(child.Id) : 0;
                    if (owned != 0)
                    {
                        _ = PostMessageW(owned, 0x8001, 0, 0x0202);
                        _ = child.WaitForExit(4000);
                    }
                }
            }
            File.WriteAllText(Path.Combine(evidence, "native-result.json"), JsonSerializer.Serialize(new { assertions = _checks, observations },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {_checks} native tray assertions; no product GUI or maintenance was started.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int RunFixture(string[] args)
    {
        var product = Environment.GetEnvironmentVariable(ProductKey)!;
        var data = Environment.GetEnvironmentVariable(DataKey)!;
        var ui = Path.Combine(product, "DeltaNFD.dll");
        var resolver = new AssemblyDependencyResolver(ui);
        AssemblyLoadContext.Default.Resolving += (context, name) => resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
            File.Exists(Path.Combine(product, name)) ? NativeLibrary.Load(Path.Combine(product, name)) : 0;
        var shared = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(product, "DeltaNFD.Shared.dll"));
        shared.GetType("DeltaNFD.Services.AppDataPaths", true)!.GetMethod("ConfigureIsolatedDataRoot", InternalStatic)!
            .Invoke(null, [data, Guid.ParseExact(Path.GetFileName(data), "N")]);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(ui);
        var tray = assembly.GetType("DeltaNFD.Native.LightweightTray", true)!;
        var acquire = assembly.GetType("DeltaNFD.Native.SingleInstanceGuard", true)!.GetMethod("TryBecomePrimary")!;
        bool TryPrimary(bool activate) => (bool)acquire.Invoke(null, [activate])!;
        if (args is ["activate"]) return TryPrimary(activate: true) ? 1 : 0;
        if (args is ["parent"])
        {
            if (!TryPrimary(activate: false)) throw new InvalidOperationException("The fixture parent could not hold the real session guard.");
            var task = (Task)tray.GetMethod("PrepareAsync", InternalStatic)!.Invoke(null, null)!;
            task.GetAwaiter().GetResult();
            using var handoff = (IDisposable?)task.GetType().GetProperty("Result")!.GetValue(task)
                ?? throw new InvalidOperationException("Native handoff did not register an icon.");
            File.WriteAllText(Path.Combine(data, "ready"), "ready");
            Eventually(() => File.Exists(Path.Combine(data, "decision")), "controller decision arrived");
            if (File.ReadAllText(Path.Combine(data, "decision")) != "cancel")
                handoff.GetType().GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handoff, null);
        }
        else
        {
            File.WriteAllText(Path.Combine(data, "child.pid"), Environment.ProcessId.ToString());
            var open = (bool)tray.GetMethod("ShouldLaunchUi", InternalStatic)!.Invoke(null, [args])!;
            if (open && !TryPrimary(activate: true)) throw new InvalidOperationException("The UI entry must reuse the tray's session guard.");
            File.WriteAllText(Path.Combine(data, "result"), open.ToString());
        }
        return 0;
    }

    private static bool IsPrimaryPresent()
    {
        if (!Mutex.TryOpenExisting(@"Local\DeltaNFD_7A3E9C4D_52B8_4E1F_9A6C_D0F1B2E3A4C5_SingleInstance", out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    private static string Logs(string data) => string.Concat(Directory.EnumerateFiles(data, "*.log", SearchOption.AllDirectories).Select(path =>
    {
        using var reader = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }));
    private static void RequireNoOtherInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName("DeltaNFD").Concat(Process.GetProcessesByName("DeltaOptimizer")))
            using (p) if (p.SessionId == current.SessionId)
                throw new InvalidOperationException("The real application must be stopped before running the isolated fixture.");
    }
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException(description);
        _checks++;
    }
    private static void Eventually(Func<bool> ready, string description)
    {
        Check(SpinWait.SpinUntil(() => { if (ready()) return true; Thread.Sleep(40); return false; }, 7000), description);
    }
    private static nint TrayWindow(int pid)
    {
        nint found = 0;
        _ = EnumWindows((hwnd, parameter) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid) return true;
            var name = new StringBuilder(128);
            if (GetClassNameW(hwnd, name, name.Capacity) <= 0 || !name.ToString().StartsWith("DeltaNFD_Tray_", StringComparison.Ordinal)) return true;
            found = hwnd;
            return false;
        }, 0);
        return found;
    }
    private delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
