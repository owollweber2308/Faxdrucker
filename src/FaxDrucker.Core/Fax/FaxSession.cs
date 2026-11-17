using System.Runtime.InteropServices;

namespace FaxDrucker.Core.Fax;

public enum FaxTransport
{
    /// <summary>Fax als Töne über G.711 (PCMA/PCMU).</summary>
    Audio,
    /// <summary>Fax als T.38-Datenpakete (UDPTL).</summary>
    T38,
}

/// <summary>
/// Eine T.30-Faxsitzung (spandsp). Nicht threadsicher: alle Aufrufe aus einem Thread.
/// </summary>
public sealed unsafe class FaxSession : IDisposable
{
    private IntPtr _handle;
    // Delegates müssen am Leben bleiben, solange spandsp sie aufrufen kann.
    private readonly FaxShim.T38TxCallback _txCallback;
    private readonly FaxShim.LogCallback _logCallback;

    /// <summary>Ausgehendes T.38-IFP-Paket (Daten, gewünschte Wiederholungen).</summary>
    public event Action<byte[], int>? T38PacketOut;

    /// <summary>Protokollmeldungen von spandsp.</summary>
    public event Action<string>? Log;

    public FaxTransport Transport { get; }

    public FaxSession(FaxTransport transport, bool calling, string? txTiff, string? rxTiff,
        string? ident, string? header, bool ecm = true, bool verboseLog = false)
    {
        Transport = transport;
        _handle = FaxShim.fs_new(transport == FaxTransport.T38 ? FaxShim.ModeT38 : FaxShim.ModeAudio,
            calling ? 1 : 0, txTiff, rxTiff, ident, header, ecm ? 1 : 0);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Faxsitzung konnte nicht angelegt werden (spandsp).");

        _txCallback = OnT38Tx;
        _logCallback = OnLog;
        if (transport == FaxTransport.T38)
            FaxShim.fs_t38_set_tx(_handle, _txCallback, IntPtr.Zero);
        FaxShim.fs_set_log(_handle, _logCallback, IntPtr.Zero, verboseLog ? 1 : 0);
    }

    public static string SpanDspVersion => Marshal.PtrToStringAnsi(FaxShim.fs_spandsp_version()) ?? "?";

    private int OnT38Tx(IntPtr userData, byte* buf, int len, int count)
    {
        var data = new ReadOnlySpan<byte>(buf, len).ToArray();
        T38PacketOut?.Invoke(data, count);
        return 0;
    }

    private void OnLog(IntPtr userData, int level, IntPtr text)
    {
        var s = Marshal.PtrToStringUTF8(text);
        if (!string.IsNullOrEmpty(s))
            Log?.Invoke(s.TrimEnd('\r', '\n'));
    }

    private IntPtr H => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(FaxSession));

    public void ReceiveT38(ReadOnlySpan<byte> ifp, ushort seq)
    {
        fixed (byte* p = ifp)
            FaxShim.fs_t38_rx(H, p, ifp.Length, seq);
    }

    /// <summary>Lässt die T.38-Zeitsteuerung laufen. Liefert true, wenn die Sitzung beendet ist.</summary>
    public bool TickT38(int samples) => FaxShim.fs_t38_tick(H, samples) != 0;

    public void ReceiveAudio(Span<short> samples)
    {
        fixed (short* p = samples)
            FaxShim.fs_audio_rx(H, p, samples.Length);
    }

    public int TransmitAudio(Span<short> samples)
    {
        fixed (short* p = samples)
            return FaxShim.fs_audio_tx(H, p, samples.Length);
    }

    public bool IsDone => FaxShim.fs_is_done(H) != 0;

    /// <summary>T.30-Abschlusscode; 0 = OK, -1 = noch nicht fertig.</summary>
    public int Result => FaxShim.fs_result(H);

    public int PagesSent => FaxShim.fs_pages_tx(H);
    public int PagesReceived => FaxShim.fs_pages_rx(H);
    public int BitRate => FaxShim.fs_bit_rate(H);
    public string RemoteIdent => Marshal.PtrToStringUTF8(FaxShim.fs_remote_ident(H))?.Trim() ?? "";

    public void Terminate() => FaxShim.fs_terminate(H);

    public static string ResultText(int code) =>
        code < 0 ? "Übertragung nicht abgeschlossen" : Marshal.PtrToStringAnsi(FaxShim.fs_result_text(code)) ?? code.ToString();

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            FaxShim.fs_free(_handle);
            _handle = IntPtr.Zero;
        }
        GC.KeepAlive(_txCallback);
        GC.KeepAlive(_logCallback);
    }
}

/// <summary>G.711-Umwandlung über spandsp.</summary>
public static unsafe class G711
{
    public static void Encode(bool alaw, ReadOnlySpan<short> pcm, Span<byte> output)
    {
        fixed (short* a = pcm)
        fixed (byte* o = output)
        {
            if (alaw) FaxShim.fs_alaw_encode(a, o, pcm.Length);
            else FaxShim.fs_ulaw_encode(a, o, pcm.Length);
        }
    }

    public static void Decode(bool alaw, ReadOnlySpan<byte> input, Span<short> pcm)
    {
        fixed (byte* i = input)
        fixed (short* a = pcm)
        {
            if (alaw) FaxShim.fs_alaw_decode(i, a, input.Length);
            else FaxShim.fs_ulaw_decode(i, a, input.Length);
        }
    }
}
