// Testlauf ohne Test-Framework (kein NuGet nötig):
//   dotnet run --project tests/D2cDataUpload.Tests
// Broker-Tests laufen nur, wenn "mosquitto" im PATH ist; TLS-Test nur mit D2C_TLS_CONF (siehe unten).
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2cDataUpload.Core;
using D2cDataUpload.Core.Mqtt;

var root = FindRoot();
AppSettings.FolderOverride = Path.Combine(Path.GetTempPath(), "d2c-tests-" + Environment.ProcessId);
int passed = 0, failed = 0, skipped = 0;

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"  ok    {name}"); }
    catch (SkipException s) { skipped++; Console.WriteLine($"  skip  {name}: {s.Message}"); }
    catch (Exception e) { failed++; Console.WriteLine($"  FAIL  {name}: {e.Message}"); }
}

static void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{expected}> but got <{actual}>");
}
static void True(bool c, string what) { if (!c) throw new Exception(what); }
static void Throws<TEx>(Action a) where TEx : Exception
{
    try { a(); } catch (TEx) { return; }
    throw new Exception($"expected {typeof(TEx).Name}");
}

string[] H = ["Zeit", "device", "Wert", "Text"];
(string, JsonElement) BuildJson(string[] row, Action<AppSettings>? cfg = null)
{
    var s = new AppSettings { TopicMode = "template", TopicTemplate = "d2c/{device}", PayloadMode = "json" };
    cfg?.Invoke(s);
    var (t, p) = new PayloadBuilder(s, H).Build(0, row);
    return (t, JsonDocument.Parse(p).RootElement);
}

string[] H2 = ["timestamp", "topic", "payload", "extra"];
(string, string) BuildCol(string[] row, Action<AppSettings>? cfg = null)
{
    var s = new AppSettings { TopicMode = "column", TopicColumn = "topic", PayloadMode = "column", PayloadColumn = "payload" };
    cfg?.Invoke(s);
    var (t, p) = new PayloadBuilder(s, H2).Build(0, row);
    return (t, Encoding.UTF8.GetString(p));
}

Console.WriteLine("Payload");
Test("JSON: Typen", () =>
{
    var (t, p) = BuildJson(["2026-10-01T12:00:00", "A1", "12.5", "hallo"]);
    Eq("d2c/A1", t);
    Eq(12.5, p.GetProperty("Wert").GetDouble());
    Eq("hallo", p.GetProperty("Text").GetString());
    Eq("2026-10-01T12:00:00", p.GetProperty("Zeit").GetString());
});
Test("JSON: Dezimalkomma, Tausender, leer=null", () =>
{
    var (_, p) = BuildJson(["x", "A", "1.234,5", ""], s => s.DecimalComma = true);
    Eq(1234.5, p.GetProperty("Wert").GetDouble());
    Eq(JsonValueKind.Null, p.GetProperty("Text").ValueKind);
    (_, p) = BuildJson(["x", "A", "1'234", ""]);
    Eq(1234L, p.GetProperty("Wert").GetInt64());
});
Test("JSON: Zeitstempel Europe/Zurich Sommerzeit", () =>
{
    var (_, p) = BuildJson(["01.10.2026 00:00:00", "A", "1", "t"], s => s.TsColumn = "Zeit");
    Eq(1790805600000L, p.GetProperty("Zeit").GetInt64());
});
Test("JSON: Zeitstempel Winterzeit + eigener Feldname", () =>
{
    var (_, p) = BuildJson(["2026-12-01 00:00:00", "A", "1", "t"], s => { s.TsColumn = "Zeit"; s.TsTargetKey = "ts"; });
    Eq(1796079600000L, p.GetProperty("ts").GetInt64()); // 2026-11-30T23:00Z
});
Test("JSON: Epoch-Eingabe -> ISO", () =>
{
    var (_, p) = BuildJson(["1790805600", "A", "1", "t"], s => { s.TsColumn = "Zeit"; s.TsOutput = "iso"; s.TsTimezone = "UTC"; });
    True(p.GetProperty("Zeit").GetString()!.StartsWith("2026-09-30T22:00:00"), p.GetProperty("Zeit").GetString()!);
});
Test("JSON: unlesbarer Zeitstempel -> Zeile übersprungen", () =>
    Throws<RowException>(() => BuildJson(["kaputt", "A", "1", "t"], s => s.TsColumn = "Zeit")));
