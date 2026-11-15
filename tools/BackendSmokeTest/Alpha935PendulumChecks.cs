using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class Alpha935PendulumChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        Check(!PendulumWin10DriverInspection.EvaluateTestSigning(0, 8, 8, 1), "Production CI mode is not test mode");
        Check(PendulumWin10DriverInspection.EvaluateTestSigning(0, 8, 8, 3), "Current boot test-sign flag");
        foreach (var value in new[] { (-1, 8u, 8u), (0, 0u, 8u), (0, 8u, 0u), (0, 7u, 8u), (1, 8u, 8u) })
        {
            bool refused = false;
            try { _ = PendulumWin10DriverInspection.EvaluateTestSigning(value.Item1, value.Item2, value.Item3, 3); }
            catch (IOException) { refused = true; }
            Check(refused, "Unknown CI results do not admit");
        }
        var os = new Version(10, 0, 19045, 6093);
        for (int bits = 0; bits < 32; bits++)
        {
            var driver = PendulumWin10DriverInspection.Evaluate((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0,
                (bits & 8) != 0, (bits & 16) != 0, "injected unknown");
            var global = PendulumGlobalInspection.EvaluateWindows10(os, driver);
            Check(global.Ready == (bits == 31), "All five prerequisites required: " + bits);
            Check(JsonSerializer.Deserialize<PendulumGlobalStatus>(JsonSerializer.Serialize(global))!.Ready == global.Ready, "IPC preserves preparation gate");
        }
        Check(!PendulumGlobalInspection.EvaluateWindows10(os).Ready, "Missing old-protocol driver evidence fails closed");
        var ready = PendulumWin10DriverInspection.Evaluate(true, true, true, true, true);
        Check(!PendulumGlobalInspection.EvaluateWindows10(new Version(10, 0, 22000, 1), ready).Ready, "Win11 cannot enter Win10 path");
        var windows11 = PendulumGlobalInspection.Evaluate(true, new(true, 1), "boot-b", new() { WrittenBootId = "boot-a", Completed = true, TargetEnabled = true });
        Check(windows11.Ready && windows11.Win10Driver is null, "Win11 does not require Win10 driver/test mode");
        var path = @"C:\Program Files\DeltaNFD Pendulum Driver\DeltaNFDPendulumTimerV1.sys";
        Check(PendulumWin10DriverInspection.MatchesService(1, 3, @"\??\" + path, path), "Exact native driver path accepted");
        foreach (var image in new[] { path, "\"" + path + "\"", @"\??\" + path + " extra", @"\??\C:\other.sys", "" })
            Check(!PendulumWin10DriverInspection.MatchesService(1, 3, image, path), "Wrong/EXE-style service path rejected");
        Check(!PendulumWin10DriverInspection.MatchesService(16, 3, @"\??\" + path, path), "Non-driver rejected");
        Check(!PendulumWin10DriverInspection.MatchesService(1, 2, @"\??\" + path, path), "Automatic start rejected");
        Check(AppVersion.Text == "Alpha0.935" && AppVersion.NumericText == "0.9350.0", "Alpha release identity");
        Console.WriteLine($"Alpha0.935 preparation: {checks} checks passed; pure CI/config/IPC fixtures, no boot/service/timer writes.");
        PendulumKernelChecks.Run();
    }
}
