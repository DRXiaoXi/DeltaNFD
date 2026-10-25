using DeltaNFD.Services;
using System.Text.Json;

internal static class StartupPresentationChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
        Check(!new AppSettings().SilentStartupEnabled, "default is visible");
        Check(!JsonSerializer.Deserialize<AppSettings>("{\"CloseToTrayEnabled\":true}")!.SilentStartupEnabled, "old settings default to visible");
        foreach (var arguments in new string[][] { ["app.exe"], ["app.exe", "--silent"], ["app.exe", "--autostart", "extra"], ["app.exe", "--verify-file-update", "s", "n"] })
            Check(!StartupPresentation.ShouldStartHidden(arguments, true, false), "only an exact logon launch may hide");
        Check(!StartupPresentation.ShouldStartHidden(["app.exe", "--autostart"], false, false), "logon remains visible without opt-in");
        Check(!StartupPresentation.ShouldStartHidden(["app.exe", "--autostart"], true, true), "recovery/migration blocks hiding");
        Check(StartupPresentation.ShouldStartHidden(["app.exe", "--autostart"], true, false), "opted-in logon may hide");
        var calls = new List<string>();
        var silent = new StartupPresentation();
        silent.Present(true, () => { calls.Add("tray"); return true; }, () => calls.Add("activate"), () => calls.Add("hide"));
        Check(calls.SequenceEqual(new[] { "tray", "activate", "hide" }) && silent.IsHidden, "tray must exist before hiding");
        var prompt = silent.WaitForVisibleAsync();
        Check(!prompt.IsCompleted, "optional dialogs wait for user reopening");
        silent.Reveal();
        await prompt.WaitAsync(TimeSpan.FromSeconds(1));
        Check(!silent.IsHidden && silent.WaitForVisibleAsync().IsCompleted, "manual/tray reopen releases prompts");
        silent.Reveal();
        Check(prompt.IsCompletedSuccessfully, "repeated reopen is idempotent");
        foreach (var trayThrows in new[] { false, true })
        {
            var fallback = new StartupPresentation(); calls.Clear();
            fallback.Present(true, () => trayThrows ? throw new IOException("synthetic tray failure") : false,
                () => calls.Add("visible"), () => calls.Add("hidden"));
            Check(calls.SequenceEqual(new[] { "visible" }) && !fallback.IsHidden && fallback.WaitForVisibleAsync().IsCompleted,
                "tray failure cannot trap the user");
        }
        var manual = new StartupPresentation(); calls.Clear();
        manual.Present(false, () => throw new Exception("manual startup must not require tray"), () => calls.Add("visible"), () => calls.Add("hidden"));
        Check(calls.SequenceEqual(new[] { "visible" }) && !manual.IsHidden, "manual launch remains visible independently of CloseToTray");
        var settings = new AppSettings { SilentStartupEnabled = true, FrameModeActive = true, TempSpoofRestorePending = true, CloseToTrayEnabled = false };
        var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Check(roundtrip.SilentStartupEnabled && roundtrip.FrameModeActive && roundtrip.TempSpoofRestorePending && !roundtrip.CloseToTrayEnabled,
            "persisting silent launch preserves restoration and close preferences");
        Console.WriteLine($"静默启动策略与可见性隔离检查通过（{count} 项）。");
    }
}
