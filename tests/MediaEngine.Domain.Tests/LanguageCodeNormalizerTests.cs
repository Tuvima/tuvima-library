using MediaEngine.Domain.Services;
namespace MediaEngine.Domain.Tests;

public class LanguageCodeNormalizerTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("english", "en")]
    [InlineData("eng", "en")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("EN_us", "en")]
    [InlineData("Portuguese", "pt")]
    [InlineData("por", "pt")]
    [InlineData("pt-BR", "pt")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("French", "fr")]
    [InlineData("Français", "fr")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("jpn", "ja")]
    [InlineData("chi", "zh")]
    [InlineData("  English  ", "en")]
    public void MapsKnownValuesToIso6391(string input, string expected) =>
        Assert.Equal(expected, LanguageCodeNormalizer.ToIso6391(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Klingon")]
    public void ReturnsNullForBlankOrUnknownValues(string? input) =>
        Assert.Null(LanguageCodeNormalizer.ToIso6391(input));

    [Fact]
    public void NormalizeDistinct_NormalisesDeduplicatesAndKeepsFirstSeenOrder()
    {
        var result = LanguageCodeNormalizer.NormalizeDistinct(
            ["eng", "eng", "ara", "ger", " ", null, "EN", "Klingon", "chi", "zh"]);

        Assert.Equal(["en", "ar", "de", "Klingon", "zh"], result);
    }
}
