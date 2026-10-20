using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class UniverseWireMappingTests
{
    [Fact]
    public void LoreDelta_MapsEachRevisionIntoItsOwnSnakeCaseField()
    {
        var mapped = UniverseGraphEndpoints.MapLoreDelta(new LoreDeltaResult("Q10", "Paul Atreides", 5, 7, true));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(mapped));
        var root = json.RootElement;
        Assert.Equal("Q10", root.GetProperty("entity_qid").GetString());
        Assert.Equal("Paul Atreides", root.GetProperty("label").GetString());
        Assert.Equal(5, root.GetProperty("cached_revision").GetInt64());
        Assert.Equal(7, root.GetProperty("current_revision").GetInt64());
        Assert.True(root.GetProperty("has_changed").GetBoolean());
    }

    [Fact]
    public void UniverseSearch_CarriesEveryCandidateFieldAndTheMatchScores()
    {
        var result = new SearchUniverseResult(
            [
                new UniverseCandidate
                {
                    Qid = "Q190192",
                    Label = "Dune",
                    Description = "1965 novel",
                    InstanceOf = "literary work",
                    Year = "1965",
                    Author = "Frank Herbert",
                    Director = "Denis Villeneuve",
                    CoverUrl = "https://example.test/dune.jpg",
                    WikipediaExtract = "Dune is a novel.",
                    ResolutionTier = "bridge",
                    Confidence = 0.92,
                    BridgeIds = new Dictionary<string, string> { ["isbn"] = "9780441013593" },
                    MediaType = "Books",
                    MediaTypeMetadata = new Dictionary<string, string> { ["author"] = "Frank Herbert" },
                    MatchScores = new FieldMatchResult { TitleScore = 0.9, AuthorScore = 0.8, YearScore = 1.0, CompositeScore = 0.88 },
                },
            ],
            "dune",
            "Books");

        var mapped = SearchEndpoints.MapUniverseResponse(result);

        Assert.Equal("dune", mapped.Query);
        Assert.Equal("Books", mapped.MediaType);
        var candidate = Assert.Single(mapped.Candidates);
        Assert.Equal("Q190192", candidate.Qid);
        Assert.Equal("Dune", candidate.Label);
        Assert.Equal("1965 novel", candidate.Description);
        Assert.Equal("literary work", candidate.InstanceOf);
        Assert.Equal("1965", candidate.Year);
        Assert.Equal("Frank Herbert", candidate.Author);
        Assert.Equal("Denis Villeneuve", candidate.Director);
        Assert.Equal("https://example.test/dune.jpg", candidate.CoverUrl);
        Assert.Equal("Dune is a novel.", candidate.WikipediaExtract);
        Assert.Equal("bridge", candidate.ResolutionTier);
        Assert.Equal(0.92, candidate.Confidence);
        Assert.Equal("9780441013593", candidate.BridgeIds["isbn"]);
        Assert.Equal("Books", candidate.MediaType);
        Assert.Equal("Frank Herbert", candidate.MediaTypeMetadata!["author"]);
        Assert.NotNull(candidate.MatchScores);
        Assert.Equal(0.9, candidate.MatchScores.TitleScore);
        Assert.Equal(0.8, candidate.MatchScores.AuthorScore);
        Assert.Equal(1.0, candidate.MatchScores.YearScore);
        Assert.Equal(0.88, candidate.MatchScores.CompositeScore);
    }
}