Test("JSON: Spalten ausschliessen + Zusatzfelder", () =>
{
    var (_, p) = BuildJson(["x", "A", "1", "t"], s => { s.ExcludeColumns = ["Text", "Zeit"]; s.StaticFields = "{\"src\":\"csv\"}"; });
    Eq("{\"src\":\"csv\",\"device\":\"A\",\"Wert\":1}", p.GetRawText());
});
Test("Vorlage: unbekannter Platzhalter", () =>
    Throws<MappingException>(() => new PayloadBuilder(new AppSettings { TopicMode = "template", TopicTemplate = "x/{fehlt}", PayloadMode = "json" }, H)));
Test("Vorlage: leeres Topic-Feld", () => Throws<RowException>(() => BuildJson(["x", "", "1", "t"])));
Test("Spalte: Payload unverändert", () =>
{
    var (t, p) = BuildCol(["x", "d2c/TWA01/energy", "{\"u\":26.3}", "y"]);
    Eq("d2c/TWA01/energy", t); Eq("{\"u\":26.3}", p);
});
Test("Spalte: Präfix", () => Eq("d2c/TWA01/e", BuildCol(["x", "TWA01/e", "1", ""], s => s.TopicPrefix = "d2c/").Item1));
Test("Spalte: JSON-Prüfung", () =>
{
    Throws<RowException>(() => BuildCol(["x", "a/b", "{kaputt", ""], s => s.ValidateJson = true));
    BuildCol(["x", "a/b", "kein json", ""]);
});
Test("Spalte: leer / Wildcard", () =>
{
    Throws<RowException>(() => BuildCol(["x", "", "1", ""]));
    Throws<RowException>(() => BuildCol(["x", "a/#", "1", ""]));
    Throws<RowException>(() => BuildCol(["x", "a/b", "  ", ""]));
});
Test("Spalte fehlt in Kopfzeile", () =>
    Throws<MappingException>(() => new PayloadBuilder(new AppSettings { TopicMode = "column", TopicColumn = "gibtsnicht", PayloadMode = "json" }, H2)));
Test("Spalten erraten", () =>
{
    string[] h = ["Zeit", "MQTT Pfad", "Nachricht"];
    Eq("MQTT Pfad", PayloadBuilder.GuessColumn(h, PayloadBuilder.TopicHints));
    Eq("Nachricht", PayloadBuilder.GuessColumn(h, PayloadBuilder.PayloadHints));
    Eq("", PayloadBuilder.GuessColumn(["a", "b"], PayloadBuilder.TopicHints));
});

