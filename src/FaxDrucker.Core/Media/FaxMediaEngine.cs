using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FaxDrucker.Core.Fax;

namespace FaxDrucker.Core.Media;

/// <summary>Ergebnis einer Faxübertragung.</summary>
public sealed record FaxOutcome(bool Success, int ResultCode, string ResultText, int Pages, int BitRate,
    string RemoteIdent, FaxTransport Transport);

/// <summary>Parameter für die T.30-Sitzung.</summary>
public sealed record FaxJobParameters(string? TxTiff, string? RxTiff, bool Calling, string Ident, string Header,
    bool Ecm = true, bool VerboseLog = false);

/// <summary>
/// Führt die eigentliche Faxübertragung über einen UDP-Port: zuerst G.711-Audio (RTP),
/// nach einem Wechsel T.38 (UDPTL). Läuft im 20-ms-Takt.
/// </summary>
public sealed class FaxMediaEngine : IDisposable
{
    private const int SamplesPerFrame = 160; // 20 ms bei 8 kHz
    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(20);

    private readonly Socket _socket;
    private readonly FaxJobParameters _job;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<FaxOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    private FaxSession? _session;
    private IPEndPoint? _remote;
    private bool _remoteLatched;
    private bool _alaw = true;
    private int _payloadType = 8;
    private UdptlEncoder? _udptlTx;
    private UdptlDecoder? _udptlRx;
    private ushort _rtpSeq = (ushort)Random.Shared.Next(ushort.MaxValue);
    private uint _rtpTimestamp = (uint)Random.Shared.Next();
    private readonly uint _ssrc = (uint)Random.Shared.Next();
    private Thread? _clockThread;
    private Thread? _rxThread;
    private bool _everStarted;

    public event Action<string>? Log;

    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;
    public FaxTransport? Mode { get; private set; }
    public Task<FaxOutcome> Completion => _done.Task;

    public FaxMediaEngine(IPAddress bindAddress, FaxJobParameters job, int portMin = 0, int portMax = 0)
    {
        _job = job;
        _socket = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Verhindert, dass ICMP "Port unreachable" den Empfang abbricht.
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        Bind(bindAddress, portMin, portMax);
    }

