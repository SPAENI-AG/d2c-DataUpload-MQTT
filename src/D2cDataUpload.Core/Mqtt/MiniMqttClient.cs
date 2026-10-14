using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace D2cDataUpload.Core.Mqtt;

public sealed record MqttConnectOptions(
    string Host, int Port, string ClientId,
    string? Username = null, string? Password = null,
    int KeepaliveSeconds = 60,
    bool UseTls = false, string? CaFile = null, bool TlsInsecure = false,
    int ConnectTimeoutMs = 10000);

/// <summary>Broker hat die Anmeldung abgelehnt (CONNACK != 0).</summary>
public sealed class MqttConnectRefusedException(byte code)
    : Exception($"CONNACK {code}: " + code switch
    {
        1 => "unacceptable protocol version",
        2 => "identifier rejected",
        3 => "server unavailable",
        4 => "bad user name or password",
        5 => "not authorized",
        _ => "unknown",
    })
{
    public byte Code { get; } = code;
}

/// <summary>
/// Schlanker MQTT-3.1.1-Client (nur Publish), ohne externe Abhängigkeiten.
/// Unterstützt QoS 0/1/2, Retain, Benutzer/Passwort, TLS (System- oder eigene CA), Keepalive.
/// Thread-sicher für Publish aus einem Thread und Empfang im Hintergrund.
/// </summary>
public sealed class MiniMqttClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _readLoop, _pingLoop;
    private ushort _nextId;
    private readonly HashSet<ushort> _inUse = [];
    private readonly object _idLock = new();
    private long _lastRx;
    private int _disconnectedRaised;

    /// <summary>Broker hat eine Nachricht bestätigt (QoS 1: PUBACK, QoS 2: PUBCOMP).</summary>
    public event Action<ushort>? Acknowledged;

    /// <summary>Verbindung verloren (nicht bei eigenem DisconnectAsync). Argument: Ursache.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(MqttConnectOptions o, CancellationToken ct = default)
    {
        await CloseTransportAsync().ConfigureAwait(false);
        _disconnectedRaised = 0;
        lock (_idLock) _inUse.Clear();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.ConnectTimeoutMs);
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(o.Host, o.Port, timeout.Token).ConfigureAwait(false);
            Stream s = _tcp.GetStream();
            if (o.UseTls)
            {
                var ssl = new SslStream(s, false, (_, cert, chain, errors) => ValidateCert(o, cert, errors));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = o.Host,
                    EnabledSslProtocols = SslProtocols.None, // Betriebssystem-Standard (TLS 1.2/1.3)
                }, timeout.Token).ConfigureAwait(false);
                s = ssl;
            }
            _stream = s;

            await WriteAsync(BuildConnect(o), timeout.Token).ConfigureAwait(false);
            var (type, body) = await ReadPacketAsync(_stream, timeout.Token).ConfigureAwait(false);
            if (type != 0x20 || body.Length < 2) throw new IOException("Unexpected answer to CONNECT");
            if (body[1] != 0) throw new MqttConnectRefusedException(body[1]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await CloseTransportAsync().ConfigureAwait(false);
            throw new TimeoutException("MQTT connect timeout");
        }
        catch
        {
            await CloseTransportAsync().ConfigureAwait(false);
            throw;
        }

        IsConnected = true;
        Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(Math.Max(5, o.KeepaliveSeconds), _cts.Token));
    }

    private static bool ValidateCert(MqttConnectOptions o, X509Certificate? cert, SslPolicyErrors errors)
    {
        if (o.TlsInsecure) return true;
        if (errors == SslPolicyErrors.None) return true;
        if (string.IsNullOrWhiteSpace(o.CaFile) || cert is null) return false;
        // Eigene CA: Kette gegen die angegebene CA-Datei prüfen, Hostname muss trotzdem stimmen
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        var ca = new X509Certificate2Collection();
        try { ca.ImportFromPemFile(o.CaFile); }
        catch (Exception) { ca.Add(X509CertificateLoader.LoadCertificateFromFile(o.CaFile)); } // DER
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
    }

    /// <summary>Sendet eine Nachricht. Rückgabe: Paket-ID (QoS 1/2) bzw. 0 (QoS 0, sobald geschrieben).</summary>
    public async Task<ushort> PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct = default)
    {
        if (!IsConnected || _stream is null) throw new IOException("not connected");
        ushort id = 0;
        if (qos > 0) id = AllocateId();
        var t = Encoding.UTF8.GetBytes(topic);
        using var ms = new MemoryStream(t.Length + payload.Length + 8);
        WriteU16(ms, t.Length); ms.Write(t);
        if (qos > 0) WriteU16(ms, id);
        ms.Write(payload);
        byte header = (byte)(0x30 | (qos << 1) | (retain ? 1 : 0));
        try
        {
            await WriteAsync(Frame(header, ms.ToArray()), ct).ConfigureAwait(false);
        }
        catch
        {
            if (qos > 0) ReleaseId(id);
            throw;
        }
        return id;
    }

    public async Task DisconnectAsync()
    {
        if (IsConnected && _stream is not null)
        {
            try { await WriteAsync([0xE0, 0x00], CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        _disconnectedRaised = 1; // kein Disconnected-Event bei gewolltem Trennen
        await CloseTransportAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------
    private ushort AllocateId()
    {
        lock (_idLock)
        {
            for (int i = 0; i < 65535; i++)
            {
                _nextId = (ushort)(_nextId == 65535 ? 1 : _nextId + 1);
                if (_inUse.Add(_nextId)) return _nextId;
            }
        }
        throw new InvalidOperationException("no free packet id");
    }

    private void ReleaseId(ushort id) { lock (_idLock) _inUse.Remove(id); }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? cause = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, body) = await ReadPacketAsync(_stream!, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
                switch (type & 0xF0)
                {
                    case 0x40: // PUBACK
                    case 0x70: // PUBCOMP
                    {
                        var id = (ushort)(body[0] << 8 | body[1]);
                        ReleaseId(id);
                        Acknowledged?.Invoke(id);
                        break;
                    }
                    case 0x50: // PUBREC -> PUBREL
                        await WriteAsync([0x62, 0x02, body[0], body[1]], ct).ConfigureAwait(false);
                        break;
                    case 0xD0: // PINGRESP
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) { cause = e; }
        await LostAsync(cause ?? new IOException("connection closed")).ConfigureAwait(false);
    }

    private async Task PingLoopAsync(int keepalive, CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(2, keepalive / 2.0));
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastRx) > keepalive * 1500L)
                {
                    await LostAsync(new TimeoutException("keepalive timeout")).ConfigureAwait(false);
                    return;
                }
                await WriteAsync([0xC0, 0x00], ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { await LostAsync(e).ConfigureAwait(false); }
    }

    private async Task LostAsync(Exception cause)
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 1) return;
        await CloseTransportAsync().ConfigureAwait(false);
        Disconnected?.Invoke(cause);
    }

    private async Task CloseTransportAsync()
    {
        IsConnected = false;
        try { _cts?.Cancel(); } catch { }
        try { if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var s = _stream ?? throw new IOException("not connected");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await s.WriteAsync(data, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task<(byte Type, byte[] Body)> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        await s.ReadExactlyAsync(one, ct).ConfigureAwait(false);
        byte type = one[0];
        int len = 0, mult = 1;
        for (int i = 0; i < 4; i++)
        {
            await s.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            len += (one[0] & 0x7F) * mult;
            if ((one[0] & 0x80) == 0) break;
            mult *= 128;
        }
        var body = new byte[len];
        if (len > 0) await s.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return (type, body);
    }

    private static byte[] BuildConnect(MqttConnectOptions o)
    {
        using var ms = new MemoryStream();
        WriteStr(ms, "MQTT");
        ms.WriteByte(4); // Protokoll 3.1.1
        byte flags = 0x02; // clean session
        if (!string.IsNullOrEmpty(o.Username)) flags |= 0x80;
        if (!string.IsNullOrEmpty(o.Username) && !string.IsNullOrEmpty(o.Password)) flags |= 0x40;
        ms.WriteByte(flags);
        WriteU16(ms, Math.Clamp(o.KeepaliveSeconds, 0, 65535));
        WriteStr(ms, o.ClientId ?? "");
        if ((flags & 0x80) != 0) WriteStr(ms, o.Username!);
        if ((flags & 0x40) != 0) WriteStr(ms, o.Password!);
        return Frame(0x10, ms.ToArray());
    }

    private static byte[] Frame(byte header, byte[] body)
    {
        using var ms = new MemoryStream(body.Length + 5);
        ms.WriteByte(header);
        int len = body.Length;
        do
        {
            byte b = (byte)(len % 128);
            len /= 128;
            if (len > 0) b |= 0x80;
            ms.WriteByte(b);
        } while (len > 0);
        ms.Write(body);
        return ms.ToArray();
    }

    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    private static void WriteStr(Stream s, string v)
    {
        var b = Encoding.UTF8.GetBytes(v);
        WriteU16(s, b.Length);
        s.Write(b);
    }
}
