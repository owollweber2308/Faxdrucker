using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Fax;
using FaxDrucker.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace FaxDrucker.Tests;

/// <summary>
/// Sender und Empfänger direkt miteinander verbunden, mit simulierter Zeit (läuft schneller als Echtzeit).
/// </summary>
public class FaxLoopbackTests
{
    private readonly ITestOutputHelper _out;
    public FaxLoopbackTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(FaxResolution.Normal, ImageMode.Text)]
    [InlineData(FaxResolution.Fein, ImageMode.Text)]
    [InlineData(FaxResolution.Superfein, ImageMode.Text)]
    [InlineData(FaxResolution.Fein, ImageMode.Foto)]
    [InlineData(FaxResolution.Superfein, ImageMode.Foto)]
    public async Task T38LoopbackTransfersAllPages(FaxResolution res, ImageMode mode)
    {
        var tif = await TestDocs.CreateTiffAsync(res, mode);
        var rx = Path.Combine(Path.GetTempPath(), $"rx-{Guid.NewGuid():N}.tif");
        using var sender = new FaxSession(FaxTransport.T38, true, tif, null, "+49 30 1111", "FaxDrucker Test");
        using var receiver = new FaxSession(FaxTransport.T38, false, null, rx, "+49 30 2222", null);

        // Über echtes UDPTL kodieren/dekodieren, damit auch die Rahmung geprüft wird.
        var encA = new UdptlEncoder(); var decB = new UdptlDecoder();
        var encB = new UdptlEncoder(); var decA = new UdptlDecoder();
        var toB = new Queue<byte[]>(); var toA = new Queue<byte[]>();
        sender.T38PacketOut += (ifp, count) => toB.Enqueue(encA.Encode(ifp));
        receiver.T38PacketOut += (ifp, count) => toA.Enqueue(encB.Encode(ifp));

        int ticks = 0;
        while (!(sender.IsDone && receiver.IsDone) && ticks < 50 * 60 * 15)
        {
            sender.TickT38(160);
            receiver.TickT38(160);
            while (toB.Count > 0) foreach (var p in decB.Decode(toB.Dequeue())) receiver.ReceiveT38(p.Data, p.Seq);
            while (toA.Count > 0) foreach (var p in decA.Decode(toA.Dequeue())) sender.ReceiveT38(p.Data, p.Seq);
            ticks++;
        }
        _out.WriteLine($"spandsp {FaxSession.SpanDspVersion}: {FaxSession.ResultText(sender.Result)} nach {ticks * 20 / 1000.0} s simulierter Zeit, {sender.BitRate} bit/s");
        Assert.Equal(0, sender.Result);
        Assert.Equal(2, sender.PagesSent);
        Assert.Equal(2, TiffInfo.CountPages(rx));
        Assert.Equal("+49 30 2222", sender.RemoteIdent);
        _out.WriteLine($"Empfangen: {rx}");
    }

    [Fact]
    public async Task AudioLoopbackOverG711TransfersAllPages()
    {
        var tif = await TestDocs.CreateTiffAsync();
        var rx = Path.Combine(Path.GetTempPath(), $"rx-{Guid.NewGuid():N}.tif");
        using var sender = new FaxSession(FaxTransport.Audio, true, tif, null, "A", null);
        using var receiver = new FaxSession(FaxTransport.Audio, false, null, rx, "B", null);
        var a = new short[160]; var b = new short[160]; var g711 = new byte[160];

        int frames = 0;
        while (!(sender.IsDone && receiver.IsDone) && frames < 50 * 60 * 5)
        {
            Array.Clear(a); Array.Clear(b);
            sender.TransmitAudio(a);
            receiver.TransmitAudio(b);
            // Durch A-law, wie im echten Netz
            G711.Encode(true, a, g711); G711.Decode(true, g711, a);
            G711.Encode(true, b, g711); G711.Decode(true, g711, b);
            receiver.ReceiveAudio(a);
            sender.ReceiveAudio(b);
            frames++;
        }
        _out.WriteLine($"G.711: {FaxSession.ResultText(sender.Result)} nach {frames * 20 / 1000.0} s, {sender.BitRate} bit/s");
        Assert.Equal(0, sender.Result);
        Assert.Equal(2, TiffInfo.CountPages(rx));
    }
}
