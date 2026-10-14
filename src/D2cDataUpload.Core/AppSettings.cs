using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2cDataUpload.Core;

/// <summary>Persistente Einstellungen als JSON unter %APPDATA%\d2c_DataUpload_MQTT\settings.json.</summary>
public sealed class AppSettings
{
    public const string AppFolderName = "d2c_DataUpload_MQTT";

    // --- Allgemein ---
    public string Language { get; set; } = "de";

    // --- Updates (GitHub Releases) ---
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>"Diese Version überspringen" – kein Hinweis mehr für genau diese Version.</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>Zuletzt gestartete Version – für den Hinweis "Was ist neu" nach einem Update.</summary>
    public string LastRunVersion { get; set; } = "";

    // --- Broker ---
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "d2c-dataupload";
    public string Username { get; set; } = "";
    /// <summary>Klartext nur im Speicher – wird nie so in die Datei geschrieben.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>In settings.json: verschlüsselt (DPAPI, siehe SecretStore).</summary>
    [JsonPropertyName("PasswordProtected")]
    public string PasswordProtected
    {
        get => SecretStore.Protect(Password);
        set => Password = SecretStore.Unprotect(value);
    }

    /// <summary>Alte Dateien mit Klartext-Passwort: wird gelesen, nie geschrieben (Migration).</summary>
    [JsonPropertyName("Password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlainPassword
    {
        get => null;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            Password = value;
            _migrated = true;
        }
    }

    private bool _migrated;
    public int Keepalive { get; set; } = 60;
    public bool UseTls { get; set; }
    public string CaFile { get; set; } = "";
    public bool TlsInsecure { get; set; }

    // --- Zuordnung ---
    public string TopicMode { get; set; } = "column";     // column | template
    public string TopicColumn { get; set; } = "";
    public string TopicPrefix { get; set; } = "";
    public string TopicTemplate { get; set; } = "d2c/nachlieferung/{device}";
    public string PayloadMode { get; set; } = "column";   // column | json
    public string PayloadColumn { get; set; } = "";
    public bool ValidateJson { get; set; }
    public List<string> ExcludeColumns { get; set; } = [];
    public string TsColumn { get; set; } = "";

    // --- Senden ---
    public int Qos { get; set; } = 1;
    public bool Retain { get; set; }
    public double RatePerSecond { get; set; } = 50;
    public int MaxInflight { get; set; } = 50;

    // --- CSV ---
    public string Delimiter { get; set; } = "auto";       // auto ; , tab |
    public string Encoding { get; set; } = "utf-8";       // utf-8 | windows-1252 | latin1

    // --- JSON-Optionen ---
    public bool ConvertNumbers { get; set; } = true;
    public bool DecimalComma { get; set; }
    public bool EmptyAsNull { get; set; } = true;
    public bool SkipEmptyFields { get; set; }
    public string StaticFields { get; set; } = "";
    public string TsFormat { get; set; } = "auto";
    public string TsTimezone { get; set; } = "Europe/Zurich";
    public string TsOutput { get; set; } = "epoch_ms";    // epoch_ms | epoch_s | iso | unchanged
    public string TsTargetKey { get; set; } = "";

    // --- intern ---
    public Dictionary<string, long> Checkpoints { get; set; } = [];
    public string LastDirectory { get; set; } = "";

    // ------------------------------------------------------------------
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Kann für Tests umgelenkt werden.</summary>
    public static string? FolderOverride { get; set; }

    public static string Folder
    {
        get
        {
            var d = FolderOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");
    public static string LogFolder => Directory.CreateDirectory(Path.Combine(Folder, "logs")).FullName;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new();
                if (s._migrated)
                {
                    try { s.Save(); } catch (IOException) { } // Klartext sofort durch verschlüsselten Wert ersetzen
                    s._migrated = false;
                }
                return s;
            }
        }
        catch (Exception)
        {
            // defekte Datei -> Standardwerte
        }
        return new AppSettings();
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Clone()
    {
        var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        c.Password = Password; // Klartext im Speicher übernehmen (ohne Umweg über die Verschlüsselung)
        return c;
    }

    // --- Fortsetzungspunkt je Datei (Pfad + Grösse + Änderungszeit) ---
    public static string FileKey(string path)
    {
        var fi = new FileInfo(path);
        return $"{fi.FullName}|{fi.Length}|{new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()}";
    }

    public long GetCheckpoint(string path)
    {
        try { return Checkpoints.TryGetValue(FileKey(path), out var v) ? v : 0; }
        catch (IOException) { return 0; }
    }

    public void SetCheckpoint(string path, long rowsDone)
    {
        try
        {
            Checkpoints[FileKey(path)] = rowsDone;
            while (Checkpoints.Count > 50)
                Checkpoints.Remove(Checkpoints.Keys.First());
        }
        catch (IOException) { }
    }
}
