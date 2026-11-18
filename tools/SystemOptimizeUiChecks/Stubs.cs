using System.Reflection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

public class FakeProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler;
    public static T CreateFake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    { var value = Create<T, FakeProxy>(); ((FakeProxy)(object)value).Handler = handler; return value; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Handler is not null) return Handler(method!, args);
        return Default(method!, args);
    }
    public static object? Default(MethodInfo method, object?[]? args)
    {
        if (method.Name == "GetBackupStatusAsync") return Task.FromResult(new BxBackupStatus { Readable = true });
        if (method.Name == "GetGearInfoAsync") return Task.FromResult<BxGearInfo?>(null);
        if (method.Name == "GetItemStatesAsync") return Task.FromResult(((IReadOnlyList<BxItem>)args![0]!).ToDictionary(i => i, _ => BxState.Off));
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = method.ReturnType.GenericTypeArguments[0];
            object? value = inner == typeof(OperationResult) ? OperationResult.Ok("fake")
                : inner.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(inner)
                : inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        if (method.ReturnType == typeof(bool)) return false;
        throw new InvalidOperationException("Unconfigured fake: " + method.Name);
    }
}
namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue
    {
        public static DispatcherQueue? GetForCurrentThread() => null;
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
namespace DeltaNFD.Services
{
    internal static class ServiceLocator
    {
        internal static IBxService Bx = FakeProxy.CreateFake<IBxService>();
        internal static ISystemTweakService SystemTweaks = FakeProxy.CreateFake<ISystemTweakService>();
        internal static IPagefileService Pagefile = FakeProxy.CreateFake<IPagefileService>();
        internal static IDefenderGuardService DefenderGuard = FakeProxy.CreateFake<IDefenderGuardService>();
        internal static IAdvancedTweakService AdvancedTweaks = FakeProxy.CreateFake<IAdvancedTweakService>();
    }
    internal record DriveReport(char Letter, bool IsMechanical = false, bool IsGameDrive = false, bool IsSystemDrive = true, bool IsSolidState = true, string MediaLabel = "SSD");
    internal static class SystemInspectionCache
    { internal static Task<List<DriveReport>> Drives() => Task.FromResult(new List<DriveReport> { new('C') }); }
    internal static class OptimizationScan
    {
        internal static event Action? Changed { add {} remove {} }
        internal static bool HasData => false;
        internal static bool TryGetItemState(string section, string name, out BxState value) { value = BxState.Unknown; return false; }
        internal static bool TryGetGroupState(string id, out BxState value) { value = BxState.Unknown; return false; }
        internal static bool TryGetTweakStatus(SystemTweak tweak, out TweakStatus? value) { value = null; return false; }
        internal static bool Uncertain(TweakStatus status) => !status.StateKnown;
        internal static void SetItemState(string section, string name, BxState state) {}
        internal static void SetGroupState(string id, BxState state) {}
        internal static void SetTweakState(SystemTweak tweak, bool state) {}
        internal static void Invalidate() {}
    }
}
namespace DeltaNFD.Services.Backends
{
    internal static class PageBackendSessions
    { internal static object? ConstructionOwner => null; internal static string? SystemWriteBlocker => null; }
    internal sealed class BackendCaller
    { internal BackendCaller(string module, object? owner) {} internal Task<object> InvokeAsync(string command, object args, object? progress, CancellationToken token) => throw new Exception("Unexpected real IPC path"); }
    internal static class BackendResults
    { internal static OperationResult Map(object result, string what) => throw new Exception("Unexpected real IPC path"); }
}
