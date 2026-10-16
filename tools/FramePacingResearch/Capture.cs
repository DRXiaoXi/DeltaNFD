using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FramePacingResearch;

internal sealed record CaptureManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Source { get; init; } = "presentmon";
    public required string Condition { get; init; }
    public required string Scene { get; init; }
    public required string FrameGeneration { get; init; }
    public int TargetDisplayedFps { get; init; }
    public int RequestedSeconds { get; init; }
    public required string GameExe { get; init; }
    public required string GameExeSha256 { get; init; }
    public int Pid { get; init; }
    public long ProcessStartUtcTicks { get; init; }
    public required string OsBuild { get; init; }
    public required string DriverFingerprint { get; init; }
    public required string PresentMonSha256 { get; init; }
    public string PresentMonVersion { get; init; } = "2.6.0";
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }
    public int? CollectorExitCode { get; init; }
    public required string Status { get; init; }
    public string CsvSha256 { get; init; } = "";
    public string SessionName { get; init; } = "";
    public string[] CollectorWarnings { get; init; } = [];
    public string Note { get; init; } = "Condition/scene/FG are user declarations; no game/driver settings were changed by this tool.";
}

internal static class Capture
{
    // Pin compiled into the runner too: editing a copied lock file does not authorize another executable.
    public const string ExpectedHash = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const long ExpectedSize = 980320;
    public static string Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    private static string Drivers()
    {
        var drivers = new List<string>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (key is null) return "unavailable";
        foreach (var name in key.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var child = key.OpenSubKey(name);
            if (child is not null) drivers.Add($"{child.GetValue("DriverDesc")}|{child.GetValue("DriverVersion")}");
        }
        return string.Join(";", drivers.Order(StringComparer.Ordinal));
    }
    internal static void ValidateTargetName(string exe, bool lab)
    {
        var expected = lab ? "FramePacingLab.exe" : "DeltaForceClient-Win64-Shipping.exe";
        if (!Path.IsPathFullyQualified(exe) || !Path.GetFileName(exe).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--exe must be the full path of " + expected);
    }
    public static async Task RunAsync(Options options, CancellationToken token)
    {
        var exe = Path.GetFullPath(options.Required("exe"));
        bool lab = options.Flag("lab"); ValidateTargetName(exe, lab);
        var pid = options.Integer("pid", 1, int.MaxValue);
        var seconds = options.Integer("seconds", 1, 120, 60);
        var target = options.Integer("target", 30, 1000, 144);
        var condition = options.Required("condition");
        if (condition is not ("uncapped" or "native" or "nvidia" or "lab")) throw new ArgumentException("condition: uncapped|native|nvidia|lab");
        if (lab != (condition == "lab")) throw new ArgumentException("Lab captures cannot be labelled as game comparison runs.");
        var fg = options.Required("fg");
        if (fg is not ("off" or "on" or "unknown")) throw new ArgumentException("fg: off|on|unknown");
        var scene = options.Required("scene");
        if (scene.Length > 200 || scene.Any(char.IsControl)) throw new ArgumentException("Invalid scene label.");
        var output = options.FreshDirectory("output");
        var collector = Path.GetFullPath(options.Required("presentmon"));
        // Hold the verified image open without write/delete sharing throughout launch and collection.
        using var image = new FileStream(collector, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length != ExpectedSize || Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant() != ExpectedHash)
            throw new InvalidDataException("Collector does not match pinned PresentMon 2.6.0 x64 hash.");
        using var game = Process.GetProcessById(pid);
        var startTicks = game.StartTime.ToUniversalTime().Ticks;
        var observedPath = game.MainModule?.FileName;
        if (observedPath is null || !Path.GetFullPath(observedPath).Equals(exe, StringComparison.OrdinalIgnoreCase) || game.HasExited)
            throw new InvalidDataException("Target PID, process path or lifetime does not match.");
        var manifest = new CaptureManifest
        {
            Source = lab ? "own-dx12-presentmon" : "presentmon", Condition = condition, Scene = scene, FrameGeneration = fg,
            TargetDisplayedFps = target, RequestedSeconds = seconds, GameExe = exe, GameExeSha256 = Hash(exe),
            Pid = pid, ProcessStartUtcTicks = startTicks, OsBuild = Environment.OSVersion.Version.ToString(),
            DriverFingerprint = Drivers(), PresentMonSha256 = ExpectedHash, StartedUtc = DateTimeOffset.UtcNow, Status = "collecting",
            SessionName = "DeltaNFD.FrameResearch." + Guid.NewGuid().ToString("N"),
        };
        Directory.CreateDirectory(output);
        void save(CaptureManifest m) => File.WriteAllText(Path.Combine(output, "capture.json"), JsonSerializer.Serialize(m, Analysis.Json), new UTF8Encoding(false));
        save(manifest);
        var csv = Path.Combine(output, "frames.csv");
        var info = new ProcessStartInfo(collector) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output };
        // Fixed console CLI only. No installer, service, injection, process suspension or settings write.
        foreach (var argument in new[] { "--process_id", pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--timed", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--terminate_after_timed",
            "--output_file", csv, "--no_console_stats", "--track_frame_type", "--qpc_time_ms",
            "--session_name", manifest.SessionName, "--terminate_on_proc_exit" })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start verified collector.");
        var stdoutTask = DrainAsync(process.StandardOutput);
        var stderrTask = DrainAsync(process.StandardError);
        var clock = Stopwatch.StartNew();
        string status = "complete";
        var cleanupWarnings = new List<string>();
        try
        {
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (game.HasExited || game.StartTime.ToUniversalTime().Ticks != startTicks) { status = "target-exited"; break; }
                if (clock.Elapsed > TimeSpan.FromSeconds(seconds + 20)) { status = "collector-timeout"; break; }
                await Task.Delay(200, token);
            }
            if (status == "complete" && clock.Elapsed.TotalSeconds < seconds - 1 && game.HasExited) status = "target-exited";
        }
        catch (OperationCanceledException) { status = "cancelled"; }
        finally
        {
            // Only the Process object started above is terminated. Never stop/kill the game or unrelated ETW sessions.
            if (!process.HasExited)
            {
                // Stop only our uniquely named trace session, allowing the collector to flush CSV first.
                try { await StopOwnSessionAsync(collector, manifest.SessionName); }
                catch (Exception ex) { cleanupWarnings.Add("Trace cleanup failed for " + manifest.SessionName + ": " + ex.Message); }
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: false);
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(killed.Token);
                }
            }
            var stdout = await stdoutTask; var stderr = await stderrTask;
            File.WriteAllText(Path.Combine(output, "collector.stdout.txt"), stdout, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(output, "collector.stderr.txt"), stderr, new UTF8Encoding(false));
            if (status == "complete" && process.ExitCode != 0) status = "collector-failed";
            if (status == "collector-failed" && (stdout + stderr).Contains("access denied", StringComparison.OrdinalIgnoreCase)) status = "permission-denied";
            if (status == "complete" && (!File.Exists(csv) || new FileInfo(csv).Length == 0)) status = "no-data";
            manifest = manifest with { Status = status, CollectorExitCode = process.ExitCode, FinishedUtc = DateTimeOffset.UtcNow,
                CsvSha256 = File.Exists(csv) ? Hash(csv) : "",
                CollectorWarnings = (stdout + "\n" + stderr).Split('\n').Where(line => line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("lost", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(20).Concat(cleanupWarnings).ToArray() };
            save(manifest);
        }
        Console.WriteLine($"Capture {status}: {output}");
        if (status != "complete") throw new InvalidDataException("Capture incomplete; evidence retained, no stable-lock claim.");
    }
    private static async Task StopOwnSessionAsync(string collector, string session)
    {
        var info = new ProcessStartInfo(collector) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--session_name", session, "--terminate_existing_session" }) info.ArgumentList.Add(argument);
        using var cleanup = Process.Start(info) ?? throw new IOException("Cannot stop own trace session: " + session);
        var stdout = DrainAsync(cleanup.StandardOutput); var stderr = DrainAsync(cleanup.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cleanup.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { cleanup.Kill(); throw new IOException("Trace cleanup timed out."); }
        await Task.WhenAll(stdout, stderr);
        if (cleanup.ExitCode != 0) throw new IOException("Trace cleanup exit code " + cleanup.ExitCode);
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) return text.ToString();
            if (text.Length < 128 * 1024) text.Append(buffer, 0, Math.Min(count, 128 * 1024 - text.Length));
        }
    }
}
