using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A picture chosen in the editor replaces the poster file already in a managed folder (universal editor v7 follow-up), while the background
/// reconcile still leaves a poster the Engine did not write alone.
/// </summary>
public sealed class AssetExportReplaceExistingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-export-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseConnection _database;
    private readonly EntityAssetRepository _assets;
    private readonly AssetExportService _service;

    public AssetExportReplaceExistingTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new EntityAssetRepository(_database);
        var paths = new AssetPathService(_root, new LibraryStoragePolicy { Mode = StorageMode.CoLocated });
        _service = new AssetExportService(_database, _assets, paths, NullLogger<AssetExportService>.Instance);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task Reconcile_WhenAPosterFileIsAlreadyThere_LeavesItAloneUnlessTheChoiceWasJustMade()
    {
        var (workId, posterFile) = await SeedAsync();

        await _service.ReconcileArtworkAsync(workId.ToString("D"), "Work", "CoverArt");
        Assert.Equal("old poster", await File.ReadAllTextAsync(posterFile));

        await _service.ReconcileArtworkAsync(workId.ToString("D"), "Work", "CoverArt", replaceExistingFile: true);
        Assert.Equal("new poster", await File.ReadAllTextAsync(posterFile));
    }

    private async Task<(Guid WorkId, string PosterFile)> SeedAsync()
    {
        var workId = Guid.NewGuid();
        var folder = Path.Combine(_root, "Show");
        Directory.CreateDirectory(folder);
        var mediaFile = Path.Combine(folder, "Show S01E01.mkv");
        await File.WriteAllTextAsync(mediaFile, "video");
        var posterFile = Path.Combine(folder, "poster.jpg");
        await File.WriteAllTextAsync(posterFile, "old poster");
        var chosen = Path.Combine(_root, "chosen.jpg");
        await File.WriteAllTextAsync(chosen, "new poster");

        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
            connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@assetId, @editionId, 'hash', @path, 'Normal');",
                new { assetId, editionId, path = mediaFile });
        }

        await _assets.UpsertAsync(new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = workId.ToString("D"),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            LocalImagePath = chosen,
            IsPreferred = true,
        });
        return (workId, posterFile);
    }
}
