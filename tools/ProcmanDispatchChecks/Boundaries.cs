using DeltaNFD.Backends;

// Only the transport, settings and logging boundaries are fakes. The entire production
// BackendProcmanService and its contracts are compiled unchanged into this test.
namespace DeltaNFD.Services
{
    internal static class Log
    {
        internal static void Warn(string text) { }
        internal static void Error(string text, Exception error) { }
    }
    internal static class OfflineModeGuard { internal static bool BlocksNormalAutomation => false; }
    internal static class AppDataPaths
    {
        internal static string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    }
    internal sealed class Settings
    {
        internal bool FrameModeActive { get; set; }
        internal string[] TimerIgnoreExtraProcesses { get; set; } = [];
    }
    internal static class AppSettingsStore
    {
        internal static Settings Current { get; } = new();
        internal static Settings Read() => Current;
    }
}

namespace DeltaNFD.Services.Backends
{
    internal sealed class BackendClient
    {
        internal bool IsUsable { get; set; } = true;
        internal event Action<BackendStatus>? StatusReceived;
        internal void Publish(ProcmanStatusSnapshot snapshot) => StatusReceived?.Invoke(new("idle", null,
            System.Text.Json.JsonSerializer.SerializeToElement(snapshot, BackendJson.SerializerOptions)));
    }
    internal sealed class BackendCaller
    {
        internal BackendCaller(string module)
        {
            if (module != BackendModules.Procman) throw new Exception("Wrong transport module");
        }
        internal static BackendClient Client { get; set; } = new();
        internal static Func<string, object, CancellationToken, Task<BackendResult>> Dispatch { get; set; } =
            (_, _, _) => throw new Exception("No fake transport configured");
        internal static List<(string Command, object Args, CancellationToken Token)> Calls { get; } = [];
        internal BackendClient SharedClient => Client;
        internal Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken token)
        {
            if (progress is not null) throw new Exception("Unexpected progress receiver");
            Calls.Add((command, args, token));
            return Dispatch(command, args, token);
        }
    }
    internal static class BackendResults
    {
        internal static T RequireData<T>(BackendResult result, string what) where T : notnull =>
            result.Data is { } data ? BackendJson.Deserialize<T>(data) : throw new Exception(what);
        internal static OperationResult Map(BackendResult result, string what) =>
            result.Status == BackendResultStatuses.Ok ? OperationResult.Ok(result.Message) : OperationResult.Fail(what);
    }
}
