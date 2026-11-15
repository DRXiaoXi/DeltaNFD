using System.Globalization;

namespace DeltaNFD.Services;

internal static class ClockValueInput
{
    internal static bool TryParse(string text, bool windows10, out uint ticks)
    {
        ticks = 0;
        var trimmed = text.Trim(); var point = trimmed.IndexOf('.');
        if (point >= 0 && trimmed.Length - point - 1 > 4) return false;
        if (text.Length > 24 || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var ms) || ms < 0.5m || ms > (windows10 ? 1m : 15.625m)) return false;
        var value = ms * 10000m;
        if (value != decimal.Truncate(value)) return false;
        ticks = (uint)value; return true;
    }
}
