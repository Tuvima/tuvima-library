using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The Chapters panel (D20, mockup screen 10) and the Episodes panel (D19, mockup screen 9).</summary>
public sealed class ChaptersAndEpisodesPanelTests : AsyncBunitContext
{
    private const double ChapterSeconds = 1840;

    public ChaptersAndEpisodesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    // ---- Chapters ----

    // Twenty-four chapters of 30:40 each; the listener is 12:08 into chapter 8, so 18:32 remain in it and about 30% of the book is done.
    private static ListenPlaybackSnapshot Book()
    {
        var asset = Guid.NewGuid();
        var chapters = Enumerable.Range(0, 24)
            .Select(index => new PlaybackChapterDto { Index = index, AssetId = asset, Title = $"Chapter title {index + 1}", StartSeconds = index * ChapterSeconds, EndSeconds = (index + 1) * ChapterSeconds })
            .ToList();
        var book = new ListenQueueItem
        {
            WorkId = Guid.NewGuid(),
            AssetId = asset,
            MediaType = "Audiobook",
            Title = "The Way Beyond",
            AuthorName = "Maya Ellis",
            Chapters = chapters,
        };
        return new ListenPlaybackSnapshot
        {
            ProfileId = Guid.NewGuid(),
            Queue = [book],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 7 * ChapterSeconds + 12 * 60 + 8,
            DurationSeconds = 24 * ChapterSeconds,
            IsPlaying = true,
        };
    }

