using System.Diagnostics;
using DeltaNFD.Services;

internal static class DeltaForceSearchChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-deltaforce-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 构造两份安装：WeGame 布局（安装根\DeltaForce\...）与直接游戏目录（DeltaForce\...）
            var wegameRoot = Path.Combine(root, "WeGameApps", "rail_apps", "DeltaForce(2001918)");
            var directRoot = Path.Combine(root, "DeltaForce");
            BuildFakeInstall(wegameRoot);
            BuildFakeInstall(directRoot);

            // --- 搜索器 ---
            var hits = new List<string>();
            await foreach (var h in DeltaForceSearcher.SearchDirectoryAsync(root))
                hits.Add(h);
            Check(hits.Count == 2, "搜索器在临时目录发现 2 份安装");
            string Canon(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            Check(hits.All(h => Canon(h).Equals(Canon(wegameRoot), StringComparison.OrdinalIgnoreCase) ||
                                Canon(h).Equals(Canon(directRoot), StringComparison.OrdinalIgnoreCase)),
                "搜索器命中路径正确");

            // --- 取消 ---
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelledHits = new List<string>();
            try { await foreach (var h in DeltaForceSearcher.SearchDirectoryAsync(root, cancellationToken: cts.Token)) cancelledHits.Add(h); }
            catch (OperationCanceledException) { }
            Check(cancelledHits.Count == 0, "取消令牌立即使搜索终止");

            // --- 重解析点跳过 ---
            var reparseDir = Path.Combine(root, "reparse-junction");
            var reparseTarget = Path.Combine(root, "target");
            Directory.CreateDirectory(reparseTarget);
            Directory.CreateDirectory(reparseDir);
            try
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{reparseDir}\\junction\" \"{reparseTarget}\"")
                    { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit();
                var junctionHits = new List<string>();
                await foreach (var h in DeltaForceSearcher.SearchDirectoryAsync(reparseDir)) junctionHits.Add(h);
                Check(junctionHits.Count == 0, "重解析点被跳过");
            }
            catch { /* 环境不支持 junction，跳过 */ }

            // --- Locator：登记列表合并（用独立子目录构造干净的两份安装） ---
            var isoRoot = Path.Combine(root, "iso");
            Directory.CreateDirectory(isoRoot);
            var isoWegame = Path.Combine(isoRoot, "WeGameApps", "rail_apps", "DeltaForce(2001918)");
            var isoDirect = Path.Combine(isoRoot, "DeltaForce");
            BuildFakeInstall(isoWegame);
            BuildFakeInstall(isoDirect);

            AppSettingsStore.Update(s => { s.GameInstallRoots = new List<string>(); s.GameRootOverride = ""; });

            DeltaForceLocator.AddRegisteredRoot(isoWegame);
            DeltaForceLocator.AddRegisteredRoot(isoDirect);
            DeltaForceLocator.AddRegisteredRoot(isoWegame); // 重复添加无效果
            var registered = DeltaForceLocator.FindRoots().ToList();
            Check(registered.Any(r => r.Equals(isoWegame, StringComparison.OrdinalIgnoreCase)), "登记列表包含 WeGame 布局");
            Check(registered.Any(r => r.Equals(isoDirect, StringComparison.OrdinalIgnoreCase)), "登记列表包含直接布局");
            Check(registered.Count(r => r.Equals(isoWegame, StringComparison.OrdinalIgnoreCase)) == 1, "登记去重（同一路径只出现一次）");

            // --- 复数查找器：PSO / UE4 / EXE（登记仍生效时测） ---
            var psoPaths = DeltaForceLocator.FindPsoCachePaths();
            Check(psoPaths.Any(p => p.Contains(isoWegame, StringComparison.OrdinalIgnoreCase)) &&
                  psoPaths.Any(p => p.Contains(isoDirect, StringComparison.OrdinalIgnoreCase)),
                "FindPsoCachePaths 覆盖两份安装");

            var ue4Paths = await DeltaForceLocator.FindUe4PrereqPathsAsync();
            Check(ue4Paths.Any(p => p.Contains(isoWegame, StringComparison.OrdinalIgnoreCase)) &&
                  ue4Paths.Any(p => p.Contains(isoDirect, StringComparison.OrdinalIgnoreCase)),
                "FindUe4PrereqPathsAsync 覆盖两份安装");

            var exePaths = GameCompatibilityService.ResolveGameExePaths();
            Check(exePaths.Any(p => p.Contains(isoWegame, StringComparison.OrdinalIgnoreCase)) &&
                  exePaths.Any(p => p.Contains(isoDirect, StringComparison.OrdinalIgnoreCase)),
                "ResolveGameExePaths 覆盖两份安装");

            // --- 移除登记（在复数查找器之后测） ---
            DeltaForceLocator.RemoveRegisteredRoot(isoWegame);
            var afterRemove = AppSettingsStore.Read().GameInstallRoots;
            Check(afterRemove is null || afterRemove.All(r => !r.Equals(isoWegame, StringComparison.OrdinalIgnoreCase)), "移除登记后不再出现");

            // --- 旧单数方法向后兼容 ---
            Check(DeltaForceLocator.FindPsoCachePath().Length > 0, "FindPsoCachePath 向后兼容（非空）");
            Check((await DeltaForceLocator.FindUe4PrereqAsync()).Length > 0, "FindUe4PrereqAsync 向后兼容（非空）");
            Check(GameCompatibilityService.ResolveGameExePath() is not null, "ResolveGameExePath 向后兼容（非空）");

            // 清理登记，避免影响其它检查
            DeltaForceLocator.RemoveRegisteredRoot(isoWegame);
            DeltaForceLocator.RemoveRegisteredRoot(isoDirect);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            AppSettingsStore.Update(s => { s.GameInstallRoots = new List<string>(); s.GameRootOverride = ""; });
        }
    }

    private static void BuildFakeInstall(string gameRoot)
    {
        // 兼容两种布局：WeGame 安装根（其下含 DeltaForce 子目录）或直接游戏目录
        var binariesDir = Path.Combine(gameRoot, "DeltaForce", "Binaries", "Win64");
        Directory.CreateDirectory(binariesDir);
        File.WriteAllText(Path.Combine(binariesDir, "DeltaForceClient-Win64-Shipping.exe"), "fixture");

        var psoDir = Path.Combine(gameRoot, "DeltaForce", "Saved", "PSOCache");
        Directory.CreateDirectory(psoDir);
        File.WriteAllText(Path.Combine(psoDir, "dummy.bin"), "fixture");

        var redistDir = Path.Combine(gameRoot, "DeltaForce", "Engine", "Extras", "Redist", "en-us");
        Directory.CreateDirectory(redistDir);
        File.WriteAllText(Path.Combine(redistDir, "UE4PrereqSetup_x64.exe"), "fixture");
    }
}
