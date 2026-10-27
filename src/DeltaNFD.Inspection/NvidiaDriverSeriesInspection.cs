using System.Globalization;
using System.Management;

namespace DeltaNFD.Inspection;

/// <summary>A confirmed 596-series adapter hides the 14.44 runtime baseline.</summary>
public readonly record struct NvidiaDriverSeriesStatus(bool Has596Series, bool DriverUnknown);

internal readonly record struct DisplayDriverSample(string? Name, string? DeviceId, string? Version);

public static class NvidiaDriverSeriesInspection
{
    public static Task<NvidiaDriverSeriesStatus> ReadAsync() => Task.Run(Read);

    internal static NvidiaDriverSeriesStatus Read()
    {
        var samples = new List<DisplayDriverSample>();
        var complete = true;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) });
            using var adapters = searcher.Get();
            foreach (ManagementObject adapter in adapters)
            {
                using (adapter)
                {
                    try
                    {
                        samples.Add(new DisplayDriverSample(adapter["Name"] as string,
                            adapter["PNPDeviceID"] as string, adapter["DriverVersion"] as string));
                    }
                    catch (Exception) { complete = false; }
                }
            }
        }
        catch (Exception) { complete = false; }
        return Evaluate(samples, complete);
    }

    internal static NvidiaDriverSeriesStatus Evaluate(IReadOnlyList<DisplayDriverSample> samples, bool enumerationComplete)
    {
        var complete = enumerationComplete && samples.Count > 0;
        var match = false;
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Name) && string.IsNullOrWhiteSpace(sample.DeviceId))
            { complete = false; continue; }
            if (!ShaderInspection.IsNvidiaAdapter(null, sample.Name, sample.DeviceId)) continue;
            if (!TryGetNvidiaSeries(sample.Version, out var series)) { complete = false; continue; }
            if (series == 596) match = true;
        }
        // A positive match is sufficient even if another adapter was unreadable.
        return new NvidiaDriverSeriesStatus(match, !match && !complete);
    }

    internal static bool TryGetNvidiaSeries(string? raw, out int series)
    {
        series = 0;
        if (!Version.TryParse(raw, out var parsed) || parsed.Build < 0 || parsed.Revision is < 0 or > 9999)
            return false;
        var digits = parsed.Build.ToString(CultureInfo.InvariantCulture)
            + parsed.Revision.ToString("D4", CultureInfo.InvariantCulture);
        return digits.Length >= 5 && int.TryParse(digits.AsSpan(digits.Length - 5, 3),
            NumberStyles.None, CultureInfo.InvariantCulture, out series);
    }
}
