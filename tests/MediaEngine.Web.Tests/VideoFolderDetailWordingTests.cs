using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A folder of Other videos (a course, a saved channel) reads as Videos with named sections, a Play or Resume button and numbered titles:
/// never Seasons, Episodes or "S1 E1" (issue 703). A TV show keeps Seasons and Episodes.
/// </summary>
public sealed class VideoFolderDetailWordingTests : AsyncBunitContext
{
    private static readonly Guid Folder = Guid.Parse("50000000-0000-0000-0000-000000000005");

    public VideoFolderDetailWordingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    [Fact]
    public void AFolderOfVideosListsItsVideosUnderNamedSections_AndNeverSaysSeasonEpisodeOrAnSeCode()
    {
        var cut = RenderPanel(VideoFolderPlacement());

        var text = cut.Markup;
        Assert.Contains("Videos", cut.Find("section.tl-series-detail").GetAttribute("aria-label"));
        Assert.Contains("Ball handling", text);
        Assert.Contains("Footwork", text);
        Assert.False(VideoFolderWording.UsesTvWording(cut.Find("section.tl-series-detail").TextContent), cut.Find("section.tl-series-detail").TextContent);
        Assert.DoesNotContain("Season", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S1 E1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderOfVideosOffersASectionControlNamedAfterItsSubFolders()
    {
        var cut = RenderPanel(VideoFolderPlacement());

        var select = cut.FindComponents<AppSelect>().Single(candidate => candidate.Instance.Options!.Any(option => option.Label == "Week 1 – Basics"));
        Assert.Equal(["Week 1 – Basics", "Week 2 – Game play"], select.Instance.Options!.Select(option => option.Label));
        Assert.Equal("Section", select.Instance.AdditionalAttributes?["aria-label"]?.ToString());
        Assert.Equal("Section", cut.Find(".tl-series-detail__control--group .sr-only").TextContent);
    }

    [Fact]
    public void AFolderWithoutSubFoldersShowsNoSectionControl()
    {
        var placement = VideoFolderPlacement(singleSection: true);

        var cut = RenderPanel(placement);

        Assert.DoesNotContain(cut.FindComponents<AppSelect>(), select => select.Instance.Options!.Any(option => option.Label == "Videos"));
        Assert.Empty(cut.FindAll(".tl-series-detail__control--group"));
    }

    [Fact]
    public void ATvShowStillListsSeasonsAndEpisodes()
    {
        var cut = RenderPanel(TvPlacement());

        var select = cut.FindComponents<AppSelect>().Single(candidate => candidate.Instance.Options!.Any(option => option.Label.StartsWith("Season", StringComparison.Ordinal)));
        Assert.Equal(["Season 1 · 1 episode", "Season 2 · 1 episode"], select.Instance.Options!.Select(option => option.Label));
        Assert.Contains("E1", cut.Markup);
    }

    [Fact]
    public void TheVideoPlayersTitleLineIsTheVideosNumberAndTitle()
    {
        var video = QueueVideo("Videos", "3", "Ball handling");

        Assert.True(VideoPlayerWording.IsFolderVideo(video));
        Assert.Equal("3 · Ball handling", VideoPlayerWording.TitleLine(video));
        Assert.Equal("3 · Ball handling", VideoPlayerWording.TitleLine(video with { EpisodeNumber = "03" }));
        Assert.Equal("Ball handling", VideoPlayerWording.TitleLine(video with { EpisodeNumber = null }));
        Assert.False(VideoFolderWording.UsesTvWording(VideoPlayerWording.TitleLine(video)));
    }

    [Fact]
    public void ATvEpisodeIsNotAFolderVideo()
    {
        Assert.False(VideoPlayerWording.IsFolderVideo(QueueVideo("TV", "3", "The Door")));
        Assert.False(VideoPlayerWording.IsFolderVideo(QueueVideo("Movies", null, "Heat")));
        Assert.False(VideoPlayerWording.IsFolderVideo(null));
    }

    [Fact]
    public void AVideoTileHeadingIsItsNumberAndTitle_WhileATvTileKeepsItsSeasonAndEpisode()
    {
        var episode = new DisplayEpisodeContextDto(Folder, Guid.NewGuid(), Guid.NewGuid(), "Basketball course", "Ball handling", 1, 3,
            DisplayContinuationState.Unstarted, null, null);

        Assert.Equal("3 · Ball handling", MediaTileLabels.EpisodeHeading(new MediaTileViewModel { MediaKind = "Videos" }, episode));
        Assert.Equal("S1 E3 · Ball handling", MediaTileLabels.EpisodeHeading(new MediaTileViewModel { MediaKind = "TV" }, episode));
    }

    [Fact]
    public void ThePlayersQueueToolIsCalledVideosForAFolderVideoAndEpisodesForTv()
    {
        static string QueueLabel(bool folderVideo) => PlaybackControlCatalog.Build(
                PlaybackExperience.Video, PlaybackControlSurface.Fullscreen,
                new PlaybackControlState(HasQueue: true, IsTvEpisode: true, IsFolderVideo: folderVideo))
            .Single(control => control.Key == PlaybackControlKey.Queue).Label;

        Assert.Equal("Videos", QueueLabel(folderVideo: true));
        Assert.Equal("Episodes", QueueLabel(folderVideo: false));
    }

    [Fact]
    public void TheLockScreenNeverShowsASeasonForAFolderVideo()
    {
        var item = QueueVideo("Videos", "3", "Ball handling") with { SeasonNumber = "1", Album = "Basketball course" };
        var metadata = PlaybackSessionMetadata.Create(new PlaybackSessionContext(PlaybackBarMedia.Tv, item, new Uri("http://localhost/"), ShowTitle: "Basketball course"));

        Assert.NotNull(metadata);
        Assert.Equal("3 · Ball handling", metadata.Title);
        Assert.Equal("Basketball course", metadata.Artist);
        Assert.DoesNotContain("S1", metadata.Artist, StringComparison.Ordinal);
    }

    private IRenderedComponent<SequencePlacementPanel> RenderPanel(SequencePlacementViewModel placement) =>
        Render<SequencePlacementPanel>(p => p.Add(c => c.Placement, placement).Add(c => c.DetailSurface, true));

    private static ListenQueueItem QueueVideo(string mediaType, string? number, string title) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = mediaType,
        Title = title,
        EpisodeTitle = title,
        EpisodeNumber = number,
        SeasonNumber = number is null ? null : "1",
    };

    private static SequencePlacementViewModel VideoFolderPlacement(bool singleSection = false)
    {
        var weekOne = new[] { Video("1", "section-week-1", "Week 1 – Basics", "1", "Ball handling"), Video("2", "section-week-1", "Week 1 – Basics", "2", "Footwork") };
        var weekTwo = new[] { Video("3", "section-week-2", "Week 2 – Game play", "3", "Passing"), Video("4", "section-week-2", "Week 2 – Game play", "4", "Shooting") };
        var all = new[] { Video("1", "section-all", "Videos", "1", "Ball handling"), Video("2", "section-all", "Videos", "2", "Passing") };
        return new SequencePlacementViewModel
        {
            ContainerId = Folder.ToString("D"),
            SelectedContainerId = Folder.ToString("D"),
            ContainerTitle = "Basketball course",
            ContainerLabel = VideoFolderWording.ContainerLabel,
            ItemLabel = VideoFolderWording.ItemLabel,
            ItemPluralLabel = VideoFolderWording.UnnamedSectionTitle,
            GroupLabel = VideoFolderWording.SectionLabel,
            CurrentGroupKey = singleSection ? "section-all" : "section-week-1",
            CurrentItem = singleSection ? all[0] : weekOne[0],
            Groups = singleSection
                ? [Group("section-all", "Videos", all)]
                : [Group("section-week-1", "Week 1 – Basics", weekOne), Group("section-week-2", "Week 2 – Game play", weekTwo)],
        };
    }

    private static SequencePlacementViewModel TvPlacement()
    {
        var seasonOne = new[] { Episode("11", "season-1", "Season 1", "1", "First season opener") };
        var seasonTwo = new[] { Episode("21", "season-2", "Season 2", "1", "Second season opener") };
        return new SequencePlacementViewModel
        {
            ContainerId = Folder.ToString("D"),
            SelectedContainerId = Folder.ToString("D"),
            ContainerTitle = "Northwatch",
            ContainerLabel = "Show",
            ItemLabel = "Episode",
            ItemPluralLabel = "Episodes",
            GroupLabel = "Season",
            CurrentGroupKey = "season-1",
            CurrentItem = seasonOne[0],
            Groups = [Group("season-1", "Season 1", seasonOne), Group("season-2", "Season 2", seasonTwo)],
        };
    }

    private static SequenceGroupViewModel Group(string key, string title, SequenceItemViewModel[] items) => new()
    {
        Key = key,
        Title = title,
        OwnedCount = items.Length,
        ItemCount = items.Length,
        TotalKnownItems = items.Length,
        Items = items,
    };

    // The Engine gives a video in a folder the same shape as an episode: a position, and an episode identity with a season and episode number.
    private static SequenceItemViewModel Video(string id, string groupKey, string groupTitle, string number, string title) => Episode(id, groupKey, groupTitle, number, title);

    private static SequenceItemViewModel Episode(string id, string groupKey, string groupTitle, string number, string title) => new()
    {
        Id = $"6{id}000000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = groupKey,
        GroupTitle = groupTitle,
        PositionLabel = number,
        PositionNumber = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
        PositionSort = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = id is "1" or "11",
        EpisodeContext = new DisplayEpisodeContextDto(Folder, Guid.NewGuid(), Guid.NewGuid(), "Course", title, 1,
            int.Parse(number, System.Globalization.CultureInfo.InvariantCulture), DisplayContinuationState.Unstarted, null, null),
    };
}