    [Fact]
    public void Chapters_DrawTheBookTheChapterCountAndTheShareOfTheBookDone()
    {
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, Book()).Add(x => x.Commands, new RecordingPlaybackSink()));

        var identity = cut.Find("[data-chapters-identity]");
        Assert.Equal("The Way Beyond", identity.QuerySelector(".chp__book-title")!.TextContent);
        Assert.Equal("Maya Ellis", identity.QuerySelector(".chp__book-author")!.TextContent);
        var position = cut.Find("[data-chapters-position]");
        Assert.Equal("Chapter 8 of 24", position.Children[0].TextContent);
        Assert.Equal("Book · 30% complete", position.Children[1].TextContent);
    }

    [Fact]
    public void Chapters_TheCurrentRowShowsElapsedAndRemainingTimeAProgressLineAndTheSoundBars()
    {
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, Book()).Add(x => x.Commands, new RecordingPlaybackSink()));

        var current = cut.Find("[data-playback-list-row='chapter'][aria-current='true']");
        Assert.Equal("Chapter title 8", current.QuerySelector(".plr__title")!.TextContent);
        Assert.Equal("12:08 elapsed · 18:32 remaining", current.QuerySelector(".plr__secondary")!.TextContent.Trim());
        Assert.Equal("30:40", current.QuerySelector(".plr__length")!.TextContent);
        Assert.Contains("--plr-progress:39.5", current.QuerySelector(".plr__progress")!.GetAttribute("style"));
        Assert.NotNull(current.QuerySelector(".playback-activity-mark"));
        Assert.Equal("8", current.QuerySelector(".plr__lead")!.TextContent.Trim());
    }

    [Fact]
    public void Chapters_FinishedChaptersCarryACheckAndLaterOnesDoNot()
    {
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, Book()).Add(x => x.Commands, new RecordingPlaybackSink()));

        var rows = cut.FindAll("[data-playback-list-row='chapter']");
        Assert.Equal(24, rows.Count);
        Assert.Equal(7, rows.Count(row => row.QuerySelector("[data-chapter-done]") is not null));
        Assert.All(rows.Take(7), row => Assert.NotNull(row.QuerySelector("[data-chapter-done]")));
        Assert.All(rows.Skip(8), row => Assert.Null(row.QuerySelector("[data-chapter-done], .playback-activity-mark")));
    }

    [Fact]
    public async Task Chapters_SelectingARowPlaysThatChapter()
    {
        var sink = new RecordingPlaybackSink();
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, Book()).Add(x => x.Commands, sink));

        await cut.Find("button[aria-label='Play Chapter title 3']").ClickAsync();

        var sent = Assert.Single(sink.Commands);
        Assert.Equal(ListenPlaybackCommandActions.PlayChapter, sent.Command.Action);
        Assert.Equal(2, sent.Command.ChapterIndex);
    }

    [Fact]
    public async Task Chapters_TheFooterChoiceIsRememberedPerProfileAndTheBarReadsTheSameValue()
    {
        var snapshot = Book();
        var playback = new PlaybackSessionController(null!, null!, preferences: new ProfilePreferences(snapshot.ProfileId!.Value));
        Services.AddSingleton(playback);
        Services.AddSingleton(provider => new PlaybackTimelineScope(provider.GetRequiredService<IJSRuntime>(), playback));
        JSInterop.Setup<string?>("localStorage.getItem", PlaybackTimelineScope.StorageKey(snapshot.ProfileId)).SetResult(null);
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingPlaybackSink()));
        var scope = Services.GetRequiredService<PlaybackTimelineScope>();
        Assert.Equal("Timeline shows", cut.Find(".chp__footer-label").TextContent);
        Assert.Equal("Select a chapter to start listening.", cut.Find(".chp__footer-hint").TextContent);
        Assert.Equal(new[] { "Chapter", "Whole book" }, cut.FindAll(".app-segmented-control__item").Select(item => item.TextContent.Trim()));
        Assert.Equal("true", cut.FindAll(".app-segmented-control__item")[0].GetAttribute("aria-pressed"));

        await cut.FindAll(".app-segmented-control__item")[1].ClickAsync();

        Assert.True(scope.WholeBook);
        Assert.Equal("true", cut.FindAll(".app-segmented-control__item")[1].GetAttribute("aria-pressed"));
        var write = Assert.Single(JSInterop.Invocations["localStorage.setItem"]);
        Assert.Equal(new object?[] { PlaybackTimelineScope.StorageKey(snapshot.ProfileId), "book" }, write.Arguments);
    }

    [Fact]
    public void Chapters_NoFooterWithoutTheScopeOrWhenAskedToLeaveItOut()
    {
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, Book()).Add(x => x.Commands, new RecordingPlaybackSink()).Add(x => x.ShowFooter, false));

        Assert.Empty(cut.FindAll("[data-chapters-footer]"));
    }

    [Fact]
    public void Chapters_APositionTickRedrawsOnlyThePlayingRowNotTheRestOfAHundredsOfChapters()
    {
        var asset = Guid.NewGuid();
        var chapters = Enumerable.Range(0, 200)
            .Select(index => new PlaybackChapterDto { Index = index, AssetId = asset, Title = $"Chapter title {index + 1}", StartSeconds = index * 600d, EndSeconds = (index + 1) * 600d })
            .ToList();
        var snapshot = Book() with { DurationSeconds = 200 * 600d, CurrentTimeSeconds = 100 * 600d + 30 };
        snapshot = snapshot with { Queue = [snapshot.Queue[0] with { AssetId = asset, Chapters = chapters }] };
        var cut = Render<ChaptersPanel>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingPlaybackSink()));
        var rows = cut.FindComponents<ChaptersPanelRow>();
        Assert.Equal(200, rows.Count);
        var before = rows.Select(row => row.RenderCount).ToList();

        cut.Render(p => p.Add(x => x.Snapshot, snapshot with { CurrentTimeSeconds = snapshot.CurrentTimeSeconds + 1 }));

        var redrawn = cut.FindComponents<ChaptersPanelRow>().Where((row, index) => row.RenderCount != before[index]).ToList();
        Assert.True(redrawn.Count <= 2, $"A one second tick redrew {redrawn.Count} of 200 chapter rows.");

        // Moving into the next chapter still moves the playing mark and the check, and redraws only the rows that changed.
        var beforeMove = cut.FindComponents<ChaptersPanelRow>().Select(row => row.RenderCount).ToList();
        cut.Render(p => p.Add(x => x.Snapshot, snapshot with { CurrentTimeSeconds = 101 * 600d + 5 }));
        Assert.Equal("true", cut.FindAll("[data-playback-list-row='chapter']")[101].GetAttribute("aria-current"));
        var movedRows = cut.FindComponents<ChaptersPanelRow>().Where((row, index) => row.RenderCount != beforeMove[index]).Count();
        Assert.True(movedRows <= 3, $"Moving to the next chapter redrew {movedRows} rows.");
    }

    [Fact]
    public async Task TimelineScope_AChoiceMadeWhileTheStoredValueIsBeingReadWinsOverIt()
    {
        var storage = new GatedStorageJs { Stored = "chapter" };
        var scope = new PlaybackTimelineScope(storage, new PlaybackSessionController(null!, null!, preferences: new ProfilePreferences(Guid.NewGuid())));

        var loading = scope.EnsureLoadedAsync();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.SetWholeBookAsync(true);
        storage.Release.TrySetResult();
        await loading;

        Assert.True(scope.WholeBook);
    }

    [Fact]
    public async Task TimelineScope_AProfileSwitchDuringTheReadReloadsForTheNewProfile()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var preferences = new SwitchableProfilePreferences(first);
        var storage = new GatedStorageJs();
        storage.ByKey[PlaybackTimelineScope.StorageKey(first)] = "chapter";
        storage.ByKey[PlaybackTimelineScope.StorageKey(second)] = "book";
        var scope = new PlaybackTimelineScope(storage, new PlaybackSessionController(null!, null!, preferences: preferences));

        var loading = scope.EnsureLoadedAsync();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        preferences.Profile = second;
        storage.Release.TrySetResult();
        await loading;

        Assert.True(scope.WholeBook);
    }

    [Fact]
    public async Task TimelineScope_IsRememberedForEachProfileSeparately()
    {
        var storage = new MemoryStorageJs();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var scopeOne = new PlaybackTimelineScope(storage, new PlaybackSessionController(null!, null!, preferences: new ProfilePreferences(first)));
        await scopeOne.EnsureLoadedAsync();
        Assert.False(scopeOne.WholeBook);

        await scopeOne.SetWholeBookAsync(true);

        Assert.Equal("book", storage.Items[PlaybackTimelineScope.StorageKey(first)]);
        var scopeTwo = new PlaybackTimelineScope(storage, new PlaybackSessionController(null!, null!, preferences: new ProfilePreferences(second)));
        await scopeTwo.EnsureLoadedAsync();
        Assert.False(scopeTwo.WholeBook);
        var scopeAgain = new PlaybackTimelineScope(storage, new PlaybackSessionController(null!, null!, preferences: new ProfilePreferences(first)));
        await scopeAgain.EnsureLoadedAsync();
        Assert.True(scopeAgain.WholeBook);
        var changes = 0;
        scopeAgain.Changed += () => changes++;
        await scopeAgain.SetWholeBookAsync(false);
        Assert.Equal(1, changes);
        Assert.Equal("chapter", storage.Items[PlaybackTimelineScope.StorageKey(first)]);
    }

    // ---- Episodes ----

    private static VideoOwnedEpisode Episode(string season, int number, string title, double? progress = null, string? still = null) =>
        new(Guid.NewGuid(), title, still, $"Synopsis of {title}.", "22:38", season, $"Season {season}", int.Parse(season), number, ProgressPercent: progress);

    private (IReadOnlyList<VideoOwnedEpisode> Episodes, IRenderedComponent<EpisodesPanel> Cut) RenderEpisodes(
        Action<ComponentParameterCollectionBuilder<EpisodesPanel>>? configure = null)
    {
        IReadOnlyList<VideoOwnedEpisode> episodes =
        [
            Episode("1", 1, "The First Wish"), Episode("1", 2, "Second Thoughts", progress: 40), Episode("1", 3, "Third Time"),
            Episode("2", 1, "Back Again"), Episode("2", 2, "Further On"),
        ];
        var cut = Render<EpisodesPanel>(p =>
        {
            p.Add(x => x.Episodes, episodes).Add(x => x.CurrentWorkId, episodes[0].WorkId).Add(x => x.NextEpisode, episodes[1])
                .Add(x => x.Position, 143).Add(x => x.Duration, 1361).Add(x => x.IsPlaying, true).Add(x => x.AutoplayNextEpisode, true);
            configure?.Invoke(p);
        });
        return (episodes, cut);
    }

    [Fact]
    public void Episodes_IsOneListWithNoUpNextOrChaptersTabs()
    {
        var (_, cut) = RenderEpisodes();

        Assert.Empty(cut.FindAll("[role='tablist'], [role='tab']"));
        Assert.Equal("3 episodes", cut.Find("[data-episodes-count]").TextContent);
        Assert.Equal(3, cut.FindAll("[data-playback-list-row='episode']").Count);
    }

    [Fact]
    public void Episodes_RowsShowTheNumberedTitleLengthAndSynopsisOnOneLine()
    {
        var (_, cut) = RenderEpisodes();

        var row = cut.FindAll("[data-playback-list-row='episode']")[2];
        Assert.Equal("3. Third Time", row.QuerySelector(".plr__title")!.TextContent);
        Assert.Equal("22:38 · Synopsis of Third Time.", row.QuerySelector(".plr__secondary")!.TextContent.Trim());
        Assert.Equal("plr__art plr__art--still", row.QuerySelector(".plr__art")!.ClassName);
        Assert.Equal("3", row.QuerySelector(".plr__art-text")!.TextContent);
    }

    [Fact]
    public void Episodes_TheCurrentRowReadsPlayingWithTheTimeAndAProgressLine()
    {
        var (_, cut) = RenderEpisodes();

        var current = cut.Find("[data-playback-list-row='episode'][aria-current='true']");
        Assert.Equal("1. The First Wish", current.QuerySelector(".plr__title")!.TextContent);
        Assert.Equal("Playing · 2:23 of 22:41", current.QuerySelector(".plr__secondary")!.TextContent.Trim());
        Assert.Contains("--plr-progress:10.5", current.QuerySelector(".plr__progress")!.GetAttribute("style"));
    }

    [Fact]
    public void Episodes_AStartedEpisodeHasAProgressLineAndOnlyTheNextOneHasTheUpNextChip()
    {
        var (_, cut) = RenderEpisodes();

        var rows = cut.FindAll("[data-playback-list-row='episode']");
        Assert.Contains("--plr-progress:40", rows[1].QuerySelector(".plr__progress")!.GetAttribute("style"));
        Assert.Null(rows[2].QuerySelector(".plr__progress"));
        Assert.Equal("Up next", rows[1].QuerySelector("[data-up-next-chip]")!.TextContent);
        Assert.Single(cut.FindAll("[data-up-next-chip]"));
    }

    [Fact]
    public async Task Episodes_ChangingTheSeasonListsThatSeasonsEpisodes()
    {
        var (_, cut) = RenderEpisodes();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("2"));

        Assert.Equal("2 episodes", cut.Find("[data-episodes-count]").TextContent);
        Assert.Equal(new[] { "1. Back Again", "2. Further On" }, cut.FindAll("[data-playback-list-row='episode'] .plr__title").Select(title => title.TextContent));
    }

    [Fact]
    public async Task Episodes_SelectingARowPlaysThatEpisode()
    {
        Guid? played = null;
        var (episodes, cut) = RenderEpisodes(p => p.Add(x => x.EpisodeSelected, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Guid>(this, id => played = id)));

        await cut.Find("button[aria-label='Play Third Time']").ClickAsync();

        Assert.Equal(episodes[2].WorkId, played);
    }

    [Fact]
    public async Task Episodes_TheAutoplaySwitchReportsTheChoiceSoTheHostCanWriteThePreference()
    {
        bool? chosen = null;
        var (_, cut) = RenderEpisodes(p => p.Add(x => x.AutoplayChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<bool>(this, value => chosen = value)));

        Assert.Equal("Autoplay next episode", cut.Find(".app-switch-row__label").TextContent);
        await cut.Find("input[aria-label='Autoplay next episode']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

        Assert.False(chosen);
    }

    [Fact]
    public void Episodes_ADifferentStillFallsBackToTheShowPictureThenTheNumberTile()
    {
        var withStill = Episode("1", 1, "Has a still", still: "/engine-image/stream/artwork/11111111-1111-1111-1111-111111111111");
        var plain = Episode("1", 2, "No still");
        var cut = Render<EpisodesPanel>(p => p.Add(x => x.Episodes, [withStill, plain]).Add(x => x.CurrentWorkId, plain.WorkId)
            .Add(x => x.FallbackArtUrl, "/engine-image/stream/entity/work/22222222-2222-2222-2222-222222222222/cover"));

        var rows = cut.FindAll("[data-playback-list-row='episode']");
        Assert.Contains("11111111-1111-1111-1111-111111111111", rows[0].QuerySelector("img")!.GetAttribute("src"));
        Assert.Contains("22222222-2222-2222-2222-222222222222", rows[1].QuerySelector("img")!.GetAttribute("src"));
        var bare = Render<EpisodesPanel>(p => p.Add(x => x.Episodes, [plain]).Add(x => x.CurrentWorkId, plain.WorkId));
        Assert.Empty(bare.FindAll("img"));
        Assert.Equal("2", bare.Find(".plr__art-text").TextContent);
    }

    [Fact]
    public void Episodes_ACourseWithoutSeasonsListsItsQueuedLessonsWithTheUpNextChipOnTheFirst()
    {
        var lesson = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Videos", Title = "Ball handling", EpisodeTitle = "Ball handling", Duration = "9:00", QueueEntryId = Guid.NewGuid() };
        var cut = Render<EpisodesPanel>(p => p.Add(x => x.Upcoming, [lesson, lesson with { QueueEntryId = Guid.NewGuid(), Title = "Shooting" }]).Add(x => x.Lessons, true));

        Assert.Equal("2 lessons", cut.Find("[data-episodes-count]").TextContent);
        Assert.Single(cut.FindAll("[data-up-next-chip]"));
        Assert.Empty(cut.FindAll("[role='tab']"));
    }

    [Fact]
    public void Episodes_AFolderOfVideosNeverUsesTvWordingAndAutoplaysTheNextVideo()
    {
        var lesson = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Videos", Title = "Ball handling", Duration = "9:00", QueueEntryId = Guid.NewGuid() };
        var cut = Render<EpisodesPanel>(p => p.Add(x => x.Upcoming, [lesson, lesson with { QueueEntryId = Guid.NewGuid(), Title = "Shooting" }]).Add(x => x.Lessons, true).Add(x => x.FolderVideos, true));

        Assert.Equal("Autoplay next video", cut.Find(".eps__autoplay").TextContent.Trim());
        // What a person reads or hears: the visible text and every accessible name.
        var words = cut.Nodes.OfType<AngleSharp.Dom.IElement>().SelectMany(root => root.QuerySelectorAll("*").Append(root))
            .Select(element => element.GetAttribute("aria-label")).Where(label => label is not null).Append(cut.Find(".playback-list-panel").TextContent);
        var spoken = string.Join(" | ", words);
        Assert.DoesNotContain("episode", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("season", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Episodes_ACourseAutoplaysTheNextLessonAndATvShowTheNextEpisode()
    {
        var lesson = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Videos", Title = "Ball handling", QueueEntryId = Guid.NewGuid() };
        var course = Render<EpisodesPanel>(p => p.Add(x => x.Upcoming, [lesson]).Add(x => x.Lessons, true));
        Assert.Equal("Autoplay next lesson", course.Find(".eps__autoplay").TextContent.Trim());

        var (_, show) = RenderEpisodes();
        Assert.Equal("Autoplay next episode", show.Find(".eps__autoplay").TextContent.Trim());
    }

    [Fact]
    public void Episodes_OneEpisodeOrOneLessonIsCountedInTheSingular()
    {
        var lesson = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Videos", Title = "Ball handling", QueueEntryId = Guid.NewGuid() };
        Assert.Equal("1 lesson", Render<EpisodesPanel>(p => p.Add(x => x.Upcoming, [lesson]).Add(x => x.Lessons, true)).Find("[data-episodes-count]").TextContent);

        IReadOnlyList<VideoOwnedEpisode> one = [Episode("1", 1, "The First Wish")];
        var show = Render<EpisodesPanel>(p => p.Add(x => x.Episodes, one).Add(x => x.CurrentWorkId, one[0].WorkId));
        Assert.Equal("1 episode", show.Find("[data-episodes-count]").TextContent);
    }

    [Theory]
    [InlineData("TV", "3", "Episodes")]
    [InlineData("Videos", null, "Lessons")]
    [InlineData("Movies", null, "Lessons")]
    public void Episodes_ThePanelIsTitledEpisodesForTvAndLessonsForCoursesAndFolderVideos(string mediaType, string? episodeNumber, string expected)
    {
        var item = new ListenQueueItem { MediaType = mediaType, EpisodeNumber = episodeNumber, Title = "Item" };
        var localizer = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<MediaEngine.Web.Resources.SharedStrings>>();

        Assert.Equal(expected, EpisodesPanel.Title(localizer, VideoPresentationResolver.IsTvEpisode(item)));
    }

    private sealed class SwitchableProfilePreferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid Profile { get; set; } = profile;
        public Guid? ActiveProfileId => Profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(Profile));
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class ProfilePreferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(profile));
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    // Holds the first browser-storage read until the test releases it, so a choice or a profile switch can land while the read is in flight.
    private sealed class GatedStorageJs : IJSRuntime
    {
        public string? Stored { get; set; }
        public Dictionary<string, string?> ByKey { get; } = [];
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "localStorage.getItem")
            {
                return default!;
            }

            var key = (string)args![0]!;
            var value = ByKey.TryGetValue(key, out var keyed) ? keyed : Stored;
            ReadStarted.TrySetResult();
            await Release.Task;
            return (TValue)(object?)value!;
        }
    }

    private sealed class MemoryStorageJs : IJSRuntime
    {
        public Dictionary<string, string?> Items { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "localStorage.setItem":
                    Items[(string)args![0]!] = (string?)args[1];
                    return ValueTask.FromResult(default(TValue)!);
                case "localStorage.getItem":
                    return ValueTask.FromResult((TValue)(object?)Items.GetValueOrDefault((string)args![0]!)!);
                default:
                    return ValueTask.FromResult(default(TValue)!);
            }
        }
    }
}
