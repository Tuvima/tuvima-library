using MediaEngine.Api.Http;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The three kinds the Match tool stages, saved through the edit session over a real temp data store and real temp folders
/// (universal editor spec D5, D6, D13): a retail match never moves the item, a parent change moves it in the library and asks
/// for no file to be touched, and files move last, one at a time, with the data staying saved when a move fails.
/// </summary>
public sealed class EditSessionMatchHandlerTests : IDisposable
{
    private const string OkComputerManifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1,"duration_seconds":284},
          {"title":"Paranoid Android","track_number":2,"disc_number":1,"duration_seconds":387},
          {"title":"Subterranean Homesick Alien","track_number":3,"disc_number":1,"duration_seconds":267}]}
        """;

    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly EditSessionCallerAccessor _callers = new();
    private readonly SpyRetailMatchWriter _writer = new();

    public void Dispose() => _world.Dispose();

    private MoveFileWorld Files => _world.Files!;

    // ----- identity: records the match, never moves ----------------------------------------------------------------------------

    [Fact]
    public async Task Identity_SavesTheMatchWithTheNoMoveWrite_AndTheItemStaysInItsAlbum()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var before = _world.Work(song).Parent;

        var result = await Service().RunAsync(
            Request(previewOnly: false, Identity("match", song)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        Assert.Equal(["replace-without-moving:" + song], _writer.Calls);
        Assert.Equal(before, _world.Work(song).Parent);
        Assert.NotEqual(okComputer, _world.Work(song).Parent);
    }

    [Fact]
    public async Task Identity_ClosesTheReviewItemsOfTheMatchedItem_SoNeedsReviewDropsIt()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var other = BestOfSong("Karma Police", 5, Files.Managed, out _, out _);
        var reviews = new ReviewQueueRepository(_world.Database);
        var mine = await reviews.InsertAsync(new ReviewQueueEntry { Id = Guid.NewGuid(), EntityId = song, EntityType = "Work", Trigger = ReviewTrigger.LowConfidence });
        var theirs = await reviews.InsertAsync(new ReviewQueueEntry { Id = Guid.NewGuid(), EntityId = other, EntityType = "Work", Trigger = ReviewTrigger.LowConfidence });

        var result = await Service().RunAsync(Request(previewOnly: false, Identity("match", song)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(ReviewStatus.Resolved, (await reviews.GetByIdAsync(mine))!.Status);
        Assert.Equal(ReviewStatus.Pending, (await reviews.GetByIdAsync(theirs))!.Status);
    }

    [Fact]
    public async Task Identity_WhenOnlyChecking_LeavesTheReviewItemsAlone()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var reviews = new ReviewQueueRepository(_world.Database);
        var mine = await reviews.InsertAsync(new ReviewQueueEntry { Id = Guid.NewGuid(), EntityId = song, EntityType = "Work", Trigger = ReviewTrigger.LowConfidence });

        await Service().RunAsync(Request(previewOnly: true, Identity("match", song)), NoRefusals);

        Assert.Equal(ReviewStatus.Pending, (await reviews.GetByIdAsync(mine))!.Status);
    }

    [Fact]
    public async Task Identity_RunsAfterTheChangesThatCanBeUndone()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var order = new List<string>();
        _writer.OnCall = call => order.Add(call);
        var service = Service(new OrderHandler(EditSessionChangeKinds.Artwork, order));

        await service.RunAsync(
            Request(
                previewOnly: false,
                Identity("match", song),
                new EditSessionChange { Id = "art", Kind = EditSessionChangeKinds.Artwork, EntityId = song, Slot = "CoverArt", TargetId = "x" }),
            NoRefusals);

        Assert.Equal(["artwork", "replace-without-moving:" + song], order);
    }

    [Fact]
    public async Task Identity_WhenTheChosenMatchCannotBeRead_IsRefusedAndNothingIsWritten()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);

        var result = await Service().RunAsync(
            Request(previewOnly: false, new EditSessionChange { Id = "match", Kind = EditSessionChangeKinds.Identity, EntityId = song, TargetId = "not a match" }),
            NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.MatchUnreadable, Assert.Single(result.Changes).Reason);
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task Identity_WhenTheEngineRefusesTheMatch_SaysWhy()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        _writer.Refusal = "Television retail matches must use TheTVDB.";

        var result = await Service().RunAsync(Request(previewOnly: true, Identity("match", song)), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal("Television retail matches must use TheTVDB.", Assert.Single(result.Changes).Reason);
    }

    // ----- relationship: moves in the library, asks for no file move ------------------------------------------------------------

    [Fact]
    public async Task Relationship_MovesTheSongIntoTheAlbum_AndAsksForNoFileToMove()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(Request(previewOnly: false, Relationship("move", song, okComputer)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        }

        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Null(Files.Row(asset).MoveStep);
        await Files.DrainQueueAsync();
        Assert.True(File.Exists(oldPath));
        Assert.Equal(oldPath, Files.StoredPath(asset));
    }

    [Fact]
    public async Task Relationship_WhenALaterChangeFails_PutsTheSongBackInItsOldAlbum()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var bestOf = _world.Work(song).Parent;
        // Another song keeps the album, which a move would otherwise remove once it is empty (and then there is nowhere to go back to).
        _world.AddChild(bestOf!.Value, "Music", "Karma Police", 5);
        _writer.Failure = "The provider is not available.";

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(
                Request(previewOnly: false, Relationship("move", song, okComputer), Identity("match", song)), NoRefusals);

            Assert.False(result.DataSaved);
        }

        Assert.Equal(bestOf, _world.Work(song).Parent);
    }

    [Fact]
    public async Task Relationship_WithoutACallerOrWithADestinationThatIsMissing_IsRefused()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);

        var unavailable = await Service().RunAsync(Request(previewOnly: true, Relationship("move", song, Guid.NewGuid())), NoRefusals);
        Assert.Equal(EditSessionReasons.SessionUnavailable, Assert.Single(unavailable.Changes).Reason);

        using (_callers.Use(Caller()))
        {
            var missing = await Service().RunAsync(
                Request(previewOnly: true, new EditSessionChange { Id = "move", Kind = EditSessionChangeKinds.Relationship, EntityId = song }), NoRefusals);
            Assert.Equal(EditSessionReasons.NoDestination, Assert.Single(missing.Changes).Reason);
        }
    }

    // ----- relationship with a position token: renumbers inside the album ---------------------------------------------------------

    [Fact]
    public async Task Position_GivesTheSongTheTrackNumberInItsOwnAlbum_AndAsksForNoFileToMove()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var album = _world.Work(song).Parent!.Value;

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(Request(previewOnly: false, Position("number", song, album, 1)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        }

        var after = _world.Work(song);
        Assert.Equal(album, after.Parent);
        Assert.Equal(1, after.Ordinal);
        Assert.Null(Files.Row(asset).MoveStep);
        await Files.DrainQueueAsync();
        Assert.Equal(oldPath, Files.StoredPath(asset));
    }

    [Fact]
    public async Task Position_WhenALaterChangeFails_PutsEveryTrackNumberBack()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var album = _world.Work(song).Parent!.Value;
        var (other, _, _) = _world.AddChild(album, "Music", "Karma Police", 5);
        _writer.Failure = "The provider is not available.";

        using (_callers.Use(Caller()))
        {
            // Song 4 takes number 5, so the two songs trade places before the identity change fails.
            var result = await Service().RunAsync(
                Request(previewOnly: false, Position("number", song, album, 5), Identity("match", song)), NoRefusals);

            Assert.False(result.DataSaved);
        }

        Assert.Equal(4, _world.Work(song).Ordinal);
        Assert.Equal(5, _world.Work(other).Ordinal);
    }

    [Fact]
    public async Task Position_ForAnAlbumTheSongIsNotIn_OrAnInvalidNumber_OrACallerWhoMayNotEdit_IsRefused()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var album = _world.Work(song).Parent!.Value;
        var (okComputer, _) = OkComputerAlbum();

        using (_callers.Use(Caller()))
        {
            var elsewhere = await Service().RunAsync(Request(previewOnly: true, Position("number", song, okComputer, 2)), NoRefusals);
            Assert.Equal(EditSessionReasons.NotInThatAlbum, Assert.Single(elsewhere.Changes).Reason);

            var invalid = await Service().RunAsync(
                Request(previewOnly: true, new EditSessionChange { Id = "number", Kind = EditSessionChangeKinds.Relationship, EntityId = song, TargetId = album.ToString(), Token = "position:0" }),
                NoRefusals);
            Assert.Equal(EditSessionReasons.BadPosition, Assert.Single(invalid.Changes).Reason);

            var noPlace = await Service().RunAsync(
                Request(previewOnly: true, new EditSessionChange { Id = "place", Kind = EditSessionChangeKinds.Relationship, EntityId = song, TargetId = okComputer.ToString(), Token = EditSessionPosition.PlaceToken(0) }),
                NoRefusals);
            Assert.Equal(EditSessionReasons.BadPosition, Assert.Single(noPlace.Changes).Reason);
        }

        using (_callers.Use(new EditSessionCaller(LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Nothing)))
        {
            var denied = await Service().RunAsync(Request(previewOnly: true, Position("number", song, album, 2)), NoRefusals);
            Assert.Equal(EditSessionReasons.NotAllowedToEdit, Assert.Single(denied.Changes).Reason);
        }

        Assert.Equal(4, _world.Work(song).Ordinal);
    }

    // ----- file moves: last, one at a time, never undone ------------------------------------------------------------------------

    [Fact]
    public async Task FileMove_AfterAParentChange_FilesTheSongInTheNewAlbumFolder()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(
                Request(previewOnly: false, FileMove("files", song), Relationship("move", song, okComputer)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.All(result.Changes, change => Assert.Equal(EditSessionChangeStatuses.Ok, change.Status));
        }

        var stored = Files.StoredPath(asset);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(stored));
        Assert.Equal("OK Computer (1997)", Path.GetFileName(Path.GetDirectoryName(stored)));
    }

    [Fact]
    public async Task FileMove_WhenTheMoveFails_KeepsTheDataSavedAndNamesTheFile()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out _);
        var service = Service(organizer: new FailingRefileOrganizer(Files.Organizer));

        using (_callers.Use(Caller()))
        {
            var result = await service.RunAsync(
                Request(previewOnly: false, Relationship("move", song, okComputer), FileMove("files", song)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.Equal(EditSessionChangeStatuses.Ok, result.Changes.Single(change => change.Kind == EditSessionChangeKinds.Relationship).Status);
            var move = result.Changes.Single(change => change.Kind == EditSessionChangeKinds.FileMove);
            Assert.Equal(EditSessionChangeStatuses.Failed, move.Status);
            Assert.Contains("did not move", move.Reason, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(oldPath), move.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.True(File.Exists(oldPath));
        // The reason with the file's name goes back to Review; the Engine log names the item by id only.
        var line = Assert.Single(_log.Entries);
        Assert.Contains(song.ToString(), line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(oldPath), line.Message, StringComparison.Ordinal);
        Assert.Null(line.Exception);
    }

    [Fact]
    public async Task FileMove_InAnAsIsFolder_IsRefusedWithTheReason_AndNothingIsSaved()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.AsIs, out var oldPath, out _);
        var bestOf = _world.Work(song).Parent;

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(
                Request(previewOnly: false, Relationship("move", song, okComputer), FileMove("files", song)), NoRefusals);

            Assert.False(result.Valid);
            Assert.False(result.DataSaved);
            Assert.Equal(MoveFilePlanner.AsIsNote, result.Changes.Single(change => change.Kind == EditSessionChangeKinds.FileMove).Reason);
        }

        Assert.Equal(bestOf, _world.Work(song).Parent);
        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public async Task FileMove_InAReadOnlyFolder_IsRefusedWithTheReason()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.ReadOnly, out _, out _);

        var result = await Service().RunAsync(Request(previewOnly: true, FileMove("files", song)), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(MoveFilePlanner.ReadOnlyNote, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task FileMove_ForAnItemWithNoFiles_IsRefused()
    {
        var album = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(album, "Music", "Catalog only", 1, hasFile: false);

        var result = await Service().RunAsync(Request(previewOnly: true, FileMove("files", song)), NoRefusals);

        Assert.Equal(EditSessionReasons.NoFiles, Assert.Single(result.Changes).Reason);
    }

    // ----- the Match tab's TV and music flows, saved (universal editor v5, 3c) --------------------------------------------------

    [Fact]
    public async Task ASongMatchedToDisc2Track3OfAnotherAlbum_PreviewsTheMove_AndSavingMovesItAndItsFileIntoThatAlbum()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var bestOf = _world.Work(song).Parent!.Value;
        var (karmaPolice, _, _) = _world.AddChild(bestOf, "Music", "Karma Police", 5);
        var candidate = EditorMatchCandidates.ForMusicTrack(
            "0b1c2d3e-0000-4000-8000-000000000001", "0b1c2d3e-0000-4000-8000-000000000002", "OK Computer", "Radiohead", "1997", true,
            "0b1c2d3e-0000-4000-8000-000000000003", "Paranoid Android", "Radiohead", disc: 2, track: 3, lengthSeconds: 387, disambiguation: "2017 remaster");

        var placement = await PlanAsync(song, candidate);

        Assert.NotNull(placement);
        Assert.Equal(okComputer, placement.DestinationId);
        Assert.StartsWith("The Best Of", placement.CurrentParentLabel, StringComparison.Ordinal);
        Assert.StartsWith("OK Computer", placement.ParentLabel, StringComparison.Ordinal);
        Assert.True(placement.Managed);
        Assert.Equal(bestOf, placement.CurrentParentId);
        Assert.Equal(1, placement.CurrentParentKeeps);
        Assert.Equal(3, placement.Position);
        Assert.Equal("3", candidate.SuggestedFields["track_number"]);
        Assert.Equal("2", candidate.SuggestedFields["disc_number"]);
        Assert.Equal("0b1c2d3e-0000-4000-8000-000000000003", candidate.BridgeIds["musicbrainz_recording_id"]);

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(
                Request(previewOnly: false, SavedIdentity("match", song, candidate), Relationship("move", song, okComputer, EditSessionPosition.PlaceToken(placement.Position!.Value)), FileMove("files", song)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.All(result.Changes, change => Assert.Equal(EditSessionChangeStatuses.Ok, change.Status));
        }

        // The match that was saved names the exact release, recording, disc and track the person chose.
        var saved = Assert.Single(_writer.Requests);
        Assert.Equal("2", saved.SuggestedFields["disc_number"]);
        Assert.Equal("3", saved.SuggestedFields["track_number"]);
        Assert.Equal("0b1c2d3e-0000-4000-8000-000000000003", saved.BridgeIds["musicbrainz_recording_id"]);

        // The new album holds the song at the chosen track (3) beside its own songs; the old album keeps its other song at its own place.
        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Equal(3, _world.Work(song).Ordinal);
        Assert.Equal(0, _world.Scalar<long>("SELECT COUNT(*) FROM works WHERE parent_work_id = @bestOf AND id = @song;", new { bestOf, song }));
        Assert.Equal(2, _world.Scalar<long>("SELECT COUNT(*) FROM works WHERE parent_work_id = @okComputer;", new { okComputer }));
        Assert.Equal(bestOf, _world.Work(karmaPolice).Parent);
        Assert.Equal(5, _world.Work(karmaPolice).Ordinal);
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM works WHERE parent_work_id = @bestOf;", new { bestOf }));
        Assert.False(File.Exists(oldPath));
        Assert.Equal("OK Computer (1997)", Path.GetFileName(Path.GetDirectoryName(Files.StoredPath(asset))));
    }

    [Fact]
    public async Task AWronglyFolderedEpisodeMatchedToAnotherShowsSeason_MovesIntoThatSeasonWithItsManagedFile()
    {
        var wrongShow = _world.AddParent("TV", "Signal Lost");
        _world.Put(wrongShow, "year", "2015");
        var wrongSeason = _world.AddParent("TV", string.Empty, wrongShow, ordinal: 2);
        var rightShow = _world.AddParent("TV", "The Last Signal");
        _world.Put(rightShow, "year", "2019");
        var collection = _world.AddSeriesCollection("tvdb:100", [
            ("s2e3", "tvdb:100:season:2", 3, "Echoes in the Dark", null)]);
        _world.Scalar<long>("UPDATE works SET collection_id = @collection WHERE id = @rightShow; SELECT 0;", new { collection, rightShow });
        _world.AddParent("TV", string.Empty, rightShow, ordinal: 1);
        var rightSeason = _world.AddParent("TV", string.Empty, rightShow, ordinal: 2);
        var oldPath = MoveFileWorld.WriteFile(Files.Managed, "TV/Signal Lost (2015)/Season 02/Signal Lost - s02e03 - Echoes in the Dark.mkv");
        var (episode, _, asset) = _world.AddChild(wrongSeason, "TV", "Echoes in the Dark", 3, filePath: oldPath);
        _world.Put(asset!.Value, "title", "Echoes in the Dark");
        _world.Put(asset.Value, "media_type", "TV");
        _world.Put(asset.Value, "season_number", "2");
        _world.Put(asset.Value, "episode_number", "3");
        var candidate = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", null, null, 46);

        var placement = await PlanAsync(episode, candidate);

        Assert.NotNull(placement);
        Assert.Equal(rightSeason, placement.DestinationId);
        Assert.True(placement.Managed);
        Assert.Equal(Path.GetFullPath(oldPath), placement.FromPath);
        Assert.Contains("The Last Signal", placement.ToPath, StringComparison.Ordinal);
        Assert.Contains("Season 02", placement.ToPath, StringComparison.Ordinal);

        using (_callers.Use(Caller()))
        {
            var result = await Service().RunAsync(
                Request(previewOnly: false, Identity("match", episode), Relationship("move", episode, rightSeason), FileMove("files", episode)), NoRefusals);

            Assert.True(result.DataSaved);
            Assert.All(result.Changes, change => Assert.Equal(EditSessionChangeStatuses.Ok, change.Status));
        }

        Assert.Equal(rightSeason, _world.Work(episode).Parent);
        Assert.False(File.Exists(oldPath));
        Assert.Contains("The Last Signal", Files.StoredPath(asset.Value), StringComparison.Ordinal);
        Assert.True(File.Exists(Files.StoredPath(asset.Value)));
    }

    private Task<EditorMatchPlacementDto?> PlanAsync(Guid item, ItemCanonicalRetailCandidateDto candidate) =>
        new EditorMatchPlacementService(
            _world.Service,
            _world.Repository,
            new ItemCanonicalRepository(_world.Database, NullLogger<ItemCanonicalRepository>.Instance),
            Files.Organizer).PlanAsync(item, candidate, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private EditSessionService Service(IEditSessionChangeHandler? extra = null, IAutoOrganizeService? organizer = null)
    {
        var scopes = new ServiceCollection()
            .AddScoped<IRetailMatchWriter>(_ => _writer)
            .AddSingleton<IReviewQueueRepository>(new ReviewQueueRepository(_world.Database))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var handlers = new List<IEditSessionChangeHandler>
        {
            new IdentityEditSessionChangeHandler(scopes),
            new RelationshipEditSessionChangeHandler(_world.Service, _world.Repository, _callers),
            new FileMoveEditSessionChangeHandler(_world.Repository, organizer ?? Files.Organizer),
        };
        if (extra is not null)
        {
            handlers.Add(extra);
        }

        return new EditSessionService(handlers, _log);
    }

    private readonly CapturingLogger _log = new();

    private sealed class CapturingLogger : ILogger<EditSessionService>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    private static EditSessionCaller Caller() => new(LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) =>
        new() { PreviewOnly = previewOnly, Changes = [.. changes] };

    private static EditSessionChange Identity(string id, Guid item) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Identity,
        EntityId = item,
        TargetId = EditSessionIdentityTarget.Serialize(new ReplaceRetailMatchRequestDto
        {
            TargetKind = "work",
            TargetFieldGroup = "track",
            ProviderId = Guid.NewGuid().ToString(),
            ProviderName = "musicbrainz",
            ProviderItemId = "0c1d4b5f-recording",
        }),
    };

    // The identity change as the Dashboard stages it: the exact request a chosen record produces (mirrors EditorMatchRules.RequestFor).
    private static EditSessionChange SavedIdentity(string id, Guid item, MediaEngine.Contracts.Matching.ItemCanonicalRetailCandidateDto candidate) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Identity,
        EntityId = item,
        TargetId = EditSessionIdentityTarget.Serialize(new ReplaceRetailMatchRequestDto
        {
            TargetKind = "work",
            TargetFieldGroup = "track",
            ProviderId = candidate.ProviderId,
            ProviderName = candidate.ProviderName,
            ProviderItemId = candidate.ProviderItemId ?? string.Empty,
            CoverUrl = candidate.CoverUrl,
            RequiredFields = new Dictionary<string, string>(candidate.RequiredFields, StringComparer.OrdinalIgnoreCase),
            SuggestedFields = new Dictionary<string, string>(candidate.SuggestedFields, StringComparer.OrdinalIgnoreCase),
            BridgeIds = new Dictionary<string, string>(candidate.BridgeIds, StringComparer.OrdinalIgnoreCase),
        }),
    };

    private static EditSessionChange Relationship(string id, Guid item, Guid destination, string? token = null) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Relationship,
        EntityId = item,
        TargetId = destination.ToString(),
        Token = token,
    };

    private static EditSessionChange Position(string id, Guid item, Guid album, int number) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Relationship,
        EntityId = item,
        TargetId = album.ToString(),
        Token = EditSessionPosition.Token(number),
    };

    private static EditSessionChange FileMove(string id, Guid item) => new() { Id = id, Kind = EditSessionChangeKinds.FileMove, EntityId = item };

    private (Guid Album, Guid Airbag) OkComputerAlbum()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        _world.Put(album, "year", "1997");
        var (airbag, _, _) = _world.AddChild(album, "Music", "Airbag", 1);
        return (album, airbag);
    }

    /// <summary>A song in "The Best Of" (2000) with its real file in the given folder, named the way the organiser would.</summary>
    private Guid BestOfSong(string title, int track, string folder, out string path, out Guid asset)
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        _world.Put(bestOf, "year", "2000");
        path = MoveFileWorld.WriteFile(folder, $"Music/Radiohead/The Best Of (2000)/{track:00} - {title}.flac");
        var (work, _, assetId) = _world.AddChild(bestOf, "Music", title, track, filePath: path);
        asset = assetId!.Value;
        _world.Put(asset, "title", title);
        _world.Put(asset, "media_type", "Music");
        _world.Put(asset, "artist", "Radiohead");
        _world.Put(asset, "album", "The Best Of");
        _world.Put(asset, "track_number", track.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return work;
    }

    /// <summary>Stands in for the shared retail-match write: records which variant ran, and can refuse or fail.</summary>
    private sealed class SpyRetailMatchWriter : IRetailMatchWriter
    {
        public List<string> Calls { get; } = [];

        /// <summary>The requests the no-move write was given, in order.</summary>
        public List<ReplaceRetailMatchRequestDto> Requests { get; } = [];

        public Action<string>? OnCall { get; set; }

        /// <summary>A plain-English refusal the check gives.</summary>
        public string? Refusal { get; set; }

        /// <summary>A failure the write gives.</summary>
        public string? Failure { get; set; }

        public Task<IResult> CheckAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct) =>
            Task.FromResult(Refusal is null ? Results.Ok(new ItemCanonicalApplyResponseDto { EntityId = entityId }) : ApiErrors.BadRequest(Refusal));

        public Task<IResult> ReplaceWithoutMovingAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct)
        {
            var call = "replace-without-moving:" + entityId;
            Calls.Add(call);
            Requests.Add(request);
            OnCall?.Invoke(call);
            return Task.FromResult(Failure is null ? Results.Ok(new ItemCanonicalApplyResponseDto { EntityId = entityId }) : ApiErrors.BadRequest(Failure));
        }
    }

    private sealed class OrderHandler(string kind, List<string> order) : IEditSessionChangeHandler
    {
        public string Kind => kind;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
        {
            order.Add(kind);
            return Task.FromResult<Func<CancellationToken, Task>?>(_ => Task.CompletedTask);
        }
    }

    /// <summary>The real organiser's plans, but a move that fails (a locked file, a disk error).</summary>
    private sealed class FailingRefileOrganizer(IAutoOrganizeService inner) : IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default) => inner.TryAutoOrganizeAsync(assetId, ct);

        public Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default) =>
            inner.PlanRefileAsync(assetId, placement, ct);

        public async Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default)
        {
            var plan = await inner.PlanRefileAsync(assetId, null, ct);
            return new AssetRefileOutcome(AssetRefileOutcomeKind.Failed, plan.Disposition, plan.CurrentPath, "The file is in use by another program.");
        }
    }
}
