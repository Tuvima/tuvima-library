using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage.Playback;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class PlaybackActivityProbeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-probe-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly PlaybackTelemetryRepository _repository;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly PlaybackActivityProbe _probe;

    public PlaybackActivityProbeTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new PlaybackTelemetryRepository(_database, new ApplicationEventOutboxWriter());
        _probe = new PlaybackActivityProbe(_database, _clock);
    }

    [Fact]
    public async Task NoSessions_NobodyIsPlaying()
    {
        Assert.False(await _probe.IsAnyonePlayingAsync());
    }

    [Fact]
    public async Task RecentPlayingSession_CountsAsPlaying()
    {
        var identity = Seed();
        await _repository.ObserveAsync(Observation(identity, Guid.NewGuid(), _clock.GetUtcNow().AddSeconds(-10), PlaybackTelemetryStates.Playing));

        Assert.True(await _probe.IsAnyonePlayingAsync());
    }

    [Fact]
    public async Task PausedSession_DoesNotCount()
    {
        var identity = Seed();
        await _repository.ObserveAsync(Observation(identity, Guid.NewGuid(), _clock.GetUtcNow().AddSeconds(-5), PlaybackTelemetryStates.Paused));

        Assert.False(await _probe.IsAnyonePlayingAsync());
    }

    [Fact]
    public async Task SilentSession_IsTreatedAsGoneAfterTheFreshnessWindow()
    {
        var identity = Seed();
        await _repository.ObserveAsync(Observation(identity, Guid.NewGuid(), _clock.GetUtcNow().AddSeconds(-10), PlaybackTelemetryStates.Playing));
        Assert.True(await _probe.IsAnyonePlayingAsync());

        _clock.Advance(PlaybackActivityProbe.FreshnessWindow + TimeSpan.FromSeconds(30));

        Assert.False(await _probe.IsAnyonePlayingAsync());
    }

    [Fact]
    public async Task EndedSession_DoesNotCount()
    {
        var identity = Seed();
        var session = Guid.NewGuid();
        await _repository.ObserveAsync(Observation(identity, session, _clock.GetUtcNow().AddSeconds(-20), PlaybackTelemetryStates.Playing, sequence: 1));
        await _repository.ObserveAsync(Observation(identity, session, _clock.GetUtcNow().AddSeconds(-5), PlaybackTelemetryStates.Stopped, sequence: 2));

        Assert.False(await _probe.IsAnyonePlayingAsync());
    }

    private static PlaybackTelemetryObservation Observation(
        Identity value, Guid session, DateTimeOffset at, string state, bool ended = false, long? sequence = null) => new(
            session, value.AccountId, value.ProfileId, null, null, Guid.NewGuid(), value.AssetId,
            value.LibraryId, AccountFeatureId.Watch, "Movies", state, at, 5, 600, sequence,
            1d, false, ended, null, new(PlaybackTelemetryDeliveryModes.DirectPlay), new("web"));

    private Identity Seed()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var library = Guid.NewGuid();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@profile,'Viewer','#000000','RestrictedProfile',@now);
            INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,profile_id)
            VALUES(@account,@email,@normalized,1,1,@now,@now,@profile);
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership)
            VALUES(@work,'Movies','standalone',0,'Owned');
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
            email = $"{account:N}@example.test",
            normalized = $"{account:N}@EXAMPLE.TEST",
            now = _clock.GetUtcNow().ToString("O"),
            hash = asset.ToString("N"),
            path = $"C:/fixtures/{asset:N}.media",
            libraryText = library.ToString("D"),
        });
        return new(account, profile, asset, library);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_path + suffix))
            {
                File.Delete(_path + suffix);
            }
        }
    }

    private sealed record Identity(Guid AccountId, Guid ProfileId, Guid AssetId, Guid LibraryId);

    private sealed class ClockStub(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
