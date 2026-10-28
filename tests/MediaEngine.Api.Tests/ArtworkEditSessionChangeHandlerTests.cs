using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Artwork choices saved through the edit session (universal editor spec D8, D13): the chosen picture only changes when the session
/// is applied, a missing picture is refused by the check, and a later failure puts the previous picture back.
/// </summary>
public sealed class ArtworkEditSessionChangeHandlerTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_artwork_edit_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ArtworkAssetService _artwork;
    private readonly ArtworkEditSessionChangeHandler _handler;

    public ArtworkEditSessionChangeHandlerTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new NoHttpClientFactory());
        _handler = new ArtworkEditSessionChangeHandler(
            _artwork,
            new ItemCanonicalRepository(_database, NullLogger<ItemCanonicalRepository>.Instance),
            new SystemActivityRepository(_database));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task Check_WhenThePictureIsNotStored_RefusesItAndWritesNothing()
    {
        var work = await SeedWorkWithChosenPictureAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, ArtworkChange("a1", work.Id, "CoverArt", Guid.NewGuid().ToString())), NoRefusals);

        Assert.False(result.Valid);
        var change = Assert.Single(result.Changes);
        Assert.Equal(EditSessionChangeStatuses.Invalid, change.Status);
        Assert.Equal(ArtworkEditSessionChangeHandler.PictureGone, change.Reason);
        Assert.Equal(work.ChosenAssetId, await ChosenAsync(work.Id, "Primary"));
    }

    [Theory]
    [InlineData("Poster")]
    [InlineData("")]
    public async Task Check_WhenTheSlotIsNotOneTheEditorOffers_RefusesIt(string slot)
    {
        var work = await SeedWorkWithChosenPictureAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: true, ArtworkChange("a1", work.Id, slot, work.OtherAssetId.ToString())), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(ArtworkEditSessionChangeHandler.UnknownSlot, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Check_WhenTheItemIsGone_SaysSo()
    {
        var result = await CreateService().RunAsync(
            Request(previewOnly: true, ArtworkChange("a1", Guid.NewGuid(), "CoverArt", Guid.NewGuid().ToString())), NoRefusals);

        Assert.Equal(EditSessionReasons.Gone, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Preview_WhenThePictureIsStored_ReportsOkAndKeepsThePreviousPictureChosen()
    {
        var work = await SeedWorkWithChosenPictureAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: true, ArtworkChange("a1", work.Id, "CoverArt", work.OtherAssetId.ToString())), NoRefusals);

        Assert.True(result.Valid);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        Assert.Equal(work.ChosenAssetId, await ChosenAsync(work.Id, "Primary"));
    }

    [Fact]
    public async Task Apply_ChoosesTheStoredPictureForTheSlot()
    {
        var work = await SeedWorkWithChosenPictureAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, ArtworkChange("a1", work.Id, "CoverArt", work.OtherAssetId.ToString())), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        Assert.Equal(work.OtherAssetId, await ChosenAsync(work.Id, "Primary"));
        Assert.Equal(1, await ChosenCountAsync(work.Id, "Primary"));
    }

    [Fact]
    public async Task Apply_WhenThePictureWasNeverLinkedToTheItem_LinksAndChoosesIt()
    {
        var work = await SeedWorkWithChosenPictureAsync();
        var unlinked = SeedAsset("unlinked");

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, ArtworkChange("a1", work.Id, "CoverArt", unlinked.ToString())), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(unlinked, await ChosenAsync(work.Id, "Primary"));
    }

    [Fact]
    public async Task Apply_WithNone_LeavesNoPictureChosenButKeepsThePicturesSaved()
    {
        var work = await SeedWorkWithChosenPictureAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, ArtworkChange("a1", work.Id, "CoverArt", EditSessionArtworkTargets.None)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Null(await ChosenAsync(work.Id, "Primary"));
        Assert.Equal(2, (await _artwork.GetEntityAsync("Work", work.Id, CancellationToken.None)).Variants.Count);
    }

    [Fact]
    public async Task Undo_PutsThePreviouslyChosenPictureBack()
    {
        var work = await SeedWorkWithChosenPictureAsync();
        var change = ArtworkChange("a1", work.Id, "CoverArt", work.OtherAssetId.ToString());

        var undo = await _handler.ApplyAsync(change);
        Assert.Equal(work.OtherAssetId, await ChosenAsync(work.Id, "Primary"));
        Assert.NotNull(undo);
        await undo!(CancellationToken.None);

        Assert.Equal(work.ChosenAssetId, await ChosenAsync(work.Id, "Primary"));
        Assert.Equal(1, await ChosenCountAsync(work.Id, "Primary"));
    }

    [Fact]
    public async Task Undo_WhenNothingWasChosenBefore_LeavesNothingChosen()
    {
        var work = await SeedWorkWithChosenPictureAsync();
        await _artwork.ClearChosenAsync("Work", work.Id, "Primary", CancellationToken.None);

        var undo = await _handler.ApplyAsync(ArtworkChange("a1", work.Id, "CoverArt", work.OtherAssetId.ToString()));
        Assert.Equal(work.OtherAssetId, await ChosenAsync(work.Id, "Primary"));
        await undo!(CancellationToken.None);

        Assert.Null(await ChosenAsync(work.Id, "Primary"));
    }

    [Fact]
    public async Task Apply_WhenALaterChangeFails_PutsThePreviousPictureBack()
    {
        var work = await SeedWorkWithChosenPictureAsync();
        var service = CreateService(new FailingRelationshipHandler());

        var result = await service.RunAsync(
            Request(
                previewOnly: false,
                ArtworkChange("a1", work.Id, "CoverArt", work.OtherAssetId.ToString()),
                new EditSessionChange { Id = "rel", Kind = EditSessionChangeKinds.Relationship, EntityId = work.Id, TargetId = "album" }),
            NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.NotApplied, result.Changes.Single(change => change.Id == "a1").Status);
        Assert.Equal(EditSessionChangeStatuses.Failed, result.Changes.Single(change => change.Id == "rel").Status);
        Assert.Equal(work.ChosenAssetId, await ChosenAsync(work.Id, "Primary"));
        Assert.Equal(1, await ChosenCountAsync(work.Id, "Primary"));
    }

    // -- Helpers ---------------------------------------------------------------

    private EditSessionService CreateService(params IEditSessionChangeHandler[] extra) =>
        new([_handler, .. extra], NullLogger<EditSessionService>.Instance);

    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) =>
        new() { PreviewOnly = previewOnly, Changes = [.. changes] };

    private static EditSessionChange ArtworkChange(string id, Guid entity, string slot, string target) =>
        new() { Id = id, Kind = EditSessionChangeKinds.Artwork, EntityId = entity, Slot = slot, TargetId = target };

    private async Task<(Guid Id, Guid ChosenAssetId, Guid OtherAssetId)> SeedWorkWithChosenPictureAsync()
    {
        var workId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Movies', 'standalone');",
                new { workId = GuidSql.ToBlob(workId) });
        }

        var chosen = SeedAsset("chosen");
        var other = SeedAsset("other");
        await _artwork.LinkAsync("Work", workId, new(chosen, "Primary", Preferred: true, SourceAssetType: "CoverArt"), CancellationToken.None);
        await _artwork.LinkAsync("Work", workId, new(other, "Primary", Preferred: false, SourceAssetType: "CoverArt"), CancellationToken.None);
        return (workId, chosen, other);
    }

    private Guid SeedAsset(string name)
    {
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO artwork_assets (id, content_hash, original_path, width_px, height_px, aspect_class, source_provider, created_at)
            VALUES (@assetId, @hash, @path, 1000, 1500, 'Portrait', 'test', @createdAt);
            """,
            new
            {
                assetId,
                hash = $"hash-{name}-{assetId:N}",
                path = Path.Combine(_root, $"{assetId:N}.jpg"),
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        return assetId;
    }

    private async Task<Guid?> ChosenAsync(Guid entityId, string role) =>
        (await _artwork.GetChosenAsync("Work", entityId, role, CancellationToken.None))?.ArtworkAssetId;

    private async Task<int> ChosenCountAsync(Guid entityId, string role) =>
        (await _artwork.GetEntityAsync("Work", entityId, CancellationToken.None)).Variants.Count(v => v.IsPreferred && v.Role == role);

    private sealed class FailingRelationshipHandler : IEditSessionChangeHandler
    {
        public string Kind => EditSessionChangeKinds.Relationship;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default) =>
            throw new InvalidOperationException("The album could not be changed.");
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
