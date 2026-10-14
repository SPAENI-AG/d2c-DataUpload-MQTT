using System.Reflection;

namespace D2cDataUpload.App;

/// <summary>Produkt-, Firmen- und Supportangaben – eine Stelle für Footer und Hilfe › Info.</summary>
public static class AppInfo
{
    public const string Title = "m2v / d2c DataUpload MQTT";

    /// <summary>Firmenname – überall genau so schreiben.</summary>
    public const string Company = "SPÄNI Elektro-Mechanik AG";

    /// <summary>Entwickler – nur im Info-Fenster anzeigen, nicht im Footer.</summary>
    public const string Developer = "Ralph Kessler";

    public const string SupportUrl = "https://support.elektro-spaeni.ch";
    public const string SupportEmail = "support@elektro-spaeni.ch";

    public static Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Footer rechts und Info-Fenster.</summary>
    public static string VersionText => $"Version {Version}";

    public static string Copyright => $"© {DateTime.Now.Year} {Company}";
}
