using System.Reflection;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// An album's artist and a series' author are multi-valued credits, so the Engine stores them only as ordered
/// rows in canonical_value_arrays. The group page header and the editor's Album section read them from there,
/// in a fixed key order and by ordinal, instead of looking for a single stored value that never exists.
/// </summary>
public sealed class StoredCreditReadTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_stored_credits_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CanonicalValueArrayRepository _arrays;

    public StoredCreditReadTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _arrays = new CanonicalValueArrayRepository(_database);
    }

    [Fact]
    public async Task AlbumHeader_NamesTheAlbumArtistBeforeTheTrackArtists()
    {
        var album = await InsertAlbumAsync();
        await SetCreditsAsync(album.RootWorkId, "artist", "Guest Singer", "Main Band");
        await SetCreditsAsync(album.RootWorkId, "album_artist", "Main Band");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(_arrays, album.RootWorkId, isMusic: true, CancellationToken.None);

        Assert.Equal("Main Band", creator);
    }

    [Fact]
    public async Task AlbumHeader_WithoutAnAlbumArtist_NamesTheFirstArtistByPosition()
    {
        var album = await InsertAlbumAsync();
        await SetCreditsAsync(album.RootWorkId, "artist", "First Artist", "Second Artist");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(_arrays, album.RootWorkId, isMusic: true, CancellationToken.None);

        Assert.Equal("First Artist", creator);
    }

    [Fact]
    public async Task SeriesHeader_NamesTheFirstAuthorAndIgnoresMusicCredits()
    {
        var seriesRootId = Guid.NewGuid();
        await SetCreditsAsync(seriesRootId, "artist", "Cover Illustrator");
        await SetCreditsAsync(seriesRootId, "author", "Lead Author", "Co-Author");
        await SetCreditsAsync(seriesRootId, "creator", "Original Creator");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(
            _arrays, seriesRootId, isMusic: false, CancellationToken.None);

        Assert.Equal("Lead Author", creator);
    }

    [Fact]
    public async Task SeriesHeader_WithoutAnAuthor_FallsBackToTheStoredCreator()
    {
        var seriesRootId = Guid.NewGuid();
        await SetCreditsAsync(seriesRootId, "creator", "Original Creator");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(
            _arrays, seriesRootId, isMusic: false, CancellationToken.None);

        Assert.Equal("Original Creator", creator);
    }

    [Fact]
    public async Task SeriesHeader_WithSeveralCreatorsAndNoAuthor_NamesThemAllInOrder()
    {
        var seriesRootId = Guid.NewGuid();
        await SetCreditsAsync(seriesRootId, "creator", "Jerry Siegel", "Joe Shuster", "jerry siegel");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(
            _arrays, seriesRootId, isMusic: false, CancellationToken.None);

        Assert.Equal("Jerry Siegel, Joe Shuster", creator);
    }

    [Fact]
    public void Creator_IsStoredAsAList()
    {
        Assert.True(MetadataFieldConstants.IsMultiValued("creator"));
    }

    [Fact]
    public async Task AlbumHeader_IgnoresAStoredCreator()
    {
        var album = await InsertAlbumAsync();
        await SetCreditsAsync(album.RootWorkId, "creator", "Someone Else");

        var creator = await CollectionEndpoints.ResolveGroupCreatorAsync(
            _arrays, album.RootWorkId, isMusic: true, CancellationToken.None);

        Assert.Null(creator);
    }

    [Fact]
    public async Task GroupHeader_WithNoCreditsOrNoRootWork_NamesNobody()
    {
        Assert.Null(await CollectionEndpoints.ResolveGroupCreatorAsync(_arrays, Guid.NewGuid(), isMusic: false, CancellationToken.None));
        Assert.Null(await CollectionEndpoints.ResolveGroupCreatorAsync(_arrays, null, isMusic: false, CancellationToken.None));
    }

    [Fact]
    public async Task EditorAlbumSection_ShowsTheAlbumArtistRatherThanTheTitle()
    {
        var album = await InsertAlbumAsync();
        await SetCreditsAsync(album.RootWorkId, "artist", "Guest Singer");
        await SetCreditsAsync(album.RootWorkId, "album_artist", "Main Band");

        var albumScope = await ResolveAlbumScopeAsync(album.TrackWorkId);

        Assert.Equal("Main Band", albumScope.DisplaySubtitle);
    }

    private async Task<MetadataEndpoints.EditorScopeResolution> ResolveAlbumScopeAsync(Guid entityId)
    {
        var method = typeof(MetadataEndpoints).GetMethod(
            "ResolveEditorScopeContextAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(null,
        [
            entityId,
            new CanonicalValueRepository(_database),
            new LibraryItemRepository(_database),
            new MetadataEditorRepository(_database),
            CancellationToken.None,
        ])!;
        await task;
        var context = task.GetType().GetProperty("Result")!.GetValue(task);
        Assert.NotNull(context);
        var scopes = (IReadOnlyList<MetadataEndpoints.EditorScopeResolution>)context.GetType()
            .GetProperty("Scopes")!.GetValue(context)!;
        return Assert.Single(scopes, scope => scope.ScopeId == "album");
    }

    private Task SetCreditsAsync(Guid entityId, string key, params string[] names) =>
        _arrays.SetValuesAsync(
            entityId,
            key,
            names.Select((name, ordinal) => new CanonicalArrayEntry { Ordinal = ordinal, Value = name }).ToList());

    private async Task<SeededAlbum> InsertAlbumAsync()
    {
        var collectionId = Guid.NewGuid();
        var rootWorkId = Guid.NewGuid();
        var trackWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO collections (id, display_name, collection_type)
            VALUES (@collectionId, 'The Album', 'ContentGroup');
            INSERT INTO works (id, collection_id, media_type, work_kind, ownership)
            VALUES (@rootWorkId, @collectionId, 'Music', 'parent', 'Owned');
            INSERT INTO works (id, collection_id, parent_work_id, media_type, work_kind, ownership)
            VALUES (@trackWorkId, @collectionId, @rootWorkId, 'Music', 'child', 'Owned');
            INSERT INTO editions (id, work_id, format_label)
            VALUES (@editionId, @trackWorkId, 'FLAC');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, writeback_status)
            VALUES (@assetId, @editionId, @hash, 'C:/library/Main Band/The Album/01 Opening.flac', 'Normal', 'ok');
            """,
            new
            {
                collectionId,
                rootWorkId,
                trackWorkId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
            });
        return new SeededAlbum(rootWorkId, trackWorkId);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // SQLite can release a pooled test handle just after disposal; the OS temp sweep removes the file.
        }
    }

    private sealed record SeededAlbum(Guid RootWorkId, Guid TrackWorkId);
}
