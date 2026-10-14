using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2cDataUpload.Core;
using Microsoft.Win32;

namespace D2cDataUpload.App.Views;

/// <summary>Einstellungsseite: lädt/speichert App.Settings (%APPDATA%\d2c_DataUpload_MQTT\settings.json).</summary>
public partial class SettingsView : UserControl
{
    private bool _loading;

    /// <summary>Nach "Speichern" (Sendeseite aktualisiert Vorschau/Rate).</summary>
    public event EventHandler? Saved;

    /// <summary>Sprache in der Combo gewählt – MainWindow schaltet um.</summary>
    public event EventHandler<string>? LanguageSelected;

    public SettingsView()
    {
        InitializeComponent();
        LoadFromSettings(); // App.Settings ist beim Erzeugen des Fensters bereits geladen
    }

    private static AppSettings S => App.Settings;

    public void LoadFromSettings()
    {
        _loading = true;
        SelectByTag(cb_Language, S.Language);
        chk_CheckUpdates.IsChecked = S.CheckForUpdates;
        tb_Host.Text = S.Host;
        tb_Port.Text = S.Port.ToString(CultureInfo.InvariantCulture);
        tb_ClientId.Text = S.ClientId;
        tb_User.Text = S.Username;
        pb_Password.Password = S.Password;
        tb_Keepalive.Text = S.Keepalive.ToString(CultureInfo.InvariantCulture);
        chk_Tls.IsChecked = S.UseTls;
        tb_CaFile.Text = S.CaFile;
        chk_TlsInsecure.IsChecked = S.TlsInsecure;

        cb_Qos.SelectedIndex = Math.Clamp(S.Qos, 0, 2);
        chk_Retain.IsChecked = S.Retain;
        tb_Rate.Text = S.RatePerSecond.ToString("0.###", CultureInfo.InvariantCulture);
        tb_MaxInflight.Text = S.MaxInflight.ToString(CultureInfo.InvariantCulture);

        SelectByContent(cb_Delimiter, S.Delimiter);
        SelectByContent(cb_Encoding, S.Encoding);

        chk_ConvertNumbers.IsChecked = S.ConvertNumbers;
        chk_DecimalComma.IsChecked = S.DecimalComma;
        chk_EmptyAsNull.IsChecked = S.EmptyAsNull;
        chk_SkipEmpty.IsChecked = S.SkipEmptyFields;
        tb_StaticFields.Text = S.StaticFields;
        tb_TsFormat.Text = S.TsFormat;
        tb_TsTimezone.Text = S.TsTimezone;
        SelectByTag(cb_TsOutput, S.TsOutput);
        tb_TsTarget.Text = S.TsTargetKey;
        _loading = false;
    }

    /// <summary>Formularwerte in App.Settings übernehmen (ungültige Zahlen behalten den alten Wert).</summary>
    public void ApplyToSettings()
    {
        S.CheckForUpdates = chk_CheckUpdates.IsChecked == true;
        S.Host = tb_Host.Text.Trim();
        S.Port = ParseInt(tb_Port.Text, S.Port, 1, 65535);
        S.ClientId = tb_ClientId.Text.Trim();
        S.Username = tb_User.Text;
        S.Password = pb_Password.Password;
        S.Keepalive = ParseInt(tb_Keepalive.Text, S.Keepalive, 5, 3600);
        S.UseTls = chk_Tls.IsChecked == true;
        S.CaFile = tb_CaFile.Text.Trim();
        S.TlsInsecure = chk_TlsInsecure.IsChecked == true;

        S.Qos = Math.Max(0, cb_Qos.SelectedIndex);
        S.Retain = chk_Retain.IsChecked == true;
        if (double.TryParse(tb_Rate.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
            S.RatePerSecond = Math.Min(r, 5000);
        S.MaxInflight = ParseInt(tb_MaxInflight.Text, S.MaxInflight, 1, 1000);

        S.Delimiter = (cb_Delimiter.SelectedItem as ComboBoxItem)?.Content as string ?? "auto";
        S.Encoding = (cb_Encoding.SelectedItem as ComboBoxItem)?.Content as string ?? "utf-8";

        S.ConvertNumbers = chk_ConvertNumbers.IsChecked == true;
        S.DecimalComma = chk_DecimalComma.IsChecked == true;
        S.EmptyAsNull = chk_EmptyAsNull.IsChecked == true;
        S.SkipEmptyFields = chk_SkipEmpty.IsChecked == true;
        S.StaticFields = tb_StaticFields.Text;
        S.TsFormat = string.IsNullOrWhiteSpace(tb_TsFormat.Text) ? "auto" : tb_TsFormat.Text.Trim();
        S.TsTimezone = string.IsNullOrWhiteSpace(tb_TsTimezone.Text) ? "Europe/Zurich" : tb_TsTimezone.Text.Trim();
        S.TsOutput = (cb_TsOutput.SelectedItem as ComboBoxItem)?.Tag as string ?? "epoch_ms";
        S.TsTargetKey = tb_TsTarget.Text.Trim();
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    private static void SelectByTag(ComboBox cb, string tag)
    {
        foreach (ComboBoxItem it in cb.Items)
            if ((it.Tag as string) == tag) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 0;
    }

    private static void SelectByContent(ComboBox cb, string content)
    {
        foreach (ComboBoxItem it in cb.Items)
            if ((it.Content as string) == content) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------
    private void bt_Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();
        try
        {
            S.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Msg_SaveFailed", ex.Message), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void cb_Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || cb_Language.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        LanguageSelected?.Invoke(this, lang);
    }

    private async void bt_Test_Click(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();
        bt_Test.IsEnabled = false;
        tb_TestResult.Foreground = Brushes.Gray;
        tb_TestResult.Text = Loc.T("Msg_Testing");
        var (ok, msg) = await SendJob.TestConnectionAsync(S.Clone());
        tb_TestResult.Text = (ok ? "✔ " : "✘ ") + msg;
        tb_TestResult.Foreground = ok ? Brushes.Green : Brushes.Firebrick;
        bt_Test.IsEnabled = true;
    }

    private void bt_PickCa_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("Filter_Certificates") };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) tb_CaFile.Text = dlg.FileName;
    }

    private void Help_Click(object sender, RoutedEventArgs e) =>
        MainWindow.ShowHelp((sender as FrameworkElement)?.Tag as string ?? "");
}
