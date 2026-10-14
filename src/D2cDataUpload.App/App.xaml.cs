using System.Windows;
using D2cDataUpload.Core;

namespace D2cDataUpload.App;

public partial class App : Application
{
    /// <summary>Gemeinsame Einstellungen der ganzen App.</summary>
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        Loc.Instance.Language = Settings.Language;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HelpServer.Shutdown();
        base.OnExit(e);
    }
}
