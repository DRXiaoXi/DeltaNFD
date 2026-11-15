using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>残留登记的可处理性结论：纯数据，供预览/清理/烟测共用。</summary>
public sealed record Vc14ResidueItem(
    string HiveName, string ViewName, string KeyName, string Name, string Version, string Kind, string Detail)
{
    /// <summary>inf-legacy=固定 INF 包装；orphan=包装孤儿；msi-orphan=失效 MSI 显示项；keep=保留。</summary>
    public bool CanClean => Kind is "inf-legacy" or "orphan" or "msi-orphan";
    public string RegistrationFingerprint { get; init; } = "";
}

/// <summary>
/// 残留登记受控清理（2026-10-15 用户授权）：卸载完成后、安装所选 14.42/14.44 基线前执行。
/// 只删 14.x 相关的登记叶子键与固定 v14 INF 包装文件；不碰 Appx、System32/WinSxS、
/// 形状异常的键、重解析点或 MSI 仍安装的条目；每步先备份后删除、删后复扫。
/// </summary>
internal static class Vc14ResidueCleanup
{
    // 固定 v14 INF 包装：登记叶子键名与 INF 基名（与历史版本工具写入的一致）。
    private static readonly IReadOnlyDictionary<string, (string Leaf, string InfBase)> LegacyPackages = new Dictionary<string, (string, string)>
    {
        ["x64"] = ("vcredist14_x64", "vcred14_x64"),
        ["x86"] = ("vcredist14_x86", "vcred14_x86"),
    };
    // INF 形状哈希：规范化（去注释、压空格、动态字段替换为占位）后比对固定值。
    private static readonly IReadOnlyDictionary<string, string> LegacyInfShapeHashes = new Dictionary<string, string>
    {
        ["x64"] = "0F75FD30A1E76028CFDDEDFF9B47DF77945B27DBBC007C0F773BC051E477E45D",
        ["x86"] = "D7DE279633764C74E81B7A48C09C15ED7361DE21B9B25AC46F98FE5AAEDDA59C",
    };
    // 固定 v14 包装登记模板；仅 Vc14BetaUninstall 经额外产品身份核验后可以构造执行命令。
    private const string LegacyUninstallTemplate = "rundll32.exe advpack.dll,LaunchINFSection vcred14_{ARCH}.inf,Uninstall";
    // 登记键值名白名单：超出名单的键值形状异常，保留不处理。
    private static readonly HashSet<string> AllowedValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DisplayName", "DisplayVersion", "DisplayIcon", "Version", "Publisher", "InstallDate",
        "UninstallString", "QuietUninstallString", "ModifyPath", "RepairPath", "InstallLocation",
        "InstallSource", "HelpLink", "URLInfoAbout", "URLUpdateInfo", "NoModify", "NoRepair", "NoRemove",
        "BundleCachePath", "BundleVersion", "ParentKeyName", "WindowsInstaller", "SystemComponent",
        "EstimatedSize", "Comments", "Contact", "Language",
    };
    private static readonly HashSet<string> MsiValueNames = new(StringComparer.OrdinalIgnoreCase)
        { "AuthorizedCDFPrefix", "Readme", "Size", "VersionMajor", "VersionMinor", "HelpTelephone" };
    // 孤儿登记名称正则：微软 VC++ 可再发行包装（2015-2022/2026/v14 等形态）。
    internal static readonly Regex OrphanNamePattern =
        new(@"^Microsoft Visual C\+\+ (?:2015-2022|2015-2019|2015|2017|2019|2022|2026|v14) Redistributable \((x64|x86|arm64)\)(?: - 14\..*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>枚举当前仍存在的 14.x 相关残留登记，逐项给出可处理性结论（只读，不修改任何状态）。</summary>
    internal static IReadOnlyList<Vc14ResidueItem> Survey(Vc14Scan scan)
    {
        var items = new List<Vc14ResidueItem>();
        foreach (var (hive, view) in Vc14Catalog.RegistryViews())
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var keyName in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(keyName);
                if (key is null) continue;
                var name = key.GetValue("DisplayName") as string ?? "";
                var version = Vc14Catalog.RegistrationVersion(key);
                if (!Vc14Diagnostics.Relevant(name, version)) continue;
                var viewName = view.ToString();
                var hiveName = hive.ToString();
                if (scan.Products.Any(p => p.Code.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new(hiveName, viewName, keyName, name, version, "keep", "MSI 产品仍处于安装状态，不作为残留清理。"));
                    continue;
                }
                if (Vc14Catalog.IsAppxPackage(name))
                {
                    items.Add(new(hiveName, viewName, keyName, name, version, "keep", "系统组件包，由 Windows 管理，不清理。"));
                    continue;
                }
                if (DescribeLegacyInf(hiveName, viewName, keyName, key, name, version) is { } legacy) { items.Add(legacy); continue; }
                if (DescribeMsiOrphan(hiveName, viewName, keyName, key, name, version, scan) is { } msi) { items.Add(msi); continue; }
                items.Add(DescribeOrphan(hiveName, viewName, keyName, key, name, version, scan));
            }
        }
        return items;
    }

    /// <summary>固定 v14 INF 包装识别：登记叶子键名 + 名称正则 + 卸载命令模板（有值时）+ 发布者（有值时）核验。
    /// INF 存在时须形状哈希核验通过；INF 不存在（可能已被上一轮清理删除，含跨运行的中断重试）时
    /// 仅清理登记本身——登记侧四重核验已足够确认这是本工具历史版本的足迹。共用 INF 只删一次由
    /// DeleteLegacyInf 的幂等性保证。</summary>
    internal static Vc14ResidueItem? DescribeLegacyInf(string hiveName, string viewName, string keyName,
        RegistryKey key, string name, string version)
    {
        foreach (var arch in new[] { "x64", "x86" })
        {
            var (leaf, infBase) = LegacyPackages[arch];
            if (!keyName.Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
            var registration = OrphanNamePattern.Match(name);
            if (!registration.Success || !registration.Groups[1].Value.Equals(arch, StringComparison.OrdinalIgnoreCase))
                return new(hiveName, viewName, keyName, name, version, "keep", "v14 包装登记名称或架构与固定键不符，保留不处理。");
            var uninstall = (key.GetValue("UninstallString") as string ?? "").Trim();
            var expected = LegacyUninstallTemplate.Replace("{ARCH}", arch);
            if (uninstall.Length > 0 && !uninstall.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return new(hiveName, viewName, keyName, name, version, "keep", "v14 包装登记的卸载命令与固定模板不符，保留不处理。");
            var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", infBase + ".inf");
            var infPresent = false;
            if (File.Exists(inf))
            {
                string shape;
                try { shape = InfShape(inf); }
                catch (Exception ex) { return new(hiveName, viewName, keyName, name, version, "keep", "INF 文件无法读取：" + ex.Message); }
                if (!shape.Equals(LegacyInfShapeHashes[arch], StringComparison.OrdinalIgnoreCase))
                    return new(hiveName, viewName, keyName, name, version, "keep", "INF 文件形状哈希与固定值不符（内容可能被修改），保留不处理。");
                try { Vc14BetaUninstall.ValidateInf(File.ReadAllText(inf), arch, Vc14Catalog.ReadAny); }
                catch (Exception ex) { return new(hiveName, viewName, keyName, name, version, "keep", "INF 目标组件未确认属于 14.x，保留：" + ex.Message); }
                infPresent = true;
            }
            var publisher = key.GetValue("Publisher") as string ?? "";
            if (publisher.Length > 0 && !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                return new(hiveName, viewName, keyName, name, version, "keep", "v14 包装登记发布者不符，保留不处理。");
            return new(hiveName, viewName, keyName, name, version, "inf-legacy",
                "已核验固定 v14 INF 包装（旧版工具安装足迹：叶子键名、名称与登记值核验一致"
                + (uninstall.Length == 0 ? "；登记缺少卸载命令" : "")
                + (infPresent ? "；INF 形状哈希一致" : "；INF 文件已不在（可能已被清理），仅清登记") + "）；将备份后删除登记叶子键"
                + (infPresent ? "与 INF/PNF" : "") + "。");
        }
        return null;
    }

    /// <summary>孤儿显示登记识别：名称正则 + 微软发布者 + 无卸载命令或缓存不可用 + 键值形状正常。</summary>
    private static Vc14ResidueItem DescribeOrphan(string hiveName, string viewName, string keyName,
        RegistryKey key, string name, string version, Vc14Scan scan)
    {
        var match = OrphanNamePattern.Match(name);
        if (!match.Success)
            return new(hiveName, viewName, keyName, name, version, "keep", "名称不符合可识别的微软 VC++ 可再发行包装形态，保留不处理。");
        var arch = match.Groups[1].Value.ToLowerInvariant();
        if (arch == "arm64")
            return new(hiveName, viewName, keyName, name, version, "keep", "ARM64 包装不在 x86/x64 清理范围。");
        var publisher = key.GetValue("Publisher") as string ?? "";
        if (publisher.Length > 0 && !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            return new(hiveName, viewName, keyName, name, version, "keep", "登记发布者非微软，保留不处理。");
        // 有可用卸载途径（命令的可执行文件按系统目录/PATH 可解析，或缓存可核验）的登记属于可卸载对象，
        // 应在重装阶段处理。命令在但程序不可用（如旧版工具留下的失效命令）视为无可用途径，转入孤儿清理。
        var uninstall = Vc14BetaUninstall.PreferredCommand(key.GetValue("QuietUninstallString") as string ?? "",
            key.GetValue("UninstallString") as string ?? "");
        var cache = key.GetValue("BundleCachePath") as string;
        var commandAvailable = Vc14Catalog.UninstallExecutableAvailable(uninstall);
        if (Vc14BetaUninstall.IsMsiCommand(uninstall))
        {
            try { commandAvailable = Vc14BetaUninstall.MsiTarget(uninstall, Vc14Catalog.ReadAny) is not null; }
            catch (Exception ex) { return new(hiveName, viewName, keyName, name, version, "keep", "MSI 身份无法核验，未清理登记：" + ex.Message); }
        }
        if (commandAvailable || Vc14PackageTrust.Verify(cache, version, arch))
            return new(hiveName, viewName, keyName, name, version, "keep", "该登记仍有可用卸载途径，应在重装阶段处理，不作为孤儿清理。");
        if (!ShapeIsSimple(key, out var shapeDetail))
            return new(hiveName, viewName, keyName, name, version, "keep", shapeDetail);
        return new(hiveName, viewName, keyName, name, version, "orphan",
            "孤儿显示登记：卸载命令缺失或已失效（指向的程序不存在）且缓存不可核验，名称与发布者已核验；将备份登记导出后仅删除该叶子键（非递归）。");
    }

    /// <summary>键值形状核验：无子键、值名在白名单内、无超大值（>8KB）。</summary>
    internal static bool ShapeIsSimple(RegistryKey key, out string detail, bool msiRegistration = false)
    {
        if (key.SubKeyCount != 0) { detail = "登记键含子键，形状异常，保留不处理。"; return false; }
        foreach (var valueName in key.GetValueNames())
        {
            if (!AllowedValueNames.Contains(valueName) && !(msiRegistration && MsiValueNames.Contains(valueName))) { detail = "登记含白名单外的值名（" + valueName + "），保留不处理。"; return false; }
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null || value is string text && text.Length > 8192
                || value is byte[] binary && binary.Length > 8192
                || value is string[] list && list.Sum(s => s.Length) > 8192)
            { detail = "登记值超大（" + valueName + "），保留不处理。"; return false; }
        }
        detail = "";
        return true;
    }

    private static Vc14ResidueItem? DescribeMsiOrphan(string hive, string view, string keyName, RegistryKey key,
        string name, string version, Vc14Scan scan)
    {
        if (!Vc14MsiOrphans.IsCandidate(name)) return null;
        var simple = ShapeIsSimple(key, out _, true);
        return Vc14MsiOrphans.Describe(new(hive, view, keyName, name, version, key.GetValue("Publisher") as string ?? "",
            key.GetValue("WindowsInstaller"), key.GetValue("UninstallString") as string ?? "",
            key.GetValue("QuietUninstallString") as string ?? "", simple, simple ? RegistrationFingerprint(key) : ""),
            scan, Vc14MsiOrphans.ReadAbsence);
    }

    internal static string RegistrationFingerprint(RegistryKey key) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => new
        { Name = n, Kind = key.GetValueKind(n).ToString(), Value = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames) }))));

    internal static string Fingerprint(IReadOnlyList<Vc14ResidueItem> items) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        items.OrderBy(i => i.HiveName, StringComparer.Ordinal).ThenBy(i => i.ViewName, StringComparer.Ordinal).ThenBy(i => i.KeyName, StringComparer.Ordinal))));

    /// <summary>INF 形状哈希：去注释、压空白、四个动态数据字段替换为占位后取 SHA256。</summary>
    internal static string InfShape(string path)
    {
        return InfShapeText(File.ReadAllText(path));
    }

    internal static bool MatchesLegacyInfText(string text, string arch) => text.Length <= 65536
        && LegacyInfShapeHashes.TryGetValue(arch, out var expected) && InfShapeText(text) == expected;

    internal static string InfShapeText(string text)
    {
        var lines = text.Split('\n')
            .Select(line => line.Split(';', 2)[0])
            .Where(line => line.Trim().Length > 0)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim());
        var normalized = new List<string>();
        foreach (var line in lines)
        {
            var replaced = line;
            foreach (var field in new[] { "AppName", "AppVer", "GuidMin", "GuidAdd" })
                replaced = Regex.Replace(replaced, @"^" + field + @"\s*=\s*.*$", field + "=<value>", RegexOptions.IgnoreCase);
            normalized.Add(replaced);
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", normalized))));
    }

    internal static void BackupBeforeInfUninstall(string keyName, string diagnosticsDirectory)
    {
        var items = Survey(Vc14Catalog.Scan()).Where(i => i.KeyName.Equals(keyName, StringComparison.OrdinalIgnoreCase)
            && i.Kind == "inf-legacy").ToArray();
        if (items.Length == 0) throw new IOException("旧 INF 包装登记已变化，请重新预览。");
        foreach (var item in items)
        {
            var folder = Path.Combine(diagnosticsDirectory, "before-inf-uninstall", item.HiveName + "-" + item.ViewName + "-" + item.KeyName);
            Directory.CreateDirectory(folder);
            BackupRegistration(item, folder);
            if (!BackupLegacyInf(item, folder)) throw new IOException("旧 INF 备份未确认，未执行卸载。");
        }
    }

    /// <summary>
    /// 执行受控清理。前置条件：全量重装的卸载阶段已完成且无 14.x MSI 组件残留
    /// （在基线安装前执行——残留登记会阻止基线安装，即 MSI 1638 的成因之一）。
    /// 每项：重读核验 → 备份 → 删除 → 复扫确认。任何一步失败即停止并保留证据。
    /// </summary>
    internal static OperationResult Run(Vc14Scan clearedScan, IVc14ReinstallEnvironment environment, IProgress<string>? progress, CancellationToken token,
        string? confirmedMsiFingerprint = null)
    {
        var logs = Path.Combine(Log.LogDirectory, "vc14-residue-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var mutationAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (clearedScan.Issues.Count > 0) throw new IOException("MSI 检测未完成，不能清理显示登记。");
            var survey = Survey(clearedScan);
            var cleanable = survey.Where(i => confirmedMsiFingerprint is null ? i.CanClean : i.Kind == "msi-orphan").ToList();
            if (confirmedMsiFingerprint is not null && Fingerprint(cleanable) != confirmedMsiFingerprint)
                throw new IOException("失效显示登记清单已变化，请重新预览确认。");
            foreach (var item in survey.Where(i => !i.CanClean))
                Log.Info("14.x 残留保留：" + item.Name + "；" + item.Detail);
            if (cleanable.Count == 0)
                return new OperationResult { Success = true, DiagnosticsDirectory = logs,
                    Message = "无可清理的 14.x 残留登记" + (survey.Count > 0 ? "；" + survey.Count + " 项按规则保留（详见日志）。" : "。") };
            Directory.CreateDirectory(logs);
            var cleaned = new List<string>();
            foreach (var item in cleanable)
            {
                token.ThrowIfCancellationRequested();
                if (RuntimeInstallationReview.InstallerBusy())
                    return Partial(cleaned, logs, "Windows Installer 事务仍忙，已停止后续清理。");
                progress?.Report("清理残留登记：" + item.Name);
                // 删除前重读：登记仍存在且结论不变才继续。
                var freshScan = environment.Scan();
                if (freshScan.Issues.Count > 0 || Vc14Catalog.Fingerprint(freshScan) != Vc14Catalog.Fingerprint(clearedScan))
                    return Partial(cleaned, logs, "MSI 状态在清理期间发生变化，已停止。");
                var current = Survey(freshScan);
                if (current.FirstOrDefault(i => i.HiveName == item.HiveName && i.ViewName == item.ViewName && i.KeyName == item.KeyName)
                    is not { } recheck || recheck != item)
                    return Partial(cleaned, logs, "登记状态在清理期间发生变化，已停止：" + item.Name);
                // 备份目录含 Hive/View，防止 HKLM/HKCU 或 32/64 视图下的同名键互相覆盖。
                var backupDirectory = Path.Combine(logs, item.HiveName + "-" + item.ViewName + "-" + item.KeyName);
                Directory.CreateDirectory(backupDirectory);
                if (item.Kind == "msi-orphan")
                {
                    Vc14ResidueItem? Observe()
                    {
                        token.ThrowIfCancellationRequested();
                        var state = environment.Scan();
                        if (state.Issues.Count > 0 || Vc14Catalog.Fingerprint(state) != Vc14Catalog.Fingerprint(clearedScan))
                            throw new IOException("MSI 状态在清理期间发生变化，已停止。");
                        return Survey(state).FirstOrDefault(i => i.HiveName == item.HiveName && i.ViewName == item.ViewName && i.KeyName == item.KeyName);
                    }
                    Vc14MsiOrphans.CleanLeaf(item, Observe, () => BackupRegistration(item, backupDirectory), () => DeleteRegistration(item),
                        () => RuntimeInstallationReview.InstallerBusy() || environment.Blocker() is not null, () => mutationAttempted = true);
                    cleaned.Add(item.Name + " · " + item.Version);
                    Log.Info("14.x 失效 MSI 显示登记已清理：" + item.Name + "；备份：" + backupDirectory);
                    continue;
                }
                BackupRegistration(item, backupDirectory);
                if (RuntimeInstallationReview.InstallerBusy() || environment.Blocker() is { })
                    return Partial(cleaned, logs, "备份后安装事务或防护状态变化，已停止清理。");
                var backedUpScan = environment.Scan();
                if (backedUpScan.Issues.Count > 0 || Vc14Catalog.Fingerprint(backedUpScan) != Vc14Catalog.Fingerprint(clearedScan)
                    || Survey(backedUpScan).FirstOrDefault(i => i.HiveName == item.HiveName && i.ViewName == item.ViewName && i.KeyName == item.KeyName) != item)
                    return Partial(cleaned, logs, "备份后登记身份变化，未删除：" + item.Name);
                if (item.Kind == "inf-legacy" && !BackupLegacyInf(item, backupDirectory))
                    return Partial(cleaned, logs, "INF 备份失败，已停止：" + item.Name);
                mutationAttempted = true;
                if (!DeleteRegistration(item) || !DeleteLegacyInf(item))
                    return Partial(cleaned, logs, "登记或 INF 删除未确认，已停止：" + item.Name);
                // 删后复扫：登记未再现。
                var after = Survey(clearedScan);
                if (after.Any(i => i.HiveName == item.HiveName && i.ViewName == item.ViewName && i.KeyName == item.KeyName))
                    return Partial(cleaned, logs, "删除后登记仍存在（可能被并发写入），已停止：" + item.Name);
                cleaned.Add(item.Name + " · " + item.Version);
                Log.Info("14.x 残留已清理：" + item.Name + "；备份：" + backupDirectory);
            }
            File.WriteAllText(Path.Combine(logs, "cleaned.json"), JsonSerializer.Serialize(cleaned));
            return new OperationResult { Success = true, DiagnosticsDirectory = logs,
                Message = "已备份并清理 " + cleaned.Count + " 项 14.x 残留登记：\n" + string.Join("\n", cleaned)
                    + "\n备份目录：" + logs };
        }
        catch (OperationCanceledException)
        {
            return new OperationResult { Success = false, OutcomeUnknown = true, DiagnosticsDirectory = logs,
                Message = "清理已取消；已清理项的备份保留在：" + logs };
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, IsPartial = mutationAttempted, DiagnosticsDirectory = logs,
                Message = "残留清理已停止：" + ex.Message + "；备份保留在：" + logs };
        }

        static OperationResult Partial(List<string> cleaned, string logs, string reason) => new()
        {
            Success = false, IsPartial = cleaned.Count > 0, DiagnosticsDirectory = logs,
            Message = reason + (cleaned.Count > 0 ? "\n此前已清理：\n" + string.Join("\n", cleaned) : "")
                + "\n备份目录：" + logs,
        };
    }

    private static void BackupRegistration(Vc14ResidueItem item, string backupDirectory)
    {
        using var hive = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(item.HiveName), Enum.Parse<RegistryView>(item.ViewName));
        using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + item.KeyName)
            ?? throw new IOException("登记键已不存在：" + item.KeyName);
        var values = key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new { Name = n, Kind = key.GetValueKind(n).ToString(), Value = key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames) })
            .ToArray();
        if (item.Kind == "msi-orphan" && Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values))) != item.RegistrationFingerprint)
            throw new IOException("待备份的登记值与已确认快照不一致，未删除。");
        var data = JsonSerializer.SerializeToUtf8Bytes(new { item.HiveName, item.ViewName, item.KeyName, Values = values },
            new JsonSerializerOptions { WriteIndented = true });
        var destination = Path.Combine(backupDirectory, "registration.json");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(data); file.Flush(true); }
        if (!SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(data)))
            throw new IOException("登记备份回读未通过，未删除登记。");
    }

    private static bool BackupLegacyInf(Vc14ResidueItem item, string backupDirectory)
    {
        var infBase = LegacyPackages.Values.First(p => p.Leaf.Equals(item.KeyName, StringComparison.OrdinalIgnoreCase)).InfBase;
        var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (var extension in new[] { ".inf", ".PNF" })
        {
            var source = Path.Combine(infDirectory, infBase + extension);
            if (!File.Exists(source)) continue;
            var backup = Path.Combine(backupDirectory, infBase + extension);
            File.Copy(source, backup, true);
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup)))
                != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))))
                return false;
        }
        return true;
    }

    private static bool DeleteRegistration(Vc14ResidueItem item)
    {
        using var hive = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(item.HiveName), Enum.Parse<RegistryView>(item.ViewName));
        using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true)
            ?? throw new IOException("Uninstall 根键无法打开写入：" + item.HiveName);
        if (item.Kind == "msi-orphan")
        {
            using var leaf = root.OpenSubKey(item.KeyName);
            if (leaf is null || RegistrationFingerprint(leaf) != item.RegistrationFingerprint || !ShapeIsSimple(leaf, out _, true)
                || !Vc14MsiOrphans.ReadAbsence(item.KeyName).ConfirmedAbsent)
                throw new IOException("删除前 MSI 显示登记或安装上下文已变化，未删除。");
        }
        root.DeleteSubKey(item.KeyName, throwOnMissingSubKey: false);
        using var check = root.OpenSubKey(item.KeyName);
        return check is null;
    }

    private static bool DeleteLegacyInf(Vc14ResidueItem item)
    {
        if (item.Kind != "inf-legacy") return true;
        var infBase = LegacyPackages.Values.First(p => p.Leaf.Equals(item.KeyName, StringComparison.OrdinalIgnoreCase)).InfBase;
        var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (var extension in new[] { ".inf", ".PNF" })
        {
            var source = Path.Combine(infDirectory, infBase + extension);
            if (!File.Exists(source)) continue;
            File.Delete(source);
            if (File.Exists(source)) return false;
        }
        return true;
    }
}
