using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Match for several files (universal editor v4, D5, D7, A5, WP8): step 1 chooses the show or album, step 2 is a table lining up the person's
/// files with the provider's episodes or tracks, with the Engine's confidence and reason and a status per row. A doubtful row says Needs
/// choice and blocks staging until it is decided; staging puts one match per file in the pending list and writes nothing.
/// </summary>
public sealed class EditorLineUpPanelTests : AsyncBunitContext
{
    private static readonly Guid SeasonId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid AlbumId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private const string Reason = "Season and episode number match; the title differs";

    private readonly PendingEditList _pending = new();
    private readonly List<MediaEditorPairingPreviewRequestDto> _previews = [];
    private readonly List<string> _writes = [];
    private IReadOnlyList<MediaEditorOwnedChildDto> _owned = [];
    private Func<MediaEditorPairingPreviewRequestDto, MediaEditorPairingPreviewDto?> _preview = _ => null;

    public EditorLineUpPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), args =>
                Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto((string)args![1]!,
                [
                    new TvMatchBrowserShowDto("100", "Original Show", "2010", null, null, "https://thetvdb.test/100", true),
                ])));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserEpisodesAsync), args =>
            {
                var seriesId = (string)args![1]!;
                return Task.FromResult<TvMatchBrowserEpisodesDto?>(new TvMatchBrowserEpisodesDto(seriesId, "Original Show", "s1", 1,
                    [.. Enumerable.Range(1, 13).Select(number => Episode(number))]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), _ =>
                Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto { Page = 1, PageSize = 200, TotalCount = _owned.Count, Items = [.. _owned] }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewMediaEditorPairingAsync), args =>
            {
                var request = (MediaEditorPairingPreviewRequestDto)args![1]!;
                _previews.Add(request);
                return Task.FromResult(_preview(request));
            });
            stub.SetHandler(nameof(IEngineApiClient.SearchItemCanonicalAsync), _ =>
                Task.FromResult<ItemCanonicalSearchResponseDto?>(new ItemCanonicalSearchResponseDto
                {
                    RetailCandidates =
                    [
                        new ItemCanonicalRetailCandidateDto
                        {
                            CandidateId = "mb:album",
                            ProviderId = "mb",
                            ProviderName = "musicbrainz",
                            ProviderItemId = "rg-1",
                            Title = "OK Computer",
                            Year = "1997",
                            RequiredFields = new Dictionary<string, string> { ["artist"] = "Radiohead", ["album"] = "OK Computer" },
                            BridgeIds = new Dictionary<string, string> { [BridgeIdKeys.MusicBrainzReleaseId] = "rel-9" },
                        },
                    ],
                }));
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), _ =>
            {
                _writes.Add("session");
                return Task.FromResult<LibraryBatchEditOutcome<EditSessionResult>>(default!);
            });
        }));
        Render<AppPopoverHost>();
    }

    private static TvMatchBrowserEpisodeDto Episode(int number) =>
        new($"e{number}", "100", "Original Show", "s1", 1, number, $"Episode {number}", $"2015-03-{number:00}", null, null, "https://thetvdb.test/e", false, false, null);

    private static MediaEditorOwnedChildDto Owned(int index, string prefix) => new()
    {
        AssetId = Guid.Parse($"b{prefix}000000-0000-0000-0000-{index:000000000000}"),
        WorkId = Guid.Parse($"c{prefix}000000-0000-0000-0000-{index:000000000000}"),
        SelectionRevision = $"rev-{index}",
        SourceFileName = prefix == "1" ? $"Original.Show.S01E{index:00}.mkv" : $"{index:00} - Track.flac",
        Title = $"File {index}",
    };

    private static MediaEditorPairingChildDto Child(int number, string parent = "100", string kind = "tv") => kind == "tv"
        ? new($"e{number}", parent, "tvdb", $"Episode {number}", 1, number, null, null, null)
        : new($"t{number}", parent, "musicbrainz", $"Song {number}", null, null, 1, number, $"rec-{number}");

    private static MediaEditorPairingCandidateDto Candidate(int number, string band, string kind = "tv") =>
        new(Child(number, kind == "tv" ? "100" : "rel-9", kind), band, [], [], false, null, Reason);

    private void SeasonOfThirteen(int doubtfulIndex)
    {
        _owned = [.. Enumerable.Range(1, 13).Select(index => Owned(index, "1"))];
        _preview = request => new MediaEditorPairingPreviewDto("tv_episode", "tvdb", request.TargetParentId ?? "100", false, "Catalogue completeness has not been verified; review every proposed mapping.",
            [.. _owned.Where(file => request.AssetIds.Contains(file.AssetId)).Select((file, position) =>
            {
                var number = _owned.ToList().IndexOf(file) + 1;
                var doubtful = number == doubtfulIndex;
                return new MediaEditorPairingRowDto(
                    file.AssetId,
                    file.SourceFileName,
                    null,
                    null,
                    Candidate(number, doubtful ? "Review" : "Exact"),
                    doubtful ? [Candidate(number + 1, "Review")] : [],
                    doubtful ? "Review" : "Exact",
                    false,
                    null);
            })]);
    }

    private IRenderedComponent<EditorLineUpPanel> RenderTv(IReadOnlyList<BulkEditRow>? rows = null, bool entry = false) =>
        Render<EditorLineUpPanel>(parameters => parameters
            .Add(panel => panel.ParentId, SeasonId)
            .Add(panel => panel.MediaType, "TV")
            .Add(panel => panel.ParentScope, "season")
            .Add(panel => panel.ParentName, "Original Show")
            .Add(panel => panel.Rows, rows)
            .Add(panel => panel.Pending, _pending)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.Entry, entry));

    private IRenderedComponent<EditorLineUpPanel> RenderAlbum() =>
        Render<EditorLineUpPanel>(parameters => parameters
            .Add(panel => panel.ParentId, AlbumId)
            .Add(panel => panel.MediaType, "Music")
            .Add(panel => panel.ParentScope, "album")
            .Add(panel => panel.ParentName, "OK Computer")
            .Add(panel => panel.Pending, _pending)
            .Add(panel => panel.CanEdit, true));

    private static void ChooseShow(IRenderedComponent<EditorLineUpPanel> cut)
    {
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Select Original Show']")));
        cut.Find("button[aria-label='Select Original Show']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='lineup-table']")));
    }

    [Fact]
    public void ASeasonOfThirteenFiles_IsLinedUpInOnePass_AndADoubtfulRowBlocksStagingUntilItIsChosen()
    {
        SeasonOfThirteen(doubtfulIndex: 7);
        var cut = RenderTv();

        ChooseShow(cut);

        // One read lined up all thirteen files against the chosen show.
        var request = Assert.Single(_previews);
        Assert.Equal(13, request.AssetIds.Count);
        Assert.Equal("100", request.TargetParentId);
        Assert.Equal(13, cut.FindAll(".tl-lineup__row").Count);
        Assert.Equal(12, cut.FindAll("tr[data-status='ready']").Count);
        var doubtful = Assert.Single(cut.FindAll("tr[data-status='needs-choice']"));
        Assert.Contains("Needs choice", doubtful.TextContent, StringComparison.Ordinal);

        // Each row shows the Engine's reason in its small (i), and the number and title of the episode it is lined up with.
        Assert.Contains(Reason, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("S01 E01", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Episode 13", cut.Markup, StringComparison.Ordinal);

        // The summary counts what is left, and Stage stays off.
        Assert.Contains("1 row still needs a choice", cut.Find("[data-testid='lineup-summary']").TextContent, StringComparison.Ordinal);
        var stage = cut.FindAll("button").First(button => button.TextContent.Trim().StartsWith("Stage", StringComparison.Ordinal));
        Assert.True(stage.HasAttribute("disabled"));
        Assert.Empty(_pending.Changes);

        // Choosing one of the doubtful row's candidates makes it Ready; then all thirteen can be staged.
        cut.Find("tr[data-status='needs-choice'] select").Change("e7");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tr[data-status='needs-choice']")));
        stage = cut.FindAll("button").First(button => button.TextContent.Trim().StartsWith("Stage", StringComparison.Ordinal));
        Assert.False(stage.HasAttribute("disabled"));
        Assert.Equal("Stage 13 matches", stage.TextContent.Trim());
        stage.Click();

        var changes = _pending.Changes.Where(change => change.Kind == EditSessionChangeKinds.Identity).ToList();
        Assert.Equal(13, changes.Count);
        Assert.Equal(13, changes.Select(change => change.EntityId).Distinct().Count());
        Assert.Equal(13, _pending.ChangeCount);
        var seventh = changes.Single(change => change.EntityId == Guid.Parse("c1000000-0000-0000-0000-000000000007"));
        Assert.True(EditSessionIdentityTarget.TryParse(seventh.TargetId, out var match));
        Assert.Equal("show_episode", match!.TargetFieldGroup);
        Assert.Equal("e7", match.ProviderItemId);
        Assert.Equal("Original Show", match.RequiredFields[MetadataFieldConstants.ShowName]);
        Assert.Equal("7", match.RequiredFields[MetadataFieldConstants.EpisodeNumber]);
        Assert.Equal("100", match.BridgeIds[BridgeIdKeys.TvdbId]);

        // Nothing was written, and nothing moves files.
        Assert.Empty(_writes);
        Assert.DoesNotContain(_pending.Changes, change => change.Kind is EditSessionChangeKinds.FileMove or EditSessionChangeKinds.Relationship);
        Assert.Contains("no file is moved", cut.Find("[data-testid='lineup-nothing-moves']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubtfulRow_CanBeLeftAsItIs_AndTheRestAreStaged()
    {
        SeasonOfThirteen(doubtfulIndex: 3);
        var cut = RenderTv();
        ChooseShow(cut);

        cut.Find("tr[data-status='needs-choice'] select").Change(EditorLineUpRules.LeaveAloneValue);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr[data-status='skipped']")));
        cut.FindAll("button").First(button => button.TextContent.Trim().StartsWith("Stage", StringComparison.Ordinal)).Click();

        Assert.Equal(12, _pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Identity));
        Assert.DoesNotContain(_pending.Changes, change => change.EntityId == Guid.Parse("c1000000-0000-0000-0000-000000000003"));
    }

    [Fact]
    public void ApplyTo_SelectedFilesOnly_LinesUpOnlyTheTickedFiles_AndAllFilesInThisSeasonLinesUpEveryOne()
    {
        SeasonOfThirteen(doubtfulIndex: 0);
        IReadOnlyList<BulkEditRow> ticked =
        [
            .. Enumerable.Range(1, 3).Select(index => new BulkEditRow(Guid.Parse($"c1000000-0000-0000-0000-{index:000000000000}"), $"Episode {index}", DetailEntityType.TvEpisode, SeasonId, "TV")),
        ];
        var cut = RenderTv(ticked);

        Assert.Contains("Selected files only", cut.Find("[data-testid='lineup-apply']").TextContent, StringComparison.Ordinal);
        Assert.Contains("All files in this season", cut.Find("[data-testid='lineup-apply']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Match these 3 episodes", cut.Markup, StringComparison.Ordinal);
        ChooseShow(cut);
        Assert.Equal(3, Assert.Single(_previews).AssetIds.Count);
        Assert.Equal(3, cut.FindAll(".tl-lineup__row").Count);

        cut.FindAll("[data-testid='lineup-apply'] button").Single(button => button.TextContent.Contains("All files in this season", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(13, cut.FindAll(".tl-lineup__row").Count));
        Assert.Equal(13, _previews[^1].AssetIds.Count);
    }

    [Fact]
    public void AnAlbumOfTwelveSongs_IsLinedUpTheSameWay_AndStagesTrackMatchesAgainstTheChosenRelease()
    {
        _owned = [.. Enumerable.Range(1, 12).Select(index => Owned(index, "2"))];
        _preview = request => new MediaEditorPairingPreviewDto("music_release_track", "musicbrainz", request.TargetParentId ?? string.Empty, true, null,
            [.. _owned.Select((file, position) => new MediaEditorPairingRowDto(
                file.AssetId, file.SourceFileName, null, null, Candidate(position + 1, "Exact", "music"), [], "Exact", true, null))]);
        var cut = RenderAlbum();

        Assert.Contains("Choose the album", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Search").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Select OK Computer']")));
        cut.Find("button[aria-label='Select OK Computer']").Click();
        cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll(".tl-lineup__row").Count));

        Assert.Equal("rel-9", Assert.Single(_previews).TargetParentId);
        Assert.Equal(12, cut.FindAll("tr[data-status='ready']").Count);
        Assert.Contains("songs lined up", cut.Find(".tl-lineup__caption").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Stage 12 matches").Click();

        var changes = _pending.Changes.Where(change => change.Kind == EditSessionChangeKinds.Identity).ToList();
        Assert.Equal(12, changes.Count);
        Assert.True(EditSessionIdentityTarget.TryParse(changes[0].TargetId, out var match));
        Assert.Equal("track", match!.TargetFieldGroup);
        Assert.Equal("musicbrainz", match.ProviderName);
        Assert.Equal("rec-1", match.BridgeIds[BridgeIdKeys.MusicBrainzRecordingId]);
        Assert.Equal("rel-9", match.BridgeIds[BridgeIdKeys.MusicBrainzReleaseId]);
        Assert.Equal("OK Computer", match.RequiredFields[MetadataFieldConstants.Album]);
        Assert.Empty(_writes);
    }

    [Fact]
    public void OnTheMatchTab_ThePanelStartsAsAShortEntry_AndOpensTheStepsWhenPressed()
    {
        SeasonOfThirteen(doubtfulIndex: 0);
        var cut = RenderTv(entry: true);

        Assert.NotEmpty(cut.FindAll("[data-testid='lineup-entry']"));
        Assert.Empty(cut.FindAll("[data-testid='lineup-step-one']"));
        Assert.Contains("Match the files in this season", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Line up files").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='lineup-step-one']")));
    }

    [Fact]
    public void WhenTheEngineCannotLineThemUp_ThePanelSaysSo_AndOffersTryAgain()
    {
        SeasonOfThirteen(doubtfulIndex: 0);
        _preview = _ => null;
        var cut = RenderTv();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Select Original Show']")));
        cut.Find("button[aria-label='Select Original Show']").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not line these files up", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Try again");
        Assert.Empty(_pending.Changes);
    }

    [Fact]
    public void TwoFilesLinedUpWithTheSameEpisode_BothNeedAChoice()
    {
        var files = new[] { Owned(1, "1"), Owned(2, "1") }.Select(file => new LineUpFile(file.WorkId, file.AssetId, file.SelectionRevision, file.SourceFileName, null, null)).ToList();
        var preview = new MediaEditorPairingPreviewDto("tv_episode", "tvdb", "100", false, null,
        [
            new MediaEditorPairingRowDto(files[0].AssetId, files[0].FileName, null, null, Candidate(4, "Exact"), [], "Exact", false, null),
            new MediaEditorPairingRowDto(files[1].AssetId, files[1].FileName, null, null, Candidate(4, "Strong"), [], "Strong", false, null),
        ]);

        var rows = EditorLineUpRules.BuildRows(files, preview);

        Assert.All(rows, row => Assert.Null(row.ChosenId));
        Assert.Equal(2, EditorLineUpRules.NeedsChoiceCount(rows));
        Assert.False(EditorLineUpRules.CanStage(rows));
        Assert.Equal(-1, EditorLineUpRules.Stage(_pending, LineUpKind.TvEpisodes, rows, "Original Show", null, null, null));
        Assert.Empty(_pending.Changes);
    }

    [Theory]
    [InlineData("Exact", 100, true)]
    [InlineData("Strong", 80, true)]
    [InlineData("Review", 45, false)]
    [InlineData("NoMatch", 0, false)]
    public void TheBand_GivesTheBarsFill_AndWhetherTheEngineIsSureEnough(string band, int percent, bool confident)
    {
        Assert.Equal(percent, EditorLineUpRules.ConfidenceOf(band));
        Assert.Equal(confident, EditorLineUpRules.IsConfident(band));
    }

    [Fact]
    public void ARowTheEngineIsUnsureAbout_OrFoundAConflictWith_StartsUndecided()
    {
        var file = Owned(1, "1");
        var lineUpFile = new LineUpFile(file.WorkId, file.AssetId, file.SelectionRevision, file.SourceFileName, null, null);
        var conflicted = new MediaEditorPairingCandidateDto(Child(1), "Exact", [], ["Runtime is far off"], false, null, null);
        var preview = new MediaEditorPairingPreviewDto("tv_episode", "tvdb", "100", false, null,
            [new MediaEditorPairingRowDto(file.AssetId, file.SourceFileName, null, null, conflicted, [], "Exact", false, null)]);

        var row = Assert.Single(EditorLineUpRules.BuildRows([lineUpFile], preview));

        Assert.Null(row.ChosenId);
        Assert.True(row.Conflicted);
        Assert.Equal("Runtime is far off", row.Reason);
    }
}
