using System.Security.Cryptography;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

internal static class ReleaseRc2Checks
{
    internal static void Run(string source)
    {
        var checks = 0;
        void Check(bool ok, string text) { if (!ok) throw new IOException(text); checks++; }
        source = Path.GetFullPath(source);
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-RC2-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        string Write(string relative)
        {
            var p = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "owned metadata fixture"); return p;
        }
        try
        {
            BxCatalog.ConfigureAssetRoot(Path.Combine(source, "src", "DeltaNFD"));
            var mpo = BxCatalog.FindItem("basic", "DisableMPO");
            Check(mpo is not null && mpo.ValidationError is null, "MPO visible in basic catalog and valid");
            Check(mpo!.Tweaks.Count == 1 && mpo.Tweaks[0].TweakType == "REG", "single DWORD operation");
            var op = mpo.Tweaks[0];
            Check(op.Path == @"HKLM\SOFTWARE\Microsoft\Windows\Dwm" && op.Key == "OverlayTestMode", "NVIDIA documented fixed setting");
            Check(op.Values.Single(v => !v.ValueTypes.Contains("DEFAULT")).Value == "5", "disable writes DWORD 5");
            Check(op.Values.Single(v => v.ValueTypes.Contains("DEFAULT")).Value == "Null", "default removes override");
            Check(BxCatalog.Describe("DisableMPO").Item1.Contains("禁用 MPO"), "localized display name");
            Check(BxCatalog.Describe("DisableMPO").Item2.Contains("重启"), "restart requirement visible");
            Check(BxCatalog.Describe("DisableMPO").Item2.Contains("不保证"), "no FPS guarantee");
            Check(BxService.MpoValueMatches(5, RegistryValueKind.DWord, true), "correct disabled readback");
            Check(!BxService.MpoValueMatches(0, RegistryValueKind.DWord, true), "wrong number rejected");
            Check(!BxService.MpoValueMatches("5", RegistryValueKind.String, true), "string five not DWORD");
            Check(!BxService.MpoValueMatches(5L, RegistryValueKind.QWord, true), "QWORD not DWORD");
            Check(!BxService.MpoValueMatches(null, null, true), "missing value not disabled");
            Check(BxService.MpoValueMatches(null, null, false), "absent override is default");
            Check(!BxService.MpoValueMatches(0, RegistryValueKind.DWord, false), "explicit zero not restored default");
            var shared = Path.Combine(root, "shared");
            var core = Write(@"shared\ACE-CORE-fixture.sys");
            var manual = Path.Combine(root, "ManualGame");
            var manualFile = Write(@"ManualGame\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            var automatic = Path.Combine(root, "WeGameApps", "rail_apps", "DeltaForce(2001918)");
            Directory.CreateDirectory(Path.Combine(automatic, "DeltaForce", "Content", "Paks"));
            Write(@"WeGameApps\rail_apps\DeltaForce(2001918)\DeltaForce\Binaries\Win64\AntiCheatExpert\ACE-Service64.exe");
            AppSettingsStore.UpdateRequired(s => { s.GameInstallRoots = [automatic]; s.GameRootOverride = automatic; s.AceGameSearchRoots = [manual]; });
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manualFile)));
            AceCoreCheckResult Inspect() => AceCoreOwnershipInspection.Read(shared, () => false,
                (notes, blocked) => AceDefaultGameInspection.Read(notes, blocked, () => [automatic],
                    () => AppSettingsStore.ReadRequired().AceGameSearchRoots));
            var result = Inspect();
            Check(result.Games.Count == 2 && result.Games.Any(g => g.InstallDirectory == manual), "known Delta is combined with saved ACE manual roots");
            Check(result.ManuallyAssociatedGameCount == 1, "manual game association retained");
            Check(result.Level == AceCoreCheckLevel.Normal && result.SysCount == 1, "CORE not exceeding combined game count remains normal");
            Check(result.Games.Any(g => g.Source == AceDefaultGameInspection.DeltaSource && !g.ManuallyAssociated), "known Delta is an explicit default source");
            AppSettingsStore.UpdateRequired(s => s.AceGameSearchRoots.Clear());
            result = Inspect();
            Check(result.Games.Count == 1 && result.ManuallyAssociatedGameCount == 0, "empty manual list retains known Delta");
            Check(result.Level == AceCoreCheckLevel.Normal && !result.HasExcessCoreFiles, "known Delta accounts for its CORE without a manual bookmark");
            Check(AceCoreOwnershipInspection.Read(shared, () => true).Level == AceCoreCheckLevel.Blocked, "game-running gate unchanged");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manualFile))) == hash && File.Exists(core), "inventory changes no fixture files");
            Check(BackendModules.All.Count == 6 && !BackendModules.All.ContainsKey("shaderstutter"), "six live backend modules; stutter detector removed");
            var app = Path.Combine(source, "src", "DeltaNFD");
            Check(!File.ReadAllText(Path.Combine(app, "MainWindow.xaml")).Contains("shaderstutter"), "no stutter navigation");
            Check(!File.ReadAllText(Path.Combine(app, "DeltaNFD.csproj")).Contains("ShaderStutter"), "no stutter build dependency");
            var ace = File.ReadAllText(Path.Combine(app, "Views", "AcePage.xaml"));
            Check(ace.Contains("默认检测已识别的三角洲") && !ace.Contains("全盘检测"), "known Delta plus manual ACE UI without full disk action");
            Check(AppVersion.NumericText == "0.9316.0" && AppVersion.Text == "Beta0.931-RC3", "new release and display identities");
            Console.WriteLine($"RC2 manual ACE / MPO / retirement: {checks} checks passed; owned fixtures only, no system writes.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe test fixture.");
            Directory.Delete(full, true);
        }
    }
}
