using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Reads and deletes that take a list of ids must match the rows those ids name. Dapper expands a list
/// parameter one element at a time without the Guid handler, so an unconverted id list matches nothing.
/// </summary>
public sealed class GuidListParameterRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;

    public GuidListParameterRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_guid_list_params_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    [Fact]
    public async Task CharacterPortraitBatch_ReturnsThePortraitsOfEveryNamedCharacter()
    {
        var personId = Guid.NewGuid();
        var neoId = Guid.NewGuid();
        var trinityId = Guid.NewGuid();
        var unrelatedId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at) VALUES (@personId, 'Keanu Reeves', CURRENT_TIMESTAMP);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                VALUES (@neoId, 'Q1', 'Neo', 'Character', CURRENT_TIMESTAMP),
                       (@trinityId, 'Q2', 'Trinity', 'Character', CURRENT_TIMESTAMP),
                       (@unrelatedId, 'Q3', 'Morpheus', 'Character', CURRENT_TIMESTAMP);
                """,
                new { personId, neoId, trinityId, unrelatedId });
        }

        var repository = new CharacterPortraitRepository(_database);
        foreach (var characterId in new[] { neoId, trinityId, unrelatedId })
        {
            await repository.UpsertAsync(new CharacterPortrait
            {
                PersonId = personId,
                FictionalEntityId = characterId,
                ImageUrl = $"https://example.test/{characterId:N}.jpg",
            });
        }

        var portraits = await repository.GetByCharacterBatchAsync([neoId, trinityId]);

        Assert.Equal(
            new[] { neoId, trinityId }.OrderBy(id => id),
            portraits.Select(portrait => portrait.FictionalEntityId).OrderBy(id => id));
    }

    [Fact]
    public async Task PendingPersonSignalDelete_RemovesOnlyTheNamedSignals()
    {
        var repository = new PendingPersonSignalRepository(_database);
        var signals = Enumerable.Range(0, 3)
            .Select(index => new PendingPersonSignal
            {
                Id = Guid.NewGuid(),
                EntityId = Guid.NewGuid(),
                Name = $"Author {index}",
                Role = "Author",
                Source = "test",
                MediaType = "Books",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            })
            .ToList();
        await repository.InsertBatchAsync(signals);

        await repository.DeleteByIdsAsync([signals[0].Id, signals[1].Id]);

        var remaining = Assert.Single(await repository.GetAllPendingAsync());
        Assert.Equal(signals[2].Id, remaining.Id);
    }
}
