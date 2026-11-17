using System.Security.Cryptography;
using FaxDrucker.Core.Licensing;

namespace FaxDrucker.Tests;

public class LicenseTests
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void SignedLicenseVerifies()
    {
        var lic = new License("Müller & Söhne GmbH", 1001, new DateOnly(2026, 11, 17), new DateOnly(2027, 12, 31));
        var text = lic.Sign(Key);
        Assert.StartsWith(License.Prefix, text);
        var back = License.Verify(text, Key, out var err);
        Assert.Equal(lic, back);
        Assert.Equal("", err);
    }

    [Fact]
    public void ToleratesWhitespaceCaseAndLineBreaks()
    {
        var text = new License("Firma", 7, new DateOnly(2026, 1, 1), null).Sign(Key);
        var messy = "  " + text.ToLowerInvariant().Insert(30, "\r\n") + " \n";
        var back = License.Verify(messy, Key, out _);
        Assert.NotNull(back);
        Assert.Null(back!.Expires);
    }

    [Fact]
    public void RejectsTamperedKey()
    {
        var text = new License("Firma", 7, new DateOnly(2026, 1, 1), null).Sign(Key);
        var i = text.Length - 10;
        var tampered = text[..i] + (text[i] == '0' ? '1' : '0') + text[(i + 1)..];
        Assert.Null(License.Verify(tampered, Key, out var err));
        Assert.Contains("ungültig", err);
    }

    [Fact]
    public void RejectsKeyFromOtherSigner()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = new License("Firma", 7, new DateOnly(2026, 1, 1), null).Sign(other);
        Assert.Null(License.Verify(text, Key, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hallo")]
    [InlineData("FD1-")]
    [InlineData("FD1-ÄÖÜ")]
    [InlineData("FD1-04000-0Z917")]
    public void RejectsGarbage(string text) => Assert.Null(License.Verify(text, Key, out _));

    [Fact]
    public void ExpiryIsInclusive()
    {
        var lic = new License("Firma", 7, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.False(lic.IsExpired(new DateOnly(2026, 12, 31)));
        Assert.True(lic.IsExpired(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void BuiltInPublicKeyLoads()
    {
        Assert.Null(LicenseStore.Verify("FD1-00000", out var err));
        Assert.NotEmpty(err);
    }
}
