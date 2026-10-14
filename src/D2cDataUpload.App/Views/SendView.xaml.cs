using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using D2cDataUpload.Core;
using Microsoft.Win32;

namespace D2cDataUpload.App.Views;

/// <summary>Seite "Senden": Datei, Vorschau, Spalten-Zuordnung, Start/Pause/Stopp, Fortschritt, Log.</summary>
public partial class SendView : UserControl
{
    private Brush TopicBrush => (Brush)FindResource("TopicBrush");
    private Brush PayloadBrush => (Brush)FindResource("PayloadBrush");
    private static readonly NumberFormatInfo Swiss = new() { NumberGroupSeparator = "'", NumberGroupSizes = [3] };

    private bool _loading = true;
    private string _path = "";
    private IReadOnlyList<string> _header = [];
    private List<string[]> _preview = [];
    private string _delimiterDisplay = "";
    private long _total;
    private CancellationTokenSource? _countCts;

    private SendJob? _job;
    private Task? _jobTask;
    private long _runTarget;
    private DateTime _runStart;
    private SendProgress? _lastProgress;
    private StreamWriter? _logFile;

    private static AppSettings S => App.Settings;

    public bool IsRunning => _job is not null;

    public SendView()
    {
        InitializeComponent();
        LoadMappingControls();
        SetRunning(false);
        RefreshTexts();
        Loc.Instance.LanguageChanged += (_, _) => RefreshTexts();
        _loading = false;
    }

    private static string N(long v) => v.ToString("#,0", Swiss);

    // ==================================================================
    // Datei
    // ==================================================================
    private void bt_Open_Click(object sender, RoutedEventArgs e) => PickFile();

