using System.ComponentModel;
using System.Globalization;

namespace D2cDataUpload.Core;

/// <summary>
/// Laufzeit-umschaltbare Übersetzung DE/EN.
/// WPF:  Text="{Binding [Key], Source={x:Static core:Loc.Instance}}"
/// Code: Loc.T("Key", arg0, arg1 …)  (string.Format-Platzhalter {0}, {1} …)
/// Beim Umschalten (Loc.Instance.Language = "en") aktualisieren sich alle Bindings sofort.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly string[] Languages = ["de", "en"];

    private string _language = "de";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language
    {
        get => _language;
        set
        {
            var v = Array.IndexOf(Languages, value) >= 0 ? value : "de";
            if (v == _language) return;
            _language = v;
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(v == "de" ? "de-CH" : "en-GB");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Für Code, der Texte selbst setzt (z.B. Status, MessageBox).</summary>
    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Strings.Table.TryGetValue(key, out var t))
            return _language == "en" ? t.En : t.De;
        return $"[{key}]";
    }

    public static string T(string key, params object?[] args)
    {
        var s = Instance.Get(key);
        return args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args);
    }
}
