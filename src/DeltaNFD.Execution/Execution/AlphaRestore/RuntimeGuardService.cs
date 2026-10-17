using System.Diagnostics;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace DeltaNFD.Services.AlphaRestore;

/// <summary>
/// Alpha0.916 的内部修复适配器；防护仍调用当前实现，不提供旧防护或独立卸载 API。
/// </summary>
internal sealed class RuntimeGuardService
{
    private readonly RuntimeGuardProtection _protection;
    public RuntimeGuardService() : this(TweakBackupStore.Default) { }
    public RuntimeGuardService(TweakBackupStore backups) => _protection = new RuntimeGuardProtection(backups);
    internal RuntimeGuardService(RuntimeGuardProtection protection) => _protection = protection;

    internal Task<RuntimeRepairEnvironmentReport> CheckRepairEnvironmentAsync(CancellationToken cancellationToken = default)
        => RuntimeRepairReadiness.ReadAsync(this, cancellationToken);

    public Task<RuntimeGuardStatus> GetStatusAsync() => new DeltaNFD.Services.RuntimeGuardService().GetStatusAsync();

    // ---------------- Visual C++ 运行库分类扫描 ----------------

    /// <summary>用户确认的三角洲掉帧兼容性标记；不等于 DLL 损坏，详见 AGENTS.md。</summary>
    private const string AbnormalRedistMarker = "Visual C++ v14";

    /// <summary>
    /// 各分支推荐（最适）版本：低于该版本或未安装 → 判定「运行库非最适版本」（黄色提醒）。
    /// v14 分支基准 = 作者实机的 2015-2022 运行库（14.42.34433，与内置官方安装包一致，2026-09 核对）；
    /// 其余分支与内置 AIO 修复包所装官方版本一致。
    /// </summary>
    private static readonly (string Branch, string Arch, string MinVersion)[] RecommendedRedists =
    [
        ("2005", "x64", "8.0.61000"),
        ("2005", "x86", "8.0.61001"),
        ("2008", "x64", "9.0.30729.6161"),
        ("2008", "x86", "9.0.30729.6161"),
        ("2010", "x64", "10.0.40219"),
        ("2010", "x86", "10.0.40219"),
        ("2012", "x64", "11.0.61030"),
        ("2012", "x86", "11.0.61030"),
        ("2013", "x64", "12.0.40664.0"),
        ("2013", "x86", "12.0.40664.0"),
        ("2015-2026", "x64", "14.42.34433.0"),
        ("2015-2026", "x86", "14.42.34433.0"),
    ];

