using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace D2cDataUpload.App;

/// <summary>Hilfe › Info: Produkt, Version, Firma, Entwickler, Support-Webseite und -E-Mail.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        tb_Title.Text = AppInfo.Title;
        tb_Version.Text = AppInfo.VersionText;
        tb_Company.Text = AppInfo.Company;
        tb_Developer.Text = AppInfo.Developer;
        run_Support.Text = AppInfo.SupportUrl.Replace("https://", "");
        hl_Support.NavigateUri = new Uri(AppInfo.SupportUrl);
        run_Email.Text = AppInfo.SupportEmail;
        hl_Email.NavigateUri = new Uri("mailto:" + AppInfo.SupportEmail);
        tb_Copyright.Text = AppInfo.Copyright;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* kein Browser/Mailprogramm */ }
        e.Handled = true;
    }

    private void bt_Ok_Click(object sender, RoutedEventArgs e) => Close();
}