    private void bt_Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Length > 0) LoadFile(_path);
    }

    public void PickFile()
    {
        if (IsRunning) return;
        var dlg = new OpenFileDialog
        {
            Filter = Loc.T("Filter_Csv"),
            InitialDirectory = Directory.Exists(S.LastDirectory) ? S.LastDirectory : "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        S.LastDirectory = Path.GetDirectoryName(dlg.FileName) ?? "";
        LoadFile(dlg.FileName);
    }

    public void LoadFile(string path)
    {
        CsvSource src;
        List<string[]> rows;
        try
        {
            src = new CsvSource(path, S.Delimiter, S.Encoding);
            rows = src.Preview(20);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Msg_FileUnreadable", ex.Message), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _path = path;
        _header = src.Header;
        _preview = rows;
        _delimiterDisplay = src.DelimiterDisplay;
        tb_Path.Text = path;

        GuessMapping();
        LoadMappingControls();
        BuildPreviewColumns();
        dg_Preview.ItemsSource = _preview;

        _total = 0;
        tb_StartRow.Text = "1";
        bt_Resume.IsEnabled = false;
        UpdateInfo();
        UpdateSample();
        RefreshTexts();
        StartCounting(path);
    }

    private void StartCounting(string path)
    {
        _countCts?.Cancel();
        var cts = _countCts = new CancellationTokenSource();
        var delimiter = S.Delimiter;
        var encoding = S.Encoding;
        Task.Run(() => new CsvSource(path, delimiter, encoding).CountRows(cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.IsFaulted || t.IsCanceled || path != _path) return;
                _total = t.Result;
                UpdateInfo();
                UpdateResumeButton(logIt: true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateInfo()
    {
        if (_path.Length == 0) { tb_Info.Text = ""; return; }
        var mb = new FileInfo(_path).Length / 1e6;
        var rows = _total > 0 ? Loc.T("Info_Rows", N(_total)) : Loc.T("Info_Counting");
        tb_Info.Text = Loc.T("Info_File", _header.Count, _delimiterDisplay, mb.ToString("0.0", CultureInfo.InvariantCulture)) + " · " + rows;
    }

    private void UpdateResumeButton(bool logIt = false)
    {
        if (_path.Length == 0 || _total == 0)
        {
            bt_Resume.IsEnabled = false;
            bt_Resume.Content = Loc.T("Btn_UseResume");
            return;
        }
        var ck = S.GetCheckpoint(_path);
        if (ck > 0 && ck < _total)
        {
            bt_Resume.IsEnabled = !IsRunning;
            bt_Resume.Content = Loc.T("Btn_ResumeFrom", N(ck + 1));
            if (logIt) AppendLog(Loc.T("Log_CheckpointFound", ck + 1, _total));
        }
        else if (ck >= _total)
        {
            bt_Resume.IsEnabled = false;
            bt_Resume.Content = Loc.T("Btn_AlreadyComplete");
        }
        else
        {
            bt_Resume.IsEnabled = false;
            bt_Resume.Content = Loc.T("Btn_UseResume");
        }
    }

    private void bt_Resume_Click(object sender, RoutedEventArgs e) =>
        tb_StartRow.Text = (S.GetCheckpoint(_path) + 1).ToString(CultureInfo.InvariantCulture);

    // ==================================================================
    // Vorschau-Tabelle
    // ==================================================================
    private void BuildPreviewColumns()
    {
        dg_Preview.Columns.Clear();
        for (int i = 0; i < _header.Count; i++)
        {
            dg_Preview.Columns.Add(new DataGridTextColumn
            {
                Binding = new Binding($"[{i}]"),
                MaxWidth = 420,
            });
        }
        HighlightColumns();
    }

    private void HighlightColumns()
    {
        for (int i = 0; i < dg_Preview.Columns.Count && i < _header.Count; i++)
        {
            var name = _header[i];
            var tb = new TextBlock { Text = name };
            if (S.TopicMode == "column" && name == S.TopicColumn)
            {
                tb.Text = $"{name}  [{Loc.T("Lbl_Topic")}]";
                tb.Foreground = TopicBrush; tb.FontWeight = FontWeights.Bold;
            }
            else if (S.PayloadMode == "column" && name == S.PayloadColumn)
            {
                tb.Text = $"{name}  [{Loc.T("Lbl_Payload")}]";
                tb.Foreground = PayloadBrush; tb.FontWeight = FontWeights.Bold;
            }
            else if (S.PayloadMode == "json" && S.ExcludeColumns.Contains(name))
            {
                tb.Foreground = Brushes.Gray;
            }
            dg_Preview.Columns[i].Header = tb;
        }
    }

    private void dg_Preview_LoadingRow(object? sender, DataGridRowEventArgs e) =>
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);

    // ==================================================================
    // Zuordnung (Topic / Payload)
    // ==================================================================
    /// <summary>Neue Datei: gespeicherte Zuordnung übernehmen, wenn die Spalten passen – sonst erraten.</summary>
    private void GuessMapping()
    {
        var cols = _header.Where(h => h.Length > 0).ToList();
        if (cols.Count == 0) return;
        if (!cols.Contains(S.TopicColumn))
        {
            S.TopicColumn = PayloadBuilder.GuessColumn(cols, PayloadBuilder.TopicHints);
            if (S.TopicColumn.Length > 0) S.TopicMode = "column";
        }
        if (!cols.Contains(S.PayloadColumn))
        {
            S.PayloadColumn = PayloadBuilder.GuessColumn(cols, PayloadBuilder.PayloadHints);
            if (S.PayloadColumn.Length > 0) S.PayloadMode = "column";
            else if (S.PayloadMode == "column") S.PayloadMode = "json";
        }
        if (S.TopicMode == "column" && S.TopicColumn.Length == 0) S.TopicMode = "template";
        if (S.TsColumn.Length > 0 && !cols.Contains(S.TsColumn)) S.TsColumn = "";
    }

    private void LoadMappingControls()
    {
        var was = _loading;
        _loading = true;
        var cols = _header.Where(h => h.Length > 0).ToList();

        cb_TopicColumn.ItemsSource = cols;
        cb_TopicColumn.SelectedItem = cols.Contains(S.TopicColumn) ? S.TopicColumn : null;
        cb_PayloadColumn.ItemsSource = cols;
        cb_PayloadColumn.SelectedItem = cols.Contains(S.PayloadColumn) ? S.PayloadColumn : null;

        var tsItems = new List<string> { Loc.T("None") };
        tsItems.AddRange(cols);
        cb_TsColumn.ItemsSource = tsItems;
        cb_TsColumn.SelectedIndex = cols.Contains(S.TsColumn) ? cols.IndexOf(S.TsColumn) + 1 : 0;

        ic_JsonColumns.Items.Clear();
        foreach (var c in cols)
        {
            var chk = new CheckBox { Content = c, IsChecked = !S.ExcludeColumns.Contains(c), Margin = new Thickness(0, 1, 12, 1) };
            chk.Checked += Mapping_Changed;
            chk.Unchecked += Mapping_Changed;
            ic_JsonColumns.Items.Add(chk);
        }

        rb_TopicColumn.IsChecked = S.TopicMode == "column";
        rb_TopicTemplate.IsChecked = S.TopicMode != "column";
        rb_PayloadColumn.IsChecked = S.PayloadMode == "column";
        rb_PayloadJson.IsChecked = S.PayloadMode != "column";
        tb_TopicPrefix.Text = S.TopicPrefix;
        tb_TopicTemplate.Text = S.TopicTemplate;
        chk_ValidateJson.IsChecked = S.ValidateJson;
        UpdateMappingEnabled();
        _loading = was;
    }

    private void UpdateMappingEnabled()
    {
        bool tcol = rb_TopicColumn.IsChecked == true;
        cb_TopicColumn.IsEnabled = tcol;
        tb_TopicPrefix.IsEnabled = tcol;
        tb_TopicTemplate.IsEnabled = !tcol;
        bool pcol = rb_PayloadColumn.IsChecked == true;
        cb_PayloadColumn.IsEnabled = pcol;
        chk_ValidateJson.IsEnabled = pcol;
        // JSON-Spaltenauswahl nur zeigen, wenn sie gebraucht wird (spart Platz)
        dp_Json.Visibility = pcol ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Steuerelemente -> App.Settings.</summary>
    public void ApplyMapping()
    {
        S.TopicMode = rb_TopicColumn.IsChecked == true ? "column" : "template";
        S.PayloadMode = rb_PayloadColumn.IsChecked == true ? "column" : "json";
        if (_header.Count > 0)
        {
            S.TopicColumn = cb_TopicColumn.SelectedItem as string ?? "";
            S.PayloadColumn = cb_PayloadColumn.SelectedItem as string ?? "";
            S.TsColumn = cb_TsColumn.SelectedIndex > 0 ? cb_TsColumn.SelectedItem as string ?? "" : "";
            S.ExcludeColumns = ic_JsonColumns.Items.OfType<CheckBox>()
                .Where(c => c.IsChecked != true).Select(c => (string)c.Content).ToList();
        }
        S.TopicPrefix = tb_TopicPrefix.Text;
        S.TopicTemplate = tb_TopicTemplate.Text.Trim();
        S.ValidateJson = chk_ValidateJson.IsChecked == true;
    }

    private void Mapping_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateMappingEnabled();
        ApplyMapping();
        HighlightColumns();
        UpdateSample();
        MainWindow.TrySave();
    }

    private void UpdateSample()
    {
        if (_path.Length == 0) { tb_Sample.Text = ""; return; }
        if (_preview.Count == 0) { tb_Sample.Text = Loc.T("Sample_NoRows"); return; }
        try
        {
            var (topic, payload) = new PayloadBuilder(S, _header).Build(0, _preview[0]);
            tb_Sample.Text = $"Topic:   {topic}\nPayload: {Encoding.UTF8.GetString(payload)}";
            tb_Sample.Foreground = (Brush)FindResource("TextBrush");
        }
        catch (Exception ex) when (ex is MappingException or RowException)
        {
            tb_Sample.Text = Loc.T("Sample_Problem", ex.Message);
            tb_Sample.Foreground = Brushes.Firebrick;
        }
    }

    /// <summary>Nach Speichern der Einstellungen bzw. Wechsel auf diese Seite.</summary>
    public void OnSettingsChanged()
    {
        UpdateRateInfo();
        UpdateSample();
    }

    private void UpdateRateInfo() =>
        tb_RateInfo.Text = Loc.T("Info_Rate", S.RatePerSecond.ToString("0.##", CultureInfo.InvariantCulture), S.Qos, S.Host, S.Port);

    /// <summary>Texte, die im Code gesetzt werden, nach Sprachwechsel neu setzen.</summary>
    private void RefreshTexts()
    {
        UpdateInfo();
        UpdateRateInfo();
        UpdateResumeButton();
        UpdateSample();
        HighlightColumns();
        if (_header.Count > 0 && cb_TsColumn.ItemsSource is List<string> ts && ts.Count > 0)
        {
            var was = _loading; _loading = true;
            var sel = cb_TsColumn.SelectedIndex;
            ts[0] = Loc.T("None");
            cb_TsColumn.Items.Refresh();
            cb_TsColumn.SelectedIndex = sel;
            _loading = was;
        }
        bt_Pause.Content = "⏸ " + Loc.T(_job?.IsPaused == true ? "Btn_Continue" : "Btn_Pause");
        if (_lastProgress is not null) ShowProgress(_lastProgress);
    }

    // ==================================================================
    // Senden
    // ==================================================================
    private void SetRunning(bool running)
    {
        bt_DryRun.IsEnabled = !running;
        bt_Start.IsEnabled = !running;
        bt_Pause.IsEnabled = running;
        bt_Stop.IsEnabled = running;
        bt_Open.IsEnabled = !running;
        bt_Reload.IsEnabled = !running;
        tb_StartRow.IsEnabled = !running;
        tb_Count.IsEnabled = !running;
        gb_Mapping.IsEnabled = !running;
        if (!running) bt_Pause.Content = "⏸ " + Loc.T("Btn_Pause");
        UpdateResumeButton();
    }

    private void bt_DryRun_Click(object sender, RoutedEventArgs e) => Start(dryRun: true);
    private void bt_Start_Click(object sender, RoutedEventArgs e) => Start(dryRun: false);

    private void Start(bool dryRun)
    {
        if (_path.Length == 0)
        {
            MessageBox.Show(Loc.T("Msg_OpenFirst"), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Window.GetWindow(this) is MainWindow mw) mw.SettingsPage.ApplyToSettings();
        ApplyMapping();
        UpdateRateInfo();
        try
        {
            _ = new PayloadBuilder(S, _header);
        }
        catch (MappingException ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Grp_Mapping"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long start = Math.Max(0, (long.TryParse(tb_StartRow.Text.Replace("'", ""), out var sr) ? sr : 1) - 1);
        long? count = long.TryParse(tb_Count.Text.Replace("'", ""), out var c) && c > 0 ? c : null;
        long remaining = _total > 0 ? Math.Max(0, _total - start) : 0;
        _runTarget = count is long n ? Math.Min(n, remaining) : remaining;

        if (!dryRun)
        {
            var topicDesc = S.TopicMode == "column" ? Loc.T("Confirm_TopicColumn", S.TopicColumn) : S.TopicTemplate;
            var est = S.RatePerSecond > 0 ? TimeSpan.FromSeconds(_runTarget / S.RatePerSecond) : TimeSpan.Zero;
            var msg = Loc.T("Confirm_Send", N(_runTarget), N(start + 1), S.Host, S.Port, topicDesc,
                S.RatePerSecond.ToString("0.##", CultureInfo.InvariantCulture), FormatDuration(est));
            if (MessageBox.Show(msg, Loc.T("Btn_Start"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        MainWindow.TrySave();
        OpenLogFile();

        var job = new SendJob(S.Clone(), _path, start, count, dryRun);
        job.Log += m => Dispatcher.InvokeAsync(() => AppendLog(m));
        job.Progress += p => Dispatcher.InvokeAsync(() => ShowProgress(p));
        var path = _path;
        if (!dryRun)
            job.Checkpoint += done => Dispatcher.InvokeAsync(() => SaveCheckpoint(path, done));

        _job = job;
        _runStart = DateTime.UtcNow;
        pb_Progress.Value = 0;
        SetRunning(true);
        _jobTask = Task.Run(() =>
        {
            SendProgress result;
            try { result = job.Run(); }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => AppendLog(Loc.T("Log_Error", ex.Message)));
                result = new SendProgress(start, 0, 0, 0, 0, 0, false, Loc.T("State_Error"));
            }
            Dispatcher.InvokeAsync(() => OnFinished(job, path, result));
        });
    }

    private void bt_Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        if (_job.IsPaused)
        {
            _job.Resume();
            bt_Pause.Content = "⏸ " + Loc.T("Btn_Pause");
            AppendLog(Loc.T("Log_Resumed"));
        }
        else
        {
            _job.Pause();
            bt_Pause.Content = "⏸ " + Loc.T("Btn_Continue");
            AppendLog(Loc.T("Log_Paused"));
        }
    }

    private void bt_Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        AppendLog(Loc.T("Log_StopRequested"));
        _job.Stop();
    }

    /// <summary>Beim Schliessen des Fensters: stoppen und kurz auf Abschluss warten.</summary>
    public void StopAndWait(TimeSpan timeout)
    {
        _job?.Stop();
        _jobTask?.Wait(timeout);
    }

    private void ShowProgress(SendProgress p)
    {
        _lastProgress = p;
        long doneRun = p.Sent + p.Errors;
        if (_runTarget > 0)
        {
            var frac = Math.Min(1.0, (double)doneRun / _runTarget);
            pb_Progress.Value = frac * 1000;
            tb_Percent.Text = $"{frac * 100:0} %";
        }
        tb_State.Text = p.State;
        tb_State.Foreground = p.State == Loc.T("State_Done") ? Brushes.Green
            : p.State == Loc.T("State_NoConnection") || p.State == Loc.T("State_Error") ? Brushes.Firebrick
            : (Brush)FindResource("TextBrush");
        tb_Conn.Text = Loc.T(p.Connected ? "Conn_Connected" : "Conn_Disconnected");
        tb_Conn.Foreground = p.Connected ? Brushes.Green : Brushes.Firebrick;
        tb_Pos.Text = $"{N(p.Done)} / {N(_total)}";
        tb_Sent.Text = N(p.Sent);
        tb_Acked.Text = Loc.T("Stat_AckedValue", N(p.Acked), p.Inflight);
        tb_Errors.Text = N(p.Errors);
        tb_Rate.Text = $"{p.Rate:0.0} /s";
        var elapsed = (DateTime.UtcNow - _runStart).TotalSeconds;
        var avg = elapsed > 1 ? doneRun / elapsed : 0;
        var r = p.Rate > 0 ? p.Rate : avg;
        tb_Eta.Text = r > 0 && _runTarget > 0 ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, _runTarget - doneRun) / r)) : "–";
    }

    private void SaveCheckpoint(string path, long done)
    {
        S.SetCheckpoint(path, done);
        MainWindow.TrySave();
    }

    private void OnFinished(SendJob job, string path, SendProgress p)
    {
        ShowProgress(p);
        if (!job.DryRun)
        {
            SaveCheckpoint(path, p.Done);
            if (p.Done > 0 && p.Done < _total)
                tb_StartRow.Text = (p.Done + 1).ToString(CultureInfo.InvariantCulture);
        }
        _job = null;
        _jobTask = null;
        SetRunning(false);
        _logFile?.Dispose();
        _logFile = null;
    }

    private static string FormatDuration(TimeSpan t) =>
        $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    // ==================================================================
    // Log
    // ==================================================================
    private void OpenLogFile()
    {
        try
        {
            _logFile?.Dispose();
            var file = Path.Combine(AppSettings.LogFolder, $"upload_{DateTime.Now:yyyyMMdd}.log");
            _logFile = new StreamWriter(file, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception)
        {
            _logFile = null;
        }
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        if (tb_Log.LineCount > 3000)
            tb_Log.Text = tb_Log.Text[(tb_Log.Text.Length / 2)..];
        tb_Log.AppendText(line + Environment.NewLine);
        tb_Log.ScrollToEnd();
        try { _logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd} {line}"); }
        catch (IOException) { }
    }

    private void Help_Click(object sender, RoutedEventArgs e) =>
        MainWindow.ShowHelp((sender as FrameworkElement)?.Tag as string ?? "");
}
