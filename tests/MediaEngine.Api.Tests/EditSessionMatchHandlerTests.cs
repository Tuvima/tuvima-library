using MediaEngine.Api.Http;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private EditSessionService Service(IEditSessionChangeHandler? extra = null, IAutoOrganizeService? organizer = null)
    {
        var scopes = new ServiceCollection()
            .AddScoped<IRetailMatchWriter>(_ => _writer)
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

        return new EditSessionService(handlers, NullLogger<EditSessionService>.Instance);
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

    private static EditSessionChange Relationship(string id, Guid item, Guid destination) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Relationship,
        EntityId = item,
        TargetId = destination.ToString(),
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
