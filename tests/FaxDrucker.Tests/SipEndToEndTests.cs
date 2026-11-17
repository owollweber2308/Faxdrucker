using System.Net;
using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Fax;
using FaxDrucker.Core.Media;
using FaxDrucker.Core.Sip;
using SIPSorcery.SIP;
using Xunit;
using Xunit.Abstractions;

namespace FaxDrucker.Tests;

/// <summary>
/// Vollständiger Anruf über SIP auf 127.0.0.1 gegen eine simulierte Gegenstelle,
/// die wie ein Faxgerät hinter einem Anbieter antwortet (erst Audio, dann Re-INVITE auf T.38).
/// Läuft in Echtzeit (ca. 30–40 s je Test).
/// </summary>
public class SipEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public SipEndToEndTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task SendsFaxViaT38AfterReinvite()
    {
        var (result, rxPages) = await RunAsync(offerT38: true, policy: T38Policy.Auto);
        Assert.True(result.Success, result.Message);
        Assert.Equal(FaxTransport.T38, result.Fax!.Transport);
        Assert.Equal(2, result.Fax.Pages);
        Assert.Equal(2, rxPages);
    }

    [Fact]
    public async Task SendsFaxViaG711WhenT38Disabled()
    {
        var (result, rxPages) = await RunAsync(offerT38: true, policy: T38Policy.Off);
        Assert.True(result.Success, result.Message);
        Assert.Equal(FaxTransport.Audio, result.Fax!.Transport);
        Assert.Equal(2, rxPages);
    }

    [Fact]
    public async Task ReportsWrongPassword()
    {
        using var server = new FakeFaxServer(busy: false, offerT38: false, rxTiff: null, log: _out.WriteLine)
        {
            RequireAuth = ("777000", "richtig"),
        };
        var call = new SipFaxCall(new SipAccount { Domain = $"127.0.0.1:{server.Port}", Username = "777000", Password = "falsch" });
        var tif = await TestDocs.CreateTiffAsync();
        var result = await call.SendAsync("0221123", new FaxJobParameters(tif, null, true, "+49 1", "Test"));
        Assert.Equal(FaxCallStatus.Error, result.Status);
        Assert.Contains("Anmeldung", result.Message);
    }

    [Fact]
    public async Task ReportsBusy()
    {
        using var server = new FakeFaxServer(busy: true, offerT38: false, rxTiff: null, log: _out.WriteLine);
        var call = new SipFaxCall(new SipAccount { Domain = $"127.0.0.1:{server.Port}", Username = "test", Password = "x" });
        var tif = await TestDocs.CreateTiffAsync();
        var result = await call.SendAsync("0221123", new FaxJobParameters(tif, null, true, "+49 1", "Test"));
        Assert.Equal(FaxCallStatus.Busy, result.Status);
        Assert.True(result.Retryable);
    }

    private async Task<(FaxCallResult, int)> RunAsync(bool offerT38, T38Policy policy)
    {
        var tif = await TestDocs.CreateTiffAsync();
        var rx = Path.Combine(Path.GetTempPath(), $"e2e-rx-{Guid.NewGuid():N}.tif");
        using var server = new FakeFaxServer(busy: false, offerT38: offerT38, rxTiff: rx, log: m => Log("[Gegenstelle] " + m))
        {
            RequireAuth = ("777000", "geheim"),
        };
        var call = new SipFaxCall(new SipAccount
        {
            Domain = $"127.0.0.1:{server.Port}", Username = "777000", Password = "geheim", T38 = policy,
            RtpPortMin = 0, RtpPortMax = 0,
        });
        call.Log += m => Log("[FaxDrucker] " + m);
        var result = await call.SendAsync("0221 123456", new FaxJobParameters(tif, null, true, "+49 30 1111", "FaxDrucker Test"));
        Log($"Ergebnis: {result.Status} {result.Message}");
        await Task.Delay(300);
        int pages = File.Exists(rx) ? TiffInfo.CountPages(rx) : 0;
        return (result, pages);
    }

    private void Log(string m)
    {
        if (m.StartsWith("[FaxDrucker] spandsp") || m.StartsWith("[Gegenstelle] spandsp")) return;
        try { _out.WriteLine(m); } catch { }
    }
}

/// <summary>Simulierte Gegenstelle (Faxgerät hinter SIP-Anbieter).</summary>
internal sealed class FakeFaxServer : IDisposable
{
    private readonly SIPTransport _transport = new();
    private readonly bool _busy;
    private readonly bool _offerT38;
    private readonly string? _rxTiff;
    private readonly Action<string> _log;
    private FaxMediaEngine? _media;
    private readonly SIPUDPChannel _channel;

    public int Port => _channel.ListeningEndPoint.Port;
    public (string User, string Password)? RequireAuth { get; set; }

