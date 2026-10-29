using DeltaNFD.Inspection;

internal static class NvidiaDriverSeriesChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("Driver series check: " + name);
            checks++;
        }
        var nvidia = "PCI\\VEN_10DE&DEV_1234";
        var amd = "PCI\\VEN_1002&DEV_1234";
        DisplayDriverSample Sample(string? version) => new("NVIDIA GPU", nvidia, version);
        foreach (var (raw, expected) in new[]
        {
            ("32.0.15.9600", 596), ("32.0.15.9649", 596), ("32.0.15.9699", 596),
            ("32.0.15.9599", 595), ("32.0.15.9700", 597), ("32.0.16.2101", 621)
        })
        {
            Check(NvidiaDriverSeriesInspection.TryGetNvidiaSeries(raw, out var actual) && actual == expected,
                "parse " + raw);
            var status = NvidiaDriverSeriesInspection.Evaluate([Sample(raw)], true);
            Check(status.Has596Series == (expected == 596) && !status.DriverUnknown, "evaluate " + raw);
        }
        foreach (var raw in new string?[] { null, "", "596.49", "bad", "32.0.15", "32.0.15.10000" })
            Check(!NvidiaDriverSeriesInspection.TryGetNvidiaSeries(raw, out _), "reject unreadable version " + raw);
        Check(NvidiaDriverSeriesInspection.Evaluate([Sample(null)], true) == new NvidiaDriverSeriesStatus(false, true),
            "unreadable NVIDIA driver remains unknown");
        Check(NvidiaDriverSeriesInspection.Evaluate([], true) == new NvidiaDriverSeriesStatus(false, true),
            "empty adapter enumeration remains unknown");
        Check(NvidiaDriverSeriesInspection.Evaluate([new("AMD GPU", amd, "32.0.15.9649")], true)
            == new NvidiaDriverSeriesStatus(false, false), "non-NVIDIA adapter is not a match");
        Check(NvidiaDriverSeriesInspection.Evaluate([new("NVIDIA in misleading name", amd, "32.0.15.9649")], true)
            == new NvidiaDriverSeriesStatus(false, false), "explicit non-NVIDIA vendor takes precedence");
        Check(NvidiaDriverSeriesInspection.Evaluate([Sample("32.0.16.2101"), Sample("32.0.15.9649")], true)
            == new NvidiaDriverSeriesStatus(true, false), "any matching NVIDIA adapter is identified without selecting a runtime baseline");
        Check(NvidiaDriverSeriesInspection.Evaluate([Sample("32.0.15.9649"), Sample(null)], false)
            == new NvidiaDriverSeriesStatus(true, false), "confirmed match survives incomplete enumeration");
        Check(NvidiaDriverSeriesInspection.Evaluate([Sample("32.0.16.2101"), Sample(null)], true)
            == new NvidiaDriverSeriesStatus(false, true), "unreadable second NVIDIA adapter remains unknown");
        Check(NvidiaDriverSeriesInspection.Evaluate([new(null, null, "32.0.15.9649")], true)
            == new NvidiaDriverSeriesStatus(false, true), "unidentified adapter does not imply NVIDIA");
        Console.WriteLine($"NVIDIA driver series checks passed ({checks}); no system writes.");
    }
}