Console.WriteLine("CSV");
var tmp = Directory.CreateTempSubdirectory("d2c").FullName;
Test("Anführungszeichen, \"\", Zeilenumbruch im Feld, BOM, leere Zeilen", () =>
{
    var f = Path.Combine(tmp, "q.csv");
    File.WriteAllText(f, "topic,payload\r\nd2c/a,\"{\"\"u\"\":1,\"\"i\"\":2}\"\r\n\r\nd2c/b,\"zwei\nzeilen\"\r\n", new UTF8Encoding(true));
    var src = new CsvSource(f);
    Eq(',', src.Delimiter);
    Eq("topic", src.Header[0]);
    var rows = src.Rows().ToList();
    Eq(2, rows.Count);
    Eq("{\"u\":1,\"i\":2}", rows[0].Cells[1]);
    Eq("zwei\nzeilen", rows[1].Cells[1]);
    Eq(1L, rows[1].Index);
    Eq(2L, src.CountRows());
});
Test("Trennzeichen-Erkennung ; und windows-1252", () =>
{
    var f = Path.Combine(tmp, "w.csv");
    File.WriteAllBytes(f, Encoding.GetEncoding(1252).GetBytes("Gerät;Wert\nÄ1;2\n"));
    var src = new CsvSource(f, "auto", "windows-1252");
    Eq(';', src.Delimiter);
    Eq("Gerät", src.Header[0]);
    Eq("Ä1", src.Rows().First().Cells[0]);
});
Test("Start ab Zeile", () =>
{
    var f = Path.Combine(tmp, "s.csv");
    File.WriteAllText(f, "a\n" + string.Join("\n", Enumerable.Range(0, 100)) + "\n");
    var r = new CsvSource(f).Rows(95).ToList();
    Eq(5, r.Count); Eq(95L, r[0].Index); Eq("95", r[0].Cells[0]);
});
Test("500'000 Zeilen zählen < 5 s", () =>
{
    var f = Path.Combine(tmp, "big.csv");
    using (var w = new StreamWriter(f))
    {
        w.WriteLine("ts;topic;payload");
        for (int i = 0; i < 500_000; i++) w.WriteLine($"{i};d2c/T{i % 3}/e;\"{{\"\"v\"\":{i}}}\"");
    }
    var sw = Stopwatch.StartNew();
    Eq(500_000L, new CsvSource(f).CountRows());
    True(sw.Elapsed.TotalSeconds < 5, $"took {sw.Elapsed.TotalSeconds:0.0}s");
    Console.WriteLine($"          ({sw.Elapsed.TotalSeconds:0.00} s)");
});

Console.WriteLine("Einstellungen / Passwort");
Test("Passwort nie im Klartext in settings.json", () =>
{
    var s = new AppSettings { Password = "Geheim-123!" };
    s.Save();
    var json = File.ReadAllText(AppSettings.FilePath);
    True(!json.Contains("Geheim-123!"), "Klartext gefunden");
    True(!json.Contains("\"Password\""), "Feld Password geschrieben");
    True(json.Contains("\"PasswordProtected\": \"aes:") || json.Contains("\"PasswordProtected\": \"dpapi:"), "kein verschlüsselter Wert");
    Eq("Geheim-123!", AppSettings.Load().Password);
    Eq("Geheim-123!", s.Clone().Password);
});
Test("Alte Datei mit Klartext wird beim Laden migriert", () =>
{
    File.WriteAllText(AppSettings.FilePath, "{ \"Host\": \"x\", \"Password\": \"alt-pw\" }");
    var s = AppSettings.Load();
    Eq("alt-pw", s.Password);
    var json = File.ReadAllText(AppSettings.FilePath);
    True(!json.Contains("alt-pw"), "Klartext nach Migration noch da");
    Eq("alt-pw", AppSettings.Load().Password);
});
Test("Defekter/fremder Wert -> leeres Passwort, kein Absturz", () =>
{
    Eq("", SecretStore.Unprotect("aes:AAAA"));
    Eq("", SecretStore.Unprotect("dpapi:AAAA"));
    Eq("", SecretStore.Protect(""));
});

