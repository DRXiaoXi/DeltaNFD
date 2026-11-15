using System.Text.Json;
using DeltaNFD.Services;
using Microsoft.Win32;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeBundleProviderChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        bool Refused(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        RuntimeRegistrationSnapshot Snapshot(string provider, object? identity, int view, bool hasIdentity = true,
            RegistryValueKind kind = RegistryValueKind.String)
        {
            var values = new Dictionary<string, object>();
            if (hasIdentity) values[""] = new { Kind = kind, Data = identity };
            values["Version"] = new { Kind = RegistryValueKind.String, Data = "11.0.61030.0" };
            return new(@"SOFTWARE\Classes\Installer\Dependencies\" + provider, view,
                JsonSerializer.Serialize(new { Values = values, Children = new { } }));
        }

        // Bundle IDs from the two reported logs; provider snapshots model WiX's documented optional Id.
        foreach (var bundle in new[] { "{33d1fd90-4274-48a1-9bc1-97e33d9c2d6f}", "{042d26ef-3dbe-4c25-95d3-4c1b11b235a7}" })
        foreach (var view in new[] { 256, 512 })
        {
            foreach (var (identity, exists) in new[] { ((string?)null, false), ("", true), (bundle.ToUpperInvariant(), true) })
            {
                RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(bundle, bundle, Snapshot(bundle, identity, view, exists));
                Check(true, "旧 VC++ 2012/2013 Bundle：缺失、空或显式身份均匹配，视图=" + view);
            }
            var other = "{11111111-1111-1111-1111-111111111111}";
            Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(bundle, bundle, Snapshot(bundle, other, view))),
                "显式外部身份不能被同名 provider 回退掩盖");
            Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(bundle, other, Snapshot(other, null, view, false))),
                "缺失身份不允许回退到其他 Bundle 的 GUID");
            foreach (var malformed in new object?[] { 0, null, "unknown", " " })
                Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(bundle, bundle, Snapshot(bundle, malformed, view))),
                    "错误类型、空数据或损坏身份不能被视为缺失");
            Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(bundle, bundle,
                    Snapshot(bundle, bundle, view, kind: RegistryValueKind.MultiString))), "错误注册表值类型不采信");
        }

        const string modern = "{410c0ee1-00bb-41b6-9772-e12c2828b02f}";
        const string provider = "VC,redist.x86,x86,14.36,bundle";
        RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(modern, provider, Snapshot(provider, modern, 512));
        Check(true, "v14 命名 provider 继续使用显式 Bundle 身份");
        foreach (var (identity, exists) in new[] { ((string?)null, false), ("", true) })
            Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(modern, provider,
                Snapshot(provider, identity, 512, exists))), "v14 命名 provider 缺失身份时不猜测归属");
        Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleProviderIdentity(Guid.Empty.ToString("B"),
            Guid.Empty.ToString("B"), Snapshot(Guid.Empty.ToString("B"), "", 512))), "空 GUID 不是可靠 Bundle 身份");
        Console.WriteLine($"Bundle provider identity checks: {count} passed. Registry snapshots simulated; no registry writes.");
        return count;
    }
}
