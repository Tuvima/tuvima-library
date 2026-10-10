using MediaEngine.Domain.Entities;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class PluginLoreRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public PluginLoreRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_lore_{Guid.NewGuid():N}.db");
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
    public async Task GetEntitiesAsync_OnlyReturnsApprovedSourceRowsByDefault()
    {
        var repo = new PluginLoreRepository(_db);
        var source = await repo.AddManualSourceAsync(
            "Q42",
            "tuvima.fandom-lore",
            "Dune Fandom",
            "https://dune.fandom.com",
            "https://dune.fandom.com/api.php");

        await repo.UpsertExtractionResultAsync(
            source,
            [
                new PluginLoreEntityRecord
                {
                    SourceId = source.Id,
                    UniverseQid = source.UniverseQid,
                    PluginId = source.PluginId,
                    ExternalKey = "page:1",
                    Label = "Arrakis",
                    EntityType = "Location",
                    SourceUrl = "https://dune.fandom.com/wiki/Arrakis",
                    Confidence = 0.81,
                },
                new PluginLoreEntityRecord
                {
                    SourceId = source.Id,
                    UniverseQid = source.UniverseQid,
                    PluginId = source.PluginId,
                    ExternalKey = "page:2",
                    Label = "Fremen",
                    EntityType = "Organization",
                    SourceUrl = "https://dune.fandom.com/wiki/Fremen",
                    Confidence = 0.78,
                },
            ],
            [
                new PluginLoreRelationshipRecord
                {
                    SourceId = source.Id,
                    UniverseQid = source.UniverseQid,
                    PluginId = source.PluginId,
                    SubjectExternalKey = "page:2",
                    ObjectExternalKey = "page:1",
                    RelationshipType = "located_in",
                    SourceUrl = "https://dune.fandom.com/wiki/Fremen",
                    Confidence = 0.72,
                },
            ]);

        Assert.Empty(await repo.GetEntitiesAsync("Q42"));
        Assert.Empty(await repo.GetRelationshipsAsync("Q42"));

        await repo.SetSourceStatusAsync(source.Id, PluginLoreSourceStatus.Approved, "admin");

        var approvedEntities = await repo.GetEntitiesAsync("Q42");
        var approvedRelationships = await repo.GetRelationshipsAsync("Q42");
        Assert.Equal(2, approvedEntities.Count);
        Assert.Single(approvedRelationships);

        await repo.SetSourceStatusAsync(source.Id, PluginLoreSourceStatus.Rejected, "admin");

        Assert.Empty(await repo.GetEntitiesAsync("Q42"));
        Assert.Equal(2, (await repo.GetEntitiesAsync("Q42", approvedOnly: false)).Count);
    }

    [Fact]
    public async Task UpsertExtractionResultAsync_PreservesObjectSubtype()
    {
        var repo = new PluginLoreRepository(_db);
        var source = await repo.AddManualSourceAsync("Q42", "tuvima.fandom-lore", "Dune Fandom", "https://dune.fandom.com", "https://dune.fandom.com/api.php");
        await repo.UpsertExtractionResultAsync(source,
        [
            new PluginLoreEntityRecord
            {
                SourceId = source.Id, UniverseQid = source.UniverseQid, PluginId = source.PluginId,
                ExternalKey = "page:crysknife", Label = "Crysknife", EntityType = "Object",
                SourceUrl = "https://dune.fandom.com/wiki/Crysknife", Confidence = 0.9,
            },
        ], []);
        await repo.SetSourceStatusAsync(source.Id, PluginLoreSourceStatus.Approved, "admin");

        var entity = Assert.Single(await repo.GetEntitiesAsync("Q42"));
        Assert.Equal("Object", entity.EntityType);
    }
}
