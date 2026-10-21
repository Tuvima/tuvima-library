using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ICharacterPortraitRepository"/>.
///
/// Character portraits link a person (actor) to a fictional entity (character)
/// with optional imagery. The unique constraint on (person_id, fictional_entity_id)
/// ensures one portrait per actor–character pairing.
/// </summary>
public sealed class CharacterPortraitRepository : ICharacterPortraitRepository
{
    private const string SelectColumns = """
            SELECT id                  AS Id,
                   person_id           AS PersonId,
                   fictional_entity_id AS FictionalEntityId,
                   image_url           AS ImageUrl,
                   local_image_path    AS LocalImagePath,
                   source_provider     AS SourceProvider,
                   is_default          AS IsDefault,
                   created_at          AS CreatedAt,
                   updated_at          AS UpdatedAt
            FROM   character_portraits
        """;

    private readonly IDatabaseConnection _db;

    public CharacterPortraitRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<CharacterPortrait?> FindByIdAsync(
        Guid portraitId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<CharacterPortrait>($"""
            {SelectColumns}
            WHERE  id = @portraitId
            LIMIT  1;
            """, new { portraitId });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CharacterPortrait>> GetByCharacterAsync(
        Guid fictionalEntityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<CharacterPortrait>($"""
            {SelectColumns}
            WHERE  fictional_entity_id = @fictionalEntityId
            ORDER BY is_default DESC, created_at;
            """, new { fictionalEntityId }).AsList();

        return Task.FromResult<IReadOnlyList<CharacterPortrait>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CharacterPortrait>> GetByPersonAsync(
        Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<CharacterPortrait>($"""
            {SelectColumns}
            WHERE  person_id = @personId
            ORDER BY is_default DESC, created_at;
            """, new { personId }).AsList();

        return Task.FromResult<IReadOnlyList<CharacterPortrait>>(results);
    }

    /// <inheritdoc/>
    public Task<CharacterPortrait?> GetDefaultAsync(
        Guid fictionalEntityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<CharacterPortrait>($"""
            {SelectColumns}
            WHERE  fictional_entity_id = @fictionalEntityId
              AND  is_default = 1
            LIMIT  1;
            """, new { fictionalEntityId });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(CharacterPortrait portrait, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(portrait);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO character_portraits
                (id, person_id, fictional_entity_id, image_url, local_image_path,
                 source_provider, is_default, created_at, updated_at)
            VALUES
                (@Id, @PersonId, @FictionalEntityId, @ImageUrl, @LocalImagePath,
                 @SourceProvider, @IsDefault, @CreatedAt, @UpdatedAt)
            ON CONFLICT(person_id, fictional_entity_id) DO UPDATE SET
                image_url        = excluded.image_url,
                local_image_path = excluded.local_image_path,
                source_provider  = excluded.source_provider,
                is_default       = excluded.is_default,
                updated_at       = excluded.updated_at;
            """,
            new
            {
                Id = portrait.Id,
                PersonId = portrait.PersonId,
                FictionalEntityId = portrait.FictionalEntityId,
                portrait.ImageUrl,
                portrait.LocalImagePath,
                portrait.SourceProvider,
                IsDefault = portrait.IsDefault ? 1 : 0,
                CreatedAt = portrait.CreatedAt,
                UpdatedAt = portrait.UpdatedAt,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDefaultAsync(Guid portraitId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, transaction, innerCt) =>
        {
            // Look up the fictional_entity_id for this portrait.
            var fictionalEntityId = conn.QueryFirstOrDefault<Guid?>("""
                SELECT fictional_entity_id
                FROM   character_portraits
                WHERE  id = @portraitId
                LIMIT  1;
                """, new { portraitId }, transaction);

            if (fictionalEntityId is null)
            {
                return;
            }

            // Clear default on all portraits for this character.
            conn.Execute("""
                UPDATE character_portraits
                SET    is_default = 0,
                       updated_at = @now
                WHERE  fictional_entity_id = @fictionalEntityId
                  AND  is_default = 1;
                """, new { fictionalEntityId, now = DateTimeOffset.UtcNow }, transaction);

            // Set the target portrait as default.
            conn.Execute("""
                UPDATE character_portraits
                SET    is_default = 1,
                       updated_at = @now
                WHERE  id = @portraitId;
                """, new { portraitId, now = DateTimeOffset.UtcNow }, transaction);

        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CharacterPortrait>> GetByCharacterBatchAsync(
        IEnumerable<Guid> fictionalEntityIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(fictionalEntityIds);

        var ids = fictionalEntityIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<CharacterPortrait>>(Array.Empty<CharacterPortrait>());
        }

        using var conn = _db.CreateConnection();
        var results = conn.Query<CharacterPortrait>($"""
            {SelectColumns}
            WHERE  fictional_entity_id IN @ids
            ORDER BY fictional_entity_id, is_default DESC, created_at;
            """, new { ids = ids.Select(GuidSql.ToBlob).ToArray() }).AsList();

        return Task.FromResult<IReadOnlyList<CharacterPortrait>>(results);
    }
}
