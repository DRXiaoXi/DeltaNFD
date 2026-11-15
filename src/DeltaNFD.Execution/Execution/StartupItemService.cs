using Microsoft.Win32;
using DeltaNFD.Inspection;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

public interface IStartupItemEnvironment
{
    string? Blocker();
    IReadOnlyList<StartupItemOriginal> Discover();
    StartupItemOriginal? Read(StartupItemScope scope, string name);
    void Remove(StartupItemOriginal original);
    void Create(StartupItemOriginal original);
}

/// <summary>读前确认、写前备份、写后核对；失败不重放，发现登记变化时停止。</summary>
public sealed class StartupItemEngine(StartupItemStore store, IStartupItemEnvironment environment)
{
    public OperationResult Change(StartupItemChangeArgs args, CancellationToken token = default)
    {
        var submitted = false;
        try
        {
            if (!StartupItemStore.ValidId(args.Id) || !StartupItemStore.ValidId(args.Fingerprint) || !Enum.IsDefined(args.Action))
                return OperationResult.Fail("自启动请求无效，未修改。");
            return store.Exclusive(() =>
            {
                CheckGate(token);
                _ = store.ReadAll(); // 不在损坏的备份仓库上继续写入。
                var backup = store.Read(args.Id);
                var target = backup?.Original ?? environment.Discover().SingleOrDefault(i => i.Id == args.Id);
                if (target is null) return OperationResult.Fail("启动登记已消失，请刷新列表。");
                StartupItemStore.Validate(target);
                var current = environment.Read(target.Scope, target.Name);
                if (StartupItemInspection.Fingerprint(args.Id, current, backup) != args.Fingerprint)
                    return OperationResult.Fail("启动登记或原始备份已变化，请刷新后重新确认。");
                if (args.Action == StartupItemAction.KeepCurrent)
                {
                    if (backup is null || current is null || current.SameContent(backup.Original))
                        return OperationResult.Fail("当前没有需要保留的冲突登记。");
                    CheckGate(token); VerifyCurrent(target, current);
                    store.Archive(args.Id, backup.Fingerprint);
                    return OperationResult.Ok("已保留当前登记，旧备份归档到软件数据目录；没有修改该启动登记。");
                }
                if (args.Action == StartupItemAction.Disable)
                {
                    if (backup is not null) return OperationResult.Fail("该项已有原始备份，请先恢复或核对记录。");
                    if (current is null) return OperationResult.Fail("启动登记已不存在，未执行。");
                    backup = store.Save(current);
                    CheckGate(token); VerifyCurrent(target, current);
                    submitted = true; environment.Remove(current);
                    if (environment.Read(target.Scope, target.Name) is not null) throw new IOException("停用后仍存在启动登记。");
                    return OperationResult.Ok("已停用该启动登记，原始备份已保存；下次登录生效，不会结束当前程序。");
                }
                if (backup is null) return OperationResult.Fail("没有本工具保存的原始备份，未创建猜测的启动项。");
                if (current is not null && !current.SameContent(backup.Original))
                    return OperationResult.Fail("当前已有不同的同名登记，未覆盖；请核对后选择保留当前，原始备份仍在。");
                CheckGate(token); VerifyCurrent(target, current);
                if (current is null)
                {
                    submitted = true; environment.Create(backup.Original);
                    var after = environment.Read(target.Scope, target.Name);
                    if (after is null || !after.SameContent(backup.Original)) throw new IOException("恢复后的启动登记与备份不一致。");
                }
                store.Archive(args.Id, backup.Fingerprint);
                return OperationResult.Ok("已按原始备份恢复该启动登记；Windows或应用自身的其他启动开关保持原状，下次登录时生效。");
            });
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, IsPartial = submitted, OutcomeUnknown = submitted,
                Message = (submitted ? "操作尚未完整确认，原始备份保留，请核对：" : "未执行启动登记修改：") + ex.Message };
        }
    }
    private void CheckGate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (environment.Blocker() is { } blocker) throw new InvalidOperationException(blocker);
    }
    private void VerifyCurrent(StartupItemOriginal target, StartupItemOriginal? expected)
    {
        var now = environment.Read(target.Scope, target.Name);
        if (now?.Fingerprint != expected?.Fingerprint) throw new IOException("写入前启动登记已变化，已停止。");
    }
    public OperationResult RestoreAll(CancellationToken token = default)
    {
        var restored = 0; var errors = new List<string>(); var unknown = false;
        try
        {
            CheckGate(token);
            foreach (var backup in store.ReadAll())
            {
                CheckGate(token);
                var original = backup.Original;
                var current = environment.Read(original.Scope, original.Name);
                var result = Change(new(original.Id, StartupItemInspection.Fingerprint(original.Id, current, backup), StartupItemAction.Restore), token);
                if (result.Success) restored++; else errors.Add(original.Name + "：" + result.Message);
                if (result.OutcomeUnknown) { unknown = true; break; }
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        return new OperationResult { Success = errors.Count == 0, IsPartial = restored > 0 && errors.Count > 0, OutcomeUnknown = unknown,
            Message = $"自启动原始登记已恢复 {restored} 项。" + string.Join("\n", errors) };
    }
}

public static class StartupItemService
{
    public static OperationResult Change(StartupItemChangeArgs args, CancellationToken token)
    {
        using var target = GameTargetService.Default.BeginOperation();
        return new StartupItemEngine(StartupItemInspection.Store(), new NativeEnvironment()).Change(args, token);
    }
    public static OperationResult RestoreAll(CancellationToken token = default)
    {
        using var target = GameTargetService.Default.BeginOperation();
        return new StartupItemEngine(StartupItemInspection.Store(), new NativeEnvironment()).RestoreAll(token);
    }

    private sealed class NativeEnvironment : IStartupItemEnvironment
    {
        public string? Blocker()
        {
            if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
            try { return GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning() ? "请先退出游戏再修改自启动项目。" : null; }
            catch (Exception ex) { return "无法确认游戏状态，未修改：" + ex.Message; }
        }
        public IReadOnlyList<StartupItemOriginal> Discover() => StartupItemInspection.Discover().Items;
        public StartupItemOriginal? Read(StartupItemScope scope, string name) => StartupItemInspection.ReadTarget(scope, name);
        public void Remove(StartupItemOriginal original)
        {
            if (original.Scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
            {
                var path = StartupItemInspection.ShortcutPath(original.Scope, original.Name);
                StartupShortcutFiles.Remove(path, original); return;
            }
            StartupRunRegistration.Apply(original, restore: false);
        }
        public void Create(StartupItemOriginal original)
        {
            if (original.Scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
            {
                var path = StartupItemInspection.ShortcutPath(original.Scope, original.Name);
                StartupShortcutFiles.Create(path, original); return;
            }
            StartupRunRegistration.Apply(original, restore: true);
        }
    }
}

internal interface IStartupRegistryEdit : IDisposable
{
    StartupItemOriginal? Read();
    void Delete();
    void Set();
    void Commit();
}

/// <summary>优先在事务中比较并修改；仅事务不可用时兼容常规接口，始终重新核对并保留原始备份。</summary>
internal static class StartupRunRegistration
{
    internal static void Apply(StartupItemOriginal original, bool restore,
        Func<StartupItemOriginal, bool, IStartupRegistryEdit>? open = null)
    {
        StartupItemStore.Validate(original);
        if (original.Scope is not (StartupItemScope.UserRun or StartupItemScope.MachineRun or StartupItemScope.MachineRun32))
            throw new IOException("不是固定Run范围。");
        using var transaction = (open ?? OpenNative)(original, restore);
        var current = transaction.Read();
        if (restore)
        {
            if (current is not null) throw new IOException("同名启动登记已出现，未覆盖。");
            transaction.Set();
            if (transaction.Read()?.Fingerprint != original.Fingerprint) throw new IOException("事务内恢复核对失败。");
        }
        else
        {
            if (current?.Fingerprint != original.Fingerprint) throw new IOException("写前Run登记已变化。");
            transaction.Delete();
            if (transaction.Read() is not null) throw new IOException("事务内停用核对失败。");
        }
        transaction.Commit();
    }
    internal static IStartupRegistryEdit OpenNative(StartupItemOriginal original, bool create)
    {
        try { return new NativeTransaction(original, create); }
        catch (TransactionsUnavailableException) { return new NativeOrdinary(original, create); }
    }
    // 常规注册表API没有值级CAS/外部进程锁：不能承诺应用并发写入时的原子互斥。
    // 仅已知事务不可用且尚未提交任何写入时走此路径，不能在事务提交失败后重试写入。
    private sealed class NativeOrdinary : IStartupRegistryEdit
    {
        private readonly StartupItemOriginal _original;
        private readonly RegistryKey _key;
        internal NativeOrdinary(StartupItemOriginal original, bool create)
        {
            _original = original;
            if (create)
            {
                using var root = RegistryKey.OpenBaseKey(original.Scope == StartupItemScope.UserRun ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
                _key = root.CreateSubKey(original.Scope == StartupItemScope.MachineRun32 ? StartupItemInspection.Run32Key : StartupItemInspection.RunKey, writable: true);
            }
            else _key = StartupItemInspection.OpenRun(original.Scope, writable: true) ?? throw new IOException("Run键已消失。");
        }
        public StartupItemOriginal? Read() => ReadRegistered(_key, _original);
        public void Delete()
        {
            if (Read()?.Fingerprint != _original.Fingerprint) throw new IOException("写前Run登记已变化，未删除。");
            _key.DeleteValue(_original.Name, throwOnMissingValue: true);
        }
        public void Set()
        {
            if (Read() is not null) throw new IOException("同名启动登记已出现，未覆盖。");
            _key.SetValue(_original.Name, _original.Data, (RegistryValueKind)_original.Kind);
        }
        public void Commit() { } // 已逐项执行；外层继续做全新句柄复检，失败保留备份/未确认状态。
        public void Dispose() => _key.Dispose();
    }
    private sealed class TransactionsUnavailableException(int code) : IOException("当前注册表事务不可用。" + code);
    private static StartupItemOriginal? ReadRegistered(RegistryKey key, StartupItemOriginal original)
    {
        uint length = 0;
        var error = RegQueryValueExW(key.Handle, original.Name, IntPtr.Zero, out var type, IntPtr.Zero, ref length);
        if (error == 2) return null;
        if (error != 0) throw new IOException("Run登记读取失败：" + new Win32Exception(error).Message);
        if (type is not (1 or 2) || length > 65538) throw new IOException("非受支持的字符串Run登记，未修改。");
        var data = key.GetValue(original.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new IOException("Run登记无法读取。");
        return original with { Kind = (int)type, Data = data };
    }
    private sealed class NativeTransaction : IStartupRegistryEdit
    {
        private readonly StartupItemOriginal _original;
        private readonly SafeTransactionHandle _transaction;
        private readonly RegistryKey _key;
        private bool _committed;
        internal NativeTransaction(StartupItemOriginal original, bool create)
        {
            _original = original;
            _transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 15000, "Delta NFD startup registration");
            if (_transaction.IsInvalid) { var error = Marshal.GetLastWin32Error(); _transaction.Dispose(); throw NativeError(error); }
            SafeRegistryHandle? handle = null;
            try
            {
                using var root = RegistryKey.OpenBaseKey(original.Scope == StartupItemScope.UserRun ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
                var path = original.Scope == StartupItemScope.MachineRun32 ? StartupItemInspection.Run32Key : StartupItemInspection.RunKey;
                const int access = 0x0001 | 0x0002 | 0x0100; // QUERY_VALUE | SET_VALUE | WOW64_64KEY
                var error = create
                    ? RegCreateKeyTransactedW(root.Handle, path, 0, null, 0, access, IntPtr.Zero, out handle, out _, _transaction, IntPtr.Zero)
                    : RegOpenKeyTransactedW(root.Handle, path, 0, access, out handle, _transaction, IntPtr.Zero);
                if (error is 6801 or 50 or 120) throw new TransactionsUnavailableException(error);
                if (error != 0) throw NativeError(error);
                _key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
            }
            catch { handle?.Dispose(); _transaction.Dispose(); throw; }
        }
        public StartupItemOriginal? Read() => ReadRegistered(_key, _original);
        public void Delete() => _key.DeleteValue(_original.Name, throwOnMissingValue: true);
        public void Set() => _key.SetValue(_original.Name, _original.Data, (RegistryValueKind)_original.Kind);
        public void Commit()
        {
            if (!CommitTransaction(_transaction)) throw NativeError(Marshal.GetLastWin32Error());
            _committed = true;
        }
        public void Dispose()
        {
            _key.Dispose();
            if (!_committed) RollbackTransaction(_transaction);
            _transaction.Dispose();
        }
        private static IOException NativeError(int code) => new("Windows未能完成启动登记事务；原始备份保留：" + new Win32Exception(code).Message + $" ({code})");
    }
    private sealed class SafeTransactionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTransactionHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeTransactionHandle CreateTransaction(IntPtr attributes, IntPtr uow, uint options, uint isolation, uint isolationFlags, uint timeout, string description);
    [DllImport("ktmw32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeTransactionHandle transaction);
    [DllImport("ktmw32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RollbackTransaction(SafeTransactionHandle transaction);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyTransactedW(SafeRegistryHandle root, string path, uint options, int access, out SafeRegistryHandle key, SafeTransactionHandle transaction, IntPtr extended);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCreateKeyTransactedW(SafeRegistryHandle root, string path, uint reserved, string? keyClass, uint options, int access, IntPtr security, out SafeRegistryHandle key, out uint disposition, SafeTransactionHandle transaction, IntPtr extended);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, IntPtr data, ref uint length);
}

internal static class StartupShortcutFiles
{
    internal static void Remove(string path, StartupItemOriginal original)
    {
        StartupItemStore.Validate(original);
        if (original.Scope is not (StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
            || !Path.IsPathFullyQualified(path) || Path.GetFileName(path) != original.Name) throw new IOException("快捷方式停用目标无效。");
        StartupItemStore.RejectLinks(path);
        // 比较及删除绑定同一文件句柄，期间拒绝写入/替换，不按再次解析的路径删除。
        using var handle = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // READ|DELETE, share-read, OPEN_REPARSE_POINT
        if (handle.IsInvalid) throw new IOException("无法锁定启动快捷方式：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (StartupItemInspection.ReadShortcutStream(stream, path, original.Scope, original.Name).Fingerprint != original.Fingerprint)
            throw new IOException("写前启动快捷方式已变化。");
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new IOException("无法停用启动快捷方式：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
    internal static void Create(string path, StartupItemOriginal original)
    {
        StartupItemStore.Validate(original);
        if (original.Scope is not (StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
            || !Path.IsPathFullyQualified(path) || Path.GetFileName(path) != original.Name)
            throw new IOException("快捷方式恢复目标无效。");
        StartupItemStore.RejectLinks(path);
        var folder = Path.GetDirectoryName(path)!; Directory.CreateDirectory(folder); StartupItemStore.RejectLinks(folder);
        var security = new FileSecurity(); security.SetSecurityDescriptorSddlForm(original.SecurityDescriptor, AccessControlSections.Access);
        // 持有新文件句柄且拒绝删除；先还原来源标记和DACL，再写入可用的快捷方式内容。
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
        {
            if (original.ZoneIdentifier is not null)
                using (var zone = new FileStream(path + ":Zone.Identifier", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { zone.Write(Convert.FromBase64String(original.ZoneIdentifier)); zone.Flush(true); }
            new FileInfo(path).SetAccessControl(security);
            file.Write(Convert.FromBase64String(original.Data)); file.Flush(true);
        }
        File.SetCreationTimeUtc(path, new DateTime(original.CreatedUtcTicks, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(path, new DateTime(original.WrittenUtcTicks, DateTimeKind.Utc));
        File.SetAttributes(path, (FileAttributes)original.Attributes);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);
}
