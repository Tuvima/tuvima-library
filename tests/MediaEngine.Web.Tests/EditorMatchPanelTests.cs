using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tool (universal editor spec D5, D6, D11, WP7): step 1 finds a record, step 2 reviews it beside the item with four switches
/// (parent and files off by default, files greyed with the reason when the folder cannot be touched), and "Use this match" only stages.
/// </summary>
public sealed class EditorMatchPanelTests : AsyncBunitContext
{
    private static readonly Guid SongId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AlbumId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<ItemCanonicalSearchRequestDto> _searches = [];
    private readonly List<string> _writes = [];
    private TaskCompletionSource<ItemCanonicalSearchResponseDto?>? _gate;
    private Func<ItemCanonicalSearchResponseDto?> _answer;
    private LibraryMoveDestinationsResponse _places;
    private LibraryMovePreviewRow _fileRow = new() { ItemId = SongId, FileChange = new LibraryMoveFileChange { From = "a", To = "b" } };

    public EditorMatchPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(SongId, "Karma Police", "Item")]);
        _answer = () => Response(Candidate("Karma Police", "OK Computer", "Radiohead", "musicbrainz"), Candidate("Karma Police (Live)", "Live", "Radiohead", "apple_api"));
        _places = new LibraryMoveDestinationsResponse
        {
            Destinations = [new LibraryMoveDestination { Id = AlbumId, Kind = LibraryMoveDestinationKinds.Album, Label = "OK Computer", ParentLabel = "Radiohead", HasList = true }],
        };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.SearchItemCanonicalAsync), args =>
            {
                _searches.Add((ItemCanonicalSearchRequestDto)args![1]!);
                return _gate is null ? Task.FromResult(_answer()) : _gate.Task;
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryMoveDestinationsAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Ok(_places)));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryMoveAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMovePreview>.Ok(new LibraryMovePreview { Rows = [_fileRow] })));
            stub.SetHandler(nameof(IEngineApiClient.ReplaceRetailMatchAsync), _ =>
            {
                _writes.Add("retail");
                return Task.FromResult<ItemCanonicalApplyResponseDto?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), _ =>
            {
                _writes.Add("session");
                return Task.FromResult<LibraryBatchEditOutcome<EditSessionResult>>(default!);
            });
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSubject Song(bool overridden = false) => new(
        SongId, "Music", "track", "track", "Karma Police", "Radiohead", "Greatest Hits", "1997",
        new Dictionary<string, string> { ["title"] = "Karma Police", ["artist"] = "Radiohead", ["album"] = "Greatest Hits" },
        overridden ? ["title"] : []);

    private static ItemCanonicalRetailCandidateDto Candidate(string title, string album, string artist, string provider) => new()
    {
        CandidateId = $"{provider}:{title}",
        ProviderId = provider,
        ProviderName = provider,
        ProviderItemId = $"{provider}-{title}",
        Title = title,
        Year = "1997",
        CoverUrl = "https://example.test/cover.jpg",
        CompositeScore = title.Contains("Live", StringComparison.Ordinal) ? 0.4 : 0.9,
        ReasonText = title.Contains("Live", StringComparison.Ordinal) ? "Title matches; album differs" : "Title and artist match",
        RequiredFields = new Dictionary<string, string> { ["album"] = album, ["artist"] = artist },
    };

    private static ItemCanonicalSearchResponseDto Response(params ItemCanonicalRetailCandidateDto[] candidates) => new()
    {
        RetailCandidates = [.. candidates],
        Providers =
        [
            new ItemCanonicalSearchProviderDto { Name = "musicbrainz", DisplayName = "MusicBrainz" },
            new ItemCanonicalSearchProviderDto { Name = "apple_api", DisplayName = "Apple Music" },
        ],
    };

    private IRenderedComponent<EditorMatchPanel> RenderPanel(EditorMatchSubject? subject = null, bool canEdit = true) =>
        Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, subject ?? Song())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, canEdit));

    private static void Select(IRenderedComponent<EditorMatchPanel> cut, int index) =>
        cut.FindAll("button").Where(button => button.TextContent.Trim() == "Select").ElementAt(index).Click();

    private static bool SwitchOn(IRenderedComponent<EditorMatchPanel> cut, string label) =>
        cut.Find($"input[role=switch][aria-label='{label}']").HasAttribute("checked");

    [Fact]
    public void ChoosingAResult_MovesFocusToTheReview_AndChooseAnother_PutsItOnThatResultsSelectButton()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-match__row").Count));
        Assert.Equal("tl-match-select-1", cut.FindAll("button").Where(button => button.TextContent.Trim() == "Select").ElementAt(1).Id);

        Select(cut, 1);
        cut.WaitForAssertion(() => Assert.Equal("tl-match-review", LastFocusedId()));
        Assert.Equal("group", cut.Find("#tl-match-review").GetAttribute("role"));
        Assert.Equal("Review selected match", cut.Find("#tl-match-review").GetAttribute("aria-label"));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Choose another").Click();
        cut.WaitForAssertion(() => Assert.Equal("tl-match-select-1", LastFocusedId()));
    }

    private string? LastFocusedId() => JSInterop.Invocations
        .Where(call => call.Identifier == "tuvimaFocusById")
        .Select(call => call.Arguments[0] as string)
        .LastOrDefault();

    [Fact]
    public void StepOne_SearchesFromTheItem_AndTagsOnlyTheTopResultAsABestGuess()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-match__row").Count));
        Assert.Equal("Karma Police", cut.Find("input[aria-label='Search for a match']").GetAttribute("value"));
        Assert.Equal("track", Assert.Single(_searches).TargetFieldGroup);
        Assert.Null(_searches[0].QueryOverride);
        Assert.Single(cut.FindAll(".tl-match__best"));
        Assert.Contains("Best guess", cut.FindAll(".tl-match__row")[0].TextContent, StringComparison.Ordinal);
        // The Engine's confidence is a bar with its percentage; the reason sits in a small (i) beside it.
        var confidence = cut.FindAll("[data-testid=match-confidence]");
        Assert.Equal(2, confidence.Count);
        Assert.Contains("90%", confidence[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("40%", confidence[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Title and artist match", confidence[0].QuerySelector(".app-info-tip")!.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("OK Computer", cut.FindAll(".tl-match__row")[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Radiohead", cut.FindAll(".tl-match__row")[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASong_OffersTracksAndAlbums_AndAlbumsSearchesForAlbums()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Albums").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
        Assert.Equal("album", _searches[1].TargetFieldGroup);
        Assert.Equal("container", _searches[1].TargetKind);
    }

    [Fact]
    public void ASearchInOneProvider_AsksOnlyThatProvider()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        var picker = cut.FindComponents<AppSelect>().Single(select => select.Instance.AriaLabel == "Search in");
        cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("musicbrainz"));

        cut.WaitForAssertion(() => Assert.Equal("musicbrainz", _searches[^1].ProviderName));
    }

    [Fact]
    public void StepTwo_ShowsTheItemBesideTheMatch_WithTheParentAndFileSwitchesOff()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        Select(cut, 0);

        cut.WaitForAssertion(() => Assert.Contains("Review selected match", cut.Find(".tl-match").InnerHtml, StringComparison.Ordinal));
        // The highlighted row carries the before-and-after line: your file's deciding facts, then the candidate's.
        var active = Assert.Single(cut.FindAll(".tl-match__row.is-active"));
        var line = active.QuerySelector("[data-testid=match-before-after]")!;
        Assert.Contains("Karma Police", line.QuerySelector("[aria-label='Your file']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Greatest Hits", line.QuerySelector("[aria-label='Your file']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Karma Police", line.QuerySelector("[aria-label='Will become']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("OK Computer", line.QuerySelector("[aria-label='Will become']")!.TextContent, StringComparison.Ordinal);
        // A fact that agrees (the title) is ticked; one that differs (the album) is not.
        Assert.Single(line.QuerySelectorAll(".tl-match__ba-tick"));
        Assert.Equal(4, cut.FindAll("input[role=switch]").Count);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));
        Assert.True(SwitchOn(cut, "Keep my library details"));
        Assert.True(SwitchOn(cut, "Use as the retail match"));
        Assert.False(SwitchOn(cut, "Move to OK Computer"));
        Assert.Empty(cut.FindAll("input[role=switch][aria-label='Rename and move the files']"));
    }

    [Fact]
    public void TheFileNote_SaysTheFileStays_WhenTheFolderIsProtected()
    {
        _fileRow = new LibraryMovePreviewRow { ItemId = SongId, FileNote = "Read-only folder: the file stays where it is." };
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        Select(cut, 0);

        cut.WaitForAssertion(() => Assert.Contains("Read-only folder: the file stays where it is.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("This file will move when you save", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheParentSwitch_IsGreyedWithAReason_WhenTheLibraryHasNoSuchAlbum()
    {
        _places = new LibraryMoveDestinationsResponse();
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        Select(cut, 0);

        cut.WaitForAssertion(() => Assert.Contains("nowhere to move it", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("Music", "album", "album", "OK Computer")]
    [InlineData("TV", "season", "season", "Season 1")]
    public void AnAlbumOrSeasonPage_SearchesFromItself_AndStagesTheChosenMatch(string mediaType, string scope, string group, string title)
    {
        var subject = new EditorMatchSubject(
            SongId, mediaType, scope, group, title, null, null, "1997",
            new Dictionary<string, string> { ["title"] = title }, []);
        var cut = RenderPanel(subject);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        Assert.Equal(title, cut.Find("input[aria-label='Search for a match']").GetAttribute("value"));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#tl-match-review")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal(group, match!.TargetFieldGroup);
        Assert.Empty(_writes);
    }

    [Fact]
    public void TurningOffTheWikidataCheck_IsCarriedIntoTheStagedMatch()
    {
        var subject = new EditorMatchSubject(
            SongId, "Music", "album", "album", "OK Computer", null, null, "1997",
            new Dictionary<string, string> { ["title"] = "OK Computer" }, []);
        var cut = RenderPanel(subject);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#tl-match-review")));

        cut.Find("input[role=switch][aria-label='Check the Wikidata link again']").Change(false);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.False(match!.ClearAutoAlignedWikidata);
    }

    [Fact]
    public void UseThisMatch_StagesTheMatchOnly_AndWritesNothing()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.Equal(EditSessionChangeKinds.Identity, change.Kind);
        Assert.Equal(SongId, change.EntityId);
        Assert.Equal([EditSessionChangeKinds.Identity, EditSessionChangeKinds.FileMove], _session.Pending.Changes.Select(c => c.Kind).ToArray());
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("musicbrainz", match!.ProviderName);
        Assert.Empty(_writes);
        Assert.Contains("Nothing is saved until you press Save changes", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UseThisMatch_StagesTheFileMoveAutomatically_WhenTheFolderIsManaged_AndWarnsFirst()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));

        cut.Find("input[role=switch][aria-label='Move to OK Computer']").Change(true);
        Assert.Contains("This file will move when you save", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        Assert.Equal(
            [EditSessionChangeKinds.Identity, EditSessionChangeKinds.Relationship, EditSessionChangeKinds.FileMove],
            _session.Pending.Changes.Select(change => change.Kind).ToArray());
        Assert.Equal(AlbumId.ToString("D"), _session.Pending.Changes.Single(change => change.Kind == EditSessionChangeKinds.Relationship).TargetId);
    }

    [Fact]
    public void TurningTheRetailSwitchOff_TakesTheStagedMatchBackOut()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();
        Assert.Contains(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);

        cut.Find("input[role=switch][aria-label='Use as the retail match']").Change(false);
        cut.Find("input[role=switch][aria-label='Keep my library details']");
        Assert.True(cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenDetailsAreNotKept_TheOverriddenDetailsGoBackToTheProvidersValue()
    {
        var cut = RenderPanel(Song(overridden: true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);

        cut.Find("input[role=switch][aria-label='Keep my library details']").Change(false);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        Assert.True(_session.Pending.TryGetField(SongId, "title", out var value));
        Assert.True(string.IsNullOrEmpty(value));
    }

    [Fact]
    public void WhenAProviderDoesNotAnswer_ThePanelSaysSo_AndOffersAnotherProvider()
    {
        _answer = () => null;
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("did not get an answer", cut.Find(".tl-match__problem").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".tl-match__row"));
    }

    [Fact]
    public void WhenNothingMatches_ThePanelSaysSo_AndOffersAnotherProvider()
    {
        _answer = () => Response();
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("Nothing matches that.", cut.Find(".tl-match__problem").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[data-testid=match-manual]"));
        var again = cut.FindAll("button").First(button => button.TextContent.Trim() == "Try another provider");
        again.Click();

        cut.WaitForAssertion(() => Assert.Equal("musicbrainz", _searches[^1].ProviderName));
    }

    [Fact]
    public void WhileSearching_ThePanelShowsPlaceholderRows()
    {
        var gate = _gate = new TaskCompletionSource<ItemCanonicalSearchResponseDto?>();
        var cut = RenderPanel();

        Assert.Equal(4, cut.FindAll(".tl-match__row--skeleton").Count);
        Assert.Equal("true", cut.Find(".tl-match__results").GetAttribute("aria-busy"));
        gate.SetResult(Response(Candidate("Karma Police", "OK Computer", "Radiohead", "musicbrainz")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-match__row--skeleton")));
    }

    [Fact]
    public void WithoutPermissionToEdit_UseThisMatchStaysOff()
    {
        var cut = RenderPanel(canEdit: false);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);

        Assert.True(cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").HasAttribute("disabled"));
    }

    [Fact]
    public void TheTabShowsWhatTheItemIsMatchedToNow_OrThatItIsNotMatched()
    {
        var matched = RenderPanel();
        Assert.Contains("Karma Police", matched.Find("[data-testid=match-now]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Greatest Hits", matched.Find("[data-testid=match-now]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Matched", matched.Find("[data-testid=match-now]").TextContent, StringComparison.Ordinal);

        var unmatched = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Song())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.IsUnmatched, true));
        Assert.Contains("Not matched", unmatched.Find("[data-testid=match-now]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUniverse_IsOnlyShownWhenTheItemHasOne_AndOpenUniverseHandsItUp()
    {
        Assert.Empty(RenderPanel().FindAll("[data-testid=match-universe]"));

        EditorUniverseLink? opened = null;
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Song())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.Universe, new EditorUniverseLink("Q1234", "Radiohead Universe"))
            .Add(panel => panel.OnOpenUniverse, EventCallback.Factory.Create<EditorUniverseLink>(this, link => opened = link)));

        Assert.Contains("Radiohead Universe", cut.Find("[data-testid=match-universe]").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open Universe").Click();

        Assert.Equal("Q1234", opened!.Qid);
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void Placement_ShowsWhereTheItemSits_AndOffersChangePlacement()
    {
        var cut = RenderPanel();

        Assert.Equal("Greatest Hits", cut.Find("[data-testid=placement-current]").TextContent.Trim());
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Change placement");
    }

    [Fact]
    public void SearchBy_OffersOnlyTheOptionsThatApplyToTheKindOfItem()
    {
        var song = EditorMatchFacts.SearchOptionsFor("Music", "track").Select(option => option.Key).ToArray();
        var episode = EditorMatchFacts.SearchOptionsFor("TV", "episode").Select(option => option.Key).ToArray();
        var film = EditorMatchFacts.SearchOptionsFor("Movies", "movie").Select(option => option.Key).ToArray();

        Assert.DoesNotContain(EditorMatchFacts.SearchYear, song);
        Assert.Contains(EditorMatchFacts.SearchProviderId, song);
        Assert.Contains(EditorMatchFacts.SearchEpisodeWords, episode);
        Assert.Contains(EditorMatchFacts.SearchEpisodeNumber, episode);
        Assert.DoesNotContain(EditorMatchFacts.SearchYear, episode);
        Assert.Contains(EditorMatchFacts.SearchYear, film);
        Assert.DoesNotContain(EditorMatchFacts.SearchEpisodeWords, film);
    }

    [Fact]
    public void SearchByYear_NarrowsWithTheYear_WithoutChangingTheTitleQuery()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        var by = cut.FindComponents<AppSelect>().Single(select => select.Instance.AriaLabel == "Search by");
        cut.InvokeAsync(() => by.Instance.ValueChanged.InvokeAsync(EditorMatchFacts.SearchYear));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Year']")));
        cut.Find("input[aria-label='Year']").Input("1998");
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Search").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
        Assert.Equal("1998", _searches[^1].DraftFields["year"]);
        Assert.Null(_searches[^1].QueryOverride);
    }

    [Fact]
    public void SearchByProviderId_SearchesForThePastedId()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        var by = cut.FindComponents<AppSelect>().Single(select => select.Instance.AriaLabel == "Search by");
        cut.InvokeAsync(() => by.Instance.ValueChanged.InvokeAsync(EditorMatchFacts.SearchProviderId));
        cut.Find("input[aria-label='Search for a match']").Input("https://musicbrainz.org/recording/6f5f3f29-4a2b-4a53-8a8d-3d5a8e5b0c11");
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Search").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
        Assert.Equal("6f5f3f29-4a2b-4a53-8a8d-3d5a8e5b0c11", _searches[^1].QueryOverride);
    }

    [Fact]
    public void WhenAProviderDoesNotAnswer_TheMessageNamesTheProvider()
    {
        _answer = () =>
        {
            var response = Response();
            response.ProviderErrors = ["musicbrainz search could not complete. Please retry."];
            return response;
        };
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("MusicBrainz did not answer", cut.Find(".tl-match__problem").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenNothingMatches_TheManualDetailsCanBeStaged_AsTheItemsOwn()
    {
        _answer = () => Response();
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-manual]")));

        cut.Find("input[aria-label='Title']").Input("Karma Police (demo)");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Use these details").Click();

        Assert.True(_session.Pending.TryGetField(SongId, "title", out var title));
        Assert.Equal("Karma Police (demo)", title);
        Assert.DoesNotContain(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.Identity);
    }

    [Fact]
    public void TheFacts_ForAnEpisode_AreNumberTitleAiredAndRuntime_AndAgreeingOnesAreTicked()
    {
        var subject = new EditorMatchSubject(
            SongId, "TV", "episode", "show_episode", "S02E04.mkv", null, "The Expanse", null,
            new Dictionary<string, string> { ["season_number"] = "2", ["episode_number"] = "4", ["runtime"] = "44", ["title"] = "S02E04.mkv" }, []);
        var candidate = new ItemCanonicalRetailCandidateDto
        {
            Title = "Godspeed",
            RequiredFields = new Dictionary<string, string> { ["season_number"] = "2", ["episode_number"] = "4", ["runtime"] = "44" },
            ExtraFields = new Dictionary<string, string> { ["air_date"] = "2017-02-15" },
        };

        var facts = EditorMatchFacts.DecidingFacts(subject, candidate);

        Assert.Equal(["Episode", "Title", "Aired", "Runtime"], facts.Select(fact => fact.Label).ToArray());
        Assert.True(facts.Single(fact => fact.Label == "Episode").Agrees);
        Assert.Equal("S02E04", facts.Single(fact => fact.Label == "Episode").After);
        Assert.True(facts.Single(fact => fact.Label == "Runtime").Agrees);
        Assert.False(facts.Single(fact => fact.Label == "Title").Agrees);
    }

    [Fact]
    public void AProviderLinkOrId_IsReadForTheProvidersThatHaveThem()
    {
        Assert.Equal(new EditorProviderReference("tvdb", "354627"), EditorMatchFacts.ParseProviderReference("https://thetvdb.com/series/354627"));
        Assert.Equal(new EditorProviderReference("tvdb", "354627"), EditorMatchFacts.ParseProviderReference("354627"));
        Assert.Equal(new EditorProviderReference("imdb", "tt3230854"), EditorMatchFacts.ParseProviderReference("https://www.imdb.com/title/tt3230854/"));
        Assert.Equal(new EditorProviderReference("isbn", "9780441172719"), EditorMatchFacts.ParseProviderReference("978-0-441-17271-9"));
        Assert.Null(EditorMatchFacts.ParseProviderReference("not an id"));
    }

    [Fact]
    public void ATmdbLink_IsReadAsTmdb_AndABareNumberFollowsTheKindOfItem()
    {
        Assert.Equal(new EditorProviderReference("tmdb", "603"), EditorMatchFacts.ParseProviderReference("https://www.themoviedb.org/movie/603-the-matrix", "Movies"));
        Assert.Equal(new EditorProviderReference("tmdb", "1399"), EditorMatchFacts.ParseProviderReference("https://www.themoviedb.org/tv/1399-game-of-thrones", "TV"));
        Assert.Equal(new EditorProviderReference("tmdb", "603"), EditorMatchFacts.ParseProviderReference("603", "Movies"));
        Assert.Equal(new EditorProviderReference("tvdb", "354627"), EditorMatchFacts.ParseProviderReference("354627", "TV"));
        Assert.Null(EditorMatchFacts.ParseProviderReference("603", "Music"));
        Assert.Null(EditorMatchFacts.ParseProviderReference("603", "Books"));
        Assert.Contains("TMDB", EditorMatchFacts.AcceptedProviderForms("Movies"), StringComparison.Ordinal);
        Assert.Contains("TheTVDB", EditorMatchFacts.AcceptedProviderForms("TV"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Godspeed", true)]
    [InlineData("good god", false)]
    [InlineData("4", true)]
    [InlineData("S02E04", true)]
    [InlineData("S01E04", false)]
    [InlineData("e4", true)]
    [InlineData("2017-02-15", true)]
    [InlineData("", true)]
    public void AnEpisodeFilter_MatchesTitleWordsNumbersAndAirDates(string filter, bool expected) =>
        Assert.Equal(expected, EditorMatchFacts.EpisodeMatchesFilter(2, 4, "Godspeed", "2017-02-15", filter));

    [Fact]
    public void TheRules_GiveASongTracksAndAlbums_AndEverythingElseOneKind()
    {
        Assert.Equal(["track", "album"], EditorMatchRules.TabsFor("Music", "track").Select(tab => tab.Group).ToArray());
        Assert.Single(EditorMatchRules.TabsFor("TV", "episode"));
        Assert.Single(EditorMatchRules.TabsFor("Music", "album"));
    }

    [Fact]
    public void TheRules_NameTheSeasonAnEpisodeWouldMoveInto()
    {
        var candidate = new ItemCanonicalRetailCandidateDto
        {
            RequiredFields = new Dictionary<string, string> { ["show_name"] = "Le Continental", ["season_number"] = "1" },
        };

        var proposed = EditorMatchRules.ProposedParentOf(candidate, "TV");

        Assert.Equal(new ProposedParent("Season 1", "Le Continental"), proposed);
        Assert.True(EditorMatchRules.IsSamePlace(proposed!, "Season 1", "Le Continental"));
        Assert.False(EditorMatchRules.IsSamePlace(proposed!, "Season 1", "Another Show"));
    }
}
