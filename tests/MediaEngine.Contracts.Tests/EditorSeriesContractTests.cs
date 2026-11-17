using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Series editor (universal editor v8 D1 and D6): the header badge, the kinds of title and one kind's titles.</summary>
public sealed class EditorSeriesContractTests
{
    [Fact]
    public void ASeriesSubject_CarriesTheBadgeAndTheKindsOfTitle_WithSnakeCaseNames_AndRoundTrips()
    {
        var dto = new EditorSubjectDto
        {
            Id = Guid.NewGuid(),
            Kind = EditorSubjectKinds.Series,
            Title = "Dune",
            MatchBadge = new EditorSubjectMatchBadgeDto { RetailMatched = true, ProviderName = "TMDB", CanonicalMatched = false },
            MediaTypes = [new EditorSeriesMediaTypeDto { Key = "Books", Label = "Books", OwnedCount = 6 }],
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"match_badge\":{\"retail_matched\":true,\"provider_name\":\"TMDB\",\"canonical_matched\":false}", json, StringComparison.Ordinal);
        Assert.Contains("\"media_types\":[{\"key\":\"Books\",\"label\":\"Books\",\"owned_count\":6}]", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<EditorSubjectDto>(json)!;
        Assert.True(back.MatchBadge!.RetailMatched);
        Assert.Equal("TMDB", back.MatchBadge.ProviderName);
        Assert.Equal(6, Assert.Single(back.MediaTypes).OwnedCount);
    }

    [Fact]
    public void ASubjectThatIsNotASeries_LeavesTheBadgeOutAndHasNoKinds()
    {
        var json = JsonSerializer.Serialize(new EditorSubjectDto { Id = Guid.NewGuid(), Kind = EditorSubjectKinds.Universe });

        Assert.DoesNotContain("match_badge", json, StringComparison.Ordinal);
        Assert.Contains("\"media_types\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitlesPage_UsesSnakeCaseNames_AndATitleNotOwnedHasNoId()
    {
        var owned = Guid.NewGuid();
        var dto = new EditorSeriesTitlesDto
        {
            MediaType = "Books",
            Total = 2,
            Offset = 0,
            ShowingMissing = true,
            Items =
            [
                new EditorSeriesTitleDto { Id = owned, Title = "Dune", Number = "1" },
                new EditorSeriesTitleDto { Title = "Children of Dune", Number = "3", Owned = false },
            ],
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"media_type\":\"Books\"", json, StringComparison.Ordinal);
        Assert.Contains("\"showing_missing\":true", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<EditorSeriesTitlesDto>(json)!;
        Assert.Equal(owned, back.Items[0].Id);
        Assert.True(back.Items[0].Owned);
        Assert.Null(back.Items[1].Id);
        Assert.False(back.Items[1].Owned);
    }
}