    private void Bind(IPAddress addr, int min, int max)
    {
        if (min <= 0 || max < min)
        {
            _socket.Bind(new IPEndPoint(addr, 0));
            return;
        }
        // RTP-Ports sind üblicherweise gerade
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int port = Random.Shared.Next(min, max + 1) & ~1;
            if (port < min) port += 2;
            try
            {
                _socket.Bind(new IPEndPoint(addr, port));
                return;
            }
            catch (SocketException) { }
        }
        _socket.Bind(new IPEndPoint(addr, 0));
    }

    /// <summary>Startet die Übertragung per G.711-Audio.</summary>
    public void StartAudio(IPEndPoint remote, bool alaw)
    {
        lock (_lock)
        {
            _remote = remote;
            _alaw = alaw;
            _payloadType = alaw ? 8 : 0;
            ReplaceSession(FaxTransport.Audio);
        }
        StartThreads();
        Log?.Invoke($"Audio-Fax ({(alaw ? "PCMA" : "PCMU")}) an {remote}, lokaler Port {LocalPort}");
    }

    /// <summary>Wechselt auf T.38 (nach Re-INVITE). Die T.30-Sitzung beginnt neu.</summary>
    public void SwitchToT38(IPEndPoint remote)
    {
        lock (_lock)
        {
            if (Mode == FaxTransport.T38)
            {
                _remote = remote;
                return;
            }
            _remote = remote;
            _remoteLatched = false;
            _udptlTx = new UdptlEncoder(redundancy: 3);
            _udptlRx = new UdptlDecoder();
            ReplaceSession(FaxTransport.T38);
        }
        StartThreads();
        Log?.Invoke($"Wechsel auf T.38, Gegenstelle {remote}");
    }

    private void ReplaceSession(FaxTransport transport)
    {
        _session?.Dispose();
        var s = new FaxSession(transport, _job.Calling, _job.TxTiff, _job.RxTiff, _job.Ident, _job.Header,
            _job.Ecm, _job.VerboseLog);
        s.Log += m => Log?.Invoke("spandsp: " + m);
        if (transport == FaxTransport.T38)
            s.T38PacketOut += SendT38;
        _session = s;
        Mode = transport;
    }

    private void StartThreads()
    {
        if (_everStarted) return;
        _everStarted = true;
        _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "Fax-Takt", Priority = ThreadPriority.AboveNormal };
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Fax-Empfang" };
        _clockThread.Start();
        _rxThread.Start();
    }

    private void ClockLoop()
    {
        WinTimer.Begin();
        try
        {
            var sw = Stopwatch.StartNew();
            var next = TimeSpan.Zero;
            var pcm = new short[SamplesPerFrame];
            var payload = new byte[SamplesPerFrame];
            while (!_cts.IsCancellationRequested)
            {
                var now = sw.Elapsed;
                if (now < next)
                {
                    Thread.Sleep(Math.Max(1, (int)(next - now).TotalMilliseconds));
                    continue;
                }
                // Rückstand nicht beliebig nachholen
                if (now - next > TimeSpan.FromMilliseconds(200))
                    next = now;
                next += FrameTime;

                lock (_lock)
                {
                    if (_session == null) continue;
                    if (Mode == FaxTransport.Audio)
                    {
                        int n = _session.TransmitAudio(pcm);
                        if (n < SamplesPerFrame) Array.Clear(pcm, Math.Max(n, 0), SamplesPerFrame - Math.Max(n, 0));
                        G711.Encode(_alaw, pcm, payload);
                        SendRtp(payload);
                    }
                    else
                    {
                        _session.TickT38(SamplesPerFrame);
                    }
                    if (_session.IsDone)
                    {
                        Finish();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Fehler im Faxtakt: " + ex.Message);
            _done.TrySetException(ex);
        }
        finally
        {
            WinTimer.End();
        }
    }

    private void Finish()
    {
        var s = _session!;
        int code = s.Result;
        var outcome = new FaxOutcome(code == 0, code, FaxSession.ResultText(code), s.PagesSent, s.BitRate,
            s.RemoteIdent, s.Transport);
        Log?.Invoke($"Fax beendet: {outcome.ResultText} ({outcome.Pages} Seite(n), {outcome.BitRate} bit/s, {outcome.Transport})");
        _done.TrySetResult(outcome);
    }

    /// <summary>Bricht ab und liefert den aktuellen Stand als Ergebnis (z. B. wenn die Gegenstelle auflegt).</summary>
    public void Abort(string reason)
    {
        lock (_lock)
        {
            if (_done.Task.IsCompleted) return;
            if (_session == null)
            {
                _done.TrySetResult(new FaxOutcome(false, -1, reason, 0, 0, "", FaxTransport.Audio));
                return;
            }
            _session.Terminate();
            var s = _session;
            int code = s.Result;
            var text = code >= 0 ? FaxSession.ResultText(code) : reason;
            _done.TrySetResult(new FaxOutcome(code == 0, code, text, s.PagesSent, s.BitRate, s.RemoteIdent, s.Transport));
        }
    }

    private void SendRtp(byte[] payload)
    {
        if (_remote == null) return;
        var pkt = new byte[12 + payload.Length];
        pkt[0] = 0x80;
        pkt[1] = (byte)_payloadType;
        pkt[2] = (byte)(_rtpSeq >> 8);
        pkt[3] = (byte)_rtpSeq;
        pkt[4] = (byte)(_rtpTimestamp >> 24);
        pkt[5] = (byte)(_rtpTimestamp >> 16);
        pkt[6] = (byte)(_rtpTimestamp >> 8);
        pkt[7] = (byte)_rtpTimestamp;
        pkt[8] = (byte)(_ssrc >> 24);
        pkt[9] = (byte)(_ssrc >> 16);
        pkt[10] = (byte)(_ssrc >> 8);
        pkt[11] = (byte)_ssrc;
        Buffer.BlockCopy(payload, 0, pkt, 12, payload.Length);
        _rtpSeq++;
        _rtpTimestamp += (uint)payload.Length;
        TrySend(pkt);
    }

    // Wird innerhalb von _lock aus spandsp heraus aufgerufen.
    private void SendT38(byte[] ifp, int count)
    {
        if (_udptlTx == null) return;
        // Wie üblich: Indikatoren mehrfach senden; Daten einmal (Redundanz steckt im UDPTL).
        for (int i = 0; i < Math.Max(1, count); i++)
            TrySend(_udptlTx.Encode(ifp));
    }

    private void TrySend(byte[] data)
    {
        try
        {
            if (_remote != null)
                _socket.SendTo(data, _remote);
        }
        catch (SocketException ex)
        {
            Log?.Invoke("Senden fehlgeschlagen: " + ex.SocketErrorCode);
        }
        catch (ObjectDisposedException) { }
    }

    private void ReceiveLoop()
    {
        var buf = new byte[4096];
        var pcm = new short[2048];
        EndPoint from = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int len;
            try
            {
                len = _socket.ReceiveFrom(buf, ref from);
            }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
            catch (ObjectDisposedException) { return; }

            lock (_lock)
            {
                if (_session == null || _done.Task.IsCompleted) continue;
                var src = (IPEndPoint)from;
                if (Mode == FaxTransport.Audio)
                {
                    if (!TryRtpPayload(buf.AsSpan(0, len), out var pt, out var payload)) continue;
                    if (pt != 0 && pt != 8) continue; // z. B. DTMF/Comfort Noise ignorieren
                    Latch(src);
                    var samples = pcm.AsSpan(0, Math.Min(payload.Length, pcm.Length));
                    G711.Decode(pt == 8, payload[..samples.Length], samples);
                    _session.ReceiveAudio(samples);
                }
                else if (_udptlRx != null)
                {
                    if (len >= 12 && (buf[0] & 0xC0) == 0x80 && (buf[1] & 0x7F) is 0 or 8 or 13 or 101)
                        continue; // verspätete RTP-Pakete nach dem Wechsel
                    Latch(src);
                    foreach (var ifp in _udptlRx.Decode(buf.AsSpan(0, len)))
                        _session.ReceiveT38(ifp.Data, ifp.Seq);
                }
            }
        }
    }

    /// <summary>Symmetrisches RTP: an die Adresse antworten, von der tatsächlich Pakete kommen (NAT).</summary>
    private void Latch(IPEndPoint src)
    {
        if (_remoteLatched || _remote == null) return;
        _remoteLatched = true;
        if (!src.Equals(_remote))
        {
            Log?.Invoke($"Medienadresse angepasst: {_remote} -> {src}");
            _remote = new IPEndPoint(src.Address, src.Port);
        }
    }

    private static bool TryRtpPayload(Span<byte> p, out int pt, out Span<byte> payload)
    {
        pt = -1;
        payload = default;
        if (p.Length < 12 || (p[0] & 0xC0) != 0x80) return false;
        int cc = p[0] & 0x0F;
        int off = 12 + cc * 4;
        if ((p[0] & 0x10) != 0)
        {
            if (p.Length < off + 4) return false;
            int extLen = (p[off + 2] << 8) | p[off + 3];
            off += 4 + extLen * 4;
        }
        int end = p.Length;
        if ((p[0] & 0x20) != 0 && end > off)
            end -= p[end - 1];
        if (end <= off) return false;
        pt = p[1] & 0x7F;
        payload = p[off..end];
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket.Close(); } catch { }
        _clockThread?.Join(500);
        _rxThread?.Join(500);
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Unter Windows den Systemtakt für Thread.Sleep auf 1 ms setzen.</summary>
    private static class WinTimer
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

        public static void Begin()
        {
            if (OperatingSystem.IsWindows()) try { timeBeginPeriod(1); } catch { }
        }

        public static void End()
        {
            if (OperatingSystem.IsWindows()) try { timeEndPeriod(1); } catch { }
        }
    }
}
