using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Match tab's search (universal editor v5, WP9): the setup read, the search request and its answer.</summary>
public sealed class EditorMatchSearchContractTests
{
    [Fact]
    public void Setup_UsesSnakeCaseNames_AndRoundTrips()
    {
        var id = Guid.NewGuid();
        var setup = new EditorMatchSetupDto
        {
            EntityId = id,
            Kind = "movie",
            FileName = "Heat (1995).mkv",
            Providers = [new EditorMatchProviderDto { Name = "tmdb", DisplayName = "TMDB", State = EditorMatchProviderStates.Ready, IsDefault = true }],
            Fields = [new EditorMatchFieldDto { Key = "title", Label = "Title", Value = "Heat", From = "file_name" }],
            Evidence = new EditorMatchLocalEvidenceDto
            {
                FileTitle = "Heat",
                Year = "1995",
                MeasuredSeconds = 10200,
                MeasuredText = "2:50:00",
                Identifiers = [new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Imdb, Value = "tt0113277" }],
            },
        };

        var json = JsonSerializer.Serialize(setup);
        var back = JsonSerializer.Deserialize<EditorMatchSetupDto>(json)!;

        Assert.Contains("\"entity_id\":\"" + id.ToString("D") + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"no_provider\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"providers\":[{\"name\":\"tmdb\",\"display_name\":\"TMDB\",\"state\":\"ready\",\"is_default\":true}]", json, StringComparison.Ordinal);
        Assert.Contains("\"fields\":[{\"key\":\"title\",\"label\":\"Title\",\"value\":\"Heat\",\"from\":\"file_name\"}]", json, StringComparison.Ordinal);
        Assert.Contains("\"measured_seconds\":10200", json, StringComparison.Ordinal);
        Assert.Contains("\"identifiers\":[{\"kind\":\"imdb\",\"value\":\"tt0113277\"}]", json, StringComparison.Ordinal);
        Assert.Equal("Heat (1995).mkv", back.FileName);
        Assert.Equal("tmdb", back.Providers[0].Name);
    }

    [Fact]
    public void SearchRequest_UsesSnakeCaseNames()
    {
        var request = new EditorMatchSearchRequestDto
        {
            Provider = "openlibrary",
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Dune", ["author"] = "Frank Herbert" },
        };

        var json = JsonSerializer.Serialize(request);
        var back = JsonSerializer.Deserialize<EditorMatchSearchRequestDto>(json)!;

        Assert.Equal("{\"provider\":\"openlibrary\",\"fields\":{\"title\":\"Dune\",\"author\":\"Frank Herbert\"}}", json);
        Assert.Equal("Dune", back.Fields["title"]);
    }

    [Fact]
    public void Placement_UsesSnakeCaseNames()
    {
        var placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ParentLabel = "OK Computer (Radiohead)",
            CurrentParentLabel = "The Best Of (Radiohead)",
            Managed = true,
            FromPath = "/m/a.flac",
            ToPath = "/m/b.flac",
        };

        var json = JsonSerializer.Serialize(placement);
        var back = JsonSerializer.Deserialize<EditorMatchPlacementDto>(json)!;

        Assert.Contains("\"destination_id\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"current_parent_label\":\"The Best Of (Radiohead)\"", json, StringComparison.Ordinal);
        Assert.Contains("\"from_path\":\"/m/a.flac\"", json, StringComparison.Ordinal);
        Assert.Contains("\"to_path\":\"/m/b.flac\"", json, StringComparison.Ordinal);
        Assert.True(back.Managed);
    }

    [Fact]
    public void SearchResult_CarriesTheVerdictItsReasonsAndTheEditions()
    {
        var edition = new EditorMatchResultDto
        {
            Candidate = new ItemCanonicalRetailCandidateDto { CandidateId = "ol:2", ProviderId = "openlibrary", Title = "Dune" },
            Title = "Dune",
            Verdict = EditorMatchVerdicts.Strong,
            Edition = new EditorMatchEditionDto { Year = "1990", Publisher = "Ace", Format = "Paperback", Language = "English" },
            IsSuggested = true,
        };
        var result = new EditorMatchSearchResultDto
        {
            EntityId = Guid.NewGuid(),
            Provider = "openlibrary",
            Query = "Dune",
            RetriedWithoutPunctuation = true,
            Results =
            [
                new EditorMatchResultDto
                {
                    Candidate = new ItemCanonicalRetailCandidateDto { CandidateId = "ol:1", ProviderId = "openlibrary", Title = "Dune" },
                    ProviderName = "Open Library",
                    Title = "Dune",
                    Year = "1965",
                    Creator = "Frank Herbert",
                    Verdict = EditorMatchVerdicts.Possible,
                    Reasons = [new EditorMatchReasonDto { Fact = "title", State = EditorMatchEvidenceStates.Agrees, Text = "Title matches", Note = "exact" }],
                    Editions = [edition],
                },
            ],
            FailedProviders = [new EditorMatchFailedProviderDto { Name = "apple_api", DisplayName = "Apple" }],
            Message = "Apple did not answer. Try again, or search in another provider.",
        };

        var json = JsonSerializer.Serialize(result);
        var back = JsonSerializer.Deserialize<EditorMatchSearchResultDto>(json)!;

        Assert.Contains("\"retried_without_punctuation\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"reasons\":[{\"fact\":\"title\",\"state\":\"agrees\",\"text\":\"Title matches\",\"note\":\"exact\"}]", json, StringComparison.Ordinal);
        Assert.Contains("\"failed_providers\":[{\"name\":\"apple_api\",\"display_name\":\"Apple\"}]", json, StringComparison.Ordinal);
        Assert.Contains("\"is_suggested\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"edition\":{\"year\":\"1990\",\"publisher\":\"Ace\",\"format\":\"Paperback\",\"language\":\"English\"}", json, StringComparison.Ordinal);
        Assert.Equal(EditorMatchVerdicts.Possible, back.Results[0].Verdict);
        Assert.Equal("Ace", back.Results[0].Editions[0].Edition!.Publisher);
    }

    [Fact]
    public void CurrentMatch_CarriesTheOverallVerdict_AndWhetherThePersonChoseIt()
    {
        var match = new EditorCurrentMatchDto
        {
            EntityId = Guid.NewGuid(),
            State = EditorCurrentMatchStates.Matched,
            Verdict = EditorMatchVerdicts.Weak,
            Pinned = true,
            Why = [new EditorMatchEvidenceRowDto { Field = "runtime", State = EditorMatchEvidenceStates.Differs, Verdict = EditorMatchVerdicts.Weak, Note = "2:00:00 measured" }],
        };

        var json = JsonSerializer.Serialize(match);
        var back = JsonSerializer.Deserialize<EditorCurrentMatchDto>(json)!;

        Assert.Contains("\"verdict\":\"weak\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pinned\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"2:00:00 measured\"", json, StringComparison.Ordinal);
        Assert.True(back.Pinned);
        Assert.Equal(EditorMatchVerdicts.Weak, back.Why[0].Verdict);
    }

    [Fact]
    public void SearchResult_CarriesTheCompareRowsTheCollectionMarkAndTheSeriesFacts()
    {
        var result = new EditorMatchResultDto
        {
            Candidate = new ItemCanonicalRetailCandidateDto { CandidateId = "apple:1", ProviderId = "apple_api", Title = "The Sea of Monsters" },
            Title = "The Sea of Monsters",
            IsCollection = false,
            SeriesName = "Percy Jackson and the Olympians",
            SeriesNumber = "2",
            Verdict = EditorMatchVerdicts.Strong,
            Reasons =
            [
                new EditorMatchReasonDto { Fact = "title", State = EditorMatchEvidenceStates.Agrees, YourFile = "The Sea of Monsters", Result = "The Sea of Monsters", Text = "Title matches" },
                new EditorMatchReasonDto { Fact = "year", State = EditorMatchEvidenceStates.NotChecked, Result = "2009", Text = "Year not checked" },
            ],
        };

        var json = JsonSerializer.Serialize(result);
        var back = JsonSerializer.Deserialize<EditorMatchResultDto>(json)!;

        Assert.Contains("\"is_collection\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"series_name\":\"Percy Jackson and the Olympians\"", json, StringComparison.Ordinal);
        Assert.Contains("\"series_number\":\"2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"strong\"", json, StringComparison.Ordinal);
        Assert.Contains("{\"fact\":\"title\",\"state\":\"agrees\",\"your_file\":\"The Sea of Monsters\",\"result\":\"The Sea of Monsters\",\"text\":\"Title matches\"}", json, StringComparison.Ordinal);
        // A side that says nothing is left out, not sent as an empty string.
        Assert.Contains("{\"fact\":\"year\",\"state\":\"not_checked\",\"result\":\"2009\",\"text\":\"Year not checked\"}", json, StringComparison.Ordinal);
        Assert.Null(back.Reasons[1].YourFile);
        Assert.Equal("2009", back.Reasons[1].Result);
        Assert.Equal("2", back.SeriesNumber);
    }

    [Fact]
    public void AResultWithNoSeries_LeavesTheSeriesFactsOutOfTheJson()
    {
        var json = JsonSerializer.Serialize(new EditorMatchResultDto { Title = "Heat", IsCollection = true });

        Assert.DoesNotContain("series_name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("series_number", json, StringComparison.Ordinal);
        Assert.Contains("\"is_collection\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWeakVerdict_IsSentAsWeak_AndTheWordCheckIsGone()
    {
        Assert.Equal("weak", EditorMatchVerdicts.Weak);
        Assert.Equal(["strong", "possible", "weak"], new[] { EditorMatchVerdicts.Strong, EditorMatchVerdicts.Possible, EditorMatchVerdicts.Weak });
    }
}
