using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>The Artwork tool's Language and Size filters (universal editor v2, D10).</summary>
public sealed class ArtworkCandidateFilterTests
{
    private static ProviderArtworkCandidateDto Pic(string id, string? language, int? width = null, int? height = null) =>
        new(id, "TheTVDB", "/thumb/" + id, width, height, false, language);

    private static readonly ProviderArtworkCandidateDto[] Items =
    [
        Pic("en", "eng", 1200, 600),
        Pic("fr", "fra", 800, 400),
        Pic("plain", null, 500, 500),
        Pic("en-small", "eng", 300, 150),
    ];

    [Fact]
    public void Languages_AreNamedInPlainEnglish_WithNoTextLast()
    {
        var languages = ArtworkCandidateFilter.Languages(Items);

        Assert.Equal(["English", "French", "No text"], languages.Select(entry => entry.Label));
        Assert.Equal(["eng", "fra", ArtworkCandidateFilter.NoWords], languages.Select(entry => entry.Value));
    }

    [Fact]
    public void AllLanguagesAnySize_KeepsEverything()
    {
        Assert.Equal(4, ArtworkCandidateFilter.Apply(Items, ArtworkCandidateFilter.All, ArtworkCandidateFilter.AnySize).Count);
    }

    [Fact]
    public void OneLanguage_KeepsOnlyThatLanguage()
    {
        Assert.Equal(["en", "en-small"], ArtworkCandidateFilter.Apply(Items, "eng", ArtworkCandidateFilter.AnySize).Select(item => item.Id));
        Assert.Equal(["plain"], ArtworkCandidateFilter.Apply(Items, ArtworkCandidateFilter.NoWords, ArtworkCandidateFilter.AnySize).Select(item => item.Id));
    }

    [Fact]
    public void LargeOnly_KeepsPicturesWhoseLongerSideIsAThousandPixelsOrMore()
    {
        Assert.Equal(["en"], ArtworkCandidateFilter.Apply(Items, ArtworkCandidateFilter.All, ArtworkCandidateFilter.LargeOnly).Select(item => item.Id));
    }

    [Fact]
    public void ATileSaysItsLanguage_ButNotForAPictureWithNoWords()
    {
        Assert.Equal("English", ArtworkCandidateFilter.LabelFor("eng"));
        Assert.Null(ArtworkCandidateFilter.LabelFor(null));
        Assert.Null(ArtworkCandidateFilter.LabelFor("zxx"));
    }
}
