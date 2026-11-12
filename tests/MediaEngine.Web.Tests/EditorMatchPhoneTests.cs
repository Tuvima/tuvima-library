using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab on a phone (universal editor v6, WP16, section 4a; D20 and D21): the same steps as on a computer, drawn for touch. The search row is
/// stacked; a tapped row opens its card as a full-height sheet with Apply match fixed at the bottom; Back returns to the list with the tick kept; TV
/// and music are one step per screen (show, then season and episode; release, then track); a book, an episode and a song can each be matched by
/// touch alone. Apply match only stages; nothing is saved here. A computer (no phone layout around the panel) is drawn exactly as before.
/// </summary>
public sealed class EditorMatchPhoneTests : AsyncBunitContext
{
    private static readonly Guid ItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private const string ReleaseA = "11111111-1111-1111-1111-111111111111";
    private const string ReleaseB = "22222222-2222-2222-2222-222222222222";

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<EditorMatchSearchRequestDto> _searches = [];
    private EditorMatchSetupDto _setup = BookSetup();
    private EditorMatchSearchResultDto? _answer;
    private EditorMatchContextDto _context = new() { EntityId = ItemId, ParsedShow = "The Last Signal", ParsedSeason = 2, ParsedEpisode = 3 };
    private EditorWikidataSearchResultDto _wikidata = new() { EntityId = ItemId };
    private bool _matched;
    private readonly BunitJSModuleInterop _focusModule;

