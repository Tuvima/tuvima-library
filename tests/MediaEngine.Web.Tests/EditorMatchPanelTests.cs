using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
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
        new Dictionary<string, string> { ["title"] = "Karma Police", ["artist"] = "Radiohead" },
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
    public void ChoosingAResult_MovesFocusToTheReview_AndBackToResults_PutsItOnThatResultsSelectButton()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-match__row").Count));
        Assert.Equal("tl-match-select-1", cut.FindAll("button").Where(button => button.TextContent.Trim() == "Select").ElementAt(1).Id);

        Select(cut, 1);
        cut.WaitForAssertion(() => Assert.Equal("tl-match-review", LastFocusedId()));
        Assert.Equal("group", cut.Find("#tl-match-review").GetAttribute("role"));
        Assert.Equal("Review selected match", cut.Find("#tl-match-review").GetAttribute("aria-label"));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Back to results").Click();
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
        Assert.DoesNotContain("%", cut.Markup, StringComparison.Ordinal);
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

        var picker = cut.FindComponent<AppSelect>();
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
        Assert.Contains("Greatest Hits", cut.Find("[aria-label='Your item']").TextContent, StringComparison.Ordinal);
        Assert.Contains("OK Computer", cut.Find("[aria-label='Proposed match']").TextContent, StringComparison.Ordinal);
        Assert.Equal(4, cut.FindAll("input[role=switch]").Count);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));
        Assert.True(SwitchOn(cut, "Keep my library details"));
        Assert.True(SwitchOn(cut, "Use as the retail match"));
        Assert.False(SwitchOn(cut, "Move to OK Computer"));
        Assert.False(SwitchOn(cut, "Rename and move the files"));
    }

    [Fact]
    public void TheFileSwitch_IsGreyedWithTheReason_WhenTheFolderIsProtected()
    {
        _fileRow = new LibraryMovePreviewRow { ItemId = SongId, FileNote = "Read-only folder: the file stays where it is." };
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));

        Select(cut, 0);

        cut.WaitForAssertion(() => Assert.True(cut.Find("input[role=switch][aria-label='Rename and move the files']").HasAttribute("disabled")));
        Assert.Contains("Read-only folder: the file stays where it is.", cut.Markup, StringComparison.Ordinal);
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

    [Fact]
    public void UseThisMatch_StagesTheMatchOnly_AndWritesNothing()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var change = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.Identity, change.Kind);
        Assert.Equal(SongId, change.EntityId);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("musicbrainz", match!.ProviderName);
        Assert.Empty(_writes);
        Assert.Contains("Nothing is saved until you press Save changes", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UseThisMatch_StagesTheAlbumMoveAndTheFilesOnlyWhenTheirSwitchesAreOn()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-match__row")));
        Select(cut, 0);
        cut.WaitForAssertion(() => Assert.False(cut.Find("input[role=switch][aria-label='Move to OK Computer']").HasAttribute("disabled")));

        cut.Find("input[role=switch][aria-label='Move to OK Computer']").Change(true);
        cut.Find("input[role=switch][aria-label='Rename and move the files']").Change(true);
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
        Assert.Single(_session.Pending.Changes);

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

        cut.WaitForAssertion(() => Assert.Contains("Nothing matched", cut.Find(".tl-match__problem").TextContent, StringComparison.Ordinal));
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
