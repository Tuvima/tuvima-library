using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class UniverseContentsRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_universe_contents_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public UniverseContentsRepositoryTests()
    {
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
    }

    [Theory]
    [InlineData(FictionalEntityType.Species)]
    [InlineData(FictionalEntityType.Concept)]
    public async Task Create_AcceptsTheNewKinds(string kind)
    {
        var repo = new FictionalEntityRepository(_db);
        var entity = new FictionalEntity { WikidataQid = "Q100", Label = "Fremen", EntitySubType = kind, FictionalUniverseQid = "QU" };

        await repo.CreateAsync(entity);

        var found = await repo.FindByQidAsync("Q100");
        Assert.Equal(kind, found?.EntitySubType);
        Assert.False(found?.AddedByUser);
    }

    [Fact]
    public async Task HandAddedThing_GetsALocalIdAndTheAddedByYouFlag()
    {
        var repo = new FictionalEntityRepository(_db);

        var added = await repo.CreateUserEntityAsync("House Corrino Guard", FictionalEntityType.Organization, "QU", "Dune universe", "  Imperial elite troops  ");

        Assert.StartsWith(FictionalEntity.LocalIdPrefix, added.WikidataQid);
        var found = await repo.FindByIdAsync(added.Id);
        Assert.NotNull(found);
        Assert.True(found.AddedByUser);
        Assert.Equal("Imperial elite troops", found.Description);
    }

    [Fact]
    public async Task HandAddedThing_IsNeverOverwrittenByAProviderOrOfferedForRefresh()
    {
        var repo = new FictionalEntityRepository(_db);
        var added = await repo.CreateUserEntityAsync("My Faction", FictionalEntityType.Organization, "QU", null, "Mine");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE fictional_entities SET enriched_at = '2000-01-01T00:00:00Z' WHERE id = @id;", new { id = added.Id });
        }

        await repo.UpdateEnrichmentAsync(added.Id, "Provider name", "Provider description", "https://example.test/x.png", DateTimeOffset.UtcNow);
        await repo.UpdateUniverseAsync(added.Id, "QOTHER", "Other");
        var stale = await repo.GetStaleEntitiesAsync(30, 50);

        var found = await repo.FindByIdAsync(added.Id);
        Assert.Equal("My Faction", found?.Label);
        Assert.Equal("Mine", found?.Description);
        Assert.Null(found?.ImageUrl);
        Assert.Equal("QU", found?.FictionalUniverseQid);
        Assert.DoesNotContain(stale, e => e.Id == added.Id);
    }

    [Fact]
    public async Task DeleteUserEntity_RemovesOnlyHandAddedThings()
    {
        var repo = new FictionalEntityRepository(_db);
        var provider = new FictionalEntity { WikidataQid = "Q200", Label = "Arrakis", EntitySubType = FictionalEntityType.Location, FictionalUniverseQid = "QU" };
        await repo.CreateAsync(provider);
        var added = await repo.CreateUserEntityAsync("Hidden Base", FictionalEntityType.Location, "QU", null, null);

        Assert.False(await repo.DeleteUserEntityAsync(provider.Id));
        Assert.True(await repo.DeleteUserEntityAsync(added.Id));

        Assert.NotNull(await repo.FindByIdAsync(provider.Id));
        Assert.Null(await repo.FindByIdAsync(added.Id));
    }

    [Fact]
    public async Task UserLinks_RoundTripWithoutTouchingNameOrDescriptionOverrides()
    {
        var repo = new FictionalEntityRepository(_db);
        var faction = await repo.CreateUserEntityAsync("Guild", FictionalEntityType.Organization, "QU", null, null);
        var thing = new FictionalEntity { WikidataQid = "Q300", Label = "Navigator", EntitySubType = FictionalEntityType.Character, FictionalUniverseQid = "QU" };
        await repo.CreateAsync(thing);
        await repo.UpdateUserDetailsAsync(thing.Id, "My name", null);

        await repo.UpdateUserLinksAsync(thing.Id, new FictionalEntityUserLinks(["Steersman", " steersman ", ""], faction.Id, true));

        var links = await repo.GetUserLinksAsync(thing.Id);
        Assert.Equal(["Steersman"], links?.Aliases);
        Assert.Equal(faction.Id, links?.BelongsToEntityId);
        Assert.True(links?.IsHidden);
        Assert.Equal("My name", (await repo.GetUserDetailsAsync(thing.Id))?.Label);
        Assert.Null(await repo.GetUserLinksAsync(faction.Id));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { /* temp file still held; the OS cleans it up */ }
        }
    }
}