    public FakeFaxServer(bool busy, bool offerT38, string? rxTiff, Action<string> log)
    {
        _busy = busy;
        _offerT38 = offerT38;
        _rxTiff = rxTiff;
        _log = log;
        _channel = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        _transport.AddSIPChannel(_channel);
        _transport.SIPTransportRequestReceived += OnRequest;
    }

    private Task OnRequest(SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        switch (req.Method)
        {
            case SIPMethodsEnum.INVITE when string.IsNullOrEmpty(req.Header.To.ToTag):
                _ = HandleInvite(req);
                break;
            case SIPMethodsEnum.BYE:
                _log("BYE empfangen");
                new SIPNonInviteTransaction(_transport, req, null).SendResponse(SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null));
                _media?.Abort("BYE");
                break;
        }
        return Task.CompletedTask;
    }

    private async Task HandleInvite(SIPRequest req)
    {
        var uas = new UASInviteTransaction(_transport, req, null, false);
        if (RequireAuth != null)
        {
            // Wie Placetel: erst Digest-Anmeldung verlangen (407), dann prüfen.
            var auth = req.Header.AuthenticationHeaders.FirstOrDefault()?.SIPDigest;
            if (auth == null)
            {
                var challenge = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.ProxyAuthenticationRequired, null);
                challenge.Header.AuthenticationHeaders.Add(new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.ProxyAuthenticate, "fpbx.de", "abc123nonce"));
                uas.SendFinalResponse(challenge);
                _log("407 gesendet");
                return;
            }
            var (user, pass) = RequireAuth.Value;
            static string Md5(string x) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(x))).ToLowerInvariant();
            var ha1 = Md5($"{user}:{auth.Realm}:{pass}");
            var ha2 = Md5($"INVITE:{auth.URI}");
            var expected = string.IsNullOrEmpty(auth.Qop)
                ? Md5($"{ha1}:{auth.Nonce}:{ha2}")
                : Md5($"{ha1}:{auth.Nonce}:{auth.NonceCount:x8}:{auth.Cnonce}:{auth.Qop}:{ha2}");
            if (auth.Username != user || !string.Equals(auth.Response, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log($"Anmeldung falsch (user={auth.Username})");
                uas.SendFinalResponse(SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Forbidden, null));
                return;
            }
            _log("Anmeldung OK");
        }
        if (_busy)
        {
            uas.SendFinalResponse(SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null));
            return;
        }
        var offer = FaxSdp.Parse(req.Body);
        var callerAudio = offer.EndPointOf(offer.Audio!)!;
        _media = new FaxMediaEngine(IPAddress.Loopback, new FaxJobParameters(null, _rxTiff, false, "+49 221 123456", null));
        _media.Log += m => _log(m);

        var sdp = FaxSdp.AudioOffer(IPAddress.Loopback, _media.LocalPort, 99, 1);
        var ok = uas.GetOkResponse("application/sdp", sdp);
        ok.Header.Contact = new List<SIPContactHeader> { new(null, new SIPURI("fax", _channel.ListeningEndPoint.ToString(), null)) };
        uas.SendFinalResponse(ok);
        _media.StartAudio(callerAudio, alaw: true);
        _log("Angenommen (Audio)");

        if (!_offerT38) return;

        // Wie ein Gateway: nach Erkennen des Faxtons auf T.38 umschalten.
        await Task.Delay(1500);
        var callerContact = req.Header.Contact[0].ContactURI;
        var reinvite = SIPRequest.GetRequest(SIPMethodsEnum.INVITE, callerContact,
            new SIPToHeader(req.Header.From.FromName, req.Header.From.FromURI, req.Header.From.FromTag),
            new SIPFromHeader(null, req.Header.To.ToURI, ok.Header.To.ToTag));
        reinvite.Header.CallId = req.Header.CallId;
        reinvite.Header.CSeq = 1;
        reinvite.Header.Contact = ok.Header.Contact;
        reinvite.Header.ContentType = "application/sdp";
        reinvite.Body = FaxSdp.T38Sdp(IPAddress.Loopback, _media.LocalPort, 99, 2);

        var answered = new TaskCompletionSource<SIPResponse>();
        var tx = new UACInviteTransaction(_transport, reinvite, null, false);
        tx.UACInviteTransactionFinalResponseReceived += (l, r, t, resp) => { answered.TrySetResult(resp); return Task.FromResult(SocketErrorOk); };
        tx.SendInviteRequest();
        var resp = await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _log($"Re-INVITE beantwortet: {(int)resp.Status}");
        if (resp.Status == SIPResponseStatusCodesEnum.Ok)
        {
            var ans = FaxSdp.Parse(resp.Body);
            if (ans.T38 != null)
                _media.SwitchToT38(ans.EndPointOf(ans.T38)!);
        }
    }

    private static readonly System.Net.Sockets.SocketError SocketErrorOk = System.Net.Sockets.SocketError.Success;

    public void Dispose()
    {
        _media?.Dispose();
        _transport.Shutdown();
    }
}