    public Task<VcRedistScanReport> ScanVcRedistsAsync() => Task.Run(() =>
    {
        var all = new List<VcRedistEntry>();

        var roots = (string[])
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in roots)
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null)
            {
                continue;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                if (!seen.Add(keyName))
                {
                    continue;
                }

                using var sub = root.OpenSubKey(keyName);
                if (sub?.GetValue("DisplayName") is not string displayName ||
                    !displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = sub.GetValue("DisplayVersion") as string ?? "";
                var arch = System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\bx64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "x64"
                    : System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\barm64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "arm64"
                    : "x86";
                var uninstall = sub.GetValue("UninstallString") as string ?? "";
                var quiet = sub.GetValue("QuietUninstallString") as string ?? "";

                var kind = RuntimeUninstallCommand.Classify(keyName, uninstall, quiet);

                var entry = new VcRedistEntry
                {
                    DisplayName = displayName,
                    DisplayVersion = version,
                    Architecture = arch,
                    KeyName = keyName,
                    UninstallKind = kind,
                    UninstallString = uninstall,
                    QuietUninstallString = quiet,
                };

                all.Add(entry);
            }
        }
        return BuildVcRedistScanReport(all, entry => RuntimeMsiMetadata.ReadInstalledProduct(entry.KeyName));
    });

    // Share the registration classification with regression fixtures; native reads remain injectable/read-only.
    internal static VcRedistScanReport BuildVcRedistScanReport(IReadOnlyList<VcRedistEntry> entries,
        Func<VcRedistEntry, RuntimeMsiProduct>? readMsi = null)
    {
        var all = entries.ToList();
        var abnormal = all.Where(e => RuntimeBulkUninstall.IsCandidate(e)
            && e.DisplayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase)).ToList();
        var versions = new Dictionary<VcRedistEntry, string>();
        foreach (var entry in all.Where(RuntimeBulkUninstall.IsCandidate))
        {
            var version = entry.DisplayVersion;
            if (entry.UninstallKind == "Msi" && readMsi is not null)
            {
                try
                {
                    var product = readMsi(entry);
                    if (product.State == -1) continue; // Orphan ARP entries do not prove the product is installed.
                    version = product.State == 5 && product.Code.Equals(entry.KeyName, StringComparison.OrdinalIgnoreCase)
                        && product.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                        && product.Architecture == entry.Architecture && product.Name == entry.DisplayName
                        ? product.Version : "";
                }
                catch (Exception ex) { Log.Warn("运行库扫描无法核对 MSI 产品版本：" + entry.KeyName + "；" + ex.Message); version = ""; }
            }
            var expectedMajor = ResolveRedistBranch(entry.DisplayName) switch
            { "2005" => 8, "2008" => 9, "2010" => 10, "2012" => 11, "2013" => 12, "2015-2026" => 14, _ => -1 };
            versions[entry] = Version.TryParse(version, out var parsed) && parsed.Major == expectedMajor
                && version.Split('.').Length >= 3 ? version : "";
        }

        // 分支版本核对：低于推荐版本 / 未安装 → 非最适版本（黄色提醒）
        var suboptimal = new List<VcRedistSuboptimal>();
        foreach (var (branch, arch, minVersion) in RecommendedRedists)
        {
            VcRedistEntry? installed = null;
            foreach (var entry in versions.Keys)
            {
                if (entry.Architecture == arch && ResolveRedistBranch(entry.DisplayName) == branch)
                {
                    // 同分支多条目（异常 v14 包除外）取版本最高的一条参与判定
                    if (installed is null || CompareVersions(versions[entry], versions[installed]) > 0)
                    {
                        installed = entry;
                    }
                }
            }

            if (installed is null)
            {
                suboptimal.Add(new VcRedistSuboptimal
                {
                    Branch = $"{branch} ({arch})",
                    RecommendedVersion = minVersion,
                    InstalledVersion = "",
                    Reason = "missing",
                });
            }
            else if (versions[installed].Length == 0 || CompareRegisteredVersion(versions[installed], minVersion) < 0)
            {
                suboptimal.Add(new VcRedistSuboptimal
                {
                    Branch = $"{branch} ({arch})",
                    RecommendedVersion = minVersion,
                    InstalledVersion = versions[installed],
                    DisplayName = installed.DisplayName,
                    Reason = versions[installed].Length == 0 ? "version-unknown" : "below-recommended",
                    IsRecommendationOnly = versions[installed].Length > 0,
                });
            }
        }

        var advice = abnormal.Count > 0
            ? "检测到 v14 命名运行库；根据三角洲严重掉帧反馈标为兼容性异常，建议执行「修复运行库」。"
            : suboptimal.Count > 0
                ? $"检测到 {suboptimal.Count} 项安装记录/版本提醒；推荐版本差异不等于安装失败或 DLL 损坏。请先处理缺失或无法核对的分支。"
                : "各分支安装记录与推荐版本核对通过；此检测不代表 DLL 完整性验证。";

        return new VcRedistScanReport
        {
            Abnormal = abnormal,
            Suboptimal = suboptimal,
            All = all,
            Advice = advice,
        };
    }

    /// <summary>从显示名解析运行库分支；返回 null = 无法归入任何分支（含 v14 问题包）。</summary>
    private static string? ResolveRedistBranch(string displayName)
    {
        if (displayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase))
        {
            return null; // 三角洲强装的问题包：走红色（Abnormal）判定，不参与黄色判定
        }

        if (displayName.Contains("2005")) return "2005";
        if (displayName.Contains("2008")) return "2008";
        if (displayName.Contains("2010")) return "2010";
        if (displayName.Contains("2012")) return "2012";
        if (displayName.Contains("2013")) return "2013";

        // v14 统一分支：2015-2022 起随年份滚动改名（2015/2017/2019/2022/2026）
        foreach (var year in (string[])["2015", "2017", "2019", "2022", "2026"])
        {
            if (displayName.Contains(year))
            {
                return "2015-2026";
            }
        }

        return null;
    }

    private static int CompareRegisteredVersion(string installed, string recommended)
    {
        // Some ARP/MSI versions omit the revision. Compare only known parts; omission is not revision zero.
        var a = installed.Split('.'); var b = recommended.Split('.');
        return CompareVersions(installed, string.Join(".", b.Take(a.Length)));
    }

    /// <summary>点分版本号比较（"9.0.30729.6161" 风格；不可解析段按 0 处理）。</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb)
            {
                return va.CompareTo(vb);
            }
        }

        return 0;
    }

    // ---------------- 受控运行库修复（分支安装 / 高级全量重装） ----------------

    /// <summary>解包后的 AIO 清单和安装脚本 SHA256（锁定经过核验的完整安装内容）。</summary>
    private const string RepairPayloadManifestSha256 = "848CDEF5764D2B6D1CC4B7083E7DE60B289CCE936679D0692275C3AA1467B6F3";
    private const string RepairInstallerCmdSha256 = "4BBDA17324457E28C6DCC3C196D31BF3329CE280621E5C3E13B10F3BE8F60D1B";

    /// <summary>内置官方 2015-2022 运行库安装包（x64/x86）的 SHA256（防篡改校验）。</summary>
    private static readonly (string File, string Sha256)[] V14InstallerSha256 =
    [
        ("VCRedist2015-2022-x64.exe", "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940"),
        ("VCRedist2015-2022-x86.exe", "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4"),
    ];

    public Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null,
        RuntimeRepairMode mode = RuntimeRepairMode.FullReinstall, CancellationToken cancellationToken = default, bool strongRepairMode = false, string confirmedFingerprint = "") =>
        Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
            RepairVcRedistCoreAsync(progress, mode, cancellationToken, strongRepairMode, confirmedFingerprint).GetAwaiter().GetResult()));

    private async Task<OperationResult> RepairVcRedistCoreAsync(IProgress<string>? progress,
        RuntimeRepairMode mode, CancellationToken token, bool strongRepairMode, string confirmedFingerprint)
    {
        if (strongRepairMode) return OperationResult.Fail("强力修复模式已移除，请使用全量修复重新预览确认。");
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        if (!Enum.IsDefined(mode)) return OperationResult.Fail("修复模式无效，未操作运行库。");
        if (mode != RuntimeRepairMode.FullReinstall) return OperationResult.Fail("运行库修复已恢复 微软原包逐包安装/修复机制；请在新版界面确认完整修复范围，未执行局部分支请求。");
        token.ThrowIfCancellationRequested();
        var preview = await RuntimeHighFpsMaintenance.PreviewAsync(strongRepairMode);
        if (!preview.CanRepair) return OperationResult.Fail(preview.Blocker);
        if (string.IsNullOrWhiteSpace(confirmedFingerprint) || preview.Fingerprint != confirmedFingerprint)
            return OperationResult.Fail("运行库清单或修复模式已变化，请重新预览确认；未执行修复。");
        var blocker = _protection.RepairBlocker(await FindUe4PrereqAsync());
        if (blocker is not null) return OperationResult.Fail(blocker);
        var readiness = await CheckRepairEnvironmentAsync(token);
        progress?.Report(readiness.Summary);
        if (!readiness.CanProceed) return OperationResult.Fail(readiness.Summary + "\n\n" + readiness.Details);
        RuntimeRepairDiagnostics diagnostics;
        try { diagnostics = new RuntimeRepairDiagnostics(Log.LogDirectory); }
        catch (Exception ex) { return OperationResult.Fail("无法创建诊断目录，未更改运行库：" + ex.Message); }
        Log.Info($"运行库修复：微软 C++ 原包及辅助组件机制；诊断目录={diagnostics.DirectoryPath}");
        var environment = new RuntimeBetaRepairEnvironment(this, diagnostics, preview.V14Fingerprint);
        var repair = new RuntimeBetaRepair(environment, diagnostics);
        diagnostics.Save("repair-options.json", new { StrongRepairMode = strongRepairMode, Repair = "full-official-cpp" });
        var result = await repair.RunAsync(progress, token);
        return diagnostics.Complete(result);
    }

    internal static OperationResult VerifyRepairPackage(
        string assetsRoot,
        string packageRoot,
        IProgress<string>? progress)
    {
        try
        {
            var manifestPath = Path.Combine(packageRoot, "payload_manifest_sha256.csv");
            var installerPath = Path.Combine(packageRoot, "Installer.cmd");
            if (new[] { "2026", "2022" }.Any(year => Directory.Exists(Path.Combine(packageRoot, year))))
                return OperationResult.Fail("辅助组件资源混入了已排除的 AIO V14 安装目录，已拒绝执行。");
            if (!Directory.Exists(packageRoot) || !File.Exists(manifestPath) || !File.Exists(installerPath))
            {
                return OperationResult.Fail("未找到完整的 VCRedistRepair 安装目录、安装脚本或文件清单。");
            }

            var manifestHash = ComputeSha256(manifestPath);
            if (!manifestHash.Equals(RepairPayloadManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("运行库文件清单 SHA256 不匹配，安装包可能已损坏或被修改。");
            }

            var installerHash = ComputeSha256(installerPath);
            if (!installerHash.Equals(RepairInstallerCmdSha256, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("AIO 安装脚本 SHA256 不匹配，安装包可能已损坏或被修改。");
            }

            // 先核对将要在卸载旧运行库后使用的官方 v14 安装包，缺失或损坏时提前中止。
            foreach (var (file, expectedHash) in V14InstallerSha256)
            {
                var path = Path.Combine(assetsRoot, "Vc14Baseline14.42", file.Replace("VCRedist2015-2022-", "vc_redist."));
                if (!File.Exists(path))
                {
                    return OperationResult.Fail($"缺少官方运行库安装包：{file}");
                }

                if (!ComputeSha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"官方运行库安装包 SHA256 不匹配：{file}");
                }
            }

            var lines = File.ReadAllLines(manifestPath);
            if (lines.Length < 2 || !string.Equals(lines[0], "relative_path,size_bytes,sha256", StringComparison.Ordinal))
            {
                return OperationResult.Fail("运行库文件清单格式无效。");
            }

            var entries = lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            if (entries.Length != 375)
            {
                return OperationResult.Fail($"运行库文件清单条目数异常：预期 375，实际 {entries.Length}。");
            }

            var fullRoot = Path.GetFullPath(packageRoot);
            var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            for (var index = 0; index < entries.Length; index++)
            {
                var line = entries[index];
                var firstComma = line.IndexOf(',');
                var lastComma = line.LastIndexOf(',');
                if (firstComma <= 0 || lastComma <= firstComma || lastComma == line.Length - 1)
                {
                    return OperationResult.Fail($"运行库文件清单第 {index + 1} 项格式无效。");
                }

                var relativePath = line[..firstComma]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var sizeText = line[(firstComma + 1)..lastComma];
                var expectedHash = line[(lastComma + 1)..];
                if (Path.IsPathRooted(relativePath)
                    || !long.TryParse(sizeText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var expectedSize))
                {
                    return OperationResult.Fail($"运行库文件清单第 {index + 1} 项路径或大小无效。");
                }

                var filePath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
                if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"运行库文件清单路径越界：{relativePath}");
                }

                if (!File.Exists(filePath))
                {
                    return OperationResult.Fail($"运行库安装内容缺失：{relativePath}");
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length != expectedSize)
                {
                    return OperationResult.Fail($"运行库安装内容大小不匹配：{relativePath}");
                }

                if (!ComputeSha256(filePath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"运行库安装内容 SHA256 不匹配：{relativePath}");
                }

                if ((index + 1) % 100 == 0 || index + 1 == entries.Length)
                {
                    progress?.Report($"正在校验运行库安装内容 {index + 1}/{entries.Length}…");
                }
            }

            return OperationResult.Ok($"已通过 SHA256 校验：AIO 安装脚本及 {entries.Length} 个清单文件，官方 x86/x64 v14 安装包也有效。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"无法读取或校验运行库安装内容：{ex.Message}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern int QueryMsiProductState(string productCode);

    internal static bool IsUninstallEntryPresent(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName) || keyName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidOperationException("卸载条目键名无效。");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName);
            if (key is not null) return true;
        }
        return false;
    }

    // ---------------- 三角洲 UE4 前置包定位 ----------------

    private static Task<string> FindUe4PrereqAsync() => DeltaForceLocator.FindUe4PrereqAsync();
    internal async Task<string?> RepairProtectionBlockerAsync() => _protection.RepairBlocker(await FindUe4PrereqAsync());

}
