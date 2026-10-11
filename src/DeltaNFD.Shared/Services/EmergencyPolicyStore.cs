namespace DeltaNFD.Services;

/// <summary>每个通道保留最高序号的原始签名包，过期后也不删除防重放依据。</summary>
public sealed class EmergencyPolicyStore(string directory, IReadOnlyDictionary<string, string> keys)
{
    public IReadOnlyList<EmergencyPolicy> ActiveFor(bool enabled, UpdateChannel channel, Version version, DateTimeOffset now)
    {
        if (!enabled) return [];
        var active = new List<EmergencyPolicy>();
        foreach (var slot in channel == UpdateChannel.Internal ? new[] { "public", "internal" } : new[] { "public" })
        {
            try
            {
                var policy = Read(slot, now);
                if (policy is not null && EmergencyPolicyVerifier.Applies(policy, version, now)) active.Add(policy);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException or FormatException or ArgumentException)
            {
                // 保留签名包与序号依据；无效/过期内容不进入执行决策。
            }
        }
        return active;
    }

    public EmergencyPolicy? Read(string channel, DateTimeOffset now, bool fresh = true)
    {
        var path = PolicyPath(channel);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > EmergencyPolicyVerifier.MaximumEnvelopeBytes) throw new InvalidDataException("策略缓存过大。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var policy = EmergencyPolicyVerifier.Verify(bytes, keys, now, fresh);
        if (policy.Channel != channel) throw new InvalidDataException("策略缓存通道不符。");
        return policy;
    }

    public EmergencyPolicy Accept(byte[] bytes, string expectedChannel, DateTimeOffset now)
    {
        var policy = EmergencyPolicyVerifier.Verify(bytes, keys, now);
        if (policy.Channel != expectedChannel) throw new InvalidDataException("策略下发通道不符。");
        Directory.CreateDirectory(directory);
        // 同一用户的多个实例不能同时覆盖序号。读取损坏缓存时拒绝覆盖，保留诊断证据。
        using var gate = new FileStream(Path.Combine(directory, "write.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var previous = Read(expectedChannel, now, fresh: false);
        if (previous is not null && policy.Sequence < previous.Sequence)
            throw new InvalidDataException("拒绝重放旧序号策略。");
        if (previous is not null && policy.Sequence == previous.Sequence)
        {
            // 同序号不同内容不可覆盖；相同包重试是幂等操作。
            if (!File.ReadAllBytes(PolicyPath(expectedChannel)).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("同序号策略内容发生变化。");
            return previous;
        }
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, PolicyPath(expectedChannel), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return policy;
    }

    private string PolicyPath(string channel) => channel is "public" or "internal"
        ? Path.Combine(directory, channel + ".json") : throw new ArgumentException("未知策略通道。");
}

/// <summary>前后端共同读取已签名缓存，每次决策重新检查通道、有效期和版本。</summary>
public static class EmergencyPolicies
{
    public static string DirectoryPath => Path.Combine(AppDataPaths.Root, "emergency-policies");
    public static EmergencyPolicyStore Store => new(DirectoryPath, EmergencyPolicyTrust.Keys);
    public static IReadOnlyList<EmergencyPolicy> Active()
    {
        var settings = AppSettingsStore.Read();
        var now = DateTimeOffset.UtcNow;
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
            ?? typeof(EmergencyPolicies).Assembly.GetName().Version ?? new Version(0, 0);
        return Store.ActiveFor(settings.EmergencyPolicyEnabled, settings.UpdateChannel, version, now);
    }

    public static EmergencyPolicyDecision Current => new(Active());
    public static string? BlockReason(string command) => Current.BlockReason(command);
    public static int DwmDelaySeconds => Current.DwmDelaySeconds;
    public static string Status => !AppSettingsStore.Read().EmergencyPolicyEnabled ? "紧急策略已关闭，使用随程序发布的算法。"
        : Active() is { Count: > 0 } policies ? string.Join("；", policies.Select(p => $"{p.Id} #{p.Sequence}：{p.Message}（到期 {p.ExpiresUtc.LocalDateTime:g}）"))
        : "暂无适用于当前版本与通道的有效紧急策略，使用随程序发布的算法。";
}

/// <summary>一次算法决策使用同一份已验证快照；多通道合并采用更保守的限制。</summary>
public sealed class EmergencyPolicyDecision(IReadOnlyList<EmergencyPolicy> policies)
{
    public string? BlockReason(string command)
    {
        if (!EmergencyPolicyVerifier.CanBlock(command)) return null;
        var p = policies.LastOrDefault(p => p.BlockedCommands.Contains(command, StringComparer.Ordinal));
        return p is null ? null : $"紧急策略 {p.Id}（#{p.Sequence}）暂时停用了此操作：{p.Message}";
    }

    public int MemoryCooldownSeconds => policies.Select(p => p.MemoryCleanCooldownSeconds ?? 600).DefaultIfEmpty(600).Max();
    public int MemoryThreshold(int userValue) => Math.Max(userValue,
        policies.Select(p => p.MemoryCleanThresholdPercent ?? 30).DefaultIfEmpty(30).Max());
    public int DwmDelaySeconds => policies.Select(p => p.DwmRestartDelaySeconds ?? 5).DefaultIfEmpty(5).Max();
    public bool ManualMemoryCleanOnly => policies.Any(p => p.MemoryCleanMode == "manual-only");
}
