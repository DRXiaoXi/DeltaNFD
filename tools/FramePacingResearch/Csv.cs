using System.Globalization;
using System.Text;

namespace FramePacingResearch;

internal static class Csv
{
    // Streaming RFC4180 reader, including quoted commas/newlines and UTF-8 BOM. Never split raw CSV on ','.
    public static IEnumerable<string[]> Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 256L * 1024 * 1024) throw new InvalidDataException("CSV exceeds 256 MiB.");
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false, afterQuote = false, start = true;
        int rowCount = 0;
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                if (quoted) throw new InvalidDataException("Unclosed CSV quote.");
                if (field.Length > 0 || fields.Count > 0 || afterQuote) { fields.Add(field.ToString()); yield return fields.ToArray(); }
                yield break;
            }
            char c = (char)next;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else { quoted = false; afterQuote = true; }
                }
                else field.Append(c);
            }
            else if (c == ',' || c == '\r' || c == '\n')
            {
                fields.Add(field.ToString()); field.Clear(); start = true; afterQuote = false;
                if (fields.Count > 256) throw new InvalidDataException("Too many CSV columns.");
                if (c != ',')
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    if (++rowCount > 2_000_000) throw new InvalidDataException("CSV exceeds 2 million rows.");
                    yield return fields.ToArray(); fields.Clear();
                }
            }
            else if (c == '"' && start && !afterQuote) { quoted = true; start = false; }
            else
            {
                if (afterQuote || c == '"') throw new InvalidDataException("Malformed CSV quoting.");
                field.Append(c); start = false;
            }
            if (field.Length > 64 * 1024) throw new InvalidDataException("CSV field exceeds 64 KiB.");
        }
    }
    public static double? Number(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
}

internal sealed class Columns(string[] header)
{
    private readonly Dictionary<string, int> indices = Create(header);
    private static Dictionary<string, int> Create(string[] names)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++) if (!result.TryAdd(names[i].Trim(), i)) throw new InvalidDataException("Duplicate CSV header.");
        return result;
    }
    public bool Has(string name) => indices.ContainsKey(name);
    public string Text(string[] row, params string[] names)
    {
        foreach (var name in names) if (indices.TryGetValue(name, out var index) && index < row.Length) return row[index].Trim();
        return "";
    }
    public double? Number(string[] row, params string[] names) => Csv.Number(Text(row, names));
}
