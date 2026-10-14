using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using D2cDataUpload.Core.Mqtt;

namespace D2cDataUpload.Core;

public sealed record SendProgress(
    long Done,        // Zeilen ab Dateianfang, die sicher erledigt sind (Fortsetzungspunkt)
    long Sent,        // in diesem Lauf publiziert
    long Acked,       // in diesem Lauf bestätigt
    long Errors,      // übersprungene Zeilen
    int Inflight,     // unbestätigt
    double Rate,      // Nachrichten/s (gleitend 5 s)
    bool Connected,
    string State);

/// <summary>
/// Liest die CSV streamend und sendet gedrosselt an den Broker.
/// Run() blockiert – im Hintergrund-Thread aufrufen. Steuerung über Pause/Resume/Stop.
/// </summary>
public sealed class SendJob
{
    private sealed record Pending(long Row, string Topic, byte[] Payload);

    private readonly AppSettings _s;
    private readonly string _path;
    private readonly long _start;
    private readonly long? _maxRows;
    private readonly bool _dryRun;

    private readonly ManualResetEventSlim _run = new(true);
    private volatile bool _stop;
    private readonly object _lock = new();
    private readonly Dictionary<ushort, Pending> _pending = [];
    private readonly HashSet<ushort> _early = [];
    private readonly Queue<long> _sentTimes = new();
    private MiniMqttClient? _client;
    private volatile bool _connected;
    private long _lastIdx;
    private long _sent, _acked, _errors;
    private string _state = "";
    private long _lastEmit, _lastCkpt;

    public event Action<SendProgress>? Progress;
    public event Action<string>? Log;
    public event Action<long>? Checkpoint;

    public bool DryRun => _dryRun;
    public bool IsPaused => !_run.IsSet;

    public SendJob(AppSettings settings, string csvPath, long startRow = 0, long? maxRows = null, bool dryRun = false)
    {
        _s = settings;
        _path = csvPath;
        _start = Math.Max(0, startRow);
        _maxRows = maxRows;
        _dryRun = dryRun;
        _lastIdx = _start - 1;
    }

    public void Pause() => _run.Reset();
    public void Resume() => _run.Set();
    public void Stop() { _stop = true; _run.Set(); }

    // ------------------------------------------------------------------
    public static MqttConnectOptions Options(AppSettings s) => new(
        s.Host.Trim(), s.Port, s.ClientId, s.Username, s.Password, s.Keepalive,
        s.UseTls, string.IsNullOrWhiteSpace(s.CaFile) ? null : s.CaFile, s.TlsInsecure);

    public static string Describe(Exception e) => e switch
    {
        MqttConnectRefusedException r when r.Code is 4 or 5 => Loc.T("Err_NotAuthorized", r.Message),
        MqttConnectRefusedException r => Loc.T("Err_Refused", r.Message),
        AuthenticationException a => Loc.T("Err_TlsCert", a.InnerException?.Message ?? a.Message),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => Loc.T("Err_ConnRefused"),
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => Loc.T("Err_HostNotFound"),
        TimeoutException => Loc.T("Err_NoAnswer"),
        IOException { InnerException: not null } io => Describe(io.InnerException!),
        _ => e.Message,
    };

    public static async Task<(bool Ok, string Message)> TestConnectionAsync(AppSettings s)
    {
        await using var c = new MiniMqttClient();
        try
        {
            await c.ConnectAsync(Options(s)).ConfigureAwait(false);
            return (true, Loc.T("Msg_ConnectionOk"));
        }
        catch (Exception e)
        {
            return (false, Describe(e));
        }
    }