Console.WriteLine("Updates (GitHub Releases)");
Test("Versionsnummern aus Tags", () =>
{
    Eq(new Version(1, 3, 0, 0), UpdateChecker.ParseTag("v1.3.0"));
    Eq(new Version(1, 3, 0, 0), UpdateChecker.ParseTag("1.3"));
    Eq<Version?>(null, UpdateChecker.ParseTag("v1.4.0-beta.1"));
    Eq<Version?>(null, UpdateChecker.ParseTag("latest"));
    True(UpdateChecker.IsNewer(new Version(1, 2, 1), new Version(1, 2, 0, 0)), "1.2.1 > 1.2.0.0");
    True(!UpdateChecker.IsNewer(new Version(1, 2), new Version(1, 2, 0, 0)), "1.2 == 1.2.0.0");
    Eq("1.3.0", UpdateChecker.Format(new Version(1, 3, 0, 0)));
});
Test("Release-JSON auswerten (ZIP win-x64 bevorzugt)", () =>
{
    var json = """{"tag_name":"v1.3.0","html_url":"https://github.com/o/r/releases/tag/v1.3.0","body":"- neu","draft":false,"prerelease":false,"published_at":"2026-10-20T08:00:00Z","assets":[{"name":"src.zip","browser_download_url":"https://x/src.zip"},{"name":"d2c_DataUpload_MQTT_1.3.0_win-x64.zip","browser_download_url":"https://x/win.zip"}]}""";
    var r = UpdateChecker.Parse(JsonDocument.Parse(json).RootElement)!;
    Eq(new Version(1, 3, 0, 0), r.Version);
    Eq("https://x/win.zip", r.DownloadUrl);
    Eq("- neu", r.Notes);
    Eq<ReleaseInfo?>(null, UpdateChecker.Parse(JsonDocument.Parse("""{"tag_name":"v2.0.0","prerelease":true}""").RootElement));
});
Test("Abfrage gegen (nachgebildete) GitHub-API, 404 = kein Release", () =>
{
    var oldBase = UpdateChecker.ApiBase; var oldRepo = UpdateChecker.Repository;
    var port = FreePort();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    string? userAgent = null;
    var served = Task.Run(async () =>
    {
        for (int i = 0; i < 2; i++)
        {
            var ctx = await listener.GetContextAsync();
            userAgent = ctx.Request.UserAgent;
            var ok = ctx.Request.Url!.AbsolutePath == "/repos/spaeni/test/releases/latest";
            ctx.Response.StatusCode = ok ? 200 : 404;
            var body = Encoding.UTF8.GetBytes(ok ? """{"tag_name":"v9.1.0","html_url":"https://github.com/spaeni/test/releases/tag/v9.1.0","assets":[]}""" : "{}");
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }
    });
    try
    {
        UpdateChecker.ApiBase = $"http://127.0.0.1:{port}";
        UpdateChecker.Repository = "spaeni/test";
        var r = UpdateChecker.GetLatestAsync(new Version(1, 2, 0)).Result!;
        Eq(new Version(9, 1, 0, 0), r.Version);
        Eq<string?>(null, r.DownloadUrl);
        True(userAgent?.StartsWith("d2c_DataUpload_MQTT/") == true, "User-Agent " + userAgent);
        UpdateChecker.Repository = "spaeni/fehlt";
        Eq<ReleaseInfo?>(null, UpdateChecker.GetLatestAsync(new Version(1, 2, 0)).Result);
        served.Wait(3000);
    }
    finally { UpdateChecker.ApiBase = oldBase; UpdateChecker.Repository = oldRepo; }
});
Test("Prüfintervall und Platzhalter-Repository", () =>
{
    var old = UpdateChecker.Repository;
    try
    {
        UpdateChecker.Repository = "OWNER/d2c-dataupload-mqtt";
        True(!UpdateChecker.IsConfigured, "Platzhalter gilt als nicht eingerichtet");
        UpdateChecker.Repository = "spaeni/test";
        var s = new AppSettings();
        var now = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        True(UpdateChecker.IsDue(s, now), "erste Prüfung");
        s.LastUpdateCheckUtc = now.AddHours(-2);
        True(!UpdateChecker.IsDue(s, now), "zu früh");
        s.LastUpdateCheckUtc = now.AddHours(-21);
        True(UpdateChecker.IsDue(s, now), "fällig");
        s.CheckForUpdates = false;
        True(!UpdateChecker.IsDue(s, now), "abgeschaltet");
    }
    finally { UpdateChecker.Repository = old; }
});
Test("Repository aus Directory.Build.props eingebaut", () =>
    True(UpdateChecker.Repository.EndsWith("/d2c-dataupload-mqtt", StringComparison.OrdinalIgnoreCase), UpdateChecker.Repository));

