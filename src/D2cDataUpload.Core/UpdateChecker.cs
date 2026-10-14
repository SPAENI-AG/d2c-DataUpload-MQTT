using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace D2cDataUpload.Core;

/// <summary>Neueste veröffentlichte Version auf GitHub.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string PageUrl,          // Release-Seite mit Notizen
    string? DownloadUrl,     // ZIP für Windows x64, falls vorhanden
    string Notes,
    DateTimeOffset? Published);

/// <summary>
/// Update-Prüfung über GitHub Releases (öffentliche API, keine Anmeldung).
/// Fragt nur https://api.github.com/repos/{owner}/{repo}/releases/latest ab – es werden keine
/// Nutzungsdaten übertragen. Entwürfe und Vorabversionen werden von GitHub hier nicht geliefert.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Für Tests umlenkbar.</summary>
    public static string ApiBase { get; set; } = "https://api.github.com";

    /// <summary>"Organisation/Repository" aus Directory.Build.props (AssemblyMetadata "GitHubRepository").</summary>
    public static string Repository { get; set; } =
        typeof(UpdateChecker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepository")?.Value ?? "";

    public static bool IsConfigured =>
        Repository.Contains('/') && !Repository.StartsWith("OWNER/", StringComparison.OrdinalIgnoreCase);

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    public static string ReleasePage(Version v) => $"https://github.com/{Repository}/releases/tag/v{Format(v)}";

    /// <summary>"v1.3.0" / "1.3" -> Version; Vorabversionen ("-beta") -> null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        if (t.Contains('-') || t.Contains('+')) return null;
        if (!Version.TryParse(t, out var v)) return null;
        return Normalize(v);
    }

    /// <summary>Fehlende Teile als 0 (1.3 == 1.3.0 == 1.3.0.0).</summary>
    public static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static string Format(Version v) =>
        v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    public static bool IsNewer(Version candidate, Version current) =>
        Normalize(candidate) > Normalize(current);

    /// <summary>Neueste Version holen. Wirft bei Netzwerk-/API-Fehlern.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(Version current, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("d2c_DataUpload_MQTT", Format(current)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await http.GetAsync($"{ApiBase.TrimEnd('/')}/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // noch kein Release
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    public static ReleaseInfo? Parse(JsonElement r)
    {
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var version = ParseTag(tag);
        if (version is null) return null;

        string? download = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var urls = assets.EnumerateArray()
                .Select(a => (Name: a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                              Url: a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null))
                .Where(a => a.Url is not null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();
            download = urls.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)).Url
                       ?? urls.FirstOrDefault().Url;
        }

        DateTimeOffset? published = null;
        if (r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(pa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            published = dto;

        return new ReleaseInfo(
            version,
            tag!,
            r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            download,
            r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            published);
    }

    /// <summary>Soll beim Start geprüft werden? (höchstens alle 20 h, schont das GitHub-Limit von 60 Abfragen/h je IP)</summary>
    public static bool IsDue(AppSettings s, DateTime utcNow) =>
        s.CheckForUpdates && IsConfigured &&
        (s.LastUpdateCheckUtc is null || utcNow - s.LastUpdateCheckUtc.Value > TimeSpan.FromHours(20));
}
