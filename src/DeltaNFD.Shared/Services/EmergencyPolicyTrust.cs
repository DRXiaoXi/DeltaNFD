namespace DeltaNFD.Services;

/// <summary>发布者公钥随程序编译固定；网络配置和策略包不能添加信任密钥。</summary>
public static class EmergencyPolicyTrust
{
    public const string KeyId = "emergency-2026-01";
    public static IReadOnlyDictionary<string, string> Keys { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            [KeyId] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJxUGfFJBa8rMdCQsPwu/NBY35Jwg8TpBRIqwBMbqpsyVf36Bwz1+HxiVYINa78063X32rg2jFY8o/h+f8tfeiw==",
        });
}
