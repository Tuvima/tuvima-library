using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A folder of Other videos (a course, a saved channel) is a plain list of videos with named sections: nothing a view model builds for it
/// may say Season, Episode, Show or "S1 E1" (item 17, details page spec v4.1). A TV show keeps its own wording. The Home hero never presents a
/// video, or a kind it does not know, as something to read (D18, local check item 138).
/// </summary>
public sealed class VideoFolderWordingGuardrailTests
{
    private static readonly string[] VideoKinds = ["Videos", "Video", "Other videos", "other_videos", "YouTube", "videos"];
    private static readonly string[] UnknownKinds = ["", "Podcast", "Widget", "unknown"];

    private static void AssertNoTvWording(string? text, string where) =>
        Assert.False(VideoFolderWording.UsesTvWording(text), $"{where} says \"{text}\", which uses a TV word in a folder of videos.");

    private static DiscoveryHeroViewModel Hero(string kind, string label, DisplayContinuationState state = DisplayContinuationState.Unstarted, params string[] pills) => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Blazor From Start to Finish",
        MediaKind = kind,
        PrimaryActionLabel = label,
        PrimaryNavigationUrl = "/x",
        ContinuationState = state,
        ProgressPct = state == DisplayContinuationState.InProgress ? 12 : null,
        RemainingSeconds = state == DisplayContinuationState.InProgress ? 1380 : null,
        MetaPills = pills,
    };

    [Fact]
    public void TheWordingHelper_DecidesAFolderFromTheEnginesMediaType_AndCountsInVideoWords()
    {
        Assert.True(VideoFolderWording.IsVideoFolderMediaType("Videos"));
        Assert.True(VideoFolderWording.IsVideoFolderMediaType("videos"));
        Assert.False(VideoFolderWording.IsVideoFolderMediaType("Video"));
        Assert.False(VideoFolderWording.IsVideoFolderMediaType("TV"));
        Assert.False(VideoFolderWording.IsVideoFolderMediaType(null));

        Assert.Equal("88 videos", VideoFolderWording.VideoCount(88));
        Assert.Equal("1 video", VideoFolderWording.VideoCount(1));
        Assert.Equal("14 sections", VideoFolderWording.SectionCount(14));
        Assert.Equal("1 section", VideoFolderWording.SectionCount(1));
        Assert.Null(VideoFolderWording.SectionsLine(1));
        Assert.Equal("14 sections", VideoFolderWording.SectionsLine(14));
        Assert.Equal("14 sections", VideoFolderWording.FromTvCount("14 seasons"));
        Assert.Equal("88 videos", VideoFolderWording.FromTvCount("88 episodes"));
        Assert.Equal("21 min", VideoFolderWording.FromTvCount("21 min"));
        Assert.Equal("Play", VideoFolderWording.PlayButton(started: false));
        Assert.Equal("Resume", VideoFolderWording.PlayButton(started: true));
    }

    [Theory]
    [InlineData("14 seasons", true)]
    [InlineData("88 Episodes", true)]
    [InlineData("Open Show", true)]
    [InlineData("Watch S1 E1", true)]
    [InlineData("S01E02", true)]
    [InlineData("Seasons and episodes", true)]
    [InlineData("14 sections", false)]
    [InlineData("88 videos", false)]
    [InlineData("Open folder", false)]
    [InlineData("Resume", false)]
    [InlineData("Seasoned cooking", false)]
    [InlineData("Shows", false)]
    [InlineData("", false)]
    public void TheWordingCheck_FindsTvWordsAndPositionsAndNothingElse(string text, bool expected) =>
        Assert.Equal(expected, VideoFolderWording.UsesTvWording(text));

    [Fact]
    public void AVideoFolderHero_NeverProducesSeasonOrEpisodeWording()
    {
        var pills = new[] { "2024", "21 min", "14 sections", "88 videos" };
        foreach (var state in new[] { DisplayContinuationState.Unstarted, DisplayContinuationState.InProgress })
        {
            var open = Hero("Videos", "Open folder", state, pills);
            AssertNoTvWording(PhoneHeroPresentation.MetaLine(open), "the phone hero's meta line");
            Assert.Equal("2024 · 21m · 14 sections · 88 videos", PhoneHeroPresentation.MetaLine(open));
            AssertNoTvWording(PhoneHeroPresentation.PrimaryLabel(open).Full, "the folder's hero button");

            var play = Hero("Videos", "Watch", state, pills);
            var label = PhoneHeroPresentation.PrimaryLabel(play);
            AssertNoTvWording(label.Full, "a video's hero button");
            Assert.Equal(state == DisplayContinuationState.InProgress ? "Resume" : "Play", label.Text);
        }
    }

    [Fact]
    public void AVideoFolderTile_CountsVideos_NotEpisodes_AndGoesToTheFolder()
    {
        var folder = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Blazor From Start to Finish",
            MediaKind = "Videos",
            Presentation = MediaTilePresentation.TvSeries,
            Subject = DisplaySubjectKind.TvShow,
            IsCollection = true,
            PreviewTotalCount = 88,
        };

        Assert.Equal("88 videos", MediaTileLabels.CountText(folder));
        AssertNoTvWording(MediaTileLabels.Bottom(folder, "Kept"), "the tile's bottom label");
        Assert.Equal("folder", MediaTileActionTarget.GoToKind(folder));

        // A TV show keeps its own words.
        var show = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Le Continental",
            MediaKind = "TV",
            Presentation = MediaTilePresentation.TvSeries,
            Subject = DisplaySubjectKind.TvShow,
            IsCollection = true,
            PreviewTotalCount = 88,
        };
        Assert.Equal("88 episodes", MediaTileLabels.CountText(show));
        Assert.Equal("show", MediaTileActionTarget.GoToKind(show));
    }

    [Fact]
    public void TheEditor_SaysFolderAndVideosForAVideoFolder_AndOffersNoMatchTab()
    {
        Assert.Equal("Video folder", EditorHeaderRules.KindLabel("Videos", "series"));
        Assert.Equal("TV show", EditorHeaderRules.KindLabel("TV", "series"));
        Assert.False(MediaEditorMatchRule.CanMatch("Videos", "series"));

        // Quick Facts of a folder: videos and their length, never seasons and episodes.
        var totals = new ItemQuickFactsDto { DirectOwned = 88, DirectTotal = 88, LeafOwned = 88, LeafTotal = 88 };
        var kind = EditorQuickFactsBuilder.KindOf("Videos", "series", null, totals);
        Assert.Equal(QuickFactsKind.VideoSeries, kind);
        var facts = EditorQuickFactsBuilder.Build(kind, _ => null, totals, fileWriteOff: false, mediaType: "Videos");
        Assert.NotEmpty(facts.Facts);
        foreach (var fact in facts.Facts)
        {
            AssertNoTvWording(fact.Label, "a Quick Facts label");
            AssertNoTvWording(fact.Value, "a Quick Facts value");
        }

        AssertNoTvWording(VideoFolderWording.ContentsHeading, "the Contents heading");
        AssertNoTvWording(VideoFolderWording.KindLabel, "the kind line");
        AssertNoTvWording(VideoFolderWording.OpenLabel, "the open button");
    }

    [Fact]
    public void DeletingAVideoFolder_NamesAFolder_AndATvShowKeepsItsOwnWords()
    {
        var folder = EditorDeleteRules.GroupFor(Guid.NewGuid(), "Blazor", "series", "Videos");
        Assert.NotNull(folder);
        Assert.Equal("Delete folder…", EditorDeleteRules.MenuLabel(folder, "series"));
        Assert.Equal("Delete show…", EditorDeleteRules.MenuLabel(EditorDeleteRules.GroupFor(Guid.NewGuid(), "Le Continental", "series", "TV"), "series"));
        Assert.Equal("Delete Series…", EditorDeleteRules.MenuLabel(EditorDeleteRules.GroupFor(Guid.NewGuid(), "Dune Novels", "series", "Books"), "series"));
    }

    [Theory]
    [InlineData("Video", false)]
    [InlineData("Videos", false)]
    [InlineData("Other videos", false)]
    [InlineData("other_videos", false)]
    [InlineData("YouTube", false)]
    [InlineData("Video", true)]
    [InlineData("Other videos", true)]
    [InlineData("YouTube", true)]
    public void AVideoOnTheHomeHero_SaysPlayOrResume_NeverReading(string kind, bool started)
    {
        var state = started ? DisplayContinuationState.InProgress : DisplayContinuationState.Unstarted;
        var label = PhoneHeroPresentation.PrimaryLabel(Hero(kind, started ? "Continue Reading" : "Read", state));
        Assert.Equal(started ? "Resume" : "Play", label.Text);
        Assert.DoesNotContain("Read", label.Full, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(started ? "23 min left" : null, label.Detail);
        Assert.Equal("Video", PhoneHeroPresentation.PlaceholderLabel(kind));
    }

    [Fact]
    public void EveryVideoKind_IsRecognisedAsAVideo_AndNoOtherKindIs()
    {
        foreach (var kind in VideoKinds)
        {
            Assert.True(PhoneHeroPresentation.IsVideoKind(kind), kind);
        }

        foreach (var kind in new[] { "Book", "Comic", "Audiobook", "Music", "Movie", "TV", "" })
        {
            Assert.False(PhoneHeroPresentation.IsVideoKind(kind), kind);
        }
    }

    [Fact]
    public void AKindTheHeroDoesNotKnow_NeverFallsBackToReading()
    {
        foreach (var kind in UnknownKinds)
        {
            // Whatever the Engine's own label was, an unknown kind keeps it and is never turned into a reading button or a Book placeholder.
            var started = PhoneHeroPresentation.PrimaryLabel(Hero(kind, "Continue", DisplayContinuationState.InProgress));
            Assert.Equal("Continue", started.Text);
            Assert.DoesNotContain("Read", started.Full, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("Book", PhoneHeroPresentation.PlaceholderLabel(kind));
        }
    }

    [Fact]
    public void ABook_StillSaysReading()
    {
        Assert.Equal("Continue Reading", PhoneHeroPresentation.PrimaryLabel(Hero("Book", "Continue Reading", DisplayContinuationState.InProgress)).Text);
        Assert.Equal("Start Reading", PhoneHeroPresentation.PrimaryLabel(Hero("Book", "Read")).Text);
        Assert.Equal("Book", PhoneHeroPresentation.PlaceholderLabel("Book"));
    }
}