    // ------------------------------------------------------------------
    private void Emit(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastEmit < 250) return;
        _lastEmit = now;
        var sw = Stopwatch.GetTimestamp();
        while (_sentTimes.Count > 0 && Stopwatch.GetElapsedTime(_sentTimes.Peek(), sw).TotalSeconds > 5) _sentTimes.Dequeue();
        double rate = 0;
        if (_sentTimes.Count > 1)
        {
            var span = Stopwatch.GetElapsedTime(_sentTimes.Peek(), sw).TotalSeconds;
            if (span > 0) rate = (_sentTimes.Count - 1) / span;
        }
        int inflight;
        lock (_lock) inflight = _pending.Count;
        var done = CheckpointValue();
        Progress?.Invoke(new SendProgress(done, _sent, Interlocked.Read(ref _acked), _errors, inflight, rate, _connected, _state));
        if (force || now - _lastCkpt > 2000)
        {
            _lastCkpt = now;
            if (!_dryRun) Checkpoint?.Invoke(done);
        }
    }

    private long CheckpointValue()
    {
        lock (_lock)
            if (_pending.Count > 0) return _pending.Values.Min(p => p.Row);
        return _lastIdx + 1;
    }

    private void OnAck(ushort id)
    {
        lock (_lock)
        {
            if (_pending.Remove(id)) Interlocked.Increment(ref _acked);
            else _early.Add(id);
        }
    }

    private void OnDisconnected(Exception? e)
    {
        _connected = false;
        if (!_stop) Log?.Invoke(Loc.T("Log_Disconnected", e is null ? "" : Describe(e)));
    }

    /// <summary>Wartet, bis cond() wahr ist. false bei Stopp/Timeout.</summary>
    private bool WaitFor(Func<bool> cond, int timeoutMs = -1)
    {
        var t0 = Environment.TickCount64;
        while (!cond())
        {
            if (_stop) return false;
            if (timeoutMs >= 0 && Environment.TickCount64 - t0 > timeoutMs) return false;
            Emit();
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>Verbindung wieder aufbauen (Backoff 1..30 s) und Unbestätigtes erneut senden.</summary>
    private bool Reconnect()
    {
        int delay = 1000;
        while (!_stop)
        {
            _state = Loc.T("State_Reconnecting");
            Emit(true);
            try
            {
                _client!.ConnectAsync(Options(_s)).GetAwaiter().GetResult();
                _connected = true;
                Log?.Invoke(Loc.T("Log_Connected", _s.Host, _s.Port));
                List<Pending> resend;
                lock (_lock)
                {
                    resend = _pending.Values.OrderBy(p => p.Row).ToList();
                    _pending.Clear();
                    _early.Clear();
                }
                if (resend.Count > 0) Log?.Invoke(Loc.T("Log_Resend", resend.Count));
                foreach (var p in resend)
                    if (!PublishOne(p)) return false;
                _state = Loc.T("State_Sending");
                return true;
            }
            catch (Exception e) when (e is not MqttConnectRefusedException)
            {
                Log?.Invoke(Loc.T("Log_ReconnectFailed", Describe(e), delay / 1000));
            }
            catch (MqttConnectRefusedException e)
            {
                Log?.Invoke(Describe(e));
            }
            var until = Environment.TickCount64 + delay;
            while (!_stop && Environment.TickCount64 < until) { Emit(); Thread.Sleep(100); }
            delay = Math.Min(delay * 2, 30000);
        }
        return false;
    }

    /// <summary>Veröffentlicht eine Nachricht; bei Verbindungsverlust wird neu verbunden und wiederholt.</summary>
    private bool PublishOne(Pending p)
    {
        while (!_stop)
        {
            if (!_connected && !Reconnect()) return false;
            if (_s.Qos > 0 && !WaitFor(() => { lock (_lock) return _pending.Count < Math.Max(1, _s.MaxInflight) || !_connected; }))
                return false;
            if (!_connected) continue;
            try
            {
                var id = _client!.PublishAsync(p.Topic, p.Payload, _s.Qos, _s.Retain).GetAwaiter().GetResult();
                if (_s.Qos > 0)
                {
                    lock (_lock)
                    {
                        if (!_early.Remove(id)) _pending[id] = p;
                        else Interlocked.Increment(ref _acked);
                    }
                }
                else Interlocked.Increment(ref _acked);
                return true;
            }
            catch (Exception)
            {
                _connected = false; // Disconnected-Event folgt bzw. Reconnect im nächsten Durchlauf
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    public SendProgress Run()
    {
        var src = new CsvSource(_path, _s.Delimiter, _s.Encoding);
        var builder = new PayloadBuilder(_s, src.Header);
        Log?.Invoke(Loc.T("Log_File", _path, src.DelimiterDisplay, src.Header.Count));
        if (_start > 0) Log?.Invoke(Loc.T("Log_StartRow", _start + 1));

        if (!_dryRun)
        {
            _client = new MiniMqttClient();
            _client.Acknowledged += OnAck;
            _client.Disconnected += OnDisconnected;
            _state = Loc.T("State_Connecting");
            Emit(true);
            try
            {
                _client.ConnectAsync(Options(_s)).GetAwaiter().GetResult();
                _connected = true;
                Log?.Invoke(Loc.T("Log_Connected", _s.Host, _s.Port));
            }
            catch (Exception e)
            {
                Log?.Invoke(Loc.T("Log_ConnectFailed", Describe(e)));
                _state = Loc.T("State_NoConnection");
                Emit(true);
                return Snapshot();
            }
        }
        else Log?.Invoke(Loc.T("Log_DryRun"));

        double interval = _s.RatePerSecond > 0 ? 1.0 / _s.RatePerSecond : 0;
        var clock = Stopwatch.StartNew();
        double next = 0;
        long count = 0;
        _state = _dryRun ? Loc.T("State_Checking") : Loc.T("State_Sending");

        try
        {
            foreach (var (idx, row) in src.Rows(_start))
            {
                if (_maxRows is long m && count >= m) break;
                if (_stop) break;
                if (!_run.IsSet)
                {
                    _state = Loc.T("State_Paused");
                    Emit(true);
                    while (!_run.Wait(200)) Emit();
                    if (_stop) break;
                    _state = Loc.T("State_Sending");
                    next = clock.Elapsed.TotalSeconds;
                }

                string topic; byte[] payload;
                try
                {
                    (topic, payload) = builder.Build(idx, row);
                }
                catch (RowException e)
                {
                    _errors++;
                    if (_errors <= 200) Log?.Invoke(Loc.T("Log_RowSkipped", idx + 2, e.Message));
                    else if (_errors == 201) Log?.Invoke(Loc.T("Log_MoreErrors"));
                    _lastIdx = idx;
                    count++;
                    continue;
                }

                if (_dryRun)
                {
                    _sent++; Interlocked.Increment(ref _acked);
                    _lastIdx = idx;
                    count++;
                    if (count == 1) Log?.Invoke(Loc.T("Log_Example", topic, Short(payload)));
                    Emit();
                    continue;
                }

                // Drosselung: fester Takt, nach Unterbruch kein "Nachholen"
                if (interval > 0)
                {
                    var now = clock.Elapsed.TotalSeconds;
                    if (next > now) Thread.Sleep(TimeSpan.FromSeconds(next - now));
                    else if (now - next > 1.0) next = now;
                    next += interval;
                }

                if (!PublishOne(new Pending(idx, topic, payload))) break;
                _lastIdx = idx;
                _sent++;
                count++;
                _sentTimes.Enqueue(Stopwatch.GetTimestamp());
                if (count == 1) Log?.Invoke(Loc.T("Log_FirstMessage", topic, Short(payload)));
                Emit();
            }

            // auf ausstehende Bestätigungen warten
            if (_client is not null && _s.Qos > 0)
            {
                _state = Loc.T("State_WaitingAcks");
                Emit(true);
                var until = Environment.TickCount64 + (_stop ? 5000 : 60000);
                while (Environment.TickCount64 < until)
                {
                    int n;
                    lock (_lock) n = _pending.Count;
                    if (n == 0) break;
                    if (!_connected && !_stop && !Reconnect()) break;
                    Emit();
                    Thread.Sleep(50);
                }
                int left;
                lock (_lock) left = _pending.Count;
                if (left > 0) Log?.Invoke(Loc.T("Log_Unacked", left));
            }
        }
        finally
        {
            if (_client is not null)
            {
                _client.Disconnected -= OnDisconnected;
                try { _client.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            }
            _connected = false;
        }

        _state = _stop ? Loc.T("State_Stopped") : Loc.T("State_Done");
        Emit(true);
        var p = Snapshot();
        Log?.Invoke(Loc.T("Log_Summary", _state, p.Sent, p.Acked, p.Errors, p.Done + 1));
        return p;
    }

    private SendProgress Snapshot()
    {
        int inflight;
        lock (_lock) inflight = _pending.Count;
        return new SendProgress(CheckpointValue(), _sent, Interlocked.Read(ref _acked), _errors, inflight, 0, _connected, _state);
    }

    private static string Short(byte[] payload)
    {
        var s = Encoding.UTF8.GetString(payload);
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
