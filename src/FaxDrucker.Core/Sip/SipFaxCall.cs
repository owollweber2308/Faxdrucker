using System.Net;
using System.Net.Sockets;
using FaxDrucker.Core.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace FaxDrucker.Core.Sip;

public enum T38Policy
{
    /// <summary>T.38 annehmen, wenn die Gegenstelle es anbietet (Re-INVITE); sonst G.711.</summary>
    Auto,
    /// <summary>Nur G.711-Audio; T.38-Angebote werden abgelehnt.</summary>
    Off,
}

/// <summary>SIP-Zugangsdaten und Verbindungseinstellungen.</summary>
public sealed class SipAccount
{
    /// <summary>SIP-Domain/Registrar, bei Placetel "fpbx.de".</summary>
    public string Domain { get; set; } = "fpbx.de";
    /// <summary>Optionaler Proxy (Host oder Host:Port). Leer = Domain.</summary>
    public string? OutboundProxy { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Optional abweichender Benutzer für die Anmeldung.</summary>
    public string? AuthUsername { get; set; }
    public string Password { get; set; } = "";
    public string? DisplayName { get; set; }
    public T38Policy T38 { get; set; } = T38Policy.Auto;
    public bool PreferAlaw { get; set; } = true;
    public int RtpPortMin { get; set; } = 20000;
    public int RtpPortMax { get; set; } = 20999;
    public int RingTimeoutSeconds { get; set; } = 60;
    public int FaxTimeoutMinutes { get; set; } = 30;
}

public enum FaxCallStatus { Success, Busy, NoAnswer, Rejected, FaxFailed, Error }

public sealed record FaxCallResult(FaxCallStatus Status, string Message, int SipCode, FaxOutcome? Fax)
{
    public bool Success => Status == FaxCallStatus.Success;
    /// <summary>Lohnt sich eine Wahlwiederholung?</summary>
    public bool Retryable => Status is FaxCallStatus.Busy or FaxCallStatus.NoAnswer or FaxCallStatus.FaxFailed;
}

/// <summary>Ein ausgehender Faxanruf: SIP-Signalisierung + Medien.</summary>
public sealed class SipFaxCall
{
    private readonly SipAccount _account;
    public event Action<string>? Log;
    /// <summary>Statusmeldungen für die Oberfläche ("Wählt…", "Sendet Seite…").</summary>
    public event Action<string>? Status;

    public SipFaxCall(SipAccount account) => _account = account;

    public static string NormalizeNumber(string number)
    {
        var plus = number.TrimStart().StartsWith('+');
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return plus ? "+" + digits : digits;
    }

