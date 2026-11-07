using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match tab's search (universal editor v5, WP9, 3c): the boxes open pre-filled from the cleaned file name, a search returns results
/// judged against the file, and an item with no provider (a course, a supporting file, a local video) is told so rather than searched.
/// Real temp SQLite for the item; the providers are hand-written spies.
/// </summary>
public sealed class EditorMatchSearchServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("The Matrix (1999)");
    private readonly ProviderCatalogSpy _catalog = new();
    private readonly CandidateSearchSpy _search = new();

    public void Dispose() => _world.Dispose();

    // ── Opening the search ────────────────────────────────────────────────────

    [Fact]
    public async Task AMovie_OpensOnTheCleanedFileName_WithTheDefaultProviderFirst_AndMakesNoSearch()
    {
        var movie = Movie("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", durationSeconds: 8160);

        var setup = await Service().SetupAsync(movie, EditorKinds.Movie);

        Assert.NotNull(setup);
        Assert.False(setup.NoProvider);
        Assert.Equal("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", setup.FileName);
        Assert.Equal("tmdb", setup.Providers[0].Name);
        Assert.True(setup.Providers[0].IsDefault);
        Assert.Equal(EditorMatchProviderStates.Ready, setup.Providers[0].State);
        Assert.Equal(["title", "year", "provider_id"], setup.Fields.Select(field => field.Key).ToArray());
        Assert.Equal("The Matrix", setup.Fields[0].Value);
        Assert.Equal("1999", setup.Fields[1].Value);
        Assert.Equal("8160", setup.Evidence.MeasuredSeconds?.ToString("0"));
        Assert.Equal("2:16:00", setup.Evidence.MeasuredText);
        Assert.Empty(_search.Requests);
    }

    [Fact]
    public async Task AProviderWithNoKeyOrSwitchedOff_IsListedWithItsState()
    {
        var movie = Movie("Arrival.2016.mkv");
        _catalog.Providers =
        [
            new RetailProviderInfo("tmdb", "TMDB", true, true, [EditorKinds.Movie], 1),
            new RetailProviderInfo("other", "Other", false, false, [], 2),
        ];

        var setup = await Service().SetupAsync(movie, EditorKinds.Movie);

        Assert.Equal(EditorMatchProviderStates.NeedsKey, setup!.Providers[0].State);
        Assert.Equal(EditorMatchProviderStates.Off, setup.Providers[1].State);
    }

    [Fact]
    public async Task ACourseASupportingFileAndASeries_HaveNoProvider_AndNothingIsAsked()
    {
        var service = Service();
        foreach (var kind in new[] { EditorKinds.Course, EditorKinds.SupportingFile, EditorKinds.Series })
        {
            var setup = await service.SetupAsync(Guid.NewGuid(), kind);
            var searched = await service.SearchAsync(Guid.NewGuid(), new EditorMatchSearchRequestDto
            {
                Kind = kind,
                Fields = new Dictionary<string, string> { ["title"] = "Anything" },
            });

            Assert.True(setup!.NoProvider);
            Assert.Equal(EditorMatchSearchService.NoProviderText, setup.NoProviderMessage);
            Assert.Empty(setup.Providers);
            Assert.Empty(setup.Fields);
            Assert.Empty(searched!.Results);
            Assert.Equal(EditorMatchSearchService.NoProviderText, searched.Message);
        }

        Assert.Empty(_search.Requests);
    }

    [Fact]
    public async Task ALocalVideo_HasNoProvider_UnlessAYouTubeIdIsKnown()
    {
        _catalog.Providers = [new RetailProviderInfo("youtube", "YouTube", true, false, [EditorKinds.Video], 1)];
        var local = _world.Work("Videos", "standalone", "Family trip");
        _world.File(local, "Family trip.mp4");
        var tagged = _world.Work("Videos", "standalone", "Rick");
        _world.File(tagged, "Never Gonna Give You Up [dQw4w9WgXcQ].mp4");

        var withoutId = await Service().SetupAsync(local, EditorKinds.Video);
        var withId = await Service().SetupAsync(tagged, EditorKinds.Video);

        Assert.True(withoutId!.NoProvider);
        Assert.False(withId!.NoProvider);
        Assert.Equal(["youtube"], withId.Providers.Select(provider => provider.Name).ToArray());
        Assert.Equal("dQw4w9WgXcQ", withId.Fields.Single(field => field.Key == "provider_id").Value);
    }

    [Fact]
    public async Task ATvKind_OffersTheTvdbOnly()
    {
        _catalog.Providers =
        [
            new RetailProviderInfo("tvdb", "TheTVDB", true, false, [EditorKinds.TvShow], 1),
            new RetailProviderInfo("tmdb", "TMDB", true, false, [], 2),
        ];
        var show = _world.Work("TV", "parent", "Long Runner");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Pilot", season, 1);
        _world.File(episode, "Long.Runner.S01E01.Pilot.mkv");

        var setup = await Service().SetupAsync(episode, EditorKinds.TvEpisode);

        Assert.Equal(["tvdb"], setup!.Providers.Select(provider => provider.Name).ToArray());
        Assert.Equal("Long Runner", setup.Fields.Single(field => field.Key == "show").Value);
    }

    // ── Searching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASearch_AsksTheCanonicalSearchWithTheCleanedTitle_AndJudgesEachResultAgainstTheFile()
    {
        var movie = Movie("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", durationSeconds: 8160);
        _search.Candidates =
        [
            Candidate("603", "The Matrix", "1999", director: "Lana Wachowski", runtimeMinutes: "136", score: 0.95),
            Candidate("604", "The Matrix Reloaded", "2003", director: "Lana Wachowski", runtimeMinutes: "138", score: 0.60),
        ];
        var service = Service();
        var setup = await service.SetupAsync(movie, EditorKinds.Movie);

        var found = await service.SearchAsync(movie, new EditorMatchSearchRequestDto
        {
            Kind = EditorKinds.Movie,
            Provider = "tmdb",
            Fields = setup!.Fields.ToDictionary(field => field.Key, field => field.Value),
        });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal("The Matrix", asked.QueryOverride);
        Assert.Equal("tmdb", asked.ProviderName);
        Assert.Equal("The Matrix", asked.DraftFields["title"]);
        Assert.Equal("1999", asked.DraftFields["year"]);
        Assert.Equal("Movies", asked.MediaType);
        Assert.Equal("movie_identity", asked.TargetFieldGroup);

        Assert.Equal(["The Matrix", "The Matrix Reloaded"], found!.Results.Select(result => result.Title).ToArray());
        var strong = found.Results[0];
        Assert.Equal(EditorMatchVerdicts.Strong, strong.Verdict);
        Assert.Equal("136 min", strong.LengthText);
        Assert.Equal("Lana Wachowski", strong.Creator);
        Assert.Equal("https://www.themoviedb.org/movie/603", strong.SourceUrl);
        Assert.Contains(strong.Reasons, reason => reason.Fact == "runtime" && reason.State == EditorMatchEvidenceStates.Agrees && reason.Text == "Runtime consistent");
        Assert.Equal(EditorMatchVerdicts.Check, found.Results[1].Verdict);
        Assert.Null(found.Message);
        Assert.False(found.RetriedWithoutPunctuation);
    }

    [Fact]
    public async Task AFactTheFileCannotSpeakTo_IsNotChecked_AndNeverCountsAgainstTheMatch()
    {
        var movie = Movie("The.Matrix.1999.mkv");
        _search.Candidates = [Candidate("603", "The Matrix", "1999", director: "Lana Wachowski", runtimeMinutes: "136", score: 0.9)];

        var found = await Service().SearchAsync(movie, Request("The Matrix", "1999"));

        var result = Assert.Single(found!.Results);
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, result.Reasons.Single(reason => reason.Fact == "runtime").State);
        Assert.Equal(EditorMatchVerdicts.Strong, result.Verdict);
    }

    [Fact]
    public async Task ACheckMatch_StillHasItsCandidateIntactToApply()
    {
        var movie = Movie("The.Matrix.1999.mkv");
        var wrong = Candidate("999", "Something Else Entirely", "2012", score: 0.3);
        _search.Candidates = [wrong];

        var found = await Service().SearchAsync(movie, Request("The Matrix", "1999"));

        var result = Assert.Single(found!.Results);
        Assert.Equal(EditorMatchVerdicts.Check, result.Verdict);
        Assert.Same(wrong, result.Candidate);
        Assert.True(result.Candidate.IsApplicable);
        Assert.Equal("999", result.Candidate.ProviderItemId);
    }

    [Fact]
    public async Task ANothingFoundSearch_IsTriedOnceMoreWithThePunctuationTakenOut()
    {
        var movie = Movie("Spider-Man.Homecoming.2017.mkv");
        _search.Script = request => request.QueryOverride == "Spider Man Homecoming"
            ? [Candidate("315635", "Spider-Man: Homecoming", "2017", score: 0.9)]
            : [];

        var found = await Service().SearchAsync(movie, Request("Spider-Man: Homecoming", "2017"));

        Assert.Equal(2, _search.Requests.Count);
        Assert.Equal("Spider-Man: Homecoming", _search.Requests[0].QueryOverride);
        Assert.Equal("Spider Man Homecoming", _search.Requests[1].QueryOverride);
        Assert.True(found!.RetriedWithoutPunctuation);
        Assert.Equal("Spider-Man: Homecoming", Assert.Single(found.Results).Title);
    }

    [Fact]
    public async Task ATitleWithNoPunctuation_IsNotRetried()
    {
        var movie = Movie("Arrival.2016.mkv");

        var found = await Service().SearchAsync(movie, Request("Arrival", "2016"));

        Assert.Single(_search.Requests);
        Assert.Empty(found!.Results);
        Assert.False(found.RetriedWithoutPunctuation);
        Assert.Contains("Nothing matches", found.Message);
    }

    [Fact]
    public async Task AProviderThatDidNotAnswer_IsNamed_AndIsNotRetried()
    {
        var movie = Movie("Arrival.2016.mkv");
        _search.ProviderErrors = ["tmdb search could not complete. Please retry."];

        var found = await Service().SearchAsync(movie, Request("Arrival: The Film", "2016"));

        Assert.Single(_search.Requests);
        Assert.Equal("tmdb", Assert.Single(found!.FailedProviders).Name);
        Assert.Equal("TMDB did not answer. Try again, or search in another provider.", found.Message);
    }

    [Fact]
    public async Task AProviderThatNeedsAKey_IsNotSearched_AndSaysSo()
    {
        var movie = Movie("Arrival.2016.mkv");
        _catalog.Providers = [new RetailProviderInfo("tmdb", "TMDB", true, true, [EditorKinds.Movie], 1)];

        var found = await Service().SearchAsync(movie, Request("Arrival", "2016"));

        Assert.Empty(_search.Requests);
        Assert.Contains("needs a key", found!.Message);
    }

    [Fact]
    public async Task ABooksEditions_AreListedUnderTheirWork_WithTheClosestEditionSuggested()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        _world.File(book, "Frank Herbert - Dune (1965).epub");
        _catalog.Providers = [new RetailProviderInfo("openlibrary", "Open Library", true, false, [EditorKinds.Book], 1)];
        _search.Candidates =
        [
            Candidate("OL1W", "Dune", "1990", author: "Frank Herbert", score: 0.9, extra: new() { ["publisher"] = "Ace", ["format"] = "Paperback" }),
            Candidate("OL2W", "Dune", "1965", author: "Frank Herbert", score: 0.88, extra: new() { ["publisher"] = "Chilton", ["format"] = "Hardcover" }),
            Candidate("OL3W", "Dune Messiah", "1969", author: "Frank Herbert", score: 0.5),
        ];

        var found = await Service().SearchAsync(book, new EditorMatchSearchRequestDto
        {
            Kind = EditorKinds.Book,
            Fields = new Dictionary<string, string> { ["title"] = "Dune", ["author"] = "Frank Herbert" },
        });

        Assert.Equal(["Dune", "Dune Messiah"], found!.Results.Select(result => result.Title).ToArray());
        var work = found.Results[0];
        Assert.Equal(2, work.Editions.Count);
        var suggested = Assert.Single(work.Editions, edition => edition.IsSuggested);
        Assert.Equal("1965", suggested.Edition!.Year);
        Assert.Equal("Chilton", suggested.Edition.Publisher);
        Assert.Empty(found.Results[1].Editions);
    }

    [Fact]
    public async Task AnItemTheLibraryDoesNotHave_IsNotFound()
    {
        Assert.Null(await Service().SetupAsync(Guid.NewGuid(), EditorKinds.Movie));
    }

    [Theory]
    [InlineData("Spider-Man: Homecoming", "Spider Man Homecoming")]
    [InlineData("  Ocean's   Eleven! ", "Ocean s Eleven")]
    [InlineData("Amélie", "Amélie")]
    public void Punctuation_IsTakenOutAndSpacesTidied(string text, string expected) =>
        Assert.Equal(expected, EditorMatchSearchService.StripPunctuation(text));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid Movie(string fileName, double? durationSeconds = null)
    {
        var movie = _world.Work("Movies", "standalone", "The Matrix");
        _world.File(movie, fileName, durationSeconds);
        return movie;
    }

    private static EditorMatchSearchRequestDto Request(string title, string year) => new()
    {
        Kind = EditorKinds.Movie,
        Provider = "tmdb",
        Fields = new Dictionary<string, string> { ["title"] = title, ["year"] = year },
    };

    private EditorMatchSearchService Service()
    {
        var reader = new EditorMatchLocalEvidenceReader(
            new MetadataEditorRepository(_world.Database),
            new ItemCanonicalRepository(_world.Database, NullLogger<ItemCanonicalRepository>.Instance),
            new MetadataClaimRepository(_world.Database),
            _world.QuickFacts,
            NullLogger<EditorMatchLocalEvidenceReader>.Instance);
        return new EditorMatchSearchService(
            reader,
            _catalog,
            _search,
            new EditorConfigurationProvider(() => null),
            new EditorMatchVerdictCalculator(new MatchVerdictSettings()));
    }

    private static ItemCanonicalRetailCandidateDto Candidate(
        string id, string title, string year, string? director = null, string? author = null, string? runtimeMinutes = null,
        double score = 0.8, Dictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(extra ?? [], StringComparer.OrdinalIgnoreCase);
        if (runtimeMinutes is not null)
        {
            fields["runtime"] = runtimeMinutes;
        }

        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"tmdb:{id}",
            ProviderId = Guid.NewGuid().ToString(),
            ProviderName = "tmdb",
            ProviderItemId = id,
            Title = title,
            Year = year,
            Director = director,
            Author = author,
            CompositeScore = score,
            IsApplicable = true,
            ExtraFields = fields,
        };
    }

    private sealed class ProviderCatalogSpy : IEditorMatchProviderCatalog
    {
        public IReadOnlyList<RetailProviderInfo> Providers { get; set; } =
        [
            new("tmdb", "TMDB", true, false, [EditorKinds.Movie], 1),
            new("youtube", "YouTube", true, false, [EditorKinds.Video], 2),
        ];

        public IReadOnlyList<RetailProviderInfo> List(string mediaType) => Providers;
    }

    private sealed class CandidateSearchSpy : IEditorMatchCandidateSearch
    {
        public List<ItemCanonicalSearchRequestDto> Requests { get; } = [];

        public List<ItemCanonicalRetailCandidateDto> Candidates { get; set; } = [];

        public Func<ItemCanonicalSearchRequestDto, List<ItemCanonicalRetailCandidateDto>>? Script { get; set; }

        public List<string> ProviderErrors { get; set; } = [];

        public Task<EditorMatchSearchOutcome> SearchAsync(Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct)
        {
            Requests.Add(request);
            var candidates = Script is null ? Candidates : Script(request);
            return Task.FromResult(new EditorMatchSearchOutcome(
                new ItemCanonicalSearchResponseDto
                {
                    EntityId = entityId,
                    Query = request.QueryOverride ?? string.Empty,
                    RetailCandidates = candidates,
                    ProviderErrors = ProviderErrors,
                },
                null));
        }
    }
}
