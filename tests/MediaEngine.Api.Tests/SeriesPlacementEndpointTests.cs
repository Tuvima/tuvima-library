using System.Reflection;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Matching Series block's two Engine actions: who may call them, what they return, and that the lock-less
/// <c>/library/universe-assign</c> action is gone.
/// </summary>
public sealed class SeriesPlacementEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public SeriesPlacementEndpointTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_placement_endpoint_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public void TheSeriesPlacementActions_AreScopedToTheItemAndGuardedByPermissionOnTheEngine()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "MediaEngine.Api", "Endpoints", "MetadataEndpoints.SeriesPlacement.cs"));

        Assert.Contains("MapGet(\"/{entityId:guid}/series-placement\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPut(\"/{entityId:guid}/series-placement\"", source, StringComparison.Ordinal);
        Assert.Contains(".RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)", source, StringComparison.Ordinal);
        Assert.Contains(".RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)", source, StringComparison.Ordinal);
        // The write carries the resource scope filter, which also applies the ingestion edit guard.
        Assert.Contains(".RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)", source, StringComparison.Ordinal);
        Assert.Contains(".RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockLessUniverseAssignAction_IsGone_SoNothingCanMoveAWorkAroundAPersonsChoice()
    {
        var root = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Endpoints", "LibraryEndpoints.cs"));

        Assert.DoesNotContain("universe-assign", library, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignWorkToCollectionAsync", library, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Api", "Models", "UniverseDtos.cs")));
    }

    [Fact]
    public void TheWireNamesAreSnakeCase()
    {
        var request = JsonSerializer.Serialize(new SeriesPlacementChoiceRequestDto { CollectionId = Guid.Empty, Locked = true });
        var response = JsonSerializer.Serialize(new SeriesPlacementDto { SupportsSeries = true });

        Assert.Contains("\"collection_id\"", request, StringComparison.Ordinal);
        Assert.Contains("\"locked\":true", request, StringComparison.Ordinal);
        Assert.Contains("\"supports_series\":true", response, StringComparison.Ordinal);
        Assert.Contains("\"is_person_choice\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRead_ForAnEpisodeOrATrack_SaysSeriesAreNotSupported_AndOffersNothing()
    {
        var episode = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@episode, NULL, 'TV', 'standalone');", new { episode });
        }

        var dto = await BuildAsync(new SeriesPlacementRepository(_db), episode);

        Assert.NotNull(dto);
        Assert.False(dto!.SupportsSeries);
        Assert.Empty(dto.Listed);
        Assert.Empty(dto.Candidates);
    }

    [Fact]
    public async Task TheRead_ForABook_ShowsTheChosenSeriesAsThePersonsChoice_AndAnUnknownItemIsNotFound()
    {
        var repo = new SeriesPlacementRepository(_db);
        var shelf = Guid.NewGuid();
        var work = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@work, NULL, 'Books', 'standalone');", new { work });
            conn.Execute(
                "INSERT INTO collections (id, display_name, collection_type, media_type, wikidata_qid) VALUES (@shelf, 'Dune Novels', 'ContentGroup', 'Books', 'Q1');",
                new { shelf });
        }

        await repo.SetChoiceAsync(work, shelf);
        var dto = await BuildAsync(repo, work);

        Assert.True(dto!.SupportsSeries);
        Assert.True(dto.IsPersonChoice);
        Assert.Equal("Dune Novels", dto.Current!.Label);
        Assert.Equal(shelf, dto.Current.CollectionId);
        Assert.Null(await BuildAsync(repo, Guid.NewGuid()));
    }

    private static async Task<SeriesPlacementDto?> BuildAsync(MediaEngine.Domain.Contracts.ISeriesPlacementRepository repo, Guid entityId)
    {
        var method = typeof(MetadataEndpoints).GetMethod("BuildSeriesPlacementAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<SeriesPlacementDto?>)method.Invoke(null, [repo, entityId, null, CancellationToken.None])!;
        return await task;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
