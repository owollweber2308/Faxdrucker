using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Media;
using FaxDrucker.Core.Report;
using FaxDrucker.Core.Sip;
using Xunit;

namespace FaxDrucker.Tests;

public class UdptlTests
{
    [Fact]
    public void RoundTripInOrder()
    {
        var enc = new UdptlEncoder(3);
        var dec = new UdptlDecoder();
        for (int i = 0; i < 10; i++)
        {
            var ifp = new byte[] { (byte)i, 1, 2, 3 };
            var got = dec.Decode(enc.Encode(ifp));
            Assert.Single(got);
            Assert.Equal((ushort)i, got[0].Seq);
            Assert.Equal(ifp, got[0].Data);
        }
    }

    [Fact]
    public void RecoversLostPacketsFromRedundancy()
    {
        var enc = new UdptlEncoder(3);
        var dec = new UdptlDecoder();
        var packets = Enumerable.Range(0, 6).Select(i => enc.Encode(new byte[] { (byte)(0x10 + i) })).ToList();
        dec.Decode(packets[0]);
        // Pakete 1 und 2 gehen verloren
        var got = dec.Decode(packets[3]);
        Assert.Equal(new ushort[] { 1, 2, 3 }, got.Select(g => g.Seq));
        Assert.Equal(new byte[] { 0x11 }, got[0].Data);
        Assert.Equal(new byte[] { 0x12 }, got[1].Data);
        // Duplikat wird verworfen
        Assert.Empty(dec.Decode(packets[3]));
    }

    [Fact]
    public void LongIfpUsesTwoByteLength()
    {
        var enc = new UdptlEncoder(0);
        var ifp = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        Assert.True(UdptlDecoder.TryParse(enc.Encode(ifp), out var seq, out var primary, out _));
        Assert.Equal(0, seq);
        Assert.Equal(ifp, primary);
    }
}

public class SdpTests
{
    private const string T38Offer =
        "v=0\r\no=- 1 2 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n" +
        "m=audio 0 RTP/AVP 8\r\n" +
        "m=image 40000 udptl t38\r\nc=IN IP4 10.0.0.2\r\na=T38FaxVersion:0\r\na=T38MaxBitRate:9600\r\n" +
        "a=T38FaxRateManagement:transferredTCF\r\na=T38FaxUdpEC:t38UDPRedundancy\r\n";

    [Fact]
    public void ParsesT38Offer()
    {
        var sdp = FaxSdp.Parse(T38Offer);
        Assert.Null(sdp.Audio); // Port 0 = abgelehnt
        var t38 = Assert.IsType<SdpMedia>(sdp.T38);
        Assert.Equal("9600", t38.Attribute("T38MaxBitRate"));
        Assert.Equal("10.0.0.2:40000", sdp.EndPointOf(t38)!.ToString());
    }

    [Fact]
    public void AudioOfferPrefersAlaw()
    {
        var sdp = FaxSdp.Parse(FaxSdp.AudioOffer(System.Net.IPAddress.Parse("192.168.1.5"), 20000, 1, 1));
        Assert.Equal("8", sdp.Audio!.Formats[0]);
        Assert.Equal("192.168.1.5:20000", sdp.EndPointOf(sdp.Audio)!.ToString());
    }

    [Theory]
    [InlineData("0221 / 123-45 67", "0221123 4567")]
    [InlineData("+49 (221) 1234567", "+492211234567")]
    public void NormalizesNumbers(string input, string expected)
    {
        Assert.Equal(expected.Replace(" ", ""), SipFaxCall.NormalizeNumber(input));
    }
}

public class ReportTests
{
    [Fact]
    public void WritesPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bericht-{Guid.NewGuid():N}.pdf");
        SendReport.Write(path, new SendReport.Data(DateTime.Now, "0221123", "Müller GmbH", "Rechnung", true,
            "Gesendet", 2, 2, 1, "+49 221 999", 14400, "T.38", "+49 30 1"));
        var head = File.ReadAllText(path)[..8];
        Assert.Equal("%PDF-1.4", head);
        File.Delete(path);
    }
}

public class TiffTests
{
    [Fact]
    public async Task GhostscriptProducesFaxTiff()
    {
        var tif = await TestDocs.CreateTiffAsync();
        Assert.Equal(2, TiffInfo.CountPages(tif));
    }
}

internal static class TestDocs
{
    public static async Task<string> CreateTiffAsync()
    {
        var tif = Path.Combine(Path.GetTempPath(), $"faxtest-{Guid.NewGuid():N}.tif");
        var ps = Path.Combine(AppContext.BaseDirectory, "testdata", "sample.ps");
        await new GhostscriptConverter().ToFaxTiffAsync(ps, tif, fine: true);
        return tif;
    }
}
