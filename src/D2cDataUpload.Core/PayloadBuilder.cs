using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace D2cDataUpload.Core;

/// <summary>Zeile übersprungen (wird gezählt und geloggt).</summary>
public sealed class RowException(string message) : Exception(message);

/// <summary>Einstellungen passen nicht zur Datei (Abbruch vor dem Senden).</summary>
public sealed class MappingException(string message) : Exception(message);

/// <summary>
/// CSV-Zeile -> (Topic, Payload).
/// Topic:   aus einer Spalte (+ Präfix) oder aus einer Vorlage mit {Spalte} / {row}.
/// Payload: unverändert aus einer Spalte oder JSON aus ausgewählten Spalten.
/// </summary>
public sealed partial class PayloadBuilder
{
    public static readonly string[] TopicHints = ["topic", "path", "pfad", "mqtt_topic", "topicpath", "kanal", "channel"];
    public static readonly string[] PayloadHints = ["payload", "message", "msg", "nachricht", "data", "daten", "value", "json"];

    [GeneratedRegex(@"\{([^{}]+)\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"^[+-]?\d+$")] private static partial Regex IntRx();
    [GeneratedRegex(@"^[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?$")] private static partial Regex FloatRx();

    private static readonly string[] FallbackTsFormats =
        ["dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm:ss.fff", "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "yyyy/MM/dd HH:mm:ss",
         "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppSettings _s;
    private readonly string[] _header;
    private readonly Dictionary<string, int> _col;
    private readonly HashSet<string> _excluded;
    private readonly JsonObject? _static;
    private readonly TimeZoneInfo _tz;

    public PayloadBuilder(AppSettings s, IReadOnlyList<string> header)
    {
        _s = s;
        _header = header.ToArray();
        _col = new Dictionary<string, int>();
        for (int i = 0; i < _header.Length; i++) _col.TryAdd(_header[i], i);
        _excluded = new HashSet<string>(s.ExcludeColumns.Select(c => c.Trim()));

        if (s.TopicMode == "column")
        {
            if (string.IsNullOrEmpty(s.TopicColumn)) throw new MappingException(Loc.T("Err_NoTopicColumn"));
            if (!_col.ContainsKey(s.TopicColumn)) throw new MappingException(Loc.T("Err_TopicColumnMissing", s.TopicColumn));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(s.TopicTemplate)) throw new MappingException(Loc.T("Err_TemplateEmpty"));
            var missing = Placeholder().Matches(s.TopicTemplate).Select(m => m.Groups[1].Value)
                .Where(k => k != "row" && !_col.ContainsKey(k)).ToList();
            if (missing.Count > 0) throw new MappingException(Loc.T("Err_PlaceholderMissing", string.Join(", ", missing)));
        }

        if (s.PayloadMode == "column")
        {
            if (string.IsNullOrEmpty(s.PayloadColumn)) throw new MappingException(Loc.T("Err_NoPayloadColumn"));
            if (!_col.ContainsKey(s.PayloadColumn)) throw new MappingException(Loc.T("Err_PayloadColumnMissing", s.PayloadColumn));
        }
        else if (!string.IsNullOrEmpty(s.TsColumn) && !_col.ContainsKey(s.TsColumn))
        {
            throw new MappingException(Loc.T("Err_TsColumnMissing", s.TsColumn));
        }

        if (!string.IsNullOrWhiteSpace(s.StaticFields))
        {
            try { _static = JsonNode.Parse(s.StaticFields) as JsonObject; }
            catch (JsonException) { _static = null; }
            if (_static is null) throw new MappingException(Loc.T("Err_StaticFields", "{\"quelle\": \"nachlieferung\"}"));
        }

        try { _tz = string.IsNullOrWhiteSpace(s.TsTimezone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(s.TsTimezone); }
        catch (TimeZoneNotFoundException) { throw new MappingException(Loc.T("Err_Timezone", s.TsTimezone)); }
    }

    public static string GuessColumn(IReadOnlyList<string> header, string[] hints)
    {
        foreach (var hint in hints)
            foreach (var h in header)
                if (string.Equals(h.Trim(), hint, StringComparison.OrdinalIgnoreCase)) return h;
        foreach (var h in header)
            if (hints.Any(hint => h.Contains(hint, StringComparison.OrdinalIgnoreCase))) return h;
        return "";
    }

    // ------------------------------------------------------------------
    public (string Topic, byte[] Payload) Build(long idx, string[] row)
    {
        if (row.Length != _header.Length)
        {
            if (row.Length < _header.Length)
                row = [.. row, .. Enumerable.Repeat("", _header.Length - row.Length)];
            else if (row.Skip(_header.Length).Any(c => !string.IsNullOrWhiteSpace(c)))
                throw new RowException(Loc.T("Err_ColumnCount", row.Length, _header.Length));
        }

        string Cell(string name) => _col.TryGetValue(name, out var i) ? row[i] : "";

        var topic = MakeTopic(idx, Cell);

        if (_s.PayloadMode == "column")
        {
            var text = Cell(_s.PayloadColumn).Trim();
            if (text.Length == 0) throw new RowException(Loc.T("Err_PayloadEmpty"));
            if (_s.ValidateJson)
            {
                try { using var _ = JsonDocument.Parse(text); }
                catch (JsonException e) { throw new RowException(Loc.T("Err_PayloadInvalidJson", e.Message)); }
            }
            return (topic, Encoding.UTF8.GetBytes(text));
        }

        var obj = new JsonObject();
        if (_static is not null)
            foreach (var kv in _static) obj[kv.Key] = kv.Value?.DeepClone();
        for (int i = 0; i < _header.Length; i++)
        {
            var k = _header[i];
            if (k.Length == 0 || _excluded.Contains(k)) continue;
            if (k == _s.TopicColumn && _s.TopicMode == "column") continue;
            if (k == _s.TsColumn)
            {
                var target = string.IsNullOrWhiteSpace(_s.TsTargetKey) ? k : _s.TsTargetKey.Trim();
                obj[target] = ConvertTs(row[i]);
                continue;
            }
            var val = ConvertValue(row[i]);
            if (val is null)
            {
                if (_s.SkipEmptyFields) continue;
                if (!_s.EmptyAsNull) val = JsonValue.Create("");
            }
            obj[k] = val;
        }
        return (topic, Encoding.UTF8.GetBytes(obj.ToJsonString(JsonOpts)));
    }

    private string MakeTopic(long idx, Func<string, string> cell)
    {
        string topic;
        if (_s.TopicMode == "column")
        {
            var v = cell(_s.TopicColumn).Trim();
            if (v.Length == 0) throw new RowException(Loc.T("Err_TopicEmpty"));
            topic = (_s.TopicPrefix ?? "") + v;
        }
        else
        {
            topic = Placeholder().Replace(_s.TopicTemplate, m =>
            {
                var key = m.Groups[1].Value;
                if (key == "row") return (idx + 1).ToString(CultureInfo.InvariantCulture);
                var v = cell(key).Trim();
                if (v.Length == 0) throw new RowException(Loc.T("Err_TopicFieldEmpty", key));
                return v.Replace('+', '_').Replace('#', '_');
            });
        }
        if (topic.Length == 0 || topic.Contains('+') || topic.Contains('#'))
            throw new RowException(Loc.T("Err_TopicInvalid", topic));
        return topic;
    }

    public JsonNode? ConvertValue(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        if (!_s.ConvertNumbers) return JsonValue.Create(v);
        var num = v.Replace("'", "");
        if (_s.DecimalComma && num.Contains(','))
            num = num.Replace(".", "").Replace(',', '.');
        if (IntRx().IsMatch(num) && long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (FloatRx().IsMatch(num) && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        return JsonValue.Create(v);
    }

    public JsonNode? ConvertTs(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        var fmt = string.IsNullOrWhiteSpace(_s.TsFormat) ? "auto" : _s.TsFormat.Trim();
        DateTimeOffset? dto = null;

        if (fmt == "auto")
        {
            if (FloatRx().IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            {
                dto = x > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)x)
                               : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(x * 1000));
            }
            else if (HasOffset(v) && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                dto = withOffset;
            }
            else if (DateTime.TryParseExact(v, FallbackTsFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                dto = Localize(local);
            }
        }
        else if (DateTime.TryParseExact(v, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var custom))
        {
            dto = Localize(custom);
        }

        if (dto is null) throw new RowException(Loc.T("Err_TsUnreadable", v));
        return _s.TsOutput switch
        {
            "epoch_ms" => JsonValue.Create(dto.Value.ToUnixTimeMilliseconds()),
            "epoch_s" => JsonValue.Create(dto.Value.ToUnixTimeSeconds()),
            "iso" => JsonValue.Create(dto.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFzzz", CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(v),
        };
    }

    private static bool HasOffset(string v) =>
        v.EndsWith('Z') || (v.Length > 19 && (v[^6] == '+' || v[^6] == '-') && v[^3] == ':');

    private DateTimeOffset Localize(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, _tz.GetUtcOffset(unspecified));
    }
}
