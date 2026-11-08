using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;

internal static class Vc14AlertPolicyChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        Check((int)Vc14Severity.Normal == 0 && (int)Vc14Severity.Warning == 1 && (int)Vc14Severity.Abnormal == 2
            && (int)Vc14Severity.Evaluation == 3, "existing IPC severity values retain their identities");
        foreach (var arch in new[] { "x86", "x64" })
        foreach (var version in new[] { "14.50.35719", "14.51.36247", "14.44.35211" })
        {
            var brand = version.StartsWith("14.44") ? "v14" : "2022";
            var p = new Vc14Entry(Guid.NewGuid().ToString("B"), $"Microsoft Visual C++ {brand} {arch} Minimum Runtime - {version}", version, arch, "Minimum", "{U}");
            var severity = Vc14CatalogInspection.Classify(p.Name, p.Version);
            var scan = new Vc14Scan([p], [new(p.Name, p.Version, arch, severity, "fixture")], []);
            Check(severity == Vc14Severity.Evaluation && scan.HasEvaluation && !scan.HasAbnormal && !scan.HasWarning,
                "V14/14.50/14.51 exception remains blue: " + arch + version);
            Check(DashboardHealthPresentation.Runtime(scan) == DashboardHealthState.Information, "homepage evaluation stays blue");
            var received = JsonSerializer.Deserialize<Vc14Scan>(JsonSerializer.Serialize(scan))!;
            Check(received.HasEvaluation && !received.HasAbnormal, "evaluation survives backend serialization");
            var issue = scan with { Issues = ["缓存版本不一致，读取待核对"] };
            Check(!issue.HasAbnormal && !issue.HasVerificationFailure && issue.HasWarning && !issue.IsMissing
                && !issue.GetAbnormalNotices().Any(), "scan issues return to old yellow policy, not red or missing");
            Check(DashboardHealthPresentation.Runtime(issue) == DashboardHealthState.Warning, "yellow issue outranks blue on homepage");
            Check(DashboardHealthPresentation.Runtime(scan, true) == DashboardHealthState.Warning, "old guard warnings are retained");
            Check(Vc14CatalogInspection.Classify(p.Name, version + ".0", false, [p]) == Vc14Severity.Evaluation,
                "associated evaluated wrapper remains blue with equivalent three/four-part version");
            Check(Vc14CatalogInspection.Classify(p.Name, version, false, []) == Vc14Severity.Warning,
                "unverified wrapper returns to old yellow policy");
            Check(Vc14CatalogInspection.Classify(p.Name, "14.49.1", false, [p]) == Vc14Severity.Warning,
                "same-name wrapper mismatch returns to yellow instead of strict red");
            Check(Vc14Catalog.Fingerprint(scan) == Vc14Catalog.Fingerprint(issue), "display policy does not alter execution identity fingerprint");
        }
        foreach (var (version, expected) in new[]
        {
            ("14.40.33810", Vc14Severity.Warning), ("14.42.34433", Vc14Severity.Warning),
            ("14.44.35211", Vc14Severity.Normal), ("14.44", Vc14Severity.Normal),
            ("14.49.1", Vc14Severity.Warning), ("14.500.1", Vc14Severity.Warning),
            ("unknown", Vc14Severity.Warning), ("", Vc14Severity.Warning), ("15.0.1", Vc14Severity.Warning),
        })
        {
            Check(Vc14CatalogInspection.Classify("Microsoft Visual C++ 2022", version) == expected,
                "ordinary version follows old policy: " + version);
            Check(Vc14Catalog.Classify("Microsoft Visual C++ 2022", version) == expected, "execution facade agrees with inspection");
        }
        var ordinary = new Vc14Entry("{A}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.44.35211", "14.44.35211", "x64", "Minimum", "{U}");
        Check(Vc14CatalogInspection.Classify("Microsoft Visual C++ 2022 Redistributable (x64)", ordinary.Version, false, [ordinary]) == Vc14Severity.Normal,
            "associated ordinary wrapper stays normal");
        Check(Vc14CatalogInspection.Classify("Visual C++ Library CRT Desktop Appx Package", "14.51.1", false, []) == Vc14Severity.Normal,
            "Windows Appx notice remains outside Win32 warnings");
        var missing = new Vc14Scan([], [], []);
        Check(missing.IsMissing && missing.HasAbnormal && missing.GetAbnormalNotices().Single().Name.Contains("未检测到"),
            "completed empty scan retains old red missing diagnosis");
        var incomplete = missing with { Issues = ["MSI 读取失败"] };
        Check(!incomplete.IsMissing && !incomplete.HasAbnormal && incomplete.HasWarning,
            "incomplete scan does not claim missing or normal");
        var malformed = ordinary with { Version = "unknown" };
        var rejected = false;
        try { Vc14Catalog.Validate(malformed, malformed.Code, malformed.Name, malformed.Version, "Microsoft Corporation", "x64", "{U}"); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "relaxed display classification does not relax repair MSI identity validation");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeltaNFD.sln"))) root = root.Parent;
        var xml = XDocument.Load(Path.Combine(root!.FullName, "src/DeltaNFD/Views/RuntimePage.xaml"));
        var bars = xml.Descendants().Where(e => e.Name.LocalName == "InfoBar").ToArray();
        var red = bars.Single(e => ((string?)e.Attribute("IsOpen"))?.Contains("HasAbnormal") == true);
        var blue = bars.Single(e => ((string?)e.Attribute("IsOpen"))?.Contains("HasEvaluation") == true);
        Check((string?)red.Attribute("Severity") == "Error" && Array.IndexOf(bars, red) == 0, "actual abnormal/missing notices retain error presentation");
        Check((string?)blue.Attribute("Severity") == "Informational" && ((string?)blue.Attribute("Title"))!.Contains("V14 / 14.50 / 14.51"),
            "requested exceptions retain their informational InfoBar");
        Console.WriteLine($"VC14 rollback policy: {count} checks passed; synthetic metadata and source only, no real runtime changes.");
    }
}
