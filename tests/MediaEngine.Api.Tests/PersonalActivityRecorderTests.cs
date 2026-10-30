using Dapper;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Playback;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The person's own activity record follows playback and My list: a snapshot of the item taken from the work's
/// current values, progress that only moves forward, nothing for photos, and a failure that never reaches playback.
/// </summary>
public sealed class PersonalActivityRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePlaybackOfAMovie_StartsAnEntryWithASnapshotOfTheWorkNotTheFileName()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors", year: "1990");
        var session = Session(world.MemberA, asset, AccountFeatureId.Watch);

        await recorder.RecordPlaybackAsync([Created(session)], asset, new PersonalActivityDevice("Living room TV", "television"), default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal(PersonalActivityKinds.Watch, entry.Kind);
        Assert.Equal("Tremors", entry.Title);
        Assert.Equal(1990, entry.Year);
        Assert.Equal("Movies", entry.MediaType);
        Assert.Equal(asset.WorkId, entry.WorkId);
        Assert.Equal(asset.AssetId, entry.AssetId);
        Assert.Equal("Living room TV", entry.DeviceName);
        Assert.Equal("television", entry.DeviceKind);
        Assert.Equal(Start, entry.StartedAt);
        Assert.Equal(session.Id, entry.TelemetrySessionId);
    }

    [Fact]
    public async Task AnEpisode_IsTitledWithTheShowAndLabelledWithSeasonEpisodeAndName()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = new DisplayWorkRow
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            MediaType = "TV",
            Title = "Woe's Hollow",
            ShowName = "Severance",
            SeasonNumber = "2",
            EpisodeNumber = "4",
            Year = "2025-03-02",
        };

        await recorder.RecordPlaybackAsync([Created(Session(world.MemberA, asset, AccountFeatureId.Watch))], asset, null, default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal("Severance", entry.Title);
        Assert.Equal("S2 E4 · Woe's Hollow", entry.Subtitle);
        Assert.Equal(2025, entry.Year);
        Assert.Equal("TV", entry.MediaType);
    }

    [Fact]
    public async Task ASong_KeepsItsArtistAndAlbum_AndConsecutiveSongsFormOneSession()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        foreach (var number in Enumerable.Range(0, 3))
        {
            var song = new DisplayWorkRow
            {
                WorkId = Guid.NewGuid(),
                AssetId = Guid.NewGuid(),
                MediaType = "Music",
                Title = $"Song {number}",
                Artist = "Pink Floyd",
                Album = "Wish You Were Here",
            };
            var session = Session(world.MemberA, song, AccountFeatureId.Listen, startedAt: Start.AddMinutes(number * 5));
            await recorder.RecordPlaybackAsync([Created(session)], song, null, default);
        }

        var item = Assert.Single((await world.Repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = new HashSet<Guid> { world.MemberA.ProfileId } },
            PersonalActivityGrouping.None, null, 10)).Items);
        Assert.True(item.IsMusicSession);
        Assert.Equal(3, item.Entries.Count);
        Assert.Equal("Pink Floyd", item.Entries[0].Subtitle);
        Assert.Equal("Wish You Were Here", item.Entries[0].ParentTitle);
        Assert.All(item.Entries, entry => Assert.Equal(PersonalActivityKinds.Listen, entry.Kind));
    }

    [Fact]
    public async Task ABook_IsAReadEntryWithItsAuthor()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var book = new DisplayWorkRow { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Books", Title = "Hyperion", Author = "Dan Simmons" };

        await recorder.RecordPlaybackAsync([Created(Session(world.MemberA, book, AccountFeatureId.Read))], book, null, default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal(PersonalActivityKinds.Read, entry.Kind);
        Assert.Equal("Dan Simmons", entry.Subtitle);
    }

    [Fact]
    public async Task RepeatedObservationsOfTheSameSession_KeepOneEntryAndMoveItForward()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors");
        var created = Session(world.MemberA, asset, AccountFeatureId.Watch);
        await recorder.RecordPlaybackAsync([Created(created)], asset, null, default);
        await recorder.RecordPlaybackAsync([Created(created)], asset, null, default);

        var halfway = created with
        {
            LastObservedAt = Start.AddMinutes(48),
            LastPositionSeconds = 2880,
            DurationSeconds = 5760,
            PlayedDurationSeconds = 2880,
        };
        await recorder.RecordPlaybackAsync([Updated(halfway)], asset, null, default);
        await recorder.RecordPlaybackAsync([Updated(halfway)], asset, null, default);
        var earlier = halfway with { LastObservedAt = Start.AddMinutes(10), LastPositionSeconds = 600, PlayedDurationSeconds = 600 };
        await recorder.RecordPlaybackAsync([Updated(earlier)], asset, null, default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal(2880, entry.PlayedSeconds);
        Assert.Equal(50, entry.ProgressPct);
        Assert.False(entry.Finished);
        Assert.Equal(Start.AddMinutes(48), entry.EndedAt);
    }

    [Fact]
    public async Task ClosingASessionThatCompleted_MarksItFinished()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors");
        var created = Session(world.MemberA, asset, AccountFeatureId.Watch);
        await recorder.RecordPlaybackAsync([Created(created)], asset, null, default);

        var completed = created with
        {
            State = PlaybackTelemetryStates.Completed,
            EndedAt = Start.AddMinutes(96),
            LastObservedAt = Start.AddMinutes(96),
            CompletionReason = PlaybackCompletionReasons.Completed,
            LastPositionSeconds = 5760,
            DurationSeconds = 5760,
            PlayedDurationSeconds = 5760,
        };
        await recorder.RecordClosedAsync([Updated(completed)], default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.True(entry.Finished);
        Assert.Equal(100, entry.ProgressPct);
        Assert.Equal(Start.AddMinutes(96), entry.EndedAt);
        Assert.Equal(5760, entry.PlayedSeconds);
    }

    [Theory]
    [InlineData(99.4, false)]
    [InlineData(99.5, true)]
    [InlineData(100, true)]
    public void FinishedMeansTheFurthestPointReachedNinetyNinePointFivePercent(double percent, bool finished)
    {
        var asset = Movie("Tremors");
        var session = Session(PersonAt(), asset, AccountFeatureId.Watch) with
        {
            DurationSeconds = 1000,
            LastPositionSeconds = percent * 10,
            State = PlaybackTelemetryStates.Stopped,
            EndedAt = Start.AddMinutes(1),
            CompletionReason = PlaybackCompletionReasons.Stopped,
        };

        Assert.Equal(finished, PersonalActivityRecorder.ProgressOf(session).Finished);
    }

    [Fact]
    public async Task ASessionWhoseDurationIsUnknown_HasNoProgressPercent()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Live stream");

        await recorder.RecordPlaybackAsync([Created(Session(world.MemberA, asset, AccountFeatureId.Watch))], asset, null, default);

        Assert.Null((await OnlyEntryAsync(world, world.MemberA)).ProgressPct);
    }

    [Fact]
    public async Task APhotoOrPersonalVideo_IsNeverRecorded()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var photo = new DisplayWorkRow { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Photos", Title = "Holiday" };

        await recorder.RecordPlaybackAsync([Created(Session(world.MemberA, photo, AccountFeatureId.View))], photo, null, default);

        Assert.Empty((await Items(world, world.MemberA)).Items);
    }

    [Fact]
    public async Task AnIgnoredOrOutOfOrderObservation_ChangesNothing()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors");
        var created = Session(world.MemberA, asset, AccountFeatureId.Watch);

        await recorder.RecordPlaybackAsync([new PlaybackTelemetryTransition(created, null, true, true)], asset, null, default);

        Assert.Empty((await Items(world, world.MemberA)).Items);
    }

    [Fact]
    public async Task ASessionReplacedByAnotherItem_ClosesTheFirstEntryAndStartsTheSecond()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var first = Movie("First");
        var second = Movie("Second");
        var firstSession = Session(world.MemberA, first, AccountFeatureId.Watch);
        await recorder.RecordPlaybackAsync([Created(firstSession)], first, null, default);

        var replaced = firstSession with
        {
            State = PlaybackTelemetryStates.Stopped,
            EndedAt = Start.AddMinutes(20),
            LastObservedAt = Start.AddMinutes(20),
            CompletionReason = PlaybackCompletionReasons.Replaced,
            PlayedDurationSeconds = 1200,
        };
        var secondSession = Session(world.MemberA, second, AccountFeatureId.Watch, startedAt: Start.AddMinutes(20));
        await recorder.RecordPlaybackAsync([Updated(replaced), Created(secondSession)], second, null, default);

        var entries = (await Items(world, world.MemberA)).Items.SelectMany(item => item.Entries).OrderBy(entry => entry.StartedAt).ToList();
        Assert.Equal(["First", "Second"], entries.Select(entry => entry.Title));
        Assert.Equal(1200, entries[0].PlayedSeconds);
        Assert.Equal(Start.AddMinutes(20), entries[0].EndedAt);
    }

    [Fact]
    public async Task ClosingASessionThatHasNoEntry_AddsNothing()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors");
        var closed = Session(world.MemberA, asset, AccountFeatureId.Watch) with
        {
            State = PlaybackTelemetryStates.Stopped,
            EndedAt = Start.AddMinutes(2),
            CompletionReason = PlaybackCompletionReasons.Stale,
        };

        await recorder.RecordClosedAsync([Updated(closed)], default);

        Assert.Empty((await Items(world, world.MemberA)).Items);
    }

    [Fact]
    public async Task ThePictureOfTheCover_IsKeptWithTheEntry_AndAFailureToMakeOneStillRecordsTheEntry()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var asset = Movie("Tremors");

        await recorder.RecordPlaybackAsync([Created(Session(world.MemberA, asset, AccountFeatureId.Watch))], asset, null, default);

        // No cover is stored for this item, so the entry carries no picture but is still recorded.
        Assert.Null((await OnlyEntryAsync(world, world.MemberA)).ArtworkKey);
    }

    [Fact]
    public async Task AFailureToWriteActivity_IsLoggedAndNeverReachesPlayback()
    {
        await using var world = PersonalActivityWorld.Create();
        var logger = new ListLogger<PersonalActivityRecorder>();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database, new ThrowingRepository(), logger);
        var asset = Movie("Tremors");
        var created = Session(world.MemberA, asset, AccountFeatureId.Watch);

        await recorder.RecordPlaybackAsync([Created(created), Updated(created)], asset, null, default);
        await recorder.RecordClosedAsync([Updated(created)], default);
        await recorder.RecordMyListAddedAsync(world.MemberA.ProfileId, ProfileEntityKind.Movie, asset.WorkId, _ => Task.FromResult<IReadOnlyList<DisplayWorkRow>>([asset]), default);

        Assert.Equal(4, logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException));
    }

    [Fact]
    public async Task AddingToMyList_CreatesAnEntryWithASnapshotOfTheTitle()
    {
        await using var world = PersonalActivityWorld.Create();
        var clock = new ManualActivityClock(Start);
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database, clock: clock);
        var movie = Movie("Tremors", year: "1990");

        await recorder.RecordMyListAddedAsync(world.MemberA.ProfileId, ProfileEntityKind.Movie, movie.WorkId,
            _ => Task.FromResult<IReadOnlyList<DisplayWorkRow>>([movie]), default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal(PersonalActivityKinds.MyList, entry.Kind);
        Assert.Equal("Tremors", entry.Title);
        Assert.Equal(1990, entry.Year);
        Assert.Equal(movie.WorkId, entry.WorkId);
        Assert.Equal(Start, entry.StartedAt);
        Assert.Null(entry.TelemetrySessionId);
    }

    [Fact]
    public async Task AddingAShowToMyList_IsTitledWithTheShowNotAnEpisode()
    {
        await using var world = PersonalActivityWorld.Create();
        var recorder = PersonalActivityTestKit.CreateRecorder(world.Database);
        var show = Guid.NewGuid();
        var episode = new DisplayWorkRow
        {
            WorkId = Guid.NewGuid(),
            RootWorkId = show,
            AssetId = Guid.NewGuid(),
            MediaType = "TV",
            Title = "Pilot",
            ShowName = "Severance",
        };

        await recorder.RecordMyListAddedAsync(world.MemberA.ProfileId, ProfileEntityKind.TvShow, show,
            _ => Task.FromResult<IReadOnlyList<DisplayWorkRow>>([episode]), default);

        var entry = await OnlyEntryAsync(world, world.MemberA);
        Assert.Equal("Severance", entry.Title);
        Assert.Equal(show, entry.WorkId);
    }

    // ----- Helpers -----

    private static PersonalActivityWorld.Person PersonAt() =>
        new("Someone", Guid.NewGuid(), Guid.NewGuid(), null, false, false);

    private static DisplayWorkRow Movie(string title, string? year = null) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Movies",
        Title = title,
        Year = year,
    };

    private static PlaybackTelemetrySession Session(
        PersonalActivityWorld.Person who,
        DisplayWorkRow asset,
        AccountFeatureId feature,
        DateTimeOffset? startedAt = null)
    {
        return new PlaybackTelemetrySession
        {
            Id = Guid.CreateVersion7(),
            PlayerSessionId = Guid.NewGuid(),
            AccountId = who.AccountId,
            ProfileId = who.ProfileId,
            AssetId = asset.AssetId,
            LibraryId = Guid.NewGuid(),
            Feature = feature,
            MediaType = asset.MediaType,
            State = PlaybackTelemetryStates.Playing,
            StartedAt = startedAt ?? Start,
            LastObservedAt = startedAt ?? Start,
            Client = new PlaybackClientFacts("Dashboard", "1.0"),
        };
    }

    private static PlaybackTelemetryTransition Created(PlaybackTelemetrySession session) => new(session, null, true, false);

    private static PlaybackTelemetryTransition Updated(PlaybackTelemetrySession session) =>
        new(session, PlaybackTelemetryStates.Playing, false, false);

    private static async Task<PersonalActivityItemPage> Items(PersonalActivityWorld world, PersonalActivityWorld.Person who) =>
        await world.Repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = new HashSet<Guid> { who.ProfileId } }, PersonalActivityGrouping.None, null, 50);

    private static async Task<PersonalActivityEntry> OnlyEntryAsync(PersonalActivityWorld world, PersonalActivityWorld.Person who) =>
        Assert.Single(Assert.Single((await Items(world, who)).Items).Entries);

    /// <summary>A repository that always fails, to prove that recording is best effort.</summary>
    private sealed class ThrowingRepository : IPersonalActivityRepository
    {
        public Task<bool> AddPlaybackAsync(Guid profileId, string kind, bool isMusic, Guid telemetrySessionId, DateTimeOffset startedAt,
            PersonalActivityDevice device, PersonalActivitySnapshot snapshot, PersonalActivityProgress progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("The data store is unavailable.");

        public Task UpdateProgressAsync(Guid telemetrySessionId, PersonalActivityProgress progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("The data store is unavailable.");

        public Task AddMyListAsync(Guid profileId, DateTimeOffset at, PersonalActivitySnapshot snapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException("The data store is unavailable.");

        public Task<PersonalActivityItemPage> GetItemsAsync(PersonalActivityFilter filter, PersonalActivityGrouping grouping, string? cursor, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<PersonalActivityEntry> Entries, string? NextCursor)> GetEntriesAsync(PersonalActivityFilter filter, string? cursor, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PersonalActivityPeriodCount>> GetPeriodsAsync(PersonalActivityFilter filter, PersonalActivityGrouping grouping, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> HasArtworkAsync(string key, CancellationToken ct = default) => Task.FromResult(false);

        public Task SaveArtworkAsync(PersonalActivityArtwork artwork, CancellationToken ct = default) => Task.CompletedTask;

        public Task<PersonalActivityArtwork?> GetArtworkAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<PersonalActivityArtwork?>(null);

        public Task<bool> IsArtworkUsedAsync(string key, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<int> CountBeforeAsync(DateTimeOffset before, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, IReadOnlySet<Guid>? profileIds, Func<int, AuthorizationAuditEvent> audit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PersonalActivitySummary> GetSummaryAsync(Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PersonalActivityRolling> GetRollingAsync(Guid profileId, DateTimeOffset now, int days, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PersonalFinishedMonthGenre>> GetFinishedByMonthAsync(Guid profileId, DateTimeOffset since, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
