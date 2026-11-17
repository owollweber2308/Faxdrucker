using System.Net;
using System.Text;

namespace FaxDrucker.Core.Sip;

/// <summary>Eine Medienzeile (m=) einer SDP-Beschreibung.</summary>
public sealed class SdpMedia
{
    public string Type { get; set; } = "";        // audio | image
    public int Port { get; set; }
    public string Protocol { get; set; } = "";    // RTP/AVP | udptl
    public List<string> Formats { get; } = new();  // 8, 0, 101 | t38
    public List<string> Attributes { get; } = new();
    public IPAddress? Connection { get; set; }

    public string? Attribute(string name)
    {
        var prefix = name + ":";
        foreach (var a in Attributes)
        {
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..].Trim();
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
                return "";
        }
        return null;
    }
}

/// <summary>Minimaler SDP-Parser/-Erzeuger für G.711-Audio und T.38.</summary>
public sealed class FaxSdp
{
    public IPAddress? Connection { get; set; }
    public List<SdpMedia> Media { get; } = new();

    public SdpMedia? Audio => Media.FirstOrDefault(m => m.Type == "audio" && m.Port != 0);
    public SdpMedia? T38 => Media.FirstOrDefault(m => m.Type == "image" && m.Port != 0
        && m.Formats.Any(f => f.Equals("t38", StringComparison.OrdinalIgnoreCase)));

    public IPEndPoint? EndPointOf(SdpMedia m)
    {
        var addr = m.Connection ?? Connection;
        return addr == null ? null : new IPEndPoint(addr, m.Port);
    }

    public static FaxSdp Parse(string sdp)
    {
        var result = new FaxSdp();
        SdpMedia? current = null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '=')
                continue;
            var value = line[2..];
            switch (line[0])
            {
                case 'c':
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && IPAddress.TryParse(parts[2].Split('/')[0], out var ip))
                    {
                        if (current != null) current.Connection = ip;
                        else result.Connection = ip;
                    }
                    break;
                case 'm':
                    var mp = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    current = new SdpMedia
                    {
                        Type = mp.Length > 0 ? mp[0].ToLowerInvariant() : "",
                        Port = mp.Length > 1 && int.TryParse(mp[1].Split('/')[0], out var port) ? port : 0,
                        Protocol = mp.Length > 2 ? mp[2] : "",
                    };
                    current.Formats.AddRange(mp.Skip(3));
                    result.Media.Add(current);
                    break;
                case 'a':
                    current?.Attributes.Add(value);
                    break;
            }
        }
        return result;
    }

    private static string Header(IPAddress local, long sessionId, long version)
    {
        var ipVer = local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IP6" : "IP4";
        return "v=0\r\n" +
               $"o=FaxDrucker {sessionId} {version} IN {ipVer} {local}\r\n" +
               "s=FaxDrucker\r\n" +
               $"c=IN {ipVer} {local}\r\n" +
               "t=0 0\r\n";
    }

    /// <summary>Audio-Angebot: PCMA bevorzugt (Deutschland), PCMU als Alternative.</summary>
    public static string AudioOffer(IPAddress local, int port, long sessionId, long version, bool preferAlaw = true)
    {
        var sb = new StringBuilder(Header(local, sessionId, version));
        var order = preferAlaw ? "8 0" : "0 8";
        sb.Append($"m=audio {port} RTP/AVP {order} 101\r\n");
        sb.Append("a=rtpmap:8 PCMA/8000\r\n");
        sb.Append("a=rtpmap:0 PCMU/8000\r\n");
        sb.Append("a=rtpmap:101 telephone-event/8000\r\n");
        sb.Append("a=fmtp:101 0-15\r\n");
        sb.Append("a=ptime:20\r\n");
        sb.Append("a=sendrecv\r\n");
        return sb.ToString();
    }

    /// <summary>T.38-Beschreibung (für Angebot und Antwort).</summary>
    public static string T38Sdp(IPAddress local, int port, long sessionId, long version,
        int maxBitRate = 14400, string rateManagement = "transferredTCF", int maxDatagram = 400)
    {
        var sb = new StringBuilder(Header(local, sessionId, version));
        sb.Append($"m=image {port} udptl t38\r\n");
        sb.Append("a=T38FaxVersion:0\r\n");
        sb.Append($"a=T38MaxBitRate:{maxBitRate}\r\n");
        sb.Append($"a=T38FaxRateManagement:{rateManagement}\r\n");
        sb.Append("a=T38FaxMaxBuffer:2000\r\n");
        sb.Append($"a=T38FaxMaxDatagram:{maxDatagram}\r\n");
        sb.Append("a=T38FaxUdpEC:t38UDPRedundancy\r\n");
        return sb.ToString();
    }
}
