using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab's music flow (universal editor v5, 3c, mockup G4): the releases of an album with the current and suggested ones marked, the
/// discs and tracks with the suggested track pre-selected and each recording's disambiguation in its row, the preview of the album move,
/// and Apply match staging the match with the move and the Managed file move.
/// </summary>
public sealed class EditorMusicMatchFlowTests : AsyncBunitContext
{
    private static readonly Guid SongId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string ReleaseA = "11111111-1111-1111-1111-111111111111";
    private const string ReleaseB = "22222222-2222-2222-2222-222222222222";

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<ItemCanonicalRetailCandidateDto> _judged = [];
    private EditorMatchPlacementDto? _placement;

    public EditorMusicMatchFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(SongId, "Harbor Lights", "Item")]);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ => Task.FromResult<EditorCurrentMatchDto?>(new EditorCurrentMatchDto { EntityId = SongId }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), _ => Task.FromResult<EditorMatchSetupDto?>(new EditorMatchSetupDto
            {
                EntityId = SongId,
                Kind = "song",
                Providers = [new EditorMatchProviderDto { Name = "musicbrainz", DisplayName = "MusicBrainz", State = EditorMatchProviderStates.Ready, IsDefault = true }],
                Fields =
                [
                    new EditorMatchFieldDto { Key = "artist", Label = "Artist", Value = "The Tides" },
                    new EditorMatchFieldDto { Key = "album", Label = "Album", Value = "Low Water" },
                    new EditorMatchFieldDto { Key = "song", Label = "Song", Value = "Harbor Lights" },
                ],
                Evidence = new EditorMatchLocalEvidenceDto { Number = 4, MeasuredSeconds = 222 },
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMusicReleasesAsync), _ => Task.FromResult<EditorMusicReleasesDto?>(new EditorMusicReleasesDto
            {
                EntityId = SongId,
                Releases =
                [
                    new EditorMusicReleaseDto { ReleaseId = ReleaseA, Title = "Low Water", Artist = "The Tides", Year = "2011", Format = "CD", Country = "GB", TrackCount = 11, IsCurrent = true },
                    new EditorMusicReleaseDto { ReleaseId = ReleaseB, Title = "Low Water (Deluxe)", Artist = "The Tides", Year = "2013", Format = "2 × CD", Label = "Harbor Records", TrackCount = 24, HasCover = true, ImageUrl = "data:image/png;base64,AAAA", IsSuggested = true },
                ],
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMusicReleaseTracksAsync), args =>
            {
                var id = (string)args![1]!;
                return Task.FromResult<EditorMusicReleaseTracksDto?>(new EditorMusicReleaseTracksDto
                {
                    EntityId = SongId,
                    Release = new EditorMusicReleaseDto { ReleaseId = id, Title = "Low Water (Deluxe)", Artist = "The Tides", Year = "2013", TrackCount = 24, HasCover = true },
                    Discs =
                    [
                        new EditorMusicDiscDto { Number = 1, Tracks = [new EditorMusicTrackDto { RecordingId = "rec-1", Disc = 1, Track = 1, Title = "Opening", LengthText = "3:00" }] },
                        new EditorMusicDiscDto
                        {
                            Number = 2,
                            Tracks =
                            [
                                new EditorMusicTrackDto { RecordingId = "rec-2", Disc = 2, Track = 2, Title = "Low Tide", LengthText = "4:01" },
                                new EditorMusicTrackDto
                                {
                                    RecordingId = "rec-3", Disc = 2, Track = 3, Title = "Harbor Lights", Artist = "The Tides", LengthSeconds = 222, LengthText = "3:42",
                                    Disambiguation = "2013 remaster", IsSuggested = true,
                                },
                            ],
                        },
                    ],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchVerdictAsync), args =>
            {
                var candidate = (ItemCanonicalRetailCandidateDto)args![1]!;
                _judged.Add(candidate);
                return Task.FromResult<EditorMatchResultDto?>(new EditorMatchResultDto
                {
                    Candidate = candidate,
                    ProviderName = "musicbrainz",
                    Title = candidate.Title,
                    Verdict = EditorMatchVerdicts.Strong,
                    Reasons = [new EditorMatchReasonDto { Fact = "track_length", State = EditorMatchEvidenceStates.Agrees, Text = "Length consistent", Note = "3:42 measured · 3:42" }],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchPlacementAsync), _ => Task.FromResult(_placement));
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSubject Song() => new(
        SongId, "Music", "track", "track", "Harbor Lights", "The Tides", "Low Water", "2011",
        new Dictionary<string, string> { ["title"] = "Harbor Lights" }, []);

    private IRenderedComponent<EditorMatchPanel> Open()
    {
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Song())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]")));
        cut.Find("[data-testid=music-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-track]")));
        return cut;
    }

    [Fact]
    public void TheBoxes_StartFromTheFile_AndTheReleasesMarkTheCurrentAndTheSuggestedOne()
    {
        var cut = Open();

        Assert.Equal("The Tides", cut.Find("input[aria-label='Artist']").GetAttribute("value"));
        Assert.Equal("Low Water", cut.Find("input[aria-label='Album']").GetAttribute("value"));
        var rows = cut.FindAll("[data-testid=music-flow-release]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("current", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("suggested", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("2013 · 2 × CD · 24 tracks · Harbor Records", rows[1].TextContent, StringComparison.Ordinal);
        Assert.NotNull(rows[1].QuerySelector("img"));
        Assert.Null(rows[0].QuerySelector("img"));
        Assert.Equal("true", rows[1].GetAttribute("aria-pressed"));
        Assert.Contains("✓", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[role=listitem]"));
    }

    [Fact]
    public void TheSuggestedTrack_IsPreSelected_AndTheRecordingNoteShowsInItsRow()
    {
        var cut = Open();

        var picked = Assert.Single(cut.FindAll("[data-testid=music-flow-track].is-picked"));
        Assert.Equal("true", picked.QuerySelector("[data-testid=music-flow-track-pick]")!.GetAttribute("aria-pressed"));
        Assert.Equal("2·3", picked.QuerySelector("[data-testid=music-flow-track-pick]")!.TextContent.Trim());
        Assert.Equal("2", picked.GetAttribute("data-disc"));
        Assert.Equal("3", picked.GetAttribute("data-track"));
        Assert.Contains("2013 remaster", picked.TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));
        var candidate = Assert.Single(_judged);
        Assert.Equal(ReleaseB, candidate.BridgeIds["musicbrainz_release_id"]);
        Assert.Equal("rec-3", candidate.BridgeIds["musicbrainz_recording_id"]);
        Assert.Equal("3", candidate.SuggestedFields["track_number"]);
        Assert.Equal("2", candidate.SuggestedFields["disc_number"]);
    }

    private static EditorMatchPlacementDto MovePlacement(Guid? currentId, Guid destinationId, string current = "Low Water") => new()
    {
        DestinationId = destinationId,
        CurrentParentId = currentId,
        CurrentParentKeeps = 10,
        Position = 3,
        ParentLabel = "Low Water (Deluxe)",
        CurrentParentLabel = current,
        Managed = true,
        FromPath = "/music/Low Water/04.flac",
        ToPath = "/music/Low Water (Deluxe)/2-03.flac",
    };

    [Fact]
    public void MovingToAnotherAlbum_ShowsAnAmberCard_WithTheTrackItLeavesTheDiscAndTrackItTakesAndWhatTheOldAlbumKeeps()
    {
        _placement = MovePlacement(Guid.NewGuid(), Guid.NewGuid());
        var cut = Open();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-move]")));
        var text = cut.Find("[data-testid=music-flow-move]").TextContent;
        Assert.Contains("This song moves to another album.", text, StringComparison.Ordinal);
        Assert.Contains("From Low Water, track 4 to Low Water (Deluxe), disc 2 track 3.", text, StringComparison.Ordinal);
        Assert.Contains("Its album, artist and cover follow;", text, StringComparison.Ordinal);
        Assert.Contains("Low Water album keeps 10 tracks.", text, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=music-flow-move]").QuerySelector("i"));
    }

    [Fact]
    public void ADifferentlyNamedAlbum_ThatIsTheSameAlbumById_ShowsNoMoveCard()
    {
        var same = Guid.NewGuid();
        _placement = MovePlacement(same, same, current: "Low Water (Special Edition)");
        var cut = Open();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));

        Assert.Empty(cut.FindAll("[data-testid=music-flow-move]"));
        Assert.DoesNotContain("Stays on", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void APickTheEngineCannotPlace_SaysWhy_InsteadOfTheMoveCard()
    {
        _placement = new EditorMatchPlacementDto { BlockedReason = "That album is in a Read-only folder." };
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("That album is in a Read-only folder.", cut.Find("[data-testid=music-flow-move]").TextContent.Trim()));
    }

    [Fact]
    public void Opening_AsksNothing_UntilSearchIsPressed_AndTheTracksComeAsATableWithDiscAndTrack()
    {
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Song())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-search]")));
        Assert.Empty(cut.FindAll("[data-testid=music-flow-release]"));
        Assert.NotNull(Assert.Single(cut.FindAll("[data-testid=match-row]")).QuerySelector("[data-testid=music-flow-song]"));

        cut.Find("[data-testid=music-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=music-flow-track]")));

        Assert.Equal(["#", "Track", "Recording", "Length", "Chosen"], cut.FindAll("[data-testid=music-flow-table] thead th").Select(th => th.TextContent.Trim()).ToList());
        Assert.Contains("Discs and tracks of Low Water (Deluxe)", cut.Find("[data-testid=music-flow-tracks]").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("[data-testid=music-flow-table] .tl-musicflow__disc").Count);
    }

    [Fact]
    public void Applying_StagesTheMatch_TheAlbumMove_AndAManagedFileMove()
    {
        _placement = MovePlacement(Guid.NewGuid(), Guid.NewGuid());
        var cut = Open();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        Assert.Equal(
            new[] { EditSessionChangeKinds.FileMove, EditSessionChangeKinds.Identity, EditSessionChangeKinds.Relationship }.Order().ToList(),
            _session.Pending.Changes.Select(change => change.Kind).Order().ToList());
    }

    [Fact]
    public void PickingAnotherTrack_JudgesThatTrackInstead()
    {
        var cut = Open();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));

        cut.FindAll("[data-testid=music-flow-track]").First(row => row.GetAttribute("data-track") == "2").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _judged.Count));
        Assert.Equal("Low Tide", _judged[^1].Title);
    }
}