    public async Task<FaxCallResult> SendAsync(string number, FaxJobParameters job, CancellationToken ct = default)
    {
        number = NormalizeNumber(number);
        if (number.Length < 3)
            return new FaxCallResult(FaxCallStatus.Error, "Ungültige Faxnummer", 0, null);

        var target = string.IsNullOrWhiteSpace(_account.OutboundProxy) ? _account.Domain : _account.OutboundProxy!;
        var (host, port) = SplitHostPort(target, 5060);
        IPAddress serverIp;
        try
        {
            serverIp = IPAddress.TryParse(host, out var ip) ? ip
                : (await Dns.GetHostAddressesAsync(host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex)
        {
            return new FaxCallResult(FaxCallStatus.Error, $"SIP-Server {host} nicht gefunden: {ex.Message}", 0, null);
        }
        var localIp = LocalAddressFor(serverIp);
        Log?.Invoke($"SIP-Server {host} ({serverIp}:{port}), lokale Adresse {localIp}");

        using var transport = new SIPTransport();
        var channel = new SIPUDPChannel(new IPEndPoint(localIp, 0));
        transport.AddSIPChannel(channel);
        var localSip = channel.ListeningEndPoint;

        using var media = new FaxMediaEngine(localIp, job, _account.RtpPortMin, _account.RtpPortMax);
        media.Log += m => Log?.Invoke(m);
        var sessionId = Random.Shared.NextInt64(1, int.MaxValue);
        long sdpVersion = 1;

        var uac = new SIPClientUserAgent(transport);
        var answered = new TaskCompletionSource<SIPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<(string, SIPResponse?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteHangup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uac.CallTrying += (_, r) => Status?.Invoke("Wählt…");
        uac.CallRinging += (_, r) => Status?.Invoke("Klingelt…");
        uac.CallAnswered += (_, r) =>
        {
            if (r.Status == SIPResponseStatusCodesEnum.Ok || (int)r.Status / 100 == 2) answered.TrySetResult(r);
            else failed.TrySetResult(($"{(int)r.Status} {r.ReasonPhrase}", r));
        };
        uac.CallFailed += (_, err, r) => failed.TrySetResult((err, r));

        var contactUri = new SIPURI(_account.Username, localSip.ToString(), null, SIPSchemesEnum.sip, SIPProtocolsEnum.udp);

        transport.SIPTransportRequestReceived += (localEp, remoteEp, req) =>
        {
            try
            {
                HandleInDialogRequest(transport, uac, req, media, localIp, sessionId, ref sdpVersion, contactUri, remoteHangup);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Fehler bei eingehender SIP-Anfrage: " + ex.Message);
            }
            return Task.CompletedTask;
        };
        if (Environment.GetEnvironmentVariable("FAXDRUCKER_SIP_TRACE") == "1")
        {
            transport.SIPRequestOutTraceEvent += (l, r, m) => Log?.Invoke($">>> {r}\n{m}");
            transport.SIPResponseInTraceEvent += (l, r, m) => Log?.Invoke($"<<< {r}\n{m}");
            transport.SIPRequestInTraceEvent += (l, r, m) => Log?.Invoke($"<<< {r}\n{m}");
            transport.SIPResponseOutTraceEvent += (l, r, m) => Log?.Invoke($">>> {r}\n{m}");
        }

        var offer = FaxSdp.AudioOffer(localIp, media.LocalPort, sessionId, sdpVersion, _account.PreferAlaw);
        var from = string.IsNullOrWhiteSpace(_account.DisplayName)
            ? $"<sip:{_account.Username}@{_account.Domain}>"
            : $"\"{_account.DisplayName}\" <sip:{_account.Username}@{_account.Domain}>";
        var descriptor = new SIPCallDescriptor(
            _account.Username, _account.Password,
            $"sip:{number}@{_account.Domain}",
            from, $"<sip:{number}@{_account.Domain}>",
            string.IsNullOrWhiteSpace(_account.OutboundProxy) ? null : $"<sip:{host}:{port};lr>",
            null, string.IsNullOrWhiteSpace(_account.AuthUsername) ? null : _account.AuthUsername,
            SIPCallDirection.Out, "application/sdp", offer, null);

        Status?.Invoke($"Wählt {number}…");
        Log?.Invoke($"INVITE sip:{number}@{_account.Domain}");
        uac.Call(descriptor, new SIPEndPoint(SIPProtocolsEnum.udp, new IPEndPoint(serverIp, port)));

        // Auf Annahme warten
        var ringTimeout = Task.Delay(TimeSpan.FromSeconds(_account.RingTimeoutSeconds), ct);
        var first = await Task.WhenAny(answered.Task, failed.Task, ringTimeout);
        if (first == ringTimeout)
        {
            uac.Cancel("Zeitüberschreitung");
            await Task.Delay(500, CancellationToken.None);
            return ct.IsCancellationRequested
                ? new FaxCallResult(FaxCallStatus.Error, "Abgebrochen", 0, null)
                : new FaxCallResult(FaxCallStatus.NoAnswer, "Keine Antwort", 0, null);
        }
        if (first == failed.Task)
        {
            var (err, resp) = failed.Task.Result;
            return MapFailure(err, resp);
        }

        var ok = answered.Task.Result;
        Status?.Invoke("Verbunden, sendet…");
        var answerSdp = FaxSdp.Parse(ok.Body ?? "");
        StartMediaFromSdp(media, answerSdp, isInitialAnswer: true);

        // Auf das Ende der Faxübertragung warten
        var faxTimeout = Task.Delay(TimeSpan.FromMinutes(_account.FaxTimeoutMinutes), ct);
        var done = await Task.WhenAny(media.Completion, remoteHangup.Task, faxTimeout);
        if (done == remoteHangup.Task)
        {
            // Gegenstelle legt oft direkt nach dem letzten Signal auf; kurz nachlaufen lassen.
            await Task.WhenAny(media.Completion, Task.Delay(1500, CancellationToken.None));
            media.Abort("Gegenstelle hat aufgelegt");
        }
        else if (done == faxTimeout)
        {
            media.Abort(ct.IsCancellationRequested ? "Abgebrochen" : "Zeitüberschreitung bei der Übertragung");
        }

        var outcome = await media.Completion;
        if (!remoteHangup.Task.IsCompleted)
        {
            try { uac.Hangup(); } catch (Exception ex) { Log?.Invoke("BYE fehlgeschlagen: " + ex.Message); }
            await Task.Delay(500, CancellationToken.None);
        }

        return outcome.Success
            ? new FaxCallResult(FaxCallStatus.Success, $"Gesendet: {outcome.Pages} Seite(n) über {(outcome.Transport == Fax.FaxTransport.T38 ? "T.38" : "G.711")}", 200, outcome)
            : new FaxCallResult(FaxCallStatus.FaxFailed, $"Faxübertragung fehlgeschlagen: {outcome.ResultText}", 200, outcome);
    }

    private void StartMediaFromSdp(FaxMediaEngine media, FaxSdp sdp, bool isInitialAnswer)
    {
        var t38 = sdp.T38;
        if (t38 != null && _account.T38 == T38Policy.Auto && sdp.EndPointOf(t38) is { } t38Ep)
        {
            media.SwitchToT38(t38Ep);
            return;
        }
        var audio = sdp.Audio;
        if (audio != null && sdp.EndPointOf(audio) is { } ep)
        {
            var alaw = audio.Formats.FirstOrDefault(f => f is "8" or "0") != "0";
            if (isInitialAnswer || media.Mode == null)
                media.StartAudio(ep, alaw);
            return;
        }
        Log?.Invoke("Antwort ohne verwendbare Medienbeschreibung");
    }

    private void HandleInDialogRequest(SIPTransport transport, SIPClientUserAgent uac, SIPRequest req,
        FaxMediaEngine media, IPAddress localIp, long sessionId, ref long sdpVersion, SIPURI contactUri,
        TaskCompletionSource remoteHangup)
    {
        var dialog = uac.SIPDialogue;
        bool inDialog = dialog != null && req.Header.CallId == dialog.CallId;

        switch (req.Method)
        {
            case SIPMethodsEnum.INVITE when inDialog:
            {
                var offer = FaxSdp.Parse(req.Body ?? "");
                var uas = new UASInviteTransaction(transport, req, null, false);
                SIPResponse resp;
                if (offer.T38 is { } t38 && _account.T38 == T38Policy.Auto && offer.EndPointOf(t38) is { } t38Ep)
                {
                    Log?.Invoke($"Re-INVITE mit T.38 von {t38Ep}");
                    var maxRate = int.TryParse(t38.Attribute("T38MaxBitRate"), out var r) ? Math.Min(r, 14400) : 14400;
                    var rateMgmt = t38.Attribute("T38FaxRateManagement") ?? "transferredTCF";
                    var maxDgram = int.TryParse(t38.Attribute("T38FaxMaxDatagram"), out var d) ? Math.Max(d, 160) : 400;
                    var sdp = FaxSdp.T38Sdp(localIp, media.LocalPort, sessionId, ++sdpVersion, maxRate, rateMgmt, maxDgram);
                    resp = OkWithSdp(req, sdp, contactUri, dialog!.LocalTag);
                    uas.SendFinalResponse(resp);
                    Status?.Invoke("Sendet per T.38…");
                    media.SwitchToT38(t38Ep);
                }
                else if (offer.Audio is { } audio && offer.EndPointOf(audio) is { } aEp)
                {
                    Log?.Invoke($"Re-INVITE mit Audio von {aEp}");
                    var sdp = FaxSdp.AudioOffer(localIp, media.LocalPort, sessionId, ++sdpVersion, _account.PreferAlaw);
                    resp = OkWithSdp(req, sdp, contactUri, dialog!.LocalTag);
                    uas.SendFinalResponse(resp);
                }
                else
                {
                    Log?.Invoke("Re-INVITE abgelehnt (keine passenden Medien)");
                    resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.NotAcceptableHere, null);
                    uas.SendFinalResponse(resp);
                }
                break;
            }
            case SIPMethodsEnum.BYE:
            {
                Log?.Invoke("Gegenstelle hat aufgelegt (BYE)");
                var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                var tx = new SIPNonInviteTransaction(transport, req, null);
                tx.SendResponse(resp);
                if (inDialog) remoteHangup.TrySetResult();
                break;
            }
            case SIPMethodsEnum.ACK:
                break; // wird von den Transaktionen behandelt
            case SIPMethodsEnum.OPTIONS:
            case SIPMethodsEnum.INFO:
            case SIPMethodsEnum.NOTIFY:
            case SIPMethodsEnum.UPDATE:
            {
                var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                _ = transport.SendResponseAsync(resp);
                break;
            }
            default:
            {
                var resp = SIPResponse.GetResponse(req,
                    inDialog ? SIPResponseStatusCodesEnum.NotImplemented : SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null);
                _ = transport.SendResponseAsync(resp);
                break;
            }
        }
    }

    private static SIPResponse OkWithSdp(SIPRequest req, string sdp, SIPURI contact, string localTag)
    {
        var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
        if (string.IsNullOrEmpty(resp.Header.To.ToTag))
            resp.Header.To.ToTag = localTag;
        resp.Header.Contact = new List<SIPContactHeader> { new SIPContactHeader(null, contact) };
        resp.Header.ContentType = "application/sdp";
        resp.Body = sdp;
        return resp;
    }

    private static FaxCallResult MapFailure(string err, SIPResponse? resp)
    {
        int code = resp != null ? (int)resp.Status : 0;
        var text = resp != null ? $"{code} {resp.ReasonPhrase}" : err;
        return code switch
        {
            486 or 600 => new FaxCallResult(FaxCallStatus.Busy, "Besetzt", code, null),
            480 or 408 or 487 => new FaxCallResult(FaxCallStatus.NoAnswer, $"Nicht erreichbar ({text})", code, null),
            401 or 407 or 403 => new FaxCallResult(FaxCallStatus.Error, $"Anmeldung beim SIP-Anbieter fehlgeschlagen ({text}). Benutzer und Passwort prüfen.", code, null),
            404 or 484 or 604 => new FaxCallResult(FaxCallStatus.Rejected, $"Nummer nicht erreichbar ({text})", code, null),
            488 or 606 => new FaxCallResult(FaxCallStatus.Rejected, $"Medien nicht akzeptiert ({text})", code, null),
            _ => new FaxCallResult(FaxCallStatus.Error, $"Anruf fehlgeschlagen: {text}", code, null),
        };
    }

    private static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        value = value.Trim();
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        var idx = value.LastIndexOf(':');
        if (idx > 0 && int.TryParse(value[(idx + 1)..], out var p))
            return (value[..idx], p);
        return (value, defaultPort);
    }

    /// <summary>Ermittelt die lokale IP-Adresse, über die der Server erreicht wird.</summary>
    private static IPAddress LocalAddressFor(IPAddress remote)
    {
        if (IPAddress.IsLoopback(remote)) return remote;
        using var s = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(remote, 5060);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}
