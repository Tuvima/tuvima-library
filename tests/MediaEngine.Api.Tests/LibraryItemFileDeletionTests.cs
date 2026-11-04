using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Delete one file of an item (universal editor v4): the same plan, safety gate and fingerprint as a title delete, narrowed to one file.
/// An item with other files stays with them; the item's last file is the ordinary whole-item delete.
/// </summary>
public sealed class LibraryItemFileDeletionTests : IDisposable
{
    private readonly LibraryItemDeletionTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task DeletingOneOfTwoFilesOfAnEpisode_RemovesThatFile_AndTheEpisodeStaysWithTheOther()
    {
        var first = _world.WriteFile(_world.Managed, Path.Combine("Show", "Show.S01E01.1080p.mkv"), bytes: 400);
        var second = _world.WriteFile(_world.Managed, Path.Combine("Show", "Show.S01E01.2160p.mkv"), bytes: 900);
        var (episodeId, assetIds) = _world.AddWork("Pilot", [first, second], mediaType: "TV", workKind: "standalone");

        var preview = (await _world.Service.PreviewFileAsync(episodeId, assetIds[1])).Value!;
        Assert.True(preview.FileScope!.ItemRemains);
        Assert.Equal(1, preview.FileScope.RemainingFiles);
        Assert.Equal("Show.S01E01.2160p.mkv", preview.FileScope.FileName);
        var row = Assert.Single(preview.Files);
        Assert.Equal(assetIds[1], row.AssetId);
        Assert.Equal(900, preview.BytesFreed);

        var result = await _world.Service.DeleteFileAsync(episodeId, assetIds[1], preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.DeletedWorkIds);
        Assert.Equal(1, result.Value.FilesDeleted);
        Assert.Equal(900, result.Value.BytesFreed);
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(first));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = episodeId }));
        var remaining = ScalarGuids("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @id;", new { id = episodeId });
        Assert.Equal([assetIds[0]], remaining);
        var announced = Assert.IsType<MediaEngine.Contracts.Realtime.LibraryItemsRemovedEvent>(Assert.Single(_world.Events.Published).Payload);
        Assert.Empty(announced.WorkIds);
        Assert.Equal([assetIds[1]], announced.AssetIds);
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM system_activity WHERE action_type = 'MediaRemoved' AND detail LIKE '%one file of%';"));
    }

    [Fact]
    public async Task DeletingTheLastFileOfAnItem_RemovesTheWholeItem()
    {
        var only = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [only]);

        var preview = (await _world.Service.PreviewFileAsync(workId, assetIds[0])).Value!;
        Assert.False(preview.FileScope!.ItemRemains);
        Assert.Equal(0, preview.FileScope.RemainingFiles);

        var result = await _world.Service.DeleteFileAsync(workId, assetIds[0], preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal([workId], result.Value!.DeletedWorkIds);
        Assert.False(File.Exists(only));
        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = workId }));
    }

    [Fact]
    public async Task AFileInAReadOnlyFolder_IsNeverDeleted_ButLeavesTheItem_AndIsExcludedFromScans()
    {
        var kept = _world.WriteFile(_world.Managed, Path.Combine("Show", "Show.S01E01.mkv"));
        var readOnly = _world.WriteFile(_world.AsIs, "Show.S01E01.alt.mkv", bytes: 300);
        var (episodeId, assetIds) = _world.AddWork("Pilot", [kept, readOnly], mediaType: "TV");

        var preview = (await _world.Service.PreviewFileAsync(episodeId, assetIds[1])).Value!;
        Assert.Equal(LibraryItemDeletionOutcomes.KeepAsIs, Assert.Single(preview.Files).Outcome);
        Assert.Equal(0, preview.BytesFreed);

        var result = await _world.Service.DeleteFileAsync(episodeId, assetIds[1], preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(readOnly));
        Assert.True(File.Exists(kept));
        Assert.Equal(0, result.Value!.FilesDeleted);
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM excluded_source_files;"));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM media_assets WHERE id = @id;", new { id = assetIds[0] }));
        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM media_assets WHERE id = @id;", new { id = assetIds[1] }));
    }

    [Fact]
    public async Task WithTheRecycleBinOn_TheFileMovesToTheBin_AndThePreviewSaysSo()
    {
        _world.TurnOnMoviesRecycleBin();
        var first = _world.WriteFile(_world.Managed, "Dune.mkv");
        var second = _world.WriteFile(_world.Managed, "Dune.4k.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [first, second]);

        var preview = (await _world.Service.PreviewFileAsync(workId, assetIds[1])).Value!;
        Assert.True(preview.GoesToRecycleBin);

        var result = await _world.Service.DeleteFileAsync(workId, assetIds[1], preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(1, result.Value!.FilesDeleted);
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task AStaleFingerprint_IsRefusedWithAFreshPreview_AndNothingChanges()
    {
        var first = _world.WriteFile(_world.Managed, "Dune.mkv");
        var second = _world.WriteFile(_world.Managed, "Dune.4k.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [first, second]);

        var result = await _world.Service.DeleteFileAsync(workId, assetIds[1], "not-the-fingerprint", LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(LibraryItemDeletionRefusalKind.Changed, result.Refusal!.Kind);
        Assert.NotNull(result.Refusal.FreshPreview!.FileScope);
        Assert.True(File.Exists(second));
        Assert.Equal(2, ScalarInt("SELECT COUNT(*) FROM media_assets;"));
    }

    [Fact]
    public async Task AFileThatIsNotPartOfTheItem_IsNotFound()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (_, otherAssets) = _world.AddWork("Arrival", [other]);

        var result = await _world.Service.PreviewFileAsync(workId, otherAssets[0]);

        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, result.Refusal!.Kind);
        Assert.True(File.Exists(other));
    }

    private int ScalarInt(string sql, object? parameters = null)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.ExecuteScalar<int>(sql, parameters);
    }

    private List<Guid> ScalarGuids(string sql, object? parameters = null)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.Query<Guid>(sql, parameters).ToList();
    }
}