Console.WriteLine("Übersetzung / Hilfe");
Test("Alle Schlüssel DE+EN vorhanden, gleiche Platzhalter", () =>
{
    var used = new HashSet<string>();
    foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                 .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains("/obj/") && !f.Contains("\\obj\\")))
    {
        var text = File.ReadAllText(f);
        if (f.EndsWith(".cs")) text = Regex.Replace(text, @"^\s*///?.*$", "", RegexOptions.Multiline); // Kommentare
        foreach (Match m in Regex.Matches(text, @"Loc\.T\(""(\w+)""|\{l:Tr (\w+)\}|Loc\.T\([\w.]+ \? ""(\w+)"" : ""(\w+)""\)|""(Btn_\w+)"" : ""(Btn_\w+)"""))
            foreach (Group g in m.Groups.Values.Skip(1))
                if (g.Success) used.Add(g.Value);
    }
    var missing = used.Where(k => !Strings.Table.ContainsKey(k)).ToList();
    True(missing.Count == 0, "fehlt: " + string.Join(", ", missing));
    var unused = Strings.Table.Keys.Where(k => !used.Contains(k)).ToList();
    if (unused.Count > 0) Console.WriteLine("          (unbenutzt: " + string.Join(", ", unused) + ")");
    foreach (var (k, (de, en)) in Strings.Table)
    {
        var pd = Regex.Matches(de, @"\{\d+\}").Select(m => m.Value).Distinct().Order();
        var pe = Regex.Matches(en, @"\{\d+\}").Select(m => m.Value).Distinct().Order();
        True(pd.SequenceEqual(pe), "Platzhalter " + k);
        True(de.Length > 0 && en.Length > 0, "leer " + k);
    }
});
Test("Sprachwechsel", () =>
{
    Loc.Instance.Language = "en";
    Eq("Rows: 5", Loc.T("Info_Rows", 5));
    var changed = 0;
    Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") changed++; };
    Loc.Instance.Language = "de";
    Eq("Zeilen: 5", Loc.T("Info_Rows", 5));
    Eq(1, changed);
});
Test("Hilfe-Anker aus XAML existieren in de.html und en.html", () =>
{
    var help = Path.Combine(root, "src", "D2cDataUpload.App", "Help");
    var de = Regex.Matches(File.ReadAllText(Path.Combine(help, "de.html")), "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
    var en = Regex.Matches(File.ReadAllText(Path.Combine(help, "en.html")), "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
    True(de.SetEquals(en), "Anker DE/EN verschieden");
    var used = Directory.EnumerateFiles(Path.Combine(root, "src", "D2cDataUpload.App"), "*.xaml", SearchOption.AllDirectories)
        .SelectMany(f => Regex.Matches(File.ReadAllText(f), "Tag=\"([a-z-]+)\"[^>]*Click=\"(Help_Click|mi_HelpTopic_Click)\"").Select(m => m.Groups[1].Value))
        .ToHashSet();
    True(used.Count >= 8, $"nur {used.Count} Hilfe-Anker gefunden");
    var bad = used.Where(a => !de.Contains(a)).ToList();
    True(bad.Count == 0, "fehlende Anker: " + string.Join(", ", bad));
});
Test("Hilfe-Webserver liefert Seiten und schützt vor ../", () =>
{
    HelpServer.HelpFolder = Path.Combine(root, "src", "D2cDataUpload.App", "Help");
    using var http = new HttpClient();
    foreach (var lang in new[] { "de", "en" })
    {
        Loc.Instance.Language = lang;
        var url = HelpServer.Url("zuordnung");
        True(Regex.IsMatch(url, $@"^http://127\.0\.0\.1:\d+/{lang}\.html#zuordnung$"), url);
        var html = http.GetStringAsync(url.Split('#')[0]).Result;
        True(html.Contains("id=\"zuordnung\""), "Inhalt " + lang);
    }
    Loc.Instance.Language = "de";
    var baseUrl = HelpServer.Url().Replace("/de.html", "");
    Eq(HttpStatusCode.OK, http.GetAsync(baseUrl + "/style.css").Result.StatusCode);
    Eq(HttpStatusCode.NotFound, http.GetAsync(baseUrl + "/..%2F..%2FD2cDataUpload.App.csproj").Result.StatusCode);
});

// ---------------------------------------------------------------------
Console.WriteLine("Broker (mosquitto)");
var mosq = FindExe("mosquitto");
string bigCsv = Path.Combine(tmp, "send.csv");
using (var w = new StreamWriter(bigCsv))
{
    w.WriteLine("ts;topic;payload;kommentar");
    for (int i = 0; i < 3000; i++) w.WriteLine($"{i};t/D{i % 2};\"{{\"\"v\"\":{i}}}\";x");
    w.WriteLine("1;;\"{}\";leeres Topic");
}
AppSettings Cfg(int port, double rate) => new()
{
    Port = port, TopicMode = "column", TopicColumn = "topic", PayloadMode = "column", PayloadColumn = "payload",
    RatePerSecond = rate, Qos = 1, ClientId = "test-" + Guid.NewGuid().ToString("N")[..8],
};

Test("3000 Nachrichten QoS 1, alle angekommen, Rate begrenzt", () =>
{
    if (mosq is null) throw new SkipException("mosquitto fehlt");
    var port = FreePort();
    using var broker = StartBroker(mosq, port);
    using var sub = new Subscriber(port, "t/#");
    var sw = Stopwatch.StartNew();
    var p = new SendJob(Cfg(port, 1000), bigCsv).Run();
    var secs = sw.Elapsed.TotalSeconds;
    Thread.Sleep(500);
    Eq(3000L, p.Sent, "sent"); Eq(3000L, p.Acked, "acked"); Eq(1L, p.Errors, "errors"); Eq(3001L, p.Done, "done");
    var vals = sub.Values().Order().ToList();
    True(vals.SequenceEqual(Enumerable.Range(0, 3000)), $"empfangen {vals.Count}");
    True(secs >= 2.9, $"zu schnell: {secs:0.00}s");
    Console.WriteLine($"          ({secs:0.00} s für 3000 @ 1000/s)");
});
Test("Drosselung 50/s", () =>
{
    if (mosq is null) throw new SkipException("mosquitto fehlt");
    var port = FreePort();
    using var broker = StartBroker(mosq, port);
    var sw = Stopwatch.StartNew();
    var p = new SendJob(Cfg(port, 50), bigCsv, 2000, 150).Run();
    var secs = sw.Elapsed.TotalSeconds;
    Eq(150L, p.Sent);
    True(secs > 2.8 && secs < 4.5, $"{secs:0.00}s");
    Console.WriteLine($"          ({secs:0.00} s für 150 @ 50/s)");
});
Test("Broker-Neustart mitten im Senden: nichts verloren", () =>
{
    if (mosq is null) throw new SkipException("mosquitto fehlt");
    var port = FreePort();
    var broker = StartBroker(mosq, port);
    using var sub = new Subscriber(port, "t/#", persistent: true);
    var job = new SendJob(Cfg(port, 200), bigCsv, 0, 1000);
    var logs = new List<string>();
    job.Log += m => { lock (logs) logs.Add(m); };
    var task = Task.Run(job.Run);
    Thread.Sleep(1500);
    broker.Dispose();
    Thread.Sleep(2500);
    broker = StartBroker(mosq, port);
    Thread.Sleep(1000);
    job.Pause(); Thread.Sleep(800); job.Resume();
    var p = task.Result;
    Thread.Sleep(800);
    broker.Dispose();
    Eq(1000L, p.Sent); Eq(1000L, p.Done);
    var got = sub.Values().Distinct().Count();
    Eq(1000, got, "eindeutig empfangen");
    True(logs.Any(l => l.Contains("getrennt") || l.Contains("Disconnected")), "kein Trennungs-Log");
});
Test("Stopp liefert Fortsetzungspunkt, Fortsetzen sendet den Rest", () =>
{
    if (mosq is null) throw new SkipException("mosquitto fehlt");
    var port = FreePort();
    using var broker = StartBroker(mosq, port);
    using var sub = new Subscriber(port, "t/#");
    var job = new SendJob(Cfg(port, 300), bigCsv, 0, 600);
    long ck = -1;
    job.Checkpoint += v => ck = v;
    var task = Task.Run(job.Run);
    Thread.Sleep(1000);
    job.Stop();
    var p1 = task.Result;
    True(p1.Done > 0 && p1.Done < 600, $"done {p1.Done}");
    Eq(p1.Done, ck, "checkpoint");
    var p2 = new SendJob(Cfg(port, 1000), bigCsv, p1.Done, 600 - p1.Done).Run();
    Thread.Sleep(500);
    var got = sub.Values().Distinct().Order().ToList();
    True(got.SequenceEqual(Enumerable.Range(0, 600)), $"empfangen {got.Count}");
});
Test("Verbindungsfehler verständlich", () =>
{
    Loc.Instance.Language = "de";
    var (ok, msg) = SendJob.TestConnectionAsync(new AppSettings { Port = FreePort() }).Result;
    True(!ok && msg.Contains("abgelehnt"), msg);
    (ok, msg) = SendJob.TestConnectionAsync(new AppSettings { Host = "gibtsnicht.invalid" }).Result;
    True(!ok && msg.Contains("Hostname"), msg);
});

// TLS + Passwort: D2C_TLS_CONF=<mosquitto.conf>;<port>;<ca>;<user>;<pw>
Test("TLS + Benutzer/Passwort", () =>
{
    var env = Environment.GetEnvironmentVariable("D2C_TLS_CONF");
    if (mosq is null || string.IsNullOrEmpty(env)) throw new SkipException("D2C_TLS_CONF nicht gesetzt");
    var a = env.Split(';');
    using var broker = StartBroker(mosq, int.Parse(a[1]), a[0]);
    var s = new AppSettings { Port = int.Parse(a[1]), UseTls = true, CaFile = a[2], Username = a[3], Password = a[4] };
    var (ok, msg) = SendJob.TestConnectionAsync(s).Result;
    True(ok, msg);
    s.Password = "falsch";
    (ok, msg) = SendJob.TestConnectionAsync(s).Result;
    True(!ok && msg.Contains("abgelehnt"), msg);
    s.Password = a[4]; s.CaFile = "";
    (ok, msg) = SendJob.TestConnectionAsync(s).Result;
    True(!ok && msg.Contains("TLS"), msg);
    s.TlsInsecure = true;
    (ok, msg) = SendJob.TestConnectionAsync(s).Result;
    True(ok, msg);
    s.TlsInsecure = false; s.CaFile = a[2];
    var csv = Path.Combine(tmp, "tls.csv");
    File.WriteAllText(csv, "topic;payload\nt/x;1\nt/y;2\n");
    s.TopicMode = "column"; s.TopicColumn = "topic"; s.PayloadMode = "column"; s.PayloadColumn = "payload";
    var p = new SendJob(s, csv).Run();
    Eq(2L, p.Acked);
});

Console.WriteLine($"\n{passed} ok, {failed} fehlgeschlagen, {skipped} übersprungen");
try { Directory.Delete(tmp, true); Directory.Delete(AppSettings.FolderOverride!, true); } catch { }
return failed == 0 ? 0 : 1;

// =====================================================================
static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "D2cDataUpload.slnx"))) d = d.Parent;
    return d?.FullName ?? throw new Exception("Projektordner nicht gefunden");
}

