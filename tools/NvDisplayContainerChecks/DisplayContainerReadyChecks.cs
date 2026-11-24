using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class DisplayContainerReadyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string field = "nvDisplayContainerVersion";
        const int version = NvDisplayContainerChange.ProtocolVersion;
        var gpu = new BackendReady(Convert.ToBase64String(new byte[32]), BackendModules.Gpu, "0.9403.0", BackendProtocol.ProtocolMinor,
            1, 1, Path.Combine(Environment.SystemDirectory, "fixture.exe"), new string('a', 64),
            NvSettingsSaveVersion: NvDriverSettingPolicy.SaveProtocolVersion, NvDisplayContainerVersion: version);
        var session = Guid.NewGuid();
        var request = Guid.NewGuid();
        using var stream = new MemoryStream(BackendMessages.EncodeReady(gpu.Module, session, request, gpu));
        var frame = await BackendFrameStream.ReadFrameAsync(stream, CancellationToken.None);
        check(frame is { Type: BackendFrameType.Ready, Module: BackendModules.Gpu } && frame.SessionId == session && frame.RequestId == request,
            "current GPU encoder really sent a ready frame");
        check(frame!.Payload.GetProperty(field).GetInt32() == version, "wire advertises the live capability");
        check(BackendMessages.ParseReady(frame.Payload) == gpu, "production parser accepts the real GPU ready frame and preserves both NVIDIA capabilities");
        var legacy = JsonSerializer.SerializeToElement(gpu with { NvDisplayContainerVersion = 0 }, BackendJson.SerializerOptions);
        check(!legacy.TryGetProperty(field, out _) && BackendMessages.ParseReady(legacy).NvDisplayContainerVersion == 0,
            "missing optional capability keeps legacy GPU compatible but unsupported");

        JsonObject Payload(string module = BackendModules.Gpu)
        {
            var ready = gpu with { Module = module, NvSettingsSaveVersion = module == BackendModules.Gpu ? gpu.NvSettingsSaveVersion : 0 };
            return JsonSerializer.SerializeToNode(ready, BackendJson.SerializerOptions)!.AsObject();
        }
        void Reject(JsonObject payload, string expected)
        {
            try { BackendMessages.ParseReady(JsonSerializer.SerializeToElement(payload)); }
            catch (BackendContractException ex) { check(ex.Message.Contains(expected, StringComparison.Ordinal), "correct rejection: " + ex.Message); return; }
            check(false, "invalid ready was accepted");
        }
        var explicitZero = Payload();
        explicitZero[field] = 0;
        check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(explicitZero)).NvDisplayContainerVersion == 0,
            "explicit zero stays unsupported");
        foreach (var invalid in new[] { "-1", (version + 1).ToString(), "2147483648", "1.5", "\"1\"", "true", "null", "{}", "[]" })
        {
            var payload = Payload();
            payload[field] = JsonNode.Parse(invalid);
            Reject(payload, "显示容器协议版本");
        }
        var nonGpu = Payload(BackendModules.System);
        Reject(nonGpu, "显示容器协议版本");
        nonGpu[field] = NvDisplayContainerChange.FileBackupProtocolVersion;
        check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(nonGpu)).NvDisplayContainerVersion
            == NvDisplayContainerChange.FileBackupProtocolVersion, "system worker advertises independent file backup and recovery");
        nonGpu[field] = NvDisplayContainerChange.TelemetryProtocolVersion;
        check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(nonGpu)).NvDisplayContainerVersion
            == NvDisplayContainerChange.TelemetryProtocolVersion, "system worker advertises independent telemetry service support");
        nonGpu[field] = NvDisplayContainerChange.LocalSystemProtocolVersion;
        check(BackendMessages.ParseReady(JsonSerializer.SerializeToElement(nonGpu)).NvDisplayContainerVersion
            == NvDisplayContainerChange.LocalSystemProtocolVersion, "system worker advertises shared container support");
        nonGpu[field] = NvDisplayContainerChange.LocalSystemProtocolVersion + 1;
        Reject(nonGpu, "显示容器协议版本");
        nonGpu.Remove(field);
        var system = BackendMessages.ParseReady(JsonSerializer.SerializeToElement(nonGpu));
        check(system.Module == BackendModules.System && system.NvDisplayContainerVersion == 0,
            "non-GPU ready without the capability is still compatible");
        var unknown = Payload();
        unknown["unexpectedReadyField"] = true;
        Reject(unknown, "unexpectedReadyField");
    }
}
