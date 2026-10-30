using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>Provider pictures are listed in the person's own language first (editor v2, D10; F38).</summary>
public sealed class ProviderArtworkLanguageOrderTests
{
    private static ProviderArtworkCandidate Logo(string id, string? language) =>
        new(id, "TheTVDB", "https://artworks.thetvdb.com/" + id, string.Empty, null, null, language);

    [Fact]
    public void LogosInThePersonsLanguage_ComeFirst_ThenPicturesWithNoWords_ThenOtherLanguages()
    {
        var ordered = ProviderArtworkLanguageOrder.Apply(
            [Logo("fr", "fra"), Logo("none", null), Logo("en1", "eng"), Logo("de", "deu"), Logo("en2", "eng")], "en");

        Assert.Equal(["en1", "en2", "none", "fr", "de"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void TwoLetterAndThreeLetterCodes_AreTheSameLanguage()
    {
        var ordered = ProviderArtworkLanguageOrder.Apply([Logo("de", "de"), Logo("fr", "fra")], "fr");

        Assert.Equal(["fr", "de"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void WordlessMarkers_CountAsNoLanguage()
    {
        var ordered = ProviderArtworkLanguageOrder.Apply([Logo("de", "deu"), Logo("zxx", "zxx"), Logo("en", "eng")], "en");

        Assert.Equal(["en", "zxx", "de"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void WithoutAPreferredLanguage_TheProvidersOrderIsKept()
    {
        var ordered = ProviderArtworkLanguageOrder.Apply([Logo("b", "fra"), Logo("a", "eng")], null);

        Assert.Equal(["b", "a"], ordered.Select(item => item.Id));
    }
}