static string? FindExe(string name) =>
    (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Concat(["/usr/sbin", "/usr/local/sbin", @"C:\Program Files\mosquitto"])
        .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name))
        .FirstOrDefault(File.Exists);

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

static Broker StartBroker(string exe, int port, string? conf = null)
{
    var psi = new ProcessStartInfo(exe, conf is null ? $"-p {port}" : $"-c {conf}")
    { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    var b = new Broker(Process.Start(psi)!);
    for (int i = 0; i < 50; i++)
    {
        try { using var c = new TcpClient("127.0.0.1", port); return b; }
        catch (SocketException) { Thread.Sleep(100); }
    }
    b.Dispose();
    throw new Exception("Broker startet nicht");
}

sealed class Broker(Process p) : IDisposable
{
    public void Dispose()
    {
        try { p.Kill(); p.WaitForExit(3000); } catch { }
        p.Dispose();
    }
}

sealed class SkipException(string m) : Exception(m);

/// <summary>Minimaler Abonnent (rohes MQTT 3.1.1) für die Tests.</summary>
sealed class Subscriber : IDisposable
{
    private readonly int _port;
    private readonly string _filter;
    private readonly bool _persistent;
    private readonly string _id = "sub-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<int> _values = [];
    private readonly CancellationTokenSource _cts = new();

    public Subscriber(int port, string filter, bool persistent = false)
    {
        _port = port; _filter = filter; _persistent = persistent;
        var ready = new ManualResetEventSlim();
        Task.Run(() => Loop(ready));
        if (!ready.Wait(5000)) throw new Exception("Subscriber nicht verbunden");
    }

    public List<int> Values() { lock (_values) return [.. _values]; }

    private async Task Loop(ManualResetEventSlim ready)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", _port);
                var s = tcp.GetStream();
                var id = Encoding.UTF8.GetBytes(_id);
                var connect = new List<byte> { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, (byte)(_persistent ? 0 : 2), 0, 60, 0, (byte)id.Length };
                connect.AddRange(id);
                await s.WriteAsync(Frame(0x10, [.. connect]));
                await Read(s); // CONNACK
                var f = Encoding.UTF8.GetBytes(_filter);
                var sub = new List<byte> { 0, 1, 0, (byte)f.Length };
                sub.AddRange(f); sub.Add(1);
                await s.WriteAsync(Frame(0x82, [.. sub]));
                ready.Set();
                while (!_cts.IsCancellationRequested)
                {
                    var (type, body) = await Read(s);
                    if ((type & 0xF0) != 0x30) continue;
                    int qos = (type >> 1) & 3;
                    int tl = body[0] << 8 | body[1];
                    int off = 2 + tl;
                    if (qos > 0)
                    {
                        await s.WriteAsync(new byte[] { 0x40, 2, body[off], body[off + 1] });
                        off += 2;
                    }
                    var json = JsonDocument.Parse(body.AsMemory(off));
                    lock (_values) _values.Add(json.RootElement.GetProperty("v").GetInt32());
                }
            }
            catch when (!_cts.IsCancellationRequested) { await Task.Delay(300); }
            catch { return; }
        }
    }

    private static byte[] Frame(byte h, byte[] body)
    {
        var l = new List<byte> { h };
        int len = body.Length;
        do { byte b = (byte)(len % 128); len /= 128; if (len > 0) b |= 0x80; l.Add(b); } while (len > 0);
        l.AddRange(body);
        return [.. l];
    }

    private static async Task<(byte, byte[])> Read(Stream s)
    {
        var one = new byte[1];
        await s.ReadExactlyAsync(one);
        var t = one[0];
        int len = 0, mult = 1;
        do { await s.ReadExactlyAsync(one); len += (one[0] & 0x7F) * mult; mult *= 128; } while ((one[0] & 0x80) != 0);
        var body = new byte[len];
        await s.ReadExactlyAsync(body);
        return (t, body);
    }

    public void Dispose() => _cts.Cancel();
}