    public EditorMatchPhoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _focusModule = JSInterop.SetupModule("./js/editor-focus.js");
        _focusModule.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        _session.Begin([new EditTrailItem(ItemId, "The Sea of Monsters", "Item")]);
        _answer = Found(Book("The Sea of Monsters", "2009", EditorMatchVerdicts.Strong), Book("The Complete Series", "2026", EditorMatchVerdicts.Weak, collection: true));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ => Task.FromResult<EditorCurrentMatchDto?>(_matched ? CurrentMatch() : new EditorCurrentMatchDto { EntityId = ItemId }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), _ => Task.FromResult<EditorMatchSetupDto?>(_setup));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ => Task.FromResult<EditorMatchContextDto?>(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchPlacementAsync), _ => Task.FromResult<EditorMatchPlacementDto?>(null));
            stub.SetHandler(nameof(IEngineApiClient.SearchEditorMatchAsync), args =>
            {
                _searches.Add((EditorMatchSearchRequestDto)args![1]!);
                return Task.FromResult(_answer);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchVerdictAsync), args =>
            {
                var candidate = (ItemCanonicalRetailCandidateDto)args![1]!;
                return Task.FromResult<EditorMatchResultDto?>(new EditorMatchResultDto
                {
                    Candidate = candidate,
                    ProviderName = candidate.ProviderName,
                    Title = candidate.Title,
                    Verdict = EditorMatchVerdicts.Strong,
                    Reasons = [new EditorMatchReasonDto { Fact = "title", State = EditorMatchEvidenceStates.Agrees, YourFile = candidate.Title, Result = candidate.Title }],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewShowChildRematchAsync), _ => Task.FromResult<ShowChildRematchPreviewDto?>(null));
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), args =>
                Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto((string)args![1]!,
                [
                    new TvMatchBrowserShowDto("100", "The Last Signal", "2019", "A radio operator hears a message.", null, "https://example.test/100", false),
                    new TvMatchBrowserShowDto("200", "Signal Lost", "2015", null, null, "https://example.test/200", false),
                ])));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), args =>
                Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto((string)args![1]!, "The Last Signal",
                [
                    new TvMatchBrowserSeasonDto("s1", "100", 1, "Season 1", null, false),
                    new TvMatchBrowserSeasonDto("s2", "100", 2, "Season 2", null, false),
                ], "Drama", "A radio operator hears a message.", "2019", null)));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserEpisodesAsync), args =>
            {
                var season = (int)args![2]!;
                return Task.FromResult<TvMatchBrowserEpisodesDto?>(new TvMatchBrowserEpisodesDto("100", "The Last Signal", $"s{season}", season,
                [
                    Episode(season, 1, "Static"),
                    Episode(season, 2, "Carrier Wave"),
                    Episode(season, 3, "Echoes in the Dark"),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMusicReleasesAsync), _ => Task.FromResult<EditorMusicReleasesDto?>(new EditorMusicReleasesDto
            {
                EntityId = ItemId,
                Releases =
                [
                    new EditorMusicReleaseDto { ReleaseId = ReleaseA, Title = "Low Water", Artist = "The Tides", Year = "2011", Format = "CD", TrackCount = 11, IsCurrent = true },
                    new EditorMusicReleaseDto { ReleaseId = ReleaseB, Title = "Low Water (Deluxe)", Artist = "The Tides", Year = "2013", Format = "2 x CD", TrackCount = 24, IsSuggested = true },
                ],
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMusicReleaseTracksAsync), args => Task.FromResult<EditorMusicReleaseTracksDto?>(new EditorMusicReleaseTracksDto
            {
                EntityId = ItemId,
                Release = new EditorMusicReleaseDto { ReleaseId = (string)args![1]!, Title = "Low Water (Deluxe)", Artist = "The Tides", Year = "2013", TrackCount = 24 },
                Discs =
                [
                    new EditorMusicDiscDto
                    {
                        Number = 1,
                        Tracks =
                        [
                            new EditorMusicTrackDto { RecordingId = "rec-1", Disc = 1, Track = 1, Title = "Opening", LengthText = "3:00" },
                            new EditorMusicTrackDto { RecordingId = "rec-2", Disc = 1, Track = 2, Title = "Harbor Lights", Artist = "The Tides", LengthSeconds = 222, LengthText = "3:42", IsSuggested = true },
                            new EditorMusicTrackDto { RecordingId = "rec-3", Disc = 1, Track = 3, Title = "Low Tide", LengthText = "4:01" },
                        ],
                    },
                ],
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentWikidataAsync), _ => Task.FromResult<EditorWikidataIdentityDto?>(new EditorWikidataIdentityDto { EntityId = ItemId }));
            stub.SetHandler(nameof(IEngineApiClient.SearchEditorWikidataAsync), _ => Task.FromResult<EditorWikidataSearchResultDto?>(_wikidata));
            stub.SetHandler(nameof(IEngineApiClient.CompareEditorWikidataAsync), _ => Task.FromResult<EditorWikidataConflictDto?>(new EditorWikidataConflictDto { Conflict = false }));
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSetupDto BookSetup() => new()
    {
        EntityId = ItemId,
        Kind = "book",
        FileName = "The Percy Jackson and the Olympians, Book - Rick Riordan.epub",
        Providers = [new EditorMatchProviderDto { Name = "apple_api", DisplayName = "Apple Books", State = EditorMatchProviderStates.Ready, IsDefault = true }],
        Fields =
        [
            new EditorMatchFieldDto { Key = "title", Label = "Title", Value = "The Sea of Monsters" },
            new EditorMatchFieldDto { Key = "author", Label = "Author", Value = "Rick Riordan" },
            new EditorMatchFieldDto { Key = "isbn", Label = "ISBN", Value = string.Empty },
        ],
    };

    private static EditorMatchSetupDto TvSetup(string kind) => new()
    {
        EntityId = ItemId,
        Kind = kind,
        FileName = "The.Last.Signal.S02E03.mkv",
        Providers = [new EditorMatchProviderDto { Name = "tvdb", DisplayName = "TheTVDB", State = EditorMatchProviderStates.Ready, IsDefault = true }],
        Fields = [new EditorMatchFieldDto { Key = "show", Label = "Show", Value = "The Last Signal" }],
        Evidence = new EditorMatchLocalEvidenceDto { MeasuredSeconds = 2752, MeasuredText = "45:52" },
    };

    private static EditorMatchSetupDto SongSetup() => new()
    {
        EntityId = ItemId,
        Kind = "song",
        FileName = "02 - Harbor Lights.flac",
        Providers = [new EditorMatchProviderDto { Name = "musicbrainz", DisplayName = "MusicBrainz", State = EditorMatchProviderStates.Ready, IsDefault = true }],
        Fields =
        [
            new EditorMatchFieldDto { Key = "artist", Label = "Artist", Value = "The Tides" },
            new EditorMatchFieldDto { Key = "album", Label = "Album", Value = "Low Water" },
            new EditorMatchFieldDto { Key = "song", Label = "Song", Value = "Harbor Lights" },
        ],
        Evidence = new EditorMatchLocalEvidenceDto { Number = 2, MeasuredSeconds = 222 },
    };

    private static EditorMatchResultDto Book(string title, string year, string verdict, bool collection = false, IReadOnlyList<EditorMatchResultDto>? editions = null) => new()
    {
        Candidate = new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"apple:{title}",
            ProviderId = "apple_api",
            ProviderName = "apple_api",
            ProviderItemId = $"id-{year}",
            Title = title,
            Year = year,
            RequiredFields = new Dictionary<string, string> { ["title"] = title },
        },
        ProviderName = "Apple Books",
        Title = title,
        Year = year,
        Creator = "Rick Riordan",
        IsCollection = collection,
        Description = "The second book in the saga. Percy's seventh-grade year has been surprisingly quiet, until a game of dodgeball turns into a death match.",
        Verdict = verdict,
        Reasons =
        [
            new EditorMatchReasonDto { Fact = "title", State = EditorMatchEvidenceStates.Agrees, YourFile = "The Sea of Monsters", Result = title },
            new EditorMatchReasonDto { Fact = "author", State = EditorMatchEvidenceStates.Agrees, YourFile = "Rick Riordan", Result = "Rick Riordan" },
        ],
        Editions = editions ?? [],
    };

    private static EditorMatchSearchResultDto Found(params EditorMatchResultDto[] results) => new() { EntityId = ItemId, Provider = "apple_api", Query = "The Sea of Monsters", Results = results };

    private static TvMatchBrowserEpisodeDto Episode(int season, int number, string title) =>
        new($"e{season}{number}", "100", "The Last Signal", $"s{season}", season, number, title, "2021-01-18", null, null, "https://example.test/e", false, false, null, 46);

    private static EditorMatchSubject Subject(string scope = "episode") => new(
        ItemId, scope is "episode" or "series" ? "TV" : scope == "track" ? "Music" : "Books", scope, scope == "episode" ? "show_episode" : scope == "series" ? "show" : scope == "track" ? "track" : "book", "The Sea of Monsters", null, "The Last Signal", "2009",
        new Dictionary<string, string> { ["title"] = "The Sea of Monsters" }, []);

    private IRenderedComponent<EditorMatchPanel> RenderPanel(EditorMatchSubject subject, bool phone = true) =>
        Render<EditorMatchPanel>(parameters =>
        {
            parameters
                .Add(panel => panel.Subject, subject)
                .Add(panel => panel.Session, _session)
                .Add(panel => panel.CanEdit, true)
                .Add(panel => panel.IsUnmatched, true);
            if (phone)
            {
                parameters.AddCascadingValue(EditorWindowLayout.PhoneSheet);
            }
        });

    private static void Tap(IRenderedComponent<EditorMatchPanel> cut, string testId, int index = 0) =>
        cut.FindAll($"[data-testid={testId}]")[index].Click();

    private static void ApplyInTheSheet(IRenderedComponent<EditorMatchPanel> cut) =>
        cut.Find("[data-testid=match-sheet] [data-testid=match-apply]").Click();

    private IRenderedComponent<EditorMatchPanel> SearchBook(bool phone = true)
    {
        _setup = BookSetup();
        var cut = RenderPanel(Subject("book"), phone);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=match-search-button]").HasAttribute("disabled")));
        cut.Find("[data-testid=match-search-button]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));
        return cut;
    }

    [Fact]
    public void ABook_CanBeMatchedByTouchAlone_SearchTapARowCardSheetApplyAndBackInTheTabWithThePickStaged()
    {
        var cut = SearchBook();

        // The list fills the width; the best result is ticked but its card waits for a tap.
        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));
        Assert.Equal("true", cut.FindAll("[data-testid=match-result]")[0].GetAttribute("aria-pressed"));
        Assert.Empty(_session.Pending.Changes);

        Tap(cut, "match-result", 1);

        var sheet = cut.WaitForElement("[data-testid=match-sheet]");
        Assert.Equal("The Complete Series", sheet.QuerySelector("[data-testid=match-card-title]")!.TextContent.Trim());
        Assert.NotNull(sheet.QuerySelector("[data-testid=match-verdict]"));
        Assert.NotNull(sheet.QuerySelector("[data-testid=match-compare]"));
        Assert.Equal("Selected result", sheet.QuerySelector("[data-testid=match-sheet-title]")!.TextContent.Trim());
        Assert.Empty(_session.Pending.Changes);

        ApplyInTheSheet(cut);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("id-2026", match!.ProviderItemId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
    }

    [Fact]
    public void AfterApplyOnAPhone_TheStagedNoteIsALiveRegionThatIsFocusable_AndIsBroughtIntoView()
    {
        var cut = SearchBook();
        Tap(cut, "match-result", 0);
        cut.WaitForElement("[data-testid=match-sheet]");
        Assert.DoesNotContain(_focusModule.Invocations, call => call.Identifier == "revealStagedNote");

        ApplyInTheSheet(cut);

        // The note is drawn, announced politely, can take focus, and the editor's script is asked to scroll it into view and focus it.
        var note = cut.WaitForElement("[data-testid=match-staged]");
        Assert.Equal("status", note.GetAttribute("role"));
        Assert.Equal("-1", note.GetAttribute("tabindex"));
        Assert.True(note.HasAttribute("data-staged-note"));
        cut.WaitForAssertion(() => _focusModule.VerifyInvoke("revealStagedNote"));
    }

    [Fact]
    public void AfterApplyOnAComputer_NothingIsScrolledOrFocused()
    {
        var cut = SearchBook(phone: false);
        Tap(cut, "match-result", 0);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=match-apply]").HasAttribute("disabled")));

        cut.Find("[data-testid=match-apply]").Click();

        cut.WaitForElement("[data-testid=match-staged]");
        Assert.DoesNotContain(_focusModule.Invocations, call => call.Identifier == "revealStagedNote");
    }

    [Fact]
    public void BackOnTheCardSheet_ReturnsToTheListWithTheTickKept_AndStagesNothing()
    {
        var cut = SearchBook();
        Tap(cut, "match-result", 1);
        cut.WaitForElement("[data-testid=match-sheet]");

        cut.Find("[data-testid=match-sheet-back]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        Assert.Equal("true", cut.FindAll("[data-testid=match-result]")[1].GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.FindAll("[data-testid=match-result]")[0].GetAttribute("aria-pressed"));
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void ARowTappedAgain_OpensItsSheetAgain_EvenWhenItIsAlreadyTheTickedOne()
    {
        var cut = SearchBook();

        Tap(cut, "match-result", 0);

        Assert.Contains("The Sea of Monsters", cut.WaitForElement("[data-testid=match-sheet] [data-testid=match-card-title]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAComputer_ARowShowsItsCardBesideTheList_AndNoSheetOpens()
    {
        var cut = SearchBook(phone: false);

        Tap(cut, "match-result", 1);

        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));
        cut.WaitForAssertion(() => Assert.Equal("The Complete Series", cut.Find("[data-testid=match-preview] [data-testid=match-card-title]").TextContent.Trim()));
        Assert.DoesNotContain("tl-mcard--phone", cut.Find("[data-testid=match-preview]").ClassList);
    }

    [Fact]
    public void TheEditionsOfAWork_SitInsideTheCardSheetOnAPhone_NotUnderTheList()
    {
        _answer = Found(
            Book("The Sea of Monsters", "2009", EditorMatchVerdicts.Strong, editions: [EditionOf("2009", "Hyperion"), EditionOf("2013", "Puffin")]),
            Book("The Complete Series", "2026", EditorMatchVerdicts.Weak));
        var cut = SearchBook();

        Assert.Empty(cut.FindAll("[data-testid=match-editions]"));
        Tap(cut, "match-result", 0);

        var inSheet = cut.WaitForElement("[data-testid=match-sheet] [data-testid=match-editions]");
        Assert.Equal(2, inSheet.QuerySelectorAll("button").Length);
        inSheet.QuerySelectorAll("button")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=match-sheet] [data-testid=match-editions]").QuerySelectorAll("button")[1].GetAttribute("aria-pressed")));
        ApplyInTheSheet(cut);
        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("edition-2013", match!.ProviderItemId);
    }

    private static EditorMatchResultDto EditionOf(string year, string publisher) => new()
    {
        Candidate = new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"apple:edition-{year}",
            ProviderId = "apple_api",
            ProviderName = "apple_api",
            ProviderItemId = $"edition-{year}",
            Title = "The Sea of Monsters",
            Year = year,
            RequiredFields = new Dictionary<string, string> { ["title"] = "The Sea of Monsters" },
        },
        ProviderName = "Apple Books",
        Title = "The Sea of Monsters",
        Year = year,
        Verdict = EditorMatchVerdicts.Strong,
        Edition = new EditorMatchEditionDto { Year = year, Publisher = publisher, Format = "EPUB" },
    };

    [Fact]
    public void TheSearchRow_IsStackedOnAPhone_OneFieldPerLine_WithSearchAtTheFullWidth()
    {
        var cut = SearchBook();

        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("app-form-row--stack", row.ClassList);
        Assert.DoesNotContain("app-form-row--wrap", row.ClassList);
        var button = cut.Find("[data-testid=match-search-button]");
        Assert.Contains("tl-full-width", button.ClassList);
        // No blank label line above Search when everything is one per line.
        Assert.Empty(row.QuerySelectorAll("[data-form-row-field=fit] > .app-field__label"));
    }

    [Fact]
    public void TheSearchRow_OnAComputer_StillWrapsAsWholeFields()
    {
        var cut = SearchBook(phone: false);

        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("app-form-row--wrap", row.ClassList);
        Assert.DoesNotContain("app-form-row--stack", row.ClassList);
        Assert.DoesNotContain("tl-full-width", cut.Find("[data-testid=match-search-button]").ClassList);
    }

    [Fact]
    public void ReadMore_WorksByTap_InTheCardSheet_AndNoButtonThereUsesTheCompactSize()
    {
        var cut = SearchBook();
        Tap(cut, "match-result", 0);
        var sheet = cut.WaitForElement("[data-testid=match-sheet]");

        var readMore = sheet.QuerySelector("[data-testid=match-card-read-more]")!;
        Assert.Equal("false", readMore.GetAttribute("aria-expanded"));
        readMore.Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=match-sheet] [data-testid=match-card-description]").GetAttribute("data-expanded")));
        // Only the size class can be checked here (the phone arrangement asks for the normal size); the 44 px height itself is proven by the headless layout measurement.
        foreach (var button in cut.Find("[data-testid=match-sheet]").QuerySelectorAll("button, a"))
        {
            Assert.DoesNotContain("app-control--compact", button.ClassList);
        }
    }

    [Fact]
    public void WhenTheEngineDoesNotAnswer_TheSearchShowsThePlainErrorAndTryAgain_AndTheStagedPickIsKept()
    {
        var cut = SearchBook();
        Tap(cut, "match-result", 0);
        cut.WaitForElement("[data-testid=match-sheet]");
        ApplyInTheSheet(cut);
        var staged = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);

        _answer = null;
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=match-search-button]").HasAttribute("disabled")));
        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-message]")));
        Assert.Contains("Try again", cut.Find("[data-testid=match-try-again]").TextContent, StringComparison.Ordinal);
        Assert.Equal(staged.Id, Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity).Id);

        _answer = Found(Book("The Sea of Monsters", "2009", EditorMatchVerdicts.Strong));
        cut.Find("[data-testid=match-try-again]").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=match-result]")));
    }

    [Fact]
    public void AnEpisode_CanBeMatchedByTouchAlone_ShowThenSeasonAndEpisodeThenTheCardSheet()
    {
        _setup = TvSetup("tv_episode");
        var cut = RenderPanel(Subject("episode"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=tv-flow-search]").HasAttribute("disabled")));

        // Screen one: the show. Season and episodes are not drawn yet.
        cut.Find("[data-testid=tv-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=tv-flow-show-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-episode-pick]"));
        Tap(cut, "tv-flow-show-row");

        // Screen two: the chosen show with a way back, the season and its episodes; the suggested episode is ticked, its card waits for a tap.
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=tv-flow-episode-pick]").Count));
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-show-row]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-change-show]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-season]"));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[data-testid=tv-flow-episode-pick]")[2].GetAttribute("aria-pressed")));
        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));

        Tap(cut, "tv-flow-episode-pick", 1);

        var sheet = cut.WaitForElement("[data-testid=match-sheet]");
        cut.WaitForAssertion(() => Assert.Equal("Carrier Wave", cut.Find("[data-testid=match-sheet] [data-testid=match-card-title]").TextContent.Trim()));
        Assert.NotNull(sheet.QuerySelector("[data-testid=match-apply]"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=match-sheet] [data-testid=match-apply]").HasAttribute("disabled")));

        ApplyInTheSheet(cut);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        Assert.Equal("true", cut.FindAll("[data-testid=tv-flow-episode-pick]")[1].GetAttribute("aria-pressed"));
        cut.WaitForAssertion(() => _focusModule.VerifyInvoke("revealStagedNote"));
    }

    [Fact]
    public void TheEpisodeStep_HasAWayBack_ToTheShows()
    {
        _setup = TvSetup("tv_episode");
        var cut = RenderPanel(Subject("episode"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=tv-flow-search]").HasAttribute("disabled")));
        cut.Find("[data-testid=tv-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-show-row]")));
        Tap(cut, "tv-flow-show-row");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-episode-pick]")));

        cut.Find("[data-testid=tv-flow-change-show]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-search]")));
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-episode-pick]"));
    }

    [Fact]
    public void AShow_OpensItsCardAsASheetWhenItsRowIsTapped_AndStaysTickedInTheListAfterBack()
    {
        _setup = TvSetup("tv_series");
        var cut = RenderPanel(Subject("series"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=tv-flow-search]").HasAttribute("disabled")));
        cut.Find("[data-testid=tv-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=tv-flow-show-row]").Count));

        Tap(cut, "tv-flow-show-row", 1);

        cut.WaitForElement("[data-testid=match-sheet] [data-testid=match-apply]");
        cut.Find("[data-testid=match-sheet-back]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        Assert.Equal(2, cut.FindAll("[data-testid=tv-flow-show-row]").Count);
        Assert.Equal("true", cut.FindAll("[data-testid=tv-flow-show-row]")[1].GetAttribute("aria-pressed"));
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void ASong_CanBeMatchedByTouchAlone_ReleaseThenTrackThenTheCardSheet()
    {
        _setup = SongSetup();
        var cut = RenderPanel(Subject("track"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]")));

        // Screen one: the releases. The tracks are not drawn yet.
        cut.Find("[data-testid=music-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=music-flow-release]").Count));
        Assert.Empty(cut.FindAll("[data-testid=music-flow-track-pick]"));
        Tap(cut, "music-flow-release", 1);

        // Screen two: the tracks of that release with a way back; the suggested track is ticked, its card waits for a tap.
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=music-flow-track-pick]").Count));
        Assert.Empty(cut.FindAll("[data-testid=music-flow-release]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-back]"));
        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[data-testid=music-flow-track-pick]")[1].GetAttribute("aria-pressed")));

        Tap(cut, "music-flow-track-pick", 2);

        var sheet = cut.WaitForElement("[data-testid=match-sheet]");
        cut.WaitForAssertion(() => Assert.Equal("Low Tide", cut.Find("[data-testid=match-sheet] [data-testid=match-card-title]").TextContent.Trim()));
        Assert.NotNull(sheet.QuerySelector("[data-testid=match-compare]"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=match-sheet] [data-testid=match-apply]").HasAttribute("disabled")));

        ApplyInTheSheet(cut);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        Assert.Equal("true", cut.FindAll("[data-testid=music-flow-track-pick]")[2].GetAttribute("aria-pressed"));
        cut.WaitForAssertion(() => _focusModule.VerifyInvoke("revealStagedNote"));
    }

    [Fact]
    public void TheTrackStep_HasAWayBack_ToTheReleases()
    {
        _setup = SongSetup();
        var cut = RenderPanel(Subject("track"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]")));
        cut.Find("[data-testid=music-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-release]")));
        Tap(cut, "music-flow-release", 0);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-track-pick]")));

        cut.Find("[data-testid=music-flow-back]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=music-flow-release]").Count));
        Assert.Empty(cut.FindAll("[data-testid=music-flow-track-pick]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]"));
    }

    [Fact]
    public void OnAComputer_TheReleasesAndTheirTracksStayOnOneScreenBesideTheCard()
    {
        _setup = SongSetup();
        var cut = RenderPanel(Subject("track"), phone: false);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]")));
        cut.Find("[data-testid=music-flow-search]").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=music-flow-track-pick]").Count));
        Assert.Equal(2, cut.FindAll("[data-testid=music-flow-release]").Count);
        Assert.Empty(cut.FindAll("[data-testid=music-flow-back]"));
    }

    [Fact]
    public void OnTheWikidataSide_ARowOpensItsCardAsASheet_AndApplyIdentityStagesAndLeavesIt()
    {
        _setup = BookSetup();
        _wikidata = new EditorWikidataSearchResultDto
        {
            EntityId = ItemId,
            Results =
            [
                WikidataResult("Q1196532", "The Sea of Monsters"),
                WikidataResult("Q99", "Percy Jackson: Sea of Monsters"),
            ],
        };
        var cut = RenderPanel(Subject("book"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-source]")));
        cut.FindAll("button, [role=radio], [role=tab]").First(element => element.TextContent.Trim() == "Wikidata").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wikidata-match]")));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=wikidata-search-button]").HasAttribute("disabled")));
        Assert.Contains("app-form-row--stack", cut.Find("[data-testid=match-row]").ClassList);

        cut.Find("[data-testid=wikidata-search-button]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=wikidata-result]").Count));
        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));

        Tap(cut, "wikidata-result", 1);

        var sheet = cut.WaitForElement("[data-testid=match-sheet]");
        Assert.NotNull(sheet.QuerySelector("[data-testid=wikidata-apply]"));
        cut.Find("[data-testid=match-sheet] [data-testid=wikidata-apply]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-sheet]")));
        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.WikidataLink);
    }

    private static EditorWikidataResultDto WikidataResult(string qid, string label) => new()
    {
        Qid = qid,
        Label = label,
        Description = "2006 novel",
        Type = "written work",
        Fact = "2006",
        Relation = EditorWikidataRelations.Direct,
        CanApply = true,
        Url = $"https://www.wikidata.org/wiki/{qid}",
        Evidence = [new EditorWikidataEvidenceDto { Fact = EditorWikidataEvidenceFacts.Year, State = EditorWikidataEvidenceStates.Agrees, Yours = "2006", Wikidata = "2006" }],
    };

    private static EditorCurrentMatchDto CurrentMatch() => new()
    {
        EntityId = ItemId,
        State = EditorCurrentMatchStates.Matched,
        Verdict = EditorMatchVerdicts.Strong,
        Record = new EditorMatchedRecordDto
        {
            ProviderId = "apple_api",
            ProviderName = "Apple Books",
            ProviderItemId = "395523481",
            Kind = "Book",
            Title = "The Sea of Monsters",
            Year = "2009",
            Creator = "Rick Riordan",
            SourceUrl = "https://books.apple.com/book/395523481",
        },
        Why =
        [
            new EditorMatchEvidenceRowDto { Field = "title", YourFile = "The Sea of Monsters", MatchedRecord = "The Sea of Monsters", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
            new EditorMatchEvidenceRowDto { Field = "author", YourFile = "Rick Riordan", MatchedRecord = "Rick Riordan", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
        ],
    };

    [Fact]
    public void AMatchedItem_OnAPhone_FitsTheWidth_TheTableUnderTheCover_AndTheThreeActionsStacked()
    {
        _matched = true;
        _setup = BookSetup();
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Subject("book"))
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, false)
            .AddCascadingValue(EditorWindowLayout.PhoneSheet));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        var card = cut.Find("[data-testid=match-current]");
        Assert.Contains("tl-mcard--phone", card.ClassList);
        Assert.DoesNotContain("tl-mcard--sheet", card.ClassList);
        // The verdict and the table run under the cover and facts, not beside them, and no column is dropped.
        var top = card.QuerySelector(".tl-mcard__top")!;
        Assert.Null(top.QuerySelector("[data-testid=match-compare]"));
        Assert.NotNull(card.QuerySelector("[data-testid=match-compare]"));
        Assert.Equal(4, card.QuerySelectorAll("[data-testid=match-compare] thead th").Length);
        Assert.Contains("tl-matchcard__actions--phone", card.QuerySelector(".tl-matchcard__actions")!.ClassList);
        Assert.Equal(["Open source ↗", "Remove match", "Change match"], card.QuerySelectorAll(".tl-matchcard__actions button, .tl-matchcard__actions a").Select(a => a.TextContent.Trim()).ToList());
        Assert.Contains("tl-matchfile--phone", cut.Find("[data-testid=match-your-file]").ClassList);
        Assert.Empty(cut.FindAll("[data-testid=match-sheet]"));
    }

    [Fact]
    public void ChangeMatch_OnAPhone_OpensTheStackedSearch_AndKeepsTheCurrentMatchUntilAPickIsApplied()
    {
        _matched = true;
        _setup = BookSetup();
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Subject("book"))
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, false)
            .AddCascadingValue(EditorWindowLayout.PhoneSheet));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        cut.Find("[data-testid=match-change]").Click();

        cut.WaitForAssertion(() => Assert.Contains("app-form-row--stack", cut.Find("[data-testid=match-row]").ClassList));
        Assert.Empty(_searches);
        Assert.Empty(_session.Pending.Changes);
    }
}
