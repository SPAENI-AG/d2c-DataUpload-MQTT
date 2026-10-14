using System.Text;

namespace D2cDataUpload.Core;

/// <summary>
/// CSV-Datei streamend lesen (RFC 4180: Anführungszeichen, "" als Escape, Zeilenumbrüche in Feldern).
/// Die Datei wird nie komplett in den Speicher geladen.
/// </summary>
public sealed class CsvSource
{
    static CsvSource() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Path { get; }
    public char Delimiter { get; }
    public Encoding Encoding { get; }
    public IReadOnlyList<string> Header { get; }

    public CsvSource(string path, string delimiterSetting = "auto", string encodingName = "utf-8")
    {
        Path = path;
        Encoding = GetEncoding(encodingName);
        Delimiter = ResolveDelimiter(delimiterSetting);
        using var r = OpenReader();
        Header = (ReadRecord(r, Delimiter) ?? []).Select(h => h.Trim()).ToList();
    }

    public static Encoding GetEncoding(string name) => name.ToLowerInvariant() switch
    {
        "windows-1252" or "cp1252" => System.Text.Encoding.GetEncoding(1252),
        "latin1" or "latin-1" or "iso-8859-1" => System.Text.Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };

    private StreamReader OpenReader() =>
        new(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16),
            Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

    private char ResolveDelimiter(string setting)
    {
        switch (setting)
        {
            case ";": return ';';
            case ",": return ',';
            case "|": return '|';
            case "tab": case "\t": return '\t';
        }
        using var r = OpenReader();
        var head = r.ReadLine() ?? "";
        char best = ',';
        int bestCount = 0;
        foreach (var d in new[] { ';', ',', '\t', '|' })
        {
            var c = head.Count(ch => ch == d);
            if (c > bestCount) { best = d; bestCount = c; }
        }
        return best;
    }

    public string DelimiterDisplay => Delimiter == '\t' ? "TAB" : Delimiter.ToString();

    /// <summary>Liefert (Index, Zellen) ab Datenzeile <paramref name="start"/> (0-basiert). Leere Zeilen werden übersprungen.</summary>
    public IEnumerable<(long Index, string[] Cells)> Rows(long start = 0, CancellationToken ct = default)
    {
        using var r = OpenReader();
        ReadRecord(r, Delimiter); // Kopfzeile
        long idx = 0;
        while (true)
        {
            var rec = ReadRecord(r, Delimiter);
            if (rec is null) yield break;
            if (IsEmpty(rec)) continue;
            if (idx >= start) yield return (idx, rec);
            idx++;
            if ((idx & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
        }
    }

    public long CountRows(CancellationToken ct = default)
    {
        long n = 0;
        foreach (var _ in Rows(0, ct)) n++;
        return n;
    }

    public List<string[]> Preview(int n = 20) => Rows().Take(n).Select(r => r.Cells).ToList();

    private static bool IsEmpty(string[] rec)
    {
        foreach (var c in rec)
            if (!string.IsNullOrWhiteSpace(c)) return false;
        return true;
    }

    /// <summary>Liest einen Datensatz; null am Dateiende.</summary>
    public static string[]? ReadRecord(TextReader r, char delim)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        while (true)
        {
            int ci = r.Read();
            if (ci < 0)
            {
                if (!any) return null;
                fields.Add(sb.ToString());
                return fields.ToArray();
            }
            any = true;
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (r.Peek() == '"') { r.Read(); sb.Append('"'); }
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }
            if (c == '"' && sb.Length == 0) { inQuotes = true; continue; }
            if (c == delim) { fields.Add(sb.ToString()); sb.Clear(); continue; }
            if (c == '\r') { if (r.Peek() == '\n') r.Read(); fields.Add(sb.ToString()); return fields.ToArray(); }
            if (c == '\n') { fields.Add(sb.ToString()); return fields.ToArray(); }
            sb.Append(c);
        }
    }
}
