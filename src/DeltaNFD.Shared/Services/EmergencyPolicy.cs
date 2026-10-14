using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

/// <summary>有界策略解释器 v1：只接受程序已实现的能力，不加载远端代码。</summary>
public sealed record EmergencyPolicy
{
    public int SchemaVersion { get; init; }
    public int EngineVersion { get; init; }
    public long Sequence { get; init; }
    public string Id { get; init; } = "";
    // public 对应 Beta/Release；internal 对应 Alpha，内测叠加 public 与 internal。
    public string Channel { get; init; } = "";
    public string MinimumAppVersion { get; init; } = "";
    public string MaximumAppVersion { get; init; } = "";
    public DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string Message { get; init; } = "";
    public string[] BlockedCommands { get; init; } = [];
    public int? MemoryCleanCooldownSeconds { get; init; }
    public int? MemoryCleanThresholdPercent { get; init; }
    public int? DwmRestartDelaySeconds { get; init; }
    public string MemoryCleanMode { get; init; } = "default";
}

public sealed record EmergencyPolicyEnvelope(string KeyId, string Payload, string Signature);

public static class EmergencyPolicyVerifier
{
    public const int MaximumEnvelopeBytes = 64 * 1024;
    public const int MaximumPayloadBytes = 32 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    // 停止、还原、核对、解除防护不允许出现在远程禁用清单。
    public static bool CanBlock(string command) => command is
        BackendCommands.AceClean or BackendCommands.AceCoreClean or BackendCommands.RuntimeRepair or BackendCommands.RuntimeCompatibilityRepair
        or BackendCommands.RuntimeRedistUninstall or BackendCommands.RuntimeGuardEnable or BackendCommands.Vc14Repair or BackendCommands.Vc14LegacyClean
        or BackendCommands.CpuFrameDwmRestart or BackendCommands.CpuFrameMemoryClean
        or BackendCommands.SystemCleanupRun or BackendCommands.SystemCleanupShaderCache or BackendCommands.SystemShaderRecoveryClean;

    public static EmergencyPolicy Verify(ReadOnlySpan<byte> envelopeBytes,
        IReadOnlyDictionary<string, string> keys, DateTimeOffset now, bool requireFresh = true)
    {
        if (envelopeBytes.Length > MaximumEnvelopeBytes) throw new InvalidDataException("策略包过大。");
        RejectDuplicateProperties(envelopeBytes);
        var envelope = JsonSerializer.Deserialize<EmergencyPolicyEnvelope>(envelopeBytes, JsonOptions)
            ?? throw new InvalidDataException("策略包为空。");
        if (envelope.KeyId is null || !keys.TryGetValue(envelope.KeyId, out var key))
            throw new InvalidDataException("策略签名密钥不受信任。");
        var payload = Convert.FromBase64String(envelope.Payload);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidDataException("策略内容过大。");
        using var signer = ECDsa.Create();
        signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out var consumed);
        if (consumed != Convert.FromBase64String(key).Length || signer.KeySize != 256
            || !signer.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("策略签名校验失败。");
        RejectDuplicateProperties(payload);
        var policy = JsonSerializer.Deserialize<EmergencyPolicy>(payload, JsonOptions)
            ?? throw new InvalidDataException("策略内容为空。");
        Validate(policy, now, requireFresh);
        return policy;
    }

    public static void Validate(EmergencyPolicy p, DateTimeOffset now, bool requireFresh = true)
    {
        if (p.SchemaVersion != 1 || p.EngineVersion != 1 || p.Sequence <= 0 || p.Sequence > 9_007_199_254_740_991
            || p.Id is null || p.Id.Length is < 1 or > 80 || p.Channel is not ("public" or "internal"))
            throw new InvalidDataException("策略格式、序号或引擎版本无效。");
        if (!TryVersion(p.MinimumAppVersion, out var min) || !TryVersion(p.MaximumAppVersion, out var max) || min > max)
            throw new InvalidDataException("策略适用版本范围无效。");
        if (p.IssuedUtc == default || p.ExpiresUtc <= p.IssuedUtc || p.ExpiresUtc - p.IssuedUtc > TimeSpan.FromDays(30)
            || p.IssuedUtc > now.AddMinutes(5) || (requireFresh && p.ExpiresUtc <= now))
            throw new InvalidDataException("策略尚未生效、已过期或有效期超过 30 天。");
        if (p.Message is null || p.Message.Length > 1000 || p.BlockedCommands is null || p.BlockedCommands.Length > 16
            || p.BlockedCommands.Any(c => !CanBlock(c)) || p.BlockedCommands.Distinct().Count() != p.BlockedCommands.Length
            || p.MemoryCleanCooldownSeconds is < 600 or > 3600 || p.MemoryCleanThresholdPercent is < 30 or > 95
            || p.DwmRestartDelaySeconds is < 5 or > 120 || p.MemoryCleanMode is not ("default" or "manual-only"))
            throw new InvalidDataException("策略包含未知能力或越界参数。");
    }

    public static bool Applies(EmergencyPolicy policy, Version version, DateTimeOffset now) =>
        policy.IssuedUtc <= now && policy.ExpiresUtc > now
        && TryVersion(policy.MinimumAppVersion, out var min) && TryVersion(policy.MaximumAppVersion, out var max)
        && Normalize(version) >= min && Normalize(version) <= max;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || value.Length > 40 || !Version.TryParse(value, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        Walk(doc.RootElement);
        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("策略存在重复字段。");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Walk(item);
        }
    }
}
