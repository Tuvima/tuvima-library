using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorSelectionHistoryReadServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_selection_history_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _show = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private readonly Guid _firstEdition = Guid.NewGuid();
    private readonly Guid _secondEdition = Guid.NewGuid();
    private readonly Guid _firstAsset = Guid.NewGuid();
    private readonly Guid _secondAsset = Guid.NewGuid();

    public MediaEditorSelectionHistoryReadServiceTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ownership) VALUES
                (@show, 'TV', 'parent', NULL, 'Owned'),
                (@season, 'TV', 'parent', @show, 'Owned'),
                (@first, 'TV', 'child', @season, 'Owned'),
                (@second, 'TV', 'child', @season, 'Owned');
            INSERT INTO editions(id, work_id) VALUES
                (@firstEdition, @first), (@secondEdition, @second);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id) VALUES
                (@firstAsset, @firstEdition, @firstHash, 'C:/tv/one.mkv', @library),
                (@secondAsset, @secondEdition, @secondHash, 'C:/tv/two.mkv', @library);
            """, new
        {
            show = _show,
            season = _season,
            first = _first,
            second = _second,
            firstEdition = _firstEdition,
            secondEdition = _secondEdition,
            firstAsset = _firstAsset,
            secondAsset = _secondAsset,
            firstHash = Guid.NewGuid().ToString("N"),
            secondHash = Guid.NewGuid().ToString("N"),
            library = Guid.NewGuid().ToString("D")
        });
    }

    [Fact]
    public async Task SelectedFileIncludesOwnAndParentEventsButExcludesSiblingEvents()
    {
        using (var connection = _database.CreateConnection())
        {
            foreach (var entity in new[] { _firstAsset, _firstEdition, _first, _season, _show,
                         _secondAsset, _secondEdition, _second })
            {
                connection.Execute("""
                        INSERT INTO system_activity(entity_id, entity_type, action_type, detail)
                        VALUES(@entity, 'Test', 'MetadataEdited', @detail);
                        """, new { entity, detail = entity.ToString("D") });
            }
        }
        var result = await new MediaEditorSelectionHistoryReadService(_database)
            .ReadAsync(_show, [_firstAsset]);
        Assert.NotNull(result);
        Assert.Equal(5, result.Items.Count);
        Assert.Contains(result.Items, item => item.EntityId == _firstAsset && item.Scope == "file");
        Assert.Contains(result.Items, item => item.EntityId == _firstEdition && item.Scope == "edition");
        Assert.Contains(result.Items, item => item.EntityId == _show && item.Scope == "parent");
        Assert.DoesNotContain(result.Items, item => item.EntityId is var id
            && (id == _secondAsset || id == _secondEdition || id == _second));
        Assert.All(result.Items, item => Assert.Equal([_firstAsset], item.SelectedAssetIds));

        var both = await new MediaEditorSelectionHistoryReadService(_database)
            .ReadAsync(_show, [_firstAsset, _secondAsset]);
        Assert.NotNull(both);
        Assert.Equal(2, Assert.Single(both.Items, item => item.EntityId == _show)
            .SelectedAssetIds.Count);
    }

    [Fact]
    public async Task EmptyHistoryIsAValidReadButCatalogOrWrongParentIsNot()
    {
        var service = new MediaEditorSelectionHistoryReadService(_database);
        var empty = await service.ReadAsync(_show, [_firstAsset]);
        Assert.NotNull(empty);
        Assert.Empty(empty.Items);

        var wrongParent = await service.ReadAsync(_first, [_secondAsset]);
        Assert.Null(wrongParent);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET is_orphaned=1 WHERE id=@asset;",
                    new { asset = _firstAsset });
        }
        Assert.Null(await service.ReadAsync(_show, [_firstAsset]));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
