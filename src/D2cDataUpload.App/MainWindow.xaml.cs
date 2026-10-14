using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using D2cDataUpload.Core;

namespace D2cDataUpload.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = $"{AppInfo.Title}  v{AppInfo.Version.ToString(3)}";
        tb_FooterCompany.Text = AppInfo.Company;
        tb_FooterVersion.Text = AppInfo.VersionText;

        SettingsPage.Saved += (_, _) => SendPage.OnSettingsChanged();
        SettingsPage.LanguageSelected += (_, lang) => SetLanguage(lang);
        UpdateLanguageChecks();
        Loc.Instance.LanguageChanged += (_, _) => RefreshUpdateBanner();
    }

    // --- Updates (GitHub Releases) -----------------------------------------
    private ReleaseInfo? _available;      // neue Version verfügbar
    private Version? _updatedTo;          // seit dem letzten Start aktualisiert

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        var current = UpdateChecker.Normalize(AppInfo.Version);

        // "Was ist neu" einmalig nach einem Update
        var last = UpdateChecker.ParseTag(s.LastRunVersion);
        if (last is not null && UpdateChecker.IsNewer(current, last)) _updatedTo = current;
        s.LastRunVersion = UpdateChecker.Format(current);
        TrySave();
        RefreshUpdateBanner();

        // automatische Prüfung (höchstens alle 20 h, still bei Fehlern)
        if (!UpdateChecker.IsDue(s, DateTime.UtcNow)) return;
        try
        {
            var latest = await UpdateChecker.GetLatestAsync(current);
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            TrySave();
            if (latest is not null && UpdateChecker.IsNewer(latest.Version, current)
                && UpdateChecker.Format(latest.Version) != s.SkippedVersion)
            {
                _available = latest;
                RefreshUpdateBanner();
            }
        }
        catch (Exception)
        {
            // offline / Proxy / GitHub nicht erreichbar: beim nächsten Start erneut
        }
    }

    private async void mi_CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateChecker.IsConfigured)
        {
            MessageBox.Show(Loc.T("Upd_NotConfigured"), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var current = UpdateChecker.Normalize(AppInfo.Version);
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var latest = await UpdateChecker.GetLatestAsync(current);
            App.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            TrySave();
            if (latest is not null && UpdateChecker.IsNewer(latest.Version, current))
            {
                _available = latest;      // manuell: auch eine übersprungene Version anzeigen
                RefreshUpdateBanner();
            }
            else
            {
                MessageBox.Show(Loc.T("Upd_UpToDate", UpdateChecker.Format(current)), AppInfo.Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Upd_Failed", ex.Message), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void RefreshUpdateBanner()
    {
        if (_available is not null)
        {
            tb_UpdateText.Text = Loc.T("Upd_Available", UpdateChecker.Format(_available.Version), UpdateChecker.Format(AppInfo.Version));
            bt_UpdateDownload.Visibility = Visibility.Visible;
            bt_UpdateSkip.Visibility = Visibility.Visible;
            bt_UpdateNotes.Visibility = Visibility.Visible;
            bd_Update.Visibility = Visibility.Visible;
        }
        else if (_updatedTo is not null)
        {
            tb_UpdateText.Text = Loc.T("Upd_Updated", UpdateChecker.Format(_updatedTo));
            bt_UpdateDownload.Visibility = Visibility.Collapsed;
            bt_UpdateSkip.Visibility = Visibility.Collapsed;
            bt_UpdateNotes.Visibility = UpdateChecker.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
            bd_Update.Visibility = Visibility.Visible;
        }
        else
        {
            bd_Update.Visibility = Visibility.Collapsed;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void bt_UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not null) OpenUrl(_available.DownloadUrl ?? _available.PageUrl);
    }

    private void bt_UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not null) OpenUrl(_available.PageUrl);
        else if (_updatedTo is not null) OpenUrl(UpdateChecker.ReleasePage(_updatedTo));
    }

    private void bt_UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_available is null) return;
        App.Settings.SkippedVersion = UpdateChecker.Format(_available.Version);
        TrySave();
        _available = null;
        RefreshUpdateBanner();
    }

    private void bt_UpdateClose_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not null) _available = null;   // bis zum nächsten Start ausblenden
        else _updatedTo = null;
        RefreshUpdateBanner();
    }

    // --- Sprache ---------------------------------------------------------
    private void SetLanguage(string lang)
    {
        if (lang == Loc.Instance.Language) { UpdateLanguageChecks(); return; }
        Loc.Instance.Language = lang;          // alle {l:Tr …}-Bindings aktualisieren sich sofort
        App.Settings.Language = lang;
        TrySave();
        UpdateLanguageChecks();
        SettingsPage.LoadFromSettings();       // Sprach-Combo synchron halten
    }

    private void UpdateLanguageChecks()
    {
        mi_LangDe.IsChecked = Loc.Instance.Language == "de";
        mi_LangEn.IsChecked = Loc.Instance.Language == "en";
    }

    private void mi_LangDe_Click(object sender, RoutedEventArgs e) => SetLanguage("de");
    private void mi_LangEn_Click(object sender, RoutedEventArgs e) => SetLanguage("en");

    // --- Menü ------------------------------------------------------------
    private void mi_Open_Click(object sender, RoutedEventArgs e)
    {
        tc_Main.SelectedItem = ti_Send;
        SendPage.PickFile();
    }

    private void mi_Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void mi_Settings_Click(object sender, RoutedEventArgs e) => tc_Main.SelectedItem = ti_Settings;

    private void mi_Help_Click(object sender, RoutedEventArgs e) => ShowHelp("");

    private void mi_HelpTopic_Click(object sender, RoutedEventArgs e) =>
        ShowHelp((sender as FrameworkElement)?.Tag as string ?? "");

    public static void ShowHelp(string anchor)
    {
        var error = HelpServer.Open(anchor);
        if (error is not null) MessageBox.Show(error, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void mi_SettingsFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppSettings.Folder) { UseShellExecute = true });

    private void mi_About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // --- Tabs / Schliessen -----------------------------------------------
    private void tc_Main_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != tc_Main) return;
        if (tc_Main.SelectedItem == ti_Send && !SendPage.IsRunning)
        {
            SettingsPage.ApplyToSettings();
            TrySave();
            SendPage.OnSettingsChanged();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (SendPage.IsRunning)
        {
            if (MessageBox.Show(Loc.T("Msg_StopAndExit"), AppInfo.Title, MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SendPage.StopAndWait(TimeSpan.FromSeconds(10));
        }
        SettingsPage.ApplyToSettings();
        SendPage.ApplyMapping();
        TrySave();
    }

    public static void TrySave()
    {
        try { App.Settings.Save(); }
        catch (Exception) { /* nicht kritisch */ }
    }
}
