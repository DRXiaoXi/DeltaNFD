using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed record NonCppSource(string Name, string Architecture, string RelativePath,
    string Sha256, string Version, long Length, bool RegisterCom, bool OsManaged);
internal sealed record NonCppCatalog(NonCppSource[] Files);
internal sealed record NonCppBackup(string Name, string Architecture, string BeforeHash, string AppliedHash,
    string Acl, bool RegistrationAttempted);
internal sealed record NonCppJournal(bool Complete, NonCppBackup[] Files);

internal interface INonCppRuntimeEnvironment
{
    string Assets { get; }
    string State { get; }
    string Target(NonCppSource file);
    Task<string?> BlockerAsync(CancellationToken token);
    bool Trusted(string path);
    Task RegisterAsync(NonCppSource file, string path, CancellationToken token);
}

/// <summary>Fixed-list DirectX inspection and repair. Never services Windows-owned or C++ files.</summary>
public static class NonCppRuntimeMaintenance
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static NonCppSource[] Sources()
    {
        using var stream = typeof(NonCppRuntimeMaintenance).Assembly.GetManifestResourceStream("NonCppRuntime.catalog.json")
            ?? throw new IOException("缺少内置 DirectX 文件清单。");
        var files = JsonSerializer.Deserialize<NonCppCatalog>(stream)?.Files ?? throw new IOException("DirectX 清单无效。");
        if (files.Length != 186 || files.Select(f => f.Architecture + "/" + f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 186)
            throw new IOException("DirectX 清单范围不完整。");
        foreach (var f in files) Validate(f);
        return files;
    }
    internal static void Validate(NonCppSource f)
    {
        if (f.Architecture is not ("x86" or "x64") || Path.GetFileName(f.Name) != f.Name
            || !f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath != f.Architecture + "/" + f.Name || f.Sha256.Length != 64
            || !f.Sha256.All(Uri.IsHexDigit) || !Version.TryParse(f.Version, out _) || f.Length <= 0
            || f.OsManaged != (f.Name.Equals("D3DCompiler_47.dll", StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals("xinput9_1_0.dll", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("DirectX 清单条目无效。");
    }
    public static bool HasPendingRecovery
    {
        get
        {
            try { return Pending(new NonCppRuntimeEnvironment()); }
            catch { return true; }
        }
    }
    public static Task<NonCppRuntimePreview> PreviewAsync(CancellationToken token) =>
        PreviewAsync(new NonCppRuntimeEnvironment(), Sources(), token);
    public static Task<OperationResult> RepairAsync(string fingerprint, IProgress<string>? progress, CancellationToken token) =>
        RepairAsync(new NonCppRuntimeEnvironment(), Sources(), fingerprint, progress, token);
    public static Task<OperationResult> RestoreAsync(IProgress<string>? progress, CancellationToken token) =>
        RestoreAsync(new NonCppRuntimeEnvironment(), Sources(), progress, token);
    private static string JournalPath(INonCppRuntimeEnvironment e) => Path.Combine(e.State, "journal.json");
    public static bool HasBackup => File.Exists(JournalPath(new NonCppRuntimeEnvironment()));
    private static bool Pending(INonCppRuntimeEnvironment e)
    {
        var path = JournalPath(e);
        try { NoLinks(path); return JsonSerializer.Deserialize<NonCppJournal>(File.ReadAllText(path))?.Complete != true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }
    private static FileStream Acquire(INonCppRuntimeEnvironment e)
    {
        var path = e.State + ".lock"; NoLinks(path, true); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal static string Hash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static void NoLinks(string path, bool missing = false)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            try
            {
                var a = File.GetAttributes(p);
                if ((a & FileAttributes.ReparsePoint) != 0) throw new IOException("路径含链接，未执行：" + p);
                if (p == Path.GetFullPath(path) && !missing && (a & FileAttributes.Directory) != 0)
                    throw new IOException("目标不是普通文件：" + p);
            }
            catch (FileNotFoundException) when (missing) { }
            catch (DirectoryNotFoundException) when (missing) { }
        }
    }
    private static string Source(INonCppRuntimeEnvironment e, NonCppSource f) =>
        Path.Combine(e.Assets, f.Architecture, f.Name);
    internal static bool ArchitectureMatches(string path, string arch)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.PEHeader is not null && (ushort)pe.PEHeaders.CoffHeader.Machine == (arch == "x64" ? 0x8664 : 0x14c);
        }
        catch (BadImageFormatException) { return false; }
    }
    private static void VerifySource(INonCppRuntimeEnvironment e, NonCppSource f)
    {
        var p = Source(e, f); NoLinks(p);
        if (new FileInfo(p).Length != f.Length || Hash(p) != f.Sha256.ToUpperInvariant()
            || !ArchitectureMatches(p, f.Architecture) || !e.Trusted(p))
            throw new IOException("内置 DirectX 载荷校验失败：" + f.RelativePath);
    }
    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
    internal static async Task<NonCppRuntimePreview> PreviewAsync(INonCppRuntimeEnvironment e, NonCppSource[] sources, CancellationToken token)
    {
        var rows = new List<NonCppRuntimeFile>(); var states = new List<string>(); var errors = new List<string>();
        foreach (var f in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Validate(f);
                if (!f.OsManaged) VerifySource(e, f);
                var target = e.Target(f); NoLinks(target, true);
                // File.Exists cannot distinguish denied access from absence; GetAttributes can.
                bool exists;
                try { File.GetAttributes(target); exists = true; }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                var hash = exists ? Hash(target) : "missing";
                var healthy = exists && ArchitectureMatches(target, f.Architecture) &&
                    (hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    e.Trusted(target) && FileVersion(target) >= Version.Parse(f.Version));
                var needs = !f.OsManaged && !healthy;
                rows.Add(new(f.Name, f.Architecture, healthy ? "正常" : f.OsManaged ? "系统组件待核对" : exists ? "需修复" : "缺失",
                    f.OsManaged ? "Windows 维护：本工具不覆盖；异常请用 Windows 系统修复。" : healthy ? "文件与架构符合要求；保留可信较新版本。" : "将使用固定清单中的微软载荷修复。", needs, f.OsManaged) { ObservedHash = hash });
                states.Add(f.RelativePath + ":" + hash + ":" + f.Sha256);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(f.RelativePath + "：" + ex.Message);
                rows.Add(new(f.Name, f.Architecture, "无法核验", ex.Message, false, f.OsManaged));
            }
        }
        var pending = File.Exists(JournalPath(e));
        if (Pending(e)) errors.Add("存在未完成非 C++ 修复；请先核对并恢复修复前文件，不能覆盖旧证据。");
        var blocker = await e.BlockerAsync(token);
        if (blocker is not null) errors.Add(blocker);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(states)));
        return new(errors.Count == 0, string.Join("\n", errors), fingerprint, rows) { RecoveryPending = pending };
    }
    private static void Save(INonCppRuntimeEnvironment e, NonCppJournal journal)
    {
        NoLinks(e.State, true); Directory.CreateDirectory(e.State);
        var path = JournalPath(e); NoLinks(path, true);
        var temporary = path + ".tmp"; NoLinks(temporary, true);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private const AccessControlSections AclSections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
    private static string ReadAcl(string path) => new FileInfo(path).GetAccessControl(AclSections).GetSecurityDescriptorSddlForm(AclSections);
    private static void VerifyOrRestoreAcl(string path, string expected)
    {
        if (expected.Length == 0) throw new IOException("缺少原权限备份。");
        if (ReadAcl(path) == expected) return;
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(expected, AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
        if (ReadAcl(path) != expected) throw new IOException("权限/属主未恢复，记录保留。");
    }
    private static void AtomicCopy(string source, string target, string expectedHash, string currentHash)
    {
        var stage = target + ".DeltaNFD-" + Guid.NewGuid().ToString("N") + ".tmp";
        NoLinks(stage, true);
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output); output.Flush(true);
            }
            if (Hash(stage) != expectedHash) throw new IOException("暂存文件校验失败，未提交。");
            NoLinks(target, true);
            if (currentHash == "missing") File.Move(stage, target);
            else
            {
                using var pinned = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (Convert.ToHexString(SHA256.HashData(pinned)) != currentHash) throw new IOException("提交前目标已改变。");
                // ReplaceFile preserves destination metadata and commits complete bytes atomically.
                File.Replace(stage, target, null);
            }
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    internal static async Task<OperationResult> RepairAsync(INonCppRuntimeEnvironment e, NonCppSource[] sources,
        string fingerprint, IProgress<string>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        var backups = new List<NonCppBackup>();
        try
        {
            using var ownership = Acquire(e);
            var preview = await PreviewAsync(e, sources, token);
            if (!preview.CanRepair) return OperationResult.Fail(preview.Blocker);
            if (fingerprint.Length == 0 || fingerprint != preview.Fingerprint) return OperationResult.Fail("检测结果已变化，请重新检测并确认。");
            var repair = sources.Where(f => preview.Files.Any(r => r.Name == f.Name && r.Architecture == f.Architecture && r.NeedsRepair)).ToArray();
            if (repair.Length == 0) return new() { Success = true, Message = "非 C++ 文件检测完成，无需修复；系统组件提示请单独处理。" };
            if (File.Exists(JournalPath(e)))
            {
                NoLinks(e.State, true);
                Directory.Move(e.State, e.State + "-completed-" + Guid.NewGuid().ToString("N"));
            }
            NoLinks(e.State, true); Directory.CreateDirectory(e.State);
            foreach (var f in repair)
            {
                token.ThrowIfCancellationRequested();
                if (await e.BlockerAsync(token) is { } blocked) throw new IOException(blocked);
                VerifySource(e, f);
                var target = e.Target(f); NoLinks(target, true);
                var row = preview.Files.Single(r => r.Name == f.Name && r.Architecture == f.Architecture);
                var exists = row.ObservedHash != "missing";
                using var original = exists ? File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
                var before = original is null ? "missing" : Convert.ToHexString(SHA256.HashData(original));
                if (before != row.ObservedHash || original?.Length > 64 * 1024 * 1024) throw new IOException("目标已变化，请重新检测。");
                var acl = original is null ? "" : original.GetAccessControl().GetSecurityDescriptorSddlForm(AclSections);
                var backupFile = Path.Combine(e.State, f.Architecture + "-" + f.Name + ".bak"); NoLinks(backupFile, true);
                if (original is not null)
                {
                    original.Position = 0;
                    using var saved = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    original.CopyTo(saved); saved.Flush(true);
                }
                backups.Add(new(f.Name, f.Architecture, before, f.Sha256.ToUpperInvariant(), acl, false));
                Save(e, new(false, backups.ToArray()));
                progress?.Report("修复 DirectX：" + f.RelativePath);
                AtomicCopy(Source(e, f), target, f.Sha256.ToUpperInvariant(), before);
                if (Hash(target) != f.Sha256.ToUpperInvariant()) throw new IOException("写后文件校验失败。");
                if (acl.Length > 0 && ReadAcl(target) != acl) throw new IOException("替换后权限核验失败，保留恢复记录。");
            }
            foreach (var f in repair.Where(f => f.RegisterCom))
            {
                token.ThrowIfCancellationRequested();
                if (await e.BlockerAsync(token) is { } blocked) throw new IOException(blocked);
                var index = backups.FindIndex(b => b.Name == f.Name && b.Architecture == f.Architecture);
                backups[index] = backups[index] with { RegistrationAttempted = true };
                Save(e, new(false, backups.ToArray()));
                await e.RegisterAsync(f, e.Target(f), token);
            }
            Save(e, new(true, backups.ToArray()));
            return new() { Success = true, Message = $"已修复并回读 {repair.Length} 个 DirectX 文件；仅对清单内需要的 COM 组件注册。备份保留。Windows 维护组件未覆盖。", DiagnosticsDirectory = e.State };
        }
        catch (Exception ex)
        {
            return new() { Success = false, IsPartial = backups.Count > 0, OutcomeUnknown = backups.Count > 0,
                Message = "非 C++ 修复未完成：" + ex.Message + "；已写入时保留备份和责任记录。", DiagnosticsDirectory = e.State };
        }
        finally { Gate.Release(); }
    }
    internal static async Task<OperationResult> RestoreAsync(INonCppRuntimeEnvironment e, NonCppSource[] sources, IProgress<string>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            using var ownership = Acquire(e);
            if (await e.BlockerAsync(token) is { } blocked) return OperationResult.Fail(blocked);
            var path = JournalPath(e); NoLinks(path);
            var journal = JsonSerializer.Deserialize<NonCppJournal>(File.ReadAllText(path)) ?? throw new IOException("备份记录无效。");
            if (journal.Files.Length == 0 || journal.Files.Select(b => b.Architecture + b.Name).Distinct().Count() != journal.Files.Length)
                throw new IOException("备份记录范围无效。");
            Save(e, journal with { Complete = false });
            foreach (var b in journal.Files.AsEnumerable().Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (await e.BlockerAsync(token) is { } changed) throw new IOException(changed);
                var f = sources.Single(f => f.Name == b.Name && f.Architecture == b.Architecture && !f.OsManaged);
                if (b.AppliedHash != f.Sha256.ToUpperInvariant()) throw new IOException("备份与固定清单不符。");
                var target = e.Target(f); NoLinks(target, true);
                var backup = Path.Combine(e.State, f.Architecture + "-" + f.Name + ".bak");
                if (b.BeforeHash != "missing") { NoLinks(backup); if (Hash(backup) != b.BeforeHash) throw new IOException("备份字节不可信。"); }
                var current = File.Exists(target) ? Hash(target) : "missing";
                if (current != b.BeforeHash)
                {
                    if (current != b.AppliedHash) throw new IOException("文件已由其他操作改变，未覆盖：" + f.Name);
                    progress?.Report("恢复 DirectX 文件：" + f.RelativePath);
                    if (b.BeforeHash == "missing")
                    {
                        using var pinned = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        if (Convert.ToHexString(SHA256.HashData(pinned)) != b.AppliedHash) throw new IOException("恢复前文件发生变化。");
                        File.Delete(target);
                    }
                    else AtomicCopy(backup, target, b.BeforeHash, b.AppliedHash);
                }
                if ((File.Exists(target) ? Hash(target) : "missing") != b.BeforeHash) throw new IOException("恢复回读失败。");
                // Byte equality never discharges an unresolved ACL responsibility.
                if (b.BeforeHash != "missing") VerifyOrRestoreAcl(target, b.Acl);
            }
            if (journal.Files.Any(b => b.RegistrationAttempted))
                return new() { Success = false, IsPartial = true, OutcomeUnknown = true,
                    Message = "文件已恢复；COM 注册变更不能证明已撤销，记录继续保留。这不是完整系统恢复。", DiagnosticsDirectory = e.State };
            Directory.Move(e.State, e.State + "-restored-" + Guid.NewGuid().ToString("N"));
            return new() { Success = true, Message = "已核验恢复修复前文件。备份留存。" };
        }
        catch (Exception ex) { return new() { Success = false, OutcomeUnknown = true, Message = "恢复未确认，记录保留：" + ex.Message, DiagnosticsDirectory = e.State }; }
        finally { Gate.Release(); }
    }
}
