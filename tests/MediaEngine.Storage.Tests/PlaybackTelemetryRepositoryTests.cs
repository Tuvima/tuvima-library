using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage.Playback;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class PlaybackTelemetryRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-telemetry-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly PlaybackTelemetryRepository _repository;
    private readonly ApplicationEventRepository _events;
    private readonly DateTimeOffset _start = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public PlaybackTelemetryRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new PlaybackTelemetryRepository(_database, new ApplicationEventOutboxWriter());
        _events = new ApplicationEventRepository(_database);
    }

    [Fact]
    public async Task Lifecycle_DeduplicatesSequencesSeparatesSeeksAndAtomicallyEmitsTransitions()
    {
        var identity = Seed("Movies");
        var session = Guid.NewGuid();
        var started = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start, 0, sequence: 1)));
        Assert.True(started.WasCreated);

        var duplicate = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(5), 8, sequence: 1)));
        Assert.True(duplicate.WasIgnored);

        var paused = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(10), 10, sequence: 2,
                state: PlaybackTelemetryStates.Paused)));
        Assert.Equal(10, paused.Session.PlayedDurationSeconds, 3);
        var resumed = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(11), 10, sequence: 3)));
        Assert.Equal(10, resumed.Session.PlayedDurationSeconds, 3);

        var sought = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(12), 590, sequence: 4, explicitSeek: true)));
        Assert.Equal(10, sought.Session.PlayedDurationSeconds, 3);
        var completed = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(22), 600, sequence: 5,
                duration: 600, ended: true)));
        Assert.Equal(PlaybackTelemetryStates.Completed, completed.Session.State);
        Assert.Equal(20, completed.Session.PlayedDurationSeconds, 3);

        var events = await _events.ReadAfterAsync(0, 20);
        Assert.Equal(
            ["playback.started", "playback.paused", "playback.started", "playback.completed"],
            events.Select(value => value.EventType));
        Assert.All(events, value =>
        {
            Assert.Equal(identity.LibraryId, value.Subject.LibraryId);
            Assert.Equal(identity.ProfileId, value.Subject.ProfileId);
            Assert.Equal(identity.Feature, value.Subject.FeatureId);
            Assert.Equal(completed.Session.Id.ToString("D"), value.Subject.Id);
        });
    }

    [Fact]
    public async Task NoSequenceReplay_DoesNotCreditStationaryOrOutOfOrderObservations()
    {
        var identity = Seed("Music");
        var session = Guid.NewGuid();
        await _repository.ObserveAsync(Observation(identity, session, _start, 50));
        var sameTime = Assert.Single(await _repository.ObserveAsync(Observation(identity, session, _start, 55)));
        Assert.True(sameTime.WasIgnored);
        var stationary = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(10), 50)));
        Assert.Equal(0, stationary.Session.PlayedDurationSeconds);
        var progressed = Assert.Single(await _repository.ObserveAsync(
            Observation(identity, session, _start.AddSeconds(20), 60)));
        Assert.Equal(10, progressed.Session.PlayedDurationSeconds, 3);
    }

    [Fact]
    public async Task ReadsFilterAccountProfileLibraryAndFeatureBeforePagingAndAggregation()
    {
        var first = Seed("Movies");
        var otherLibrary = Seed("Movies", first.AccountId, first.ProfileId);
        var otherProfile = Seed("Movies", first.AccountId);
        var listen = Seed("Music", first.AccountId, first.ProfileId, first.LibraryId);
        await _repository.ObserveAsync(Observation(first, Guid.NewGuid(), _start, 0));
        await _repository.ObserveAsync(Observation(otherLibrary, Guid.NewGuid(), _start.AddMinutes(1), 0));
        await _repository.ObserveAsync(Observation(otherProfile, Guid.NewGuid(), _start.AddMinutes(2), 0));
        await _repository.ObserveAsync(Observation(listen, Guid.NewGuid(), _start.AddMinutes(3), 0));

        var scope = new PlaybackTelemetryReadScope(true, false, first.AccountId, first.ProfileId,
            false, new HashSet<Guid> { first.LibraryId }, new HashSet<AccountFeatureId> { AccountFeatureId.Watch });
        var page = await _repository.GetActiveAsync(scope, 1, null);
        var only = Assert.Single(page.Items);
        Assert.Equal(first.AssetId, only.AssetId);
        Assert.Null(page.NextCursor);
        var aggregate = await _repository.GetPlaybackAggregateAsync(scope, null, null);
        Assert.Equal(1, aggregate.SessionCount);
        Assert.Equal(1, aggregate.UniqueProfiles);
        Assert.Equal(1, aggregate.UniqueAssets);
    }

    [Fact]
    public async Task StaleClosureUsesLastObservationAndRetentionNeverDeletesActiveRows()
    {
        var old = Seed("Movies");
        var active = Seed("Movies");
        await _repository.ObserveAsync(Observation(old, Guid.NewGuid(), _start, 0));
        await _repository.ObserveAsync(Observation(active, Guid.NewGuid(), _start.AddDays(2), 0));

        var closed = Assert.Single(await _repository.CloseStaleAsync(_start.AddMinutes(2)));
        Assert.Equal(PlaybackCompletionReasons.Stale, closed.Session.CompletionReason);
        Assert.Equal(_start, closed.Session.EndedAt);
        Assert.Equal(1, await _repository.DeleteHistoryBeforeAsync(_start.AddDays(1)));

        var all = new PlaybackTelemetryReadScope(true, true, null, null, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet());
        Assert.Single((await _repository.GetActiveAsync(all, 10, null)).Items);
        Assert.Empty((await _repository.GetHistoryAsync(all, null, null, 10, null)).Items);
    }

    [Fact]
    public async Task ExplicitTakeoverClosesActiveSessionAndWritesStoppedEventInSameTransaction()
    {
        var identity = Seed("Movies");
        var playerSession = Guid.NewGuid();
        await _repository.ObserveAsync(Observation(identity, playerSession, _start, 12));

        var closed = await _repository.CloseAsync(
            playerSession, _start.AddSeconds(1), PlaybackCompletionReasons.Takeover);

        Assert.NotNull(closed);
        Assert.Equal(PlaybackCompletionReasons.Takeover, closed.Session.CompletionReason);
        Assert.Equal(PlaybackTelemetryStates.Stopped, closed.Session.State);
        Assert.Equal(["playback.started", "playback.stopped"],
            (await _events.ReadAfterAsync(0, 10)).Select(value => value.EventType));
    }

    [Fact]
    public async Task InvalidFloatingPointValuesAreRejectedBeforeSqlite()
    {
        var identity = Seed("Movies");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.ObserveAsync(
            Observation(identity, Guid.NewGuid(), _start, double.NaN)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.ObserveAsync(
            Observation(identity, Guid.NewGuid(), _start, 0, duration: double.PositiveInfinity)));
    }

    private PlaybackTelemetryObservation Observation(
        SeededIdentity value,
        Guid session,
        DateTimeOffset at,
        double position,
        long? sequence = null,
        string state = PlaybackTelemetryStates.Playing,
        double? duration = null,
        bool explicitSeek = false,
        bool ended = false) => new(
            session, value.AccountId, value.ProfileId, null, null, Guid.NewGuid(), value.AssetId,
            value.LibraryId, value.Feature, value.MediaType, state, at, position, duration, sequence,
            1d, explicitSeek, ended, null, new(PlaybackTelemetryDeliveryModes.DirectPlay), new("web"));

    private SeededIdentity Seed(
        string mediaType,
        Guid? accountId = null,
        Guid? profileId = null,
        Guid? libraryId = null)
    {
        var account = accountId ?? Guid.NewGuid();
        var profile = profileId ?? Guid.NewGuid();
        var library = libraryId ?? Guid.NewGuid();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT OR IGNORE INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@profile,'Viewer','#000000','RestrictedProfile',@now);
            INSERT OR IGNORE INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at)
            VALUES(@account,@email,@normalized,1,0,1,@now,@now);
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership)
            VALUES(@work,@mediaType,'standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
            VALUES(@asset,@edition,@hash,@path,@libraryText);
            """, new
        {
            account,
            profile,
            work,
            edition,
            asset,
            mediaType,
            email = $"{account:N}@example.test",
            normalized = $"{account:N}@EXAMPLE.TEST",
            now = _start.ToString("O"),
            hash = asset.ToString("N"),
            path = $"C:/fixtures/{asset:N}.media",
            libraryText = library.ToString("D"),
        });
        var feature = mediaType == "Music" ? AccountFeatureId.Listen : AccountFeatureId.Watch;
        return new(account, profile, asset, library, feature, mediaType);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }

    private sealed record SeededIdentity(
        Guid AccountId,
        Guid ProfileId,
        Guid AssetId,
        Guid LibraryId,
        AccountFeatureId Feature,
        string MediaType);
}
