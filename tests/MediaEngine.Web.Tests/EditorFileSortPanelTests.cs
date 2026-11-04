using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "What is this file?" in Match &amp; Identity (universal editor v4, D20, WP7b): an episode, an Extra with a kind and place, a file that
/// belongs to another title, or a file to ignore. Every choice only stages a change that Review changes shows. The old "Move to episode" of a
/// show's own clips (Extras spec) lives in the same panel.
/// </summary>
public sealed class EditorFileSortPanelTests : AsyncBunitContext
{
    private static readonly Guid ShowId = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid SiblingId = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid OtherTitleId = Guid.Parse("33333333-0000-0000-0000-000000000003");
    private static readonly string ClipId = Guid.NewGuid().ToString("D");

    private readonly EditSessionState _session = new(new PendingEditList());
    private EditorFileSortingDto? _sorting;

    public EditorFileSortPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(ShowId, "Season 1 trailer.mkv", "Item")]);
        _sorting = new EditorFileSortingDto
        {
            EntityId = ShowId,
            CanSort = true,
            FileName = "Season 1 trailer.mkv",
            TitleKind = EditorFileSortingTitleKinds.Show,
            TitleName = "Masterclass",
            CanBeExtra = true,
            InTitleFolder = true,
            ExtraKinds = [ExtraKinds.Trailer, ExtraKinds.Featurette, ExtraKinds.OtherClip],
            Guess = new EditorFileGuessDto { ExtraKind = ExtraKinds.Trailer, Season = 1 },
            FolderFiles =
            [
                new EditorFolderFileDto
                {
                    EntityId = SiblingId, FileName = "Season 2 interview.mkv", CanBeExtra = true,
                    Guess = new EditorFileGuessDto { ExtraKind = ExtraKinds.Interview, Season = 2 },
                },
            ],
        };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorFileSortingAsync), _ => Task.FromResult(_sorting));
            stub.SetHandler(nameof(IEngineApiClient.GetUniversalSearchAsync), _ => Task.FromResult<UniversalSearchResponseDto?>(
                new UniversalSearchResponseDto("dune", null,
                [
                    new UniversalSearchSectionDto("titles", "Titles",
                    [
                        new UniversalSearchResultDto(OtherTitleId, "tv-show", "TV", "Dune Prophecy", null, null, "2024", null, null, "/x", "Open", "title", 1),
                        new UniversalSearchResultDto(Guid.NewGuid(), "person", null, "Dune Person", null, null, null, null, null, "/y", "Open", "title", 0.5),
                    ], 2, null),
                ], 2)));
        }));
    }

    [Fact]
    public void ASeasonTrailer_BecomesATrailerOfThatSeason_InTwoClicks()
    {
        var cut = RenderPanel();

        cut.Find("button:contains('An Extra')").Click();
        cut.Find("button:contains('Make it an Extra')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.ExtraMove, staged.Kind);
        Assert.Equal(ShowId, staged.EntityId);
        Assert.Equal("file:extra;kind=trailer;season=1", staged.Token);
        Assert.Contains("Trailer · Season 1", cut.Markup);
    }

    [Fact]
    public void AStagedChoice_CanBeUndone()
    {
        var cut = RenderPanel();
        cut.Find("button:contains('An Extra')").Click();
        cut.Find("button:contains('Make it an Extra')").Click();

        cut.Find("[data-testid=file-sort-staged] button:contains('Undo')").Click();

        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void Ignoring_OnlyStagesTheChoice()
    {
        _sorting!.FileName = "sample.mkv";
        var cut = RenderPanel();

        cut.Find("button:contains('Ignore it')").Click();
        cut.Find("button:contains('Ignore this file')").Click();

        Assert.Equal("file:ignore", Assert.Single(_session.Pending.Changes).Token);
    }

    [Fact]
    public void AFilesOtherFolderMates_AreListed_AndCanBeSortedAsGuessed()
    {
        var cut = RenderPanel();

        Assert.Contains("Season 2 interview.mkv", cut.Find("[data-testid=file-sort-folder]").TextContent);
        cut.Find("button:contains('Sort them all as guessed')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(SiblingId, staged.EntityId);
        Assert.Equal("file:extra;kind=interview;season=2", staged.Token);
    }

    [Fact]
    public void ABelongsToAnotherTitleSearch_OffersOnlyTitlesWithFiles_AndStagesTheAttach()
    {
        var cut = RenderPanel();

        cut.Find("button:contains('It belongs to another title')").Click();
        cut.Find("[data-testid=file-sort-attach] input").Input("dune");
        cut.Find("[data-testid=file-sort-attach] button:contains('Search')").Click();
        cut.WaitForAssertion(() => Assert.Contains("Dune Prophecy", cut.Markup));
        Assert.DoesNotContain("Dune Person", cut.Markup);

        cut.Find("[role=listitem] button").Click();
        cut.Find("button:contains('Attach to Dune Prophecy')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.StartsWith("file:attach;kind=trailer;title=" + OtherTitleId.ToString("D"), staged.Token);
    }

    [Fact]
    public void AFileThatIsNotASingleFile_ShowsNoQuestion()
    {
        _sorting = new EditorFileSortingDto { EntityId = ShowId, CanSort = false };

        var cut = Render<EditorFileSortPanel>(p => p
            .Add(c => c.Subject, new EditorMatchSubject(ShowId, "TV", "item", "series", "Masterclass", null, null, null,
                new Dictionary<string, string>(), []))
            .Add(c => c.Session, _session)
            .Add(c => c.CanEdit, true));

        Assert.Empty(cut.FindAll("[data-testid=file-sort]"));
    }

    [Fact]
    public void AShow_ListsItsVideoClips_AndMovesOneToAnEpisode()
    {
        _sorting = new EditorFileSortingDto { EntityId = ShowId, CanSort = false };
        var cut = RenderPanel(scope: "series", extras: [Card(ClipId, DetailExtraCardKinds.Clip, "Trailer"), Card(Guid.NewGuid().ToString("D"), DetailExtraCardKinds.Document, "Workbook")]);

        Assert.DoesNotContain("Workbook", cut.Markup);
        cut.Find("button:contains('Move to episode')").Click();
        cut.FindAll("input")[0].Change("2");
        cut.FindAll("input")[1].Change("5");
        cut.Find("button:contains('Move')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(ClipId, staged.TargetId);
        Assert.Equal("episode:2:5", staged.Token);
    }

    [Fact]
    public void ReviewChanges_NamesTheSortingChoice()
    {
        _session.Pending.SetFileChoice(ShowId, EditorFileSortRules.IgnoreChoice, "Ignored", "sample.mkv");

        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "sample.mkv", null));

        Assert.Equal("Sorted as", row.What);
        Assert.Equal("sample.mkv", row.Before);
        Assert.Equal("Ignored", row.After);
    }

    [Fact]
    public void TheRules_KeepAGuessToThePlacesATitleOffers()
    {
        var guess = new EditorFileGuessDto { ExtraKind = ExtraKinds.Trailer, Season = 2, Episode = 3 };

        Assert.Equal(ExtraPlaceMode.Episode, EditorFileSortRules.PlaceFromGuess(EditorFileSortingTitleKinds.Show, guess).Mode);
        Assert.Equal(ExtraPlaceMode.Whole, EditorFileSortRules.PlaceFromGuess(EditorFileSortingTitleKinds.Film, guess).Mode);
        Assert.Equal("Document, on Dune", EditorFileSortRules.Describe(EditorFileSortRules.AttachChoice(ExtraKinds.Document, OtherTitleId)!, "Dune"));
        Assert.Null(EditorFileSortRules.ExtraChoice("nonsense", ExtraPlaceMode.Whole, 1, 1, 1));
        Assert.True(EditorFileSortRules.CanAttachTo("tv-show"));
        Assert.False(EditorFileSortRules.CanAttachTo("person"));
    }

    private IRenderedComponent<EditorFileSortPanel> RenderPanel(string scope = "item", IReadOnlyList<DetailExtraCardViewModel>? extras = null)
    {
        var cut = Render<EditorFileSortPanel>(p => p
            .Add(c => c.Subject, new EditorMatchSubject(ShowId, "TV", scope, "series", "Masterclass", null, null, null,
                new Dictionary<string, string>(), []))
            .Add(c => c.Extras, extras ?? [])
            .Add(c => c.Session, _session)
            .Add(c => c.CanEdit, true));
        cut.WaitForState(() => cut.Markup.Contains("tl-filesort"), TimeSpan.FromSeconds(2));
        return cut;
    }

    private static DetailExtraCardViewModel Card(string id, string kind, string title) =>
        new() { Id = id, Kind = kind, Title = title };
}
