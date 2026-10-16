using System.Text.Json;
using DeltaNFD.Services;

internal static class DefenderSessionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { BackendEnvironment.AppRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
        var original = existing.Single();
        var journal = Path.Combine(AppDataPaths.Root, "defender-session.json");
        var failRemove = false;
        var failAdd = false;
        var rtp = true;
        DefenderGuard.PowerShellForTests = command =>
        {
            if (command.Contains("ConvertTo-Json -InputObject")) return (0, JsonSerializer.Serialize(existing.ToArray()), null);
            if (command.Contains("Get-MpComputerStatus")) return (0, JsonSerializer.Serialize(new { av = true, rtp, tamper = true }), null);
            if (command.Contains("AntiVirusProduct")) return (0, "Windows Defender", null);
            if (command.Contains("DisableRealtimeMonitoring")) { rtp = true; return (0, null, null); }
            var prefix = "-ExclusionPath '";
            var index = command.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0) throw new Exception("未预期的测试命令。");
            var path = command[(index + prefix.Length)..^1].Replace("''", "'");
            if (command.Contains("Add-MpPreference"))
            {
                check(File.Exists(journal) && File.ReadAllText(journal).Contains("Paths"), "添加前已持久化恢复意图");
                if (failAdd) return (1, null, "fixture.add.failed");
                existing.Add(path);
            }
            else
            {
                check(path != original, "不移除用户已有的排除项");
                if (failRemove) return (1, null, "fixture.remove.failed");
                existing.Remove(path);
            }
            return (0, null, null);
        };
        try
        {
            await DefenderGuard.PreflightAsync([new DefenderBatchItem { Name = "fixture", Ops = "defender security center" }]);
            await DefenderGuard.AddSessionExclusionsAsync();
            check(DefenderGuard.HasPendingSession && existing.Contains(AppDataPaths.Root), "成功添加后仍保留独立恢复记录");
            typeof(DefenderGuard).GetField("_lastReport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            rtp = false;
            failRemove = true;
            var failed = await DefenderGuard.PostApplyAsync();
            check(!failed.CleanupSucceeded && File.Exists(journal), "移除失败不宣称成功、不丢弃恢复记录");
            failRemove = false;
            var retried = await DefenderGuard.PostApplyAsync();
            check(retried.CleanupSucceeded && rtp && !File.Exists(journal), "仅凭磁盘记录重试完成排除项清理和实时保护复核");
            check(existing.SetEquals([original]), "复核后仅保留原有排除项");
            failAdd = true;
            var addFailed = false;
            try { await DefenderGuard.AddSessionExclusionsAsync(); } catch (IOException) { addFailed = true; }
            check(addFailed && File.Exists(journal), "添加失败仍保留可恢复意图");
            check((await DefenderGuard.PostApplyAsync()).CleanupSucceeded && !File.Exists(journal), "未实际添加的排除项也能复核收敛");
            failAdd = false;
            await DefenderGuard.PreflightAsync([new DefenderBatchItem { Name = "fixture", Ops = "windefend" }]);
            await DefenderGuard.AddSessionExclusionsAsync();
            rtp = false;
            var intentionallyOff = await DefenderGuard.PostApplyAsync();
            check(intentionallyOff.CleanupSucceeded && intentionallyOff.DefenderLeftOff && !rtp && !File.Exists(journal), "尊重关闭防护条目的意图，同时清理临时排除项");
        }
        finally { DefenderGuard.PowerShellForTests = null; }
    }
}
