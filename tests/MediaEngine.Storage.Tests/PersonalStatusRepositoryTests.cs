using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;
namespace MediaEngine.Storage.Tests;

public sealed class PersonalStatusRepositoryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"tv-status-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection db;
    private readonly Guid profile = Guid.NewGuid();
    public PersonalStatusRepositoryTests() { DapperConfiguration.Configure(); db = new(path); db.InitializeSchema(); }
    public void Dispose() { db.Dispose(); TestTemp.DeleteDatabase(path); }
    private (Guid work, Guid asset) Add(MediaType media, Guid? parent = null)
    {
        var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
        using var conn = db.CreateConnection();
        conn.Execute("INSERT INTO works(id,media_type,parent_work_id) VALUES(@work,@media,@parent)", new { work, media = media.ToString(), parent });
        conn.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work)", new { edition, work });
        conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@asset,@edition,@hash,@path)", new { asset, edition, hash = asset.ToString(), path = $"C:/fixtures/{asset}.{(media == MediaType.Audiobooks ? "m4b" : media == MediaType.Books ? "epub" : "mp4")}" });
        return (work, asset);
    }
    [Fact]
    public async Task CompletingOneSongDoesNotCompleteItsAlbumOrSibling()
    {
        var (album, albumAsset) = Add(MediaType.Music);
        var (song, firstAsset) = Add(MediaType.Music, album);
        var (_, secondAsset) = Add(MediaType.Music, album);
        var repo = new PersonalStatusRepository(db);
        var target = new PersonalStatusTarget(song, MediaType.Music);
        var status = await repo.ReadAsync(profile, target);
        Assert.Equal(1, status.OwnedCount);
        var result = await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, Guid.NewGuid(), status.Revision);
        Assert.Equal(1, result.AffectedCount);
        var state = new UserStateRepository(db);
        Assert.Equal(100, (await state.GetAsync(profile, firstAsset))!.ProgressPct);
        Assert.Null(await state.GetAsync(profile, secondAsset));
        Assert.Null(await state.GetAsync(profile, albumAsset));
    }

    [Fact]
    public async Task ResetAndUndo_AreAtomic_ProfileScoped_AndKeepBookmarks()
    {
        var (show, first) = Add(MediaType.TV); var (_, second) = Add(MediaType.TV, show);
        var store = new UserStateRepository(db); var repo = new PersonalStatusRepository(db);
        var initial = new UserState
        {
            UserId = profile,
            AssetId = first,
            ProgressPct = 42,
            LastAccessed = DateTimeOffset.UtcNow,
            ExtendedProperties = new() { ["position_seconds"] = "83", ["bookmarks"] = "keep" }
        };
        await store.SaveAsync(initial);
        var other = new UserState { UserId = Guid.NewGuid(), AssetId = first, ProgressPct = 75, LastAccessed = DateTimeOffset.UtcNow };
        await store.SaveAsync(other);
        var target = new PersonalStatusTarget(show, MediaType.TV);
        var snapshot = await repo.ReadAsync(profile, target);
        var result = await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Reset, Guid.NewGuid(), snapshot.Revision);
        Assert.Equal(2, result.AffectedCount);
        var reset = (await store.GetAsync(profile, first))!;
        Assert.Equal(0, reset.ProgressPct); Assert.Equal("keep", reset.ExtendedProperties["bookmarks"]);
        Assert.False(reset.ExtendedProperties.ContainsKey("position_seconds"));
        Assert.Equal(75, (await store.GetAsync(other.UserId, first))!.ProgressPct);
        await Assert.ThrowsAsync<StateRevisionConflictException>(() => store.SaveAsync(initial));
        await repo.UndoAsync(profile, result.CommandId);
        Assert.Equal(42, (await store.GetAsync(profile, first))!.ProgressPct);
        Assert.Equal("83", (await store.GetAsync(profile, first))!.ExtendedProperties["position_seconds"]);
        Assert.Single(await repo.HistoryAsync(profile, target));
    }
    [Fact]
    public async Task CombinedFile_StatusIsOneStatePerFile_AndReachesEveryCoveredEpisode()
    {
        var show = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO works(id,media_type) VALUES(@show,'TV')", new { show });
        }
        var (first, file) = Add(MediaType.TV, show);
        var (_, other) = Add(MediaType.TV, show);
        var covered = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO works(id,media_type,parent_work_id) VALUES(@covered,'TV',@show)", new { covered, show });
            conn.Execute("INSERT INTO media_asset_coverage(asset_id,work_id,position,source) VALUES(@file,@first,1,'filename'),(@file,@covered,2,'filename')",
                new { file, first, covered });
        }

        var repo = new PersonalStatusRepository(db);
        var store = new UserStateRepository(db);
        var showTarget = new PersonalStatusTarget(show, MediaType.TV);
        var before = await repo.ReadAsync(profile, showTarget);
        Assert.Equal(3, before.OwnedCount);

        var complete = await repo.ExecuteAsync(profile, showTarget, PersonalStatusCommand.Complete, Guid.NewGuid(), before.Revision);
        Assert.Equal(3, complete.AffectedCount);
        Assert.Equal(100, (await store.GetAsync(profile, file))!.ProgressPct);
        Assert.Equal(100, (await store.GetAsync(profile, other))!.ProgressPct);
        var after = await repo.ReadAsync(profile, showTarget);
        Assert.Equal(3, after.CompletedCount);

        // Resetting only the covered episode resets the file once, which resets its sibling too.
        var episodeTarget = new PersonalStatusTarget(covered, MediaType.TV);
        var episode = await repo.ReadAsync(profile, episodeTarget);
        Assert.Equal(1, episode.OwnedCount);
        var reset = await repo.ExecuteAsync(profile, episodeTarget, PersonalStatusCommand.Reset, Guid.NewGuid(), episode.Revision);
        Assert.Equal(1, reset.AffectedCount);
        Assert.Equal(0, (await store.GetAsync(profile, file))!.ProgressPct);
        Assert.Equal(100, (await store.GetAsync(profile, other))!.ProgressPct);
        Assert.Equal(1, (await repo.ReadAsync(profile, showTarget)).CompletedCount);
    }

    [Fact]
    public async Task RepeatCommandIsIdempotent_UndoCannotOverwriteNewPlayback()
    {
        var (work, asset) = Add(MediaType.Movies); var repo = new PersonalStatusRepository(db); var store = new UserStateRepository(db);
        var target = new PersonalStatusTarget(work, MediaType.Movies); var before = await repo.ReadAsync(profile, target); var id = Guid.NewGuid();
        var first = await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, id, before.Revision);
        Assert.Equal(first, await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, id, before.Revision));
        var playing = (await store.GetAsync(profile, asset))!; playing.ProgressPct = 5; await store.SaveAsync(playing);
        await Assert.ThrowsAsync<StateRevisionConflictException>(() => repo.UndoAsync(profile, id));
        Assert.Equal(5, (await store.GetAsync(profile, asset))!.ProgressPct);
    }

    [Fact]
    public async Task ScopeFiltersCountsAndWrites_AndRevocationBlocksReplayUndoAndHistory()
    {
        var (show, allowed) = Add(MediaType.TV);
        var (_, denied) = Add(MediaType.TV, show);
        var repository = new PersonalStatusRepository(db);
        var store = new UserStateRepository(db);
        var target = new PersonalStatusTarget(show, MediaType.TV) { AuthorizedAssetIds = new HashSet<Guid> { allowed } };
        var before = await repository.ReadAsync(profile, target);
        Assert.Equal(1, before.OwnedCount);
        var command = Guid.NewGuid();
        var result = await repository.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, command, before.Revision);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(100, (await store.GetAsync(profile, allowed))!.ProgressPct);
        Assert.Null(await store.GetAsync(profile, denied));
        var revoked = target with { AuthorizedAssetIds = new HashSet<Guid>() };
        Assert.Equal(0, (await repository.ReadAsync(profile, revoked)).OwnedCount);
        Assert.Empty(await repository.HistoryAsync(profile, revoked));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ExecuteAsync(profile, revoked, PersonalStatusCommand.Complete, command, before.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UndoAsync(profile, command, default, revoked.AuthorizedAssetIds));
        Assert.Equal(100, (await store.GetAsync(profile, allowed))!.ProgressPct);
        await repository.UndoAsync(profile, command, default, target.AuthorizedAssetIds);
        Assert.Equal(0, (await store.GetAsync(profile, allowed))!.ProgressPct);
    }

    [Fact]
    public async Task RecentFiltersBeforeLimit()
    {
        var (_, allowed) = Add(MediaType.TV);
        var (_, denied) = Add(MediaType.TV);
        var store = new UserStateRepository(db);
        await store.SaveAsync(new UserState { UserId = profile, AssetId = allowed, ProgressPct = 20, LastAccessed = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await store.SaveAsync(new UserState { UserId = profile, AssetId = denied, ProgressPct = 40, LastAccessed = DateTimeOffset.UtcNow });
        Assert.Equal(allowed, Assert.Single(await store.GetRecentAuthorizedAsync(profile, new HashSet<Guid> { allowed }, 1)).AssetId);
        Assert.Empty(await store.GetRecentAuthorizedAsync(profile, new HashSet<Guid>(), 1));
    }
    [Theory]
    [InlineData(MediaType.Books)]
    [InlineData(MediaType.Comics)]
    [InlineData(MediaType.Audiobooks)]
    [InlineData(MediaType.Music)]
    public async Task StatusSupportsEachExperience_WithoutFakeConsumption(MediaType media)
    {
        var (work, asset) = Add(media); var repo = new PersonalStatusRepository(db); var target = new PersonalStatusTarget(work, media);
        var status = await repo.ReadAsync(profile, target);
        await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, Guid.NewGuid(), status.Revision);
        var state = (await new UserStateRepository(db).GetAsync(profile, asset))!;
        Assert.Equal(100, state.ProgressPct); Assert.Equal(DateTimeOffset.UnixEpoch, state.LastAccessed);
        using var conn = db.CreateConnection(); Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM player_sessions"));
    }
    [Fact]
    public async Task HidePreservesProgress_AndSurvivesAutosave()
    {
        var (work, asset) = Add(MediaType.TV); var repo = new PersonalStatusRepository(db); var target = new PersonalStatusTarget(work, MediaType.TV);
        var status = await repo.ReadAsync(profile, target);
        await repo.ExecuteAsync(profile, target, PersonalStatusCommand.HideContinue, Guid.NewGuid(), status.Revision);
        var store = new UserStateRepository(db); var state = (await store.GetAsync(profile, asset))!;
        state.ProgressPct = 22; state.ExtendedProperties = []; await store.SaveAsync(state);
        Assert.True((await repo.ReadAsync(profile, target)).Hidden);
        Assert.Equal(22, (await store.GetAsync(profile, asset))!.ProgressPct);
    }
    [Fact]
    public async Task BookStatusDoesNotChangeAudioEditionOfSameWork()
    {
        var (work, ebook) = Add(MediaType.Books); var audio = Guid.NewGuid(); var edition = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work)", new { edition, work });
            conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@audio,@edition,@hash,'C:/fixtures/audio.m4b')", new { audio, edition, hash = audio.ToString() });
        }
        var repo = new PersonalStatusRepository(db); var target = new PersonalStatusTarget(work, MediaType.Books);
        var snapshot = await repo.ReadAsync(profile, target);
        await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Complete, Guid.NewGuid(), snapshot.Revision);
        Assert.Equal(100, (await new UserStateRepository(db).GetAsync(profile, ebook))!.ProgressPct);
        Assert.Null(await new UserStateRepository(db).GetAsync(profile, audio));
    }
    [Fact]
    public async Task ResetRetainsRealSessionHistory_WithoutAddingFakeSession()
    {
        var (work, asset) = Add(MediaType.TV); var store = new UserStateRepository(db);
        await store.SaveAsync(new UserState
        {
            UserId = profile,
            AssetId = asset,
            ProgressPct = 12,
            LastAccessed = DateTimeOffset.UtcNow,
            ExtendedProperties = new() { ["player_session_id"] = Guid.NewGuid().ToString("D") }
        });
        var repo = new PersonalStatusRepository(db); var target = new PersonalStatusTarget(work, MediaType.TV);
        var snapshot = await repo.ReadAsync(profile, target);
        await repo.ExecuteAsync(profile, target, PersonalStatusCommand.Reset, Guid.NewGuid(), snapshot.Revision);
        var history = await repo.HistoryAsync(profile, target);
        Assert.Equal(2, history.Count); Assert.Single(history, h => h.Command == "Played");
    }
    [Fact]
    public async Task PlaybackAsset_UsesMostRecentProfileVariant()
    {
        var (work, first) = Add(MediaType.TV);
        var second = Guid.NewGuid(); var edition = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work)", new { edition, work });
            conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@second,@edition,@hash,'C:/fixtures/variant.mp4')", new { second, edition, hash = second.ToString() });
        }
        var states = new UserStateRepository(db);
        await states.SaveAsync(new UserState { UserId = profile, AssetId = first, ProgressPct = 80, LastAccessed = DateTimeOffset.UtcNow.AddDays(-1) });
        await states.SaveAsync(new UserState { UserId = profile, AssetId = second, ProgressPct = 10, LastAccessed = DateTimeOffset.UtcNow });
        var assets = new MediaAssetRepository(db);
        Assert.Equal(second, (await assets.FindFirstByWorkIdAsync(work, profileId: profile))!.Id);
    }
    [Fact]
    public async Task EpisodeCredits_StayScoped_AndReplacementDoesNotDuplicate()
    {
        var (show, _) = Add(MediaType.TV); var (first, _a) = Add(MediaType.TV, show); var (second, _b) = Add(MediaType.TV, show);
        var repo = new TvEpisodeCreditRepository(db);
        var c = new TvEpisodeCredits(first, show, "1", 1, 1, [new("person1", "credit1", "One", "Director", null, null, 0)]);
        await repo.ReplaceAsync(c); await repo.ReplaceAsync(c);
        await repo.ReplaceAsync(new(second, show, "2", 2, 1, [new("person2", "credit2", "Two", "Actor", "Guest", null, 0)]));
        Assert.Single(await repo.ReadAsync(first)); Assert.Equal("One", (await repo.ReadAsync(first))[0].Credits[0].Name);
        Assert.Equal(2, (await repo.ReadAsync(show)).Count);
    }
}
