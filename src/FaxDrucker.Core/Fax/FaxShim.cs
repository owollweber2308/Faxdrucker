using System.Runtime.InteropServices;

namespace FaxDrucker.Core.Fax;

/// <summary>P/Invoke-Anbindung an native/faxshim (spandsp-Wrapper).</summary>
internal static unsafe class FaxShim
{
    private const string Lib = "faxshim";

    public const int ModeAudio = 0;
    public const int ModeT38 = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int T38TxCallback(IntPtr userData, byte* buf, int len, int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(IntPtr userData, int level, IntPtr text);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fs_new(int mode, int calling,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? txTiff,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rxTiff,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? ident,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? header,
        int ecm);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_free(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_set_log(IntPtr s, LogCallback? cb, IntPtr userData, int verbose);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_t38_set_tx(IntPtr s, T38TxCallback? cb, IntPtr userData);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_t38_rx(IntPtr s, byte* buf, int len, ushort seqNo);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_t38_tick(IntPtr s, int samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_audio_rx(IntPtr s, short* amp, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_audio_tx(IntPtr s, short* amp, int maxLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_is_done(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_result(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fs_result_text(int code);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_pages_tx(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_pages_rx(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_bit_rate(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fs_remote_ident(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_terminate(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_alaw_encode(short* amp, byte* output, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_alaw_decode(byte* input, short* amp, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_ulaw_encode(short* amp, byte* output, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fs_ulaw_decode(byte* input, short* amp, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fs_spandsp_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fs_code_no_resolution_support();
}
