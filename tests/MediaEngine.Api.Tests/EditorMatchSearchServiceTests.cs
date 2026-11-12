using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
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

        var setup = await Service().SetupForKindAsync(movie, EditorKinds.Movie);

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

        var setup = await Service().SetupForKindAsync(movie, EditorKinds.Movie);

        Assert.Equal(EditorMatchProviderStates.NeedsKey, setup!.Providers[0].State);
        Assert.Equal(EditorMatchProviderStates.Off, setup.Providers[1].State);
    }

    [Fact]
    public async Task ACourseASupportingFileAndASeries_HaveNoProvider_AndNothingIsAsked()
    {
        var service = Service();
        foreach (var kind in new[] { EditorKinds.Course, EditorKinds.SupportingFile, EditorKinds.Series })
        {
            var setup = await service.SetupForKindAsync(Guid.NewGuid(), kind);
            var searched = await service.SearchForKindAsync(Guid.NewGuid(), kind, new EditorMatchSearchRequestDto
            {
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

        var withoutId = await Service().SetupForKindAsync(local, EditorKinds.Video);
        var withId = await Service().SetupForKindAsync(tagged, EditorKinds.Video);

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
            new RetailProviderInfo("tvdb", "TheTVDB", true, false, [EditorKinds.TvShow], 1, [EditorKinds.TvShow, EditorKinds.TvSeason, EditorKinds.TvEpisode]),
            new RetailProviderInfo("tmdb", "TMDB", true, false, [EditorKinds.Movie], 2, [EditorKinds.Movie]),
        ];
        var show = _world.Work("TV", "parent", "Long Runner");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Pilot", season, 1);
        _world.File(episode, "Long.Runner.S01E01.Pilot.mkv");

        var setup = await Service().SetupForKindAsync(episode, EditorKinds.TvEpisode);

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
        var setup = await service.SetupForKindAsync(movie, EditorKinds.Movie);

        var found = await service.SearchForKindAsync(movie, EditorKinds.Movie, new EditorMatchSearchRequestDto
        {
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
        Assert.Equal(EditorMatchVerdicts.Weak, found.Results[1].Verdict);
        Assert.Null(found.Message);
        Assert.False(found.RetriedWithoutPunctuation);
    }

    [Fact]
    public async Task AFactTheFileCannotSpeakTo_IsNotChecked_AndNeverCountsAgainstTheMatch()
    {
        var movie = Movie("The.Matrix.1999.mkv");
        _search.Candidates = [Candidate("603", "The Matrix", "1999", director: "Lana Wachowski", runtimeMinutes: "136", score: 0.9)];

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("The Matrix", "1999"));

        var result = Assert.Single(found!.Results);
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, result.Reasons.Single(reason => reason.Fact == "runtime").State);
        Assert.Equal(EditorMatchVerdicts.Strong, result.Verdict);
    }

    [Fact]
    public async Task AWeakMatch_StillHasItsCandidateIntactToApply()
    {
        var movie = Movie("The.Matrix.1999.mkv");
        var wrong = Candidate("999", "Something Else Entirely", "2012", score: 0.3);
        _search.Candidates = [wrong];

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("The Matrix", "1999"));

        var result = Assert.Single(found!.Results);
        Assert.Equal(EditorMatchVerdicts.Weak, result.Verdict);
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

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("Spider-Man: Homecoming", "2017"));

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

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("Arrival", "2016"));

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

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("Arrival: The Film", "2016"));

        Assert.Single(_search.Requests);
        Assert.Equal("tmdb", Assert.Single(found!.FailedProviders).Name);
        Assert.Equal("TMDB did not answer. Try again, or search in another provider.", found.Message);
    }

    [Fact]
    public async Task AProviderThatNeedsAKey_IsNotSearched_AndSaysSo()
    {
        var movie = Movie("Arrival.2016.mkv");
        _catalog.Providers = [new RetailProviderInfo("tmdb", "TMDB", true, true, [EditorKinds.Movie], 1)];

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("Arrival", "2016"));

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

        var found = await Service().SearchForKindAsync(book, EditorKinds.Book, new EditorMatchSearchRequestDto
        {
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

    // ── The compare table (universal editor v6, D9) ───────────────────────────

    private async Task<EditorMatchResultDto> SearchBook(
        ItemCanonicalRetailCandidateDto candidate, string author = "Rick Riordan", string? isbn = null, string? year = null)
    {
        var book = _world.Work("Books", "standalone", "The Sea of Monsters");
        _world.File(book, "Rick Riordan - The Sea of Monsters.epub");
        _catalog.Providers = [new RetailProviderInfo("openlibrary", "Open Library", true, false, [EditorKinds.Book], 1)];
        candidate.ProviderName = "openlibrary";
        _search.Candidates = [candidate];
        var fields = new Dictionary<string, string> { ["title"] = "The Sea of Monsters", ["author"] = author };
        if (isbn is not null)
        {
            fields["isbn"] = isbn;
        }

        if (year is not null)
        {
            fields["year"] = year;
        }

        var found = await Service().SearchForKindAsync(book, EditorKinds.Book, new EditorMatchSearchRequestDto { Fields = fields });
        return Assert.Single(found!.Results);
    }

    [Fact]
    public async Task ABookResult_CarriesOneCompareRowPerFact_InTheOrderTitleAuthorIsbnYear()
    {
        var result = await SearchBook(Candidate("OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan"));

        Assert.Equal(["title", "creator", "identifier", "year"], result.Reasons.Select(reason => reason.Fact).ToArray());
    }

    [Fact]
    public async Task ACompareRow_ShowsWhatEachSideSays_WithATickWhereTheyAgreeAndACrossWhereTheyDiffer()
    {
        var result = await SearchBook(
            Candidate("OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan"), author: "Someone Else", year: "2010");

        var title = result.Reasons.Single(reason => reason.Fact == "title");
        Assert.Equal(EditorMatchEvidenceStates.Agrees, title.State);
        Assert.Equal("The Sea of Monsters", title.YourFile);
        Assert.Equal("The Sea of Monsters", title.Result);
        var creator = result.Reasons.Single(reason => reason.Fact == "creator");
        Assert.Equal(EditorMatchEvidenceStates.Differs, creator.State);
        Assert.Equal("Someone Else", creator.YourFile);
        Assert.Equal("Rick Riordan", creator.Result);
        var year = result.Reasons.Single(reason => reason.Fact == "year");
        Assert.Equal(EditorMatchEvidenceStates.Differs, year.State);
        Assert.Equal("2010", year.YourFile);
        Assert.Equal("2009", year.Result);
    }

    [Fact]
    public async Task ACompareRow_WhereOneSideSaysNothing_IsUnknownAndShowsTheSideThatDoes_AndNeverCountsAgainstTheMatch()
    {
        // The file names an ISBN the record does not carry; the record gives no year but the item says 1999.
        var result = await SearchBook(Candidate("OL1W", "The Sea of Monsters", string.Empty, author: "Rick Riordan"), isbn: "1423103343");

        var isbn = result.Reasons.Single(reason => reason.Fact == "identifier");
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, isbn.State);
        Assert.Equal("1423103343", isbn.YourFile);
        Assert.Null(isbn.Result);
        var year = result.Reasons.Single(reason => reason.Fact == "year");
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, year.State);
        Assert.Equal("1999", year.YourFile);
        Assert.Null(year.Result);
        Assert.Equal(EditorMatchVerdicts.Strong, result.Verdict);
    }

    [Fact]
    public async Task ACompareRow_ForAnIsbnOnlyTheRecordCarries_ShowsItOnTheResultSide()
    {
        var result = await SearchBook(Candidate("OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan", extra: new() { ["isbn"] = "9781423103349" }));

        var isbn = result.Reasons.Single(reason => reason.Fact == "identifier");
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, isbn.State);
        Assert.Null(isbn.YourFile);
        Assert.Equal("9781423103349", isbn.Result);
    }

    [Fact]
    public async Task AMovieResult_ComparesTitleYearAndRuntime_WithTheMeasuredAndTheRoundedLength()
    {
        var movie = Movie("Arrival.2016.mkv", durationSeconds: 6981);
        _search.Candidates = [Candidate("329865", "Arrival", "2016", director: "Denis Villeneuve", runtimeMinutes: "116")];

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, Request("Arrival", "2016"));

        var result = Assert.Single(found!.Results);
        Assert.Equal(["title", "year", "runtime", "identifier"], result.Reasons.Select(reason => reason.Fact).ToArray());
        var runtime = result.Reasons.Single(reason => reason.Fact == "runtime");
        Assert.Equal(EditorMatchEvidenceStates.Agrees, runtime.State);
        Assert.Equal("1:56:21", runtime.YourFile);
        Assert.Equal("116 min", runtime.Result);
    }

    [Fact]
    public async Task AResultWhoseTitleCarriesABoxSetWord_IsMarkedACollection()
    {
        var box = await SearchBook(Candidate("OL2W", "Percy Jackson: The Complete Series", "2026", author: "Rick Riordan"));
        Assert.True(box.IsCollection);
    }

    [Fact]
    public async Task ANumberedSeriesEntry_WhoseTitleSaysTrilogy_IsNotMarkedACollection()
    {
        var entry = await SearchBook(Candidate(
            "OL4W", "The Mistborn Trilogy, Book 1", "2006", author: "Brandon Sanderson",
            extra: new() { ["series"] = "Mistborn", ["series_position"] = "1" }));

        Assert.False(entry.IsCollection);
        Assert.Equal("1", entry.SeriesNumber);
    }

    [Fact]
    public async Task ASingleBook_IsNotMarkedACollection()
    {
        var single = await SearchBook(Candidate("OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan"));
        Assert.False(single.IsCollection);
    }

    [Fact]
    public async Task AResultTheProviderCallsACollection_IsMarkedACollection_WhateverItsTitle()
    {
        var flagged = await SearchBook(Candidate("OL3W", "Books I to III", "2014", author: "Rick Riordan", extra: new() { ["is_collection"] = "true" }));
        Assert.True(flagged.IsCollection);
    }

    [Fact]
    public async Task AResultTheProviderPlacesInASeries_CarriesTheSeriesNameAndNumber()
    {
        var result = await SearchBook(Candidate(
            "OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan",
            extra: new() { ["series"] = "Percy Jackson and the Olympians", ["series_position"] = "2.0" }));

        Assert.Equal("Percy Jackson and the Olympians", result.SeriesName);
        Assert.Equal("2", result.SeriesNumber);
    }

    [Fact]
    public async Task AResultWithNoSeriesFromTheProvider_CarriesNoSeriesFacts()
    {
        var result = await SearchBook(Candidate("OL1W", "The Sea of Monsters", "2009", author: "Rick Riordan"));

        Assert.Null(result.SeriesName);
        Assert.Null(result.SeriesNumber);
    }

    // ── Videos and typed identifiers ──────────────────────────────────────────

    // The canonical search itself has no policy for the Videos library, so the video kind must never reach it. The real search is built with
    // nothing behind it: reaching it would throw, where the spy would have quietly answered.
    private static CanonicalEditorMatchCandidateSearch RealSearchWithNothingBehindIt() => new(null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task AVideoWithAKnownYouTubeId_IsLookedUpById_NotThroughTheCanonicalSearch()
    {
        _catalog.Providers = [new RetailProviderInfo("youtube", "YouTube", true, false, [EditorKinds.Video], 1)];
        var tagged = _world.Work("Videos", "standalone", "Rick");
        _world.File(tagged, "Never Gonna Give You Up [dQw4w9WgXcQ].mp4");
        var videos = new VideoWriterSpy
        {
            Answer = new VideoIdentifyOutcome(
                VideoIdentifyStatus.Ok, null, "dQw4w9WgXcQ",
                new VideoOnlineInfo("Never Gonna Give You Up", "Rick Astley", "The official video.", new DateOnly(2009, 10, 25), null, null)),
        };
        var service = Service(RealSearchWithNothingBehindIt(), videos);
        var setup = await service.SetupForKindAsync(tagged, EditorKinds.Video);

        var found = await service.SearchForKindAsync(tagged, EditorKinds.Video, new EditorMatchSearchRequestDto
        {
            Provider = "youtube",
            Fields = setup!.Fields.ToDictionary(field => field.Key, field => field.Value),
        });

        Assert.Equal(["dQw4w9WgXcQ"], videos.Pasted);
        var result = Assert.Single(found!.Results);
        Assert.Equal("Never Gonna Give You Up", result.Title);
        Assert.Equal("dQw4w9WgXcQ", result.Candidate.ProviderItemId);
        Assert.Equal("youtube", result.Candidate.ProviderName);
        Assert.Contains(result.Reasons, reason => reason.Fact == "identifier" && reason.State == EditorMatchEvidenceStates.Agrees);
    }

    [Fact]
    public async Task AVideoWithNoId_StillTakesAPastedLink_AndNoProviderIsStillSaid()
    {
        _catalog.Providers = [new RetailProviderInfo("youtube", "YouTube", true, false, [EditorKinds.Video], 1)];
        var local = _world.Work("Videos", "standalone", "Family trip");
        _world.File(local, "Family trip.mp4");
        var videos = new VideoWriterSpy
        {
            Answer = new VideoIdentifyOutcome(
                VideoIdentifyStatus.Ok, null, "dQw4w9WgXcQ",
                new VideoOnlineInfo("Never Gonna Give You Up", "Rick Astley", null, null, null, null)),
        };
        var service = Service(RealSearchWithNothingBehindIt(), videos);

        var setup = await service.SetupForKindAsync(local, EditorKinds.Video);
        var blank = await service.SearchForKindAsync(local, EditorKinds.Video, new EditorMatchSearchRequestDto { Provider = "youtube" });
        var pasted = await service.SearchForKindAsync(local, EditorKinds.Video, new EditorMatchSearchRequestDto
        {
            Provider = "youtube",
            Fields = new Dictionary<string, string> { ["provider_id"] = "https://youtu.be/dQw4w9WgXcQ" },
        });

        Assert.True(setup!.NoProvider);
        Assert.Contains(setup.Fields, field => field.Key == "provider_id");
        Assert.Empty(blank!.Results);
        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ"], videos.Pasted);
        Assert.Equal("dQw4w9WgXcQ", Assert.Single(pasted!.Results).Candidate.ProviderItemId);
    }

    [Fact]
    public async Task ATypedProviderId_WithNoIdLookupInTheProviderFile_IsEvidenceOnly_AndNotSentAsSearchWords()
    {
        var movie = Movie("The.Matrix.1999.mkv");

        var found = await Service().SearchForKindAsync(movie, EditorKinds.Movie, new EditorMatchSearchRequestDto
        {
            Provider = "tmdb",
            Fields = new Dictionary<string, string> { ["provider_id"] = "tt0133093" },
        });

        Assert.Empty(_search.Requests);
        Assert.Equal("Enter something to search for.", found!.Message);
    }

    [Fact]
    public async Task ATypedProviderId_NextToATitle_IsNotAddedToTheSearchWords_WhenTheProviderCannotLookItUp()
    {
        var movie = Movie("The.Matrix.1999.mkv");

        await Service().SearchForKindAsync(movie, EditorKinds.Movie, new EditorMatchSearchRequestDto
        {
            Provider = "tmdb",
            Fields = new Dictionary<string, string> { ["title"] = "The Matrix", ["provider_id"] = "tt0133093" },
        });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal("The Matrix", asked.QueryOverride);
        Assert.Null(asked.LookupFields);
    }

    [Fact]
    public async Task ATypedIsbn_DrivesTheProvidersDirectLookup_WhenTheProviderFileHasAnIdLookup()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        _world.File(book, "Dune.epub");
        _catalog.Providers =
        [
            new RetailProviderInfo(
                "apple_api", "Apple Books", true, false, [EditorKinds.Book], 1, [EditorKinds.Book],
                new Dictionary<string, string> { ["isbn"] = "isbn" }),
        ];

        await Service().SearchForKindAsync(book, EditorKinds.Book, new EditorMatchSearchRequestDto
        {
            Provider = "apple_api",
            Fields = new Dictionary<string, string> { ["isbn"] = "9780441172719" },
        });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal("9780441172719", asked.LookupFields!["isbn"]);
    }

    // ── ISBN alone (universal editor v6, D11) ────────────────────────────────

    private Guid Book()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        _world.File(book, "Dune.epub");
        return book;
    }

    private static RetailProviderInfo AppleBooks() => new(
        "apple_api", "Apple Books", true, false, [EditorKinds.Book], 1, [EditorKinds.Book],
        new Dictionary<string, string> { ["isbn"] = "isbn" });

    private static RetailProviderInfo OpenLibraryWithNoIsbnLookup() => new(
        "openlibrary", "Open Library", true, false, [EditorKinds.Book], 1, [EditorKinds.Book]);

    private Task<EditorMatchSearchResultDto?> SearchBook(Guid book, string provider, Dictionary<string, string> fields) =>
        Service().SearchForKindAsync(book, EditorKinds.Book, new EditorMatchSearchRequestDto { Provider = provider, Fields = fields });

    [Theory]
    [InlineData("978-0-441-17271-9", "9780441172719")]
    [InlineData("978 0 441 17271 9", "9780441172719")]
    [InlineData("9780441172719", "9780441172719")]
    [InlineData("0441172717", "0441172717")]
    [InlineData("0-441-17271-7", "0441172717")]
    [InlineData("0-8044-2957-x", "080442957X")]
    public async Task AnIsbnTypedAlone_IsCleanedAndSentToTheProvidersDirectLookup(string typed, string expected)
    {
        _catalog.Providers = [AppleBooks()];

        await SearchBook(Book(), "apple_api", new Dictionary<string, string> { ["isbn"] = typed });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal(expected, asked.LookupFields!["isbn"]);
        Assert.Equal(expected, asked.QueryOverride);
    }

    [Fact]
    public async Task AnIsbnTypedAlone_ForAProviderThatCannotLookItUp_SaysSoAndAsksForATitle_WithoutCallingTheProvider()
    {
        _catalog.Providers = [OpenLibraryWithNoIsbnLookup()];

        var found = await SearchBook(Book(), "openlibrary", new Dictionary<string, string> { ["isbn"] = "978-0-441-17271-9" });

        Assert.Empty(_search.Requests);
        Assert.Equal("Open Library cannot search by ISBN. Add a title.", found!.Message);
        Assert.Empty(found.Results);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("978-0-441-17271")]
    [InlineData("not an isbn")]
    [InlineData("97804411727190")]
    public async Task AnInvalidIsbnTypedAlone_IsToldItIsNotAnIsbn_ForEveryProvider_WithoutCallingTheProvider(string typed)
    {
        _catalog.Providers = [AppleBooks(), OpenLibraryWithNoIsbnLookup()];
        var book = Book();

        foreach (var provider in new[] { "apple_api", "openlibrary" })
        {
            var found = await SearchBook(book, provider, new Dictionary<string, string> { ["isbn"] = typed });

            Assert.Equal("That does not look like an ISBN. An ISBN has 10 or 13 digits.", found!.Message);
        }

        Assert.Empty(_search.Requests);
    }

    [Fact]
    public async Task ATitleWithAnIsbn_ForAProviderThatCannotLookItUp_RunsTheTitleSearchAsBefore()
    {
        _catalog.Providers = [OpenLibraryWithNoIsbnLookup()];

        await SearchBook(Book(), "openlibrary", new Dictionary<string, string> { ["title"] = "Dune", ["isbn"] = "978-0-441-17271-9" });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal("Dune", asked.QueryOverride);
        Assert.Null(asked.LookupFields);
    }

    [Fact]
    public async Task ATitleWithAnIsbn_ForAProviderThatCanLookItUp_SearchesTheTitleAndSendsTheCleanedIsbn()
    {
        _catalog.Providers = [AppleBooks()];

        await SearchBook(Book(), "apple_api", new Dictionary<string, string> { ["title"] = "Dune", ["isbn"] = "978-0-441-17271-9" });

        var asked = Assert.Single(_search.Requests);
        Assert.Equal("Dune", asked.QueryOverride);
        Assert.Equal("9780441172719", asked.LookupFields!["isbn"]);
    }

    [Fact]
    public async Task AProviderListedForOtherKindsOnly_IsNotOfferedForThisOne()
    {
        _catalog.Providers =
        [
            new RetailProviderInfo("tvdb", "TheTVDB", true, false, [EditorKinds.TvShow], 1, [EditorKinds.TvShow]),
            new RetailProviderInfo("tmdb", "TMDB", true, false, [EditorKinds.Movie], 2, [EditorKinds.Movie]),
        ];
        var movie = Movie("Arrival.2016.mkv");

        var setup = await Service().SetupForKindAsync(movie, EditorKinds.Movie);

        Assert.Equal(["tmdb"], setup!.Providers.Select(provider => provider.Name).ToArray());
    }

    [Fact]
    public async Task AnItemTheLibraryDoesNotHave_IsNotFound()
    {
        Assert.Null(await Service().SetupForKindAsync(Guid.NewGuid(), EditorKinds.Movie));
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
        Provider = "tmdb",
        Fields = new Dictionary<string, string> { ["title"] = title, ["year"] = year },
    };

    // ── Judging a pick made in the TV or music flow ──────────────────────────

    private static EditorMatchLocalRead EpisodeRead(string? episodeTitle, int number, double? measuredSeconds, int? season = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), "TV", "/tv/The Last Signal/Season 02/The.Last.Signal.S02E03.mkv", "The.Last.Signal.S02E03.mkv",
        new EditorMatchLocalEvidenceDto { FileTitle = "The Last Signal", EpisodeTitle = episodeTitle, Number = number, Season = season, MeasuredSeconds = measuredSeconds, MeasuredText = "45:52" },
        new Dictionary<string, (string Value, string From)>());

    [Fact]
    public void AnEpisodePick_IsJudgedByTheEpisodeTitleTheFileNameCarries_NotTheShowName()
    {
        var read = EpisodeRead("Echoes in the Dark", 3, 2752);
        var right = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", null, null, 46);
        var wrong = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e24", 2, 4, "Dead Air", "2021-01-25", null, null, 52);

        var good = Service().JudgeRead(EditorKinds.TvEpisode, right, read);
        var bad = Service().JudgeRead(EditorKinds.TvEpisode, wrong, read);

        Assert.Equal(EditorMatchVerdicts.Strong, good.Verdict);
        var runtime = Assert.Single(good.Reasons, reason => reason.Fact == "runtime");
        Assert.Equal(EditorMatchEvidenceStates.Agrees, runtime.State);
        Assert.Equal("45:52 measured · 46 min (rounded)", runtime.Note);
        Assert.Equal(EditorMatchVerdicts.Weak, bad.Verdict);
        Assert.Equal(right, good.Candidate);
    }

    [Fact]
    public void AnEpisodePickWithADifferentLength_IsFlaggedOnTheRuntimeRow_AndKeepsItsCandidateToApply()
    {
        var read = EpisodeRead("Echoes in the Dark", 3, 2752);
        var pick = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", null, null, 90);

        var judged = Service().JudgeRead(EditorKinds.TvEpisode, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.Differs, Assert.Single(judged.Reasons, reason => reason.Fact == "runtime").State);
        Assert.Equal("e23", judged.Candidate.ProviderItemId);
    }

    [Fact]
    public void AnEpisodePick_ComparesShowSeasonAndEpisodeTitleAndRuntime_InThatOrder_WithWhatEachSideSays()
    {
        var read = EpisodeRead("Echoes in the Dark", 3, 2752, season: 2);
        var pick = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", null, null, 46);

        var judged = Service().JudgeRead(EditorKinds.TvEpisode, pick, read);

        Assert.Equal(["show", "number", "title", "runtime"], judged.Reasons.Select(reason => reason.Fact));
        Assert.All(judged.Reasons, reason => Assert.Equal(EditorMatchEvidenceStates.Agrees, reason.State));
        var slot = judged.Reasons[1];
        Assert.Equal("Season 2, Episode 3", slot.YourFile);
        Assert.Equal("Season 2, Episode 3", slot.Result);
        Assert.Equal("The Last Signal", judged.Reasons[0].Result);
    }

    [Fact]
    public void AnEpisodePick_OfAnotherSeason_DisagreesOnTheSeasonAndEpisodeRow_AndReadsWeak()
    {
        var read = EpisodeRead("Echoes in the Dark", 3, 2752, season: 2);
        var pick = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e33", 3, 3, "Echoes in the Dark", "2022-01-18", null, null, 46);

        var judged = Service().JudgeRead(EditorKinds.TvEpisode, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.Differs, Assert.Single(judged.Reasons, reason => reason.Fact == "number").State);
        Assert.Equal(EditorMatchVerdicts.Weak, judged.Verdict);
    }

    [Fact]
    public void AnEpisodePick_WhoseFileHasNoSeasonAndNoLength_LeavesThoseRowsUnknown_AndStillReadsStrong()
    {
        var read = EpisodeRead("Echoes in the Dark", 3, null);
        var pick = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", null, null, 46);

        var judged = Service().JudgeRead(EditorKinds.TvEpisode, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, Assert.Single(judged.Reasons, reason => reason.Fact == "runtime").State);
        Assert.Equal("Episode 3", Assert.Single(judged.Reasons, reason => reason.Fact == "number").YourFile);
        Assert.Equal(EditorMatchVerdicts.Strong, judged.Verdict);
    }

    private static EditorMatchLocalRead SongRead(string title, string artist, string album, double? measuredSeconds) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Music", "/music/Harbor Lights/Shoreline/04 - Low Tide.flac", "04 - Low Tide.flac",
        new EditorMatchLocalEvidenceDto { FileTitle = title, Creator = artist, Number = 4, MeasuredSeconds = measuredSeconds },
        new Dictionary<string, (string Value, string From)>
        {
            [EditorMatchFieldIds.Artist] = (artist, EditorMatchFieldSources.Tags),
            [EditorMatchFieldIds.Album] = (album, EditorMatchFieldSources.Tags),
            [EditorMatchFieldIds.Song] = (title, EditorMatchFieldSources.Tags),
        });

    [Fact]
    public void ASongPick_ComparesArtistAlbumTitleAndLength_InThatOrder_AndAgreesWhenTheyDo()
    {
        var read = SongRead("Low Tide", "Harbor Lights", "Shoreline", 221);
        var pick = EditorMatchCandidates.ForMusicTrack("rel-1", "rg-1", "Shoreline", "Harbor Lights", "2018", true, "rec-1", "Low Tide", "Harbor Lights", 1, 4, 222, null);

        var judged = Service().JudgeRead(EditorKinds.Song, pick, read);

        Assert.Equal(["creator", "album", "title", "track_length", "identifier"], judged.Reasons.Select(reason => reason.Fact));
        Assert.All(judged.Reasons.Take(4), reason => Assert.Equal(EditorMatchEvidenceStates.Agrees, reason.State));
        Assert.Equal("Shoreline", judged.Reasons[1].YourFile);
        Assert.Equal("Shoreline", judged.Reasons[1].Result);
        Assert.Equal(EditorMatchVerdicts.Strong, judged.Verdict);
    }

    [Fact]
    public void ASongPick_OnAnotherAlbum_DisagreesOnTheAlbumRow_AndReadsPossible_NotWeak()
    {
        var read = SongRead("Low Tide", "Harbor Lights", "Shoreline", 221);
        var pick = EditorMatchCandidates.ForMusicTrack("rel-2", "rg-2", "Greatest Hits", "Harbor Lights", "2019", true, "rec-1", "Low Tide", "Harbor Lights", 1, 4, 222, null);

        var judged = Service().JudgeRead(EditorKinds.Song, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.Differs, Assert.Single(judged.Reasons, reason => reason.Fact == "album").State);
        Assert.Equal(EditorMatchVerdicts.Possible, judged.Verdict);
    }

    [Fact]
    public void ASongPick_WhoseLengthDiffersByMoreThanThreeSeconds_IsFlaggedOnTheLengthRow_AndReadsPossible()
    {
        var read = SongRead("Low Tide", "Harbor Lights", "Shoreline", 221);
        var pick = EditorMatchCandidates.ForMusicTrack("rel-1", "rg-1", "Shoreline", "Harbor Lights", "2018", true, "rec-1", "Low Tide", "Harbor Lights", 1, 4, 227, null);

        var judged = Service().JudgeRead(EditorKinds.Song, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.Differs, Assert.Single(judged.Reasons, reason => reason.Fact == "track_length").State);
        Assert.Equal(EditorMatchVerdicts.Possible, judged.Verdict);
    }

    [Fact]
    public void ASongPick_WhenTheFileHasNoAlbumOrLength_LeavesThoseRowsUnknown_AndNeverCountsThemAgainstIt()
    {
        var read = new EditorMatchLocalRead(
            Guid.NewGuid(), Guid.NewGuid(), "Music", "/music/04 - Low Tide.flac", "04 - Low Tide.flac",
            new EditorMatchLocalEvidenceDto { FileTitle = "Low Tide", Creator = "Harbor Lights" },
            new Dictionary<string, (string Value, string From)>());
        var pick = EditorMatchCandidates.ForMusicTrack("rel-1", "rg-1", "Shoreline", "Harbor Lights", "2018", true, "rec-1", "Low Tide", "Harbor Lights", 1, 4, 222, null);

        var judged = Service().JudgeRead(EditorKinds.Song, pick, read);

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, Assert.Single(judged.Reasons, reason => reason.Fact == "album").State);
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, Assert.Single(judged.Reasons, reason => reason.Fact == "track_length").State);
        Assert.Equal(EditorMatchVerdicts.Strong, judged.Verdict);
    }

    private EditorMatchSearchService Service(IEditorMatchCandidateSearch? candidates = null, IVideoIdentifyWriter? videos = null)
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
            candidates ?? _search,
            new EditorConfigurationProvider(() => null),
            new EditorMatchVerdictCalculator(new MatchVerdictSettings()),
            new NoKindResolver(),
            videos ?? new VideoWriterSpy());
    }

    private sealed class NoKindResolver : IEditorMatchKindResolver
    {
        public Task<string?> ResolveAsync(Guid entityId, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class VideoWriterSpy : IVideoIdentifyWriter
    {
        public List<string?> Pasted { get; } = [];

        public VideoIdentifyOutcome Answer { get; set; } = new(VideoIdentifyStatus.NotFound, "No video.");

        public Task<VideoIdentifyOutcome> LookupAsync(Guid entityId, string? pasted, CancellationToken ct = default)
        {
            Pasted.Add(pasted);
            return Task.FromResult(Answer);
        }

        public Task<VideoIdentifyOutcome> ApplyAsync(Guid entityId, string? pasted, CancellationToken ct = default) => LookupAsync(entityId, pasted, ct);
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
