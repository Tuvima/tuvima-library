using System.Text;

namespace MediaEngine.Identity.Tests;

/// <summary>The six-digit authenticator codes, checked against the published RFC 6238 test vectors.</summary>
public sealed class TotpGeneratorTests
{
    private static readonly byte[] Rfc6238Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 appendix B, SHA-1 column (8-digit codes).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Compute_MatchesTheRfc6238TestVectors(long unixSeconds, string expected)
    {
        var step = TotpGenerator.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, TotpGenerator.Compute(Rfc6238Secret, step, digits: 8));
        // The six digits an app shows are the last six of the same number.
        Assert.Equal(expected[2..], TotpGenerator.Compute(Rfc6238Secret, step));
    }

    [Fact]
    public void Match_AcceptsTheCurrentStepAndOneEitherSide_ButNotTwo()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var current = TotpGenerator.StepAt(now);

        Assert.Equal(current, TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current), now, 0));
        Assert.Equal(current - 1, TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current - 1), now, 0));
        Assert.Equal(current + 1, TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current + 1), now, 0));
        Assert.Null(TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current - 2), now, 0));
        Assert.Null(TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current + 2), now, 0));
    }

    [Fact]
    public void Match_RefusesAStepThatWasAlreadyUsed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var current = TotpGenerator.StepAt(now);
        var code = TotpGenerator.Compute(Rfc6238Secret, current);

        var first = TotpGenerator.Match(Rfc6238Secret, code, now, 0);
        Assert.Equal(current, first);
        // The same code again, and the code for an earlier step, are both replays.
        Assert.Null(TotpGenerator.Match(Rfc6238Secret, code, now, first!.Value));
        Assert.Null(TotpGenerator.Match(Rfc6238Secret, TotpGenerator.Compute(Rfc6238Secret, current - 1), now, first.Value));
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12345a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_AcceptsOnlySixDigits(string? input, string? expected) =>
        Assert.Equal(expected, TotpGenerator.Normalize(input));

    [Fact]
    public void Match_IgnoresSpacesTheAppMayShow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code = TotpGenerator.Compute(Rfc6238Secret, TotpGenerator.StepAt(now));

        Assert.NotNull(TotpGenerator.Match(Rfc6238Secret, $"{code[..3]} {code[3..]}", now, 0));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_MatchesRfc4648AndRoundTrips(string plain, string encoded)
    {
        var bytes = Encoding.ASCII.GetBytes(plain);

        Assert.Equal(encoded, TotpGenerator.ToBase32(bytes));
        Assert.Equal(bytes, TotpGenerator.FromBase32(encoded));
        Assert.Equal(bytes, TotpGenerator.FromBase32(encoded.ToLowerInvariant()));
    }

    [Fact]
    public void FromBase32_RefusesCharactersThatAreNotInTheAlphabet() =>
        Assert.Throws<FormatException>(() => TotpGenerator.FromBase32("MZXW1!"));

    [Fact]
    public void NewSecret_IsRandomAndTwentyBytes()
    {
        var a = TotpGenerator.NewSecret();
        var b = TotpGenerator.NewSecret();

        Assert.Equal(TotpGenerator.SecretBytes, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildUri_CarriesTheIssuerAccountKeyAndSettingsAnAppNeeds()
    {
        var uri = TotpGenerator.BuildUri("Tuvima Library", "owner@example.com", "MZXW6YTBOI");

        Assert.StartsWith("otpauth://totp/Tuvima%20Library:owner%40example.com?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=MZXW6YTBOI", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Tuvima%20Library", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }
}
