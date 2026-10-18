using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class CollectionViewSourceRepository(IDatabaseConnection database)
    : ICollectionViewSourceRepository
{
    public Task<CollectionViewSource> AddGalleryAsync(
        AddCollectionGallerySourceCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.CollectionId, nameof(command));
        ValidateId(command.OwnerProfileId, nameof(command));
        ValidateId(command.GalleryId, nameof(command));
        ValidatePosition(command.Position);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireCollectionOwner(connection, transaction, command.CollectionId, command.OwnerProfileId, token);
            RequireOwnedGallery(connection, transaction, command.GalleryId, command.OwnerProfileId, token);
            if (connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT COUNT(1) FROM collection_view_sources
                     WHERE collection_id = @CollectionId AND gallery_id = @GalleryId;
                    """, command, transaction, cancellationToken: token)) != 0)
            {
                throw new InvalidOperationException("This Gallery is already a source for the Collection.");
            }

            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                INSERT INTO collection_view_sources
                    (id, collection_id, owner_profile_id, source_kind, gallery_id,
                     rule_version, rule_json, position, created_at, updated_at)
                VALUES
                    (@id, @CollectionId, @OwnerProfileId, 'gallery', @GalleryId,
                     NULL, NULL, @Position, @now, @now);
                """, new
            {
                id,
                command.CollectionId,
                command.OwnerProfileId,
                command.GalleryId,
                command.Position,
                now,
            }, transaction, cancellationToken: token));
            return new CollectionViewSource(id, command.CollectionId, command.OwnerProfileId,
                CollectionViewSourceKind.Gallery, command.GalleryId, null, command.Position, now, now);
        }, ct);
    }

    public Task<CollectionViewSource> AddSmartRuleAsync(
        AddCollectionViewRuleSourceCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.CollectionId, nameof(command));
        ValidateId(command.OwnerProfileId, nameof(command));
        ArgumentNullException.ThrowIfNull(command.SmartRule);
        ValidatePosition(command.Position);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireCollectionOwner(connection, transaction, command.CollectionId, command.OwnerProfileId, token);
            RequirePersonalSpace(connection, transaction, command.OwnerProfileId, token);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                INSERT INTO collection_view_sources
                    (id, collection_id, owner_profile_id, source_kind, gallery_id,
                     rule_version, rule_json, position, created_at, updated_at)
                VALUES
                    (@id, @CollectionId, @OwnerProfileId, 'smart_rule', NULL,
                     @Version, @Json, @Position, @now, @now);
                """, new
            {
                id,
                command.CollectionId,
                command.OwnerProfileId,
                command.SmartRule.Version,
                command.SmartRule.Json,
                command.Position,
                now,
            }, transaction, cancellationToken: token));
            return new CollectionViewSource(id, command.CollectionId, command.OwnerProfileId,
                CollectionViewSourceKind.SmartRule, null, command.SmartRule, command.Position, now, now);
        }, ct);
    }

    public Task<IReadOnlyList<CollectionViewSource>> ListAsync(
        Guid collectionId,
        Guid ownerProfileId,
        CancellationToken ct = default)
    {
        ValidateId(collectionId, nameof(collectionId));
        ValidateId(ownerProfileId, nameof(ownerProfileId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        RequireCollectionOwner(connection, transaction: null, collectionId, ownerProfileId, ct);
        var rows = connection.Query<SourceRow>(new CommandDefinition(
            SelectSql + """
             WHERE cvs.collection_id = @collectionId
               AND cvs.owner_profile_id = @ownerProfileId
             ORDER BY cvs.position, cvs.id;
            """, new { collectionId, ownerProfileId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<CollectionViewSource>>(rows.Select(Map).ToList());
    }

    public Task<CollectionViewSource?> UpdateAsync(
        UpdateCollectionViewSourceCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.SourceId, nameof(command));
        ValidateId(command.CollectionId, nameof(command));
        ValidateId(command.OwnerProfileId, nameof(command));
        ValidatePosition(command.Position);
        ValidateExclusiveSource(command.Kind, command.GalleryId, command.SmartRule);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireCollectionOwner(connection, transaction, command.CollectionId, command.OwnerProfileId, token);
            var existing = connection.QuerySingleOrDefault<SourceRow>(new CommandDefinition(
                SelectSql + """
                 WHERE cvs.id = @SourceId AND cvs.collection_id = @CollectionId
                   AND cvs.owner_profile_id = @OwnerProfileId;
                """, command, transaction, cancellationToken: token));
            if (existing is null)
            {
                return null;
            }

            if (command.Kind == CollectionViewSourceKind.Gallery)
            {
                RequireOwnedGallery(connection, transaction, command.GalleryId!.Value, command.OwnerProfileId, token);
            }
            else
            {
                RequirePersonalSpace(connection, transaction, command.OwnerProfileId, token);
            }

            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                UPDATE collection_view_sources SET
                    source_kind = @Kind, gallery_id = @GalleryId,
                    rule_version = @RuleVersion, rule_json = @RuleJson,
                    position = @Position, updated_at = @now
                 WHERE id = @SourceId AND collection_id = @CollectionId
                   AND owner_profile_id = @OwnerProfileId;
                """, new
            {
                command.SourceId,
                command.CollectionId,
                command.OwnerProfileId,
                Kind = ToStorage(command.Kind),
                command.GalleryId,
                RuleVersion = command.SmartRule?.Version,
                RuleJson = command.SmartRule?.Json,
                command.Position,
                now,
            }, transaction, cancellationToken: token));
            return new CollectionViewSource(command.SourceId, command.CollectionId, command.OwnerProfileId,
                command.Kind, command.GalleryId, command.SmartRule, command.Position,
                ParseDate(existing.CreatedAt), now);
        }, ct);
    }

    public Task<bool> RemoveAsync(
        Guid collectionId,
        Guid sourceId,
        Guid ownerProfileId,
        CancellationToken ct = default)
    {
        ValidateId(collectionId, nameof(collectionId));
        ValidateId(sourceId, nameof(sourceId));
        ValidateId(ownerProfileId, nameof(ownerProfileId));
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireCollectionOwner(connection, transaction, collectionId, ownerProfileId, token);
            return connection.Execute(new CommandDefinition("""
                DELETE FROM collection_view_sources
                 WHERE id = @sourceId AND collection_id = @collectionId
                   AND owner_profile_id = @ownerProfileId;
                """, new { sourceId, collectionId, ownerProfileId },
                transaction, cancellationToken: token)) != 0;
        }, ct);
    }

    public Task<CollectionViewSourceRemoval> RemoveUnlessLastMemberAsync(
        Guid collectionId,
        Guid sourceId,
        Guid ownerProfileId,
        CancellationToken ct = default)
    {
        ValidateId(collectionId, nameof(collectionId));
        ValidateId(sourceId, nameof(sourceId));
        ValidateId(ownerProfileId, nameof(ownerProfileId));
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireCollectionOwner(connection, transaction, collectionId, ownerProfileId, token);

            // One conditional DELETE: the source goes only while another member (a collection item, or a different
            // source) remains, so two concurrent removals cannot both succeed and empty the collection.
            var removed = connection.Execute(new CommandDefinition("""
                DELETE FROM collection_view_sources
                 WHERE id = @sourceId AND collection_id = @collectionId
                   AND owner_profile_id = @ownerProfileId
                   AND (EXISTS (SELECT 1 FROM collection_items i WHERE i.collection_id = @collectionId)
                        OR EXISTS (SELECT 1 FROM collection_view_sources other
                                    WHERE other.collection_id = @collectionId AND other.id <> @sourceId));
                """, new { sourceId, collectionId, ownerProfileId },
                transaction, cancellationToken: token));
            if (removed != 0)
            {
                return CollectionViewSourceRemoval.Removed;
            }

            // Nothing was deleted: either there is no such source, or it is the last member.
            var exists = connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM collection_view_sources
                 WHERE id = @sourceId AND collection_id = @collectionId
                   AND owner_profile_id = @ownerProfileId;
                """, new { sourceId, collectionId, ownerProfileId },
                transaction, cancellationToken: token)) != 0;
            return exists
                ? CollectionViewSourceRemoval.WouldLeaveCollectionEmpty
                : CollectionViewSourceRemoval.NotFound;
        }, ct);
    }

    public Task<IReadOnlyList<CollectionViewSourceProjection>> GetAuthorizedProjectionAsync(
        IReadOnlyCollection<Guid> collectionIds,
        Guid viewerProfileId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);
        ValidateId(viewerProfileId, nameof(viewerProfileId));
        var ids = collectionIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<CollectionViewSourceProjection>>([]);
        }

        ct.ThrowIfCancellationRequested();

        var parameters = new DynamicParameters();
        parameters.Add("ViewerProfileId", GuidSql.ToBlob(viewerProfileId), System.Data.DbType.Binary);
        var collectionPredicate = string.Join(" OR ", ids.Select((id, index) =>
        {
            parameters.Add($"CollectionId{index}", GuidSql.ToBlob(id), System.Data.DbType.Binary);
            return $"cvs.collection_id = @CollectionId{index}";
        }));
        using var connection = database.CreateConnection();
        var rows = connection.Query<SourceRow>(new CommandDefinition($$"""
            {{SelectSql}}
              JOIN collections c ON c.id = cvs.collection_id
             WHERE ({{collectionPredicate}})
               AND (c.scope = 'library' OR c.profile_id = @ViewerProfileId)
               AND (
                    cvs.owner_profile_id = @ViewerProfileId
                    OR (cvs.source_kind = 'gallery' AND EXISTS (
                        SELECT 1 FROM view_gallery_shares vgs
                         WHERE vgs.gallery_id = cvs.gallery_id
                           AND vgs.profile_id = @ViewerProfileId))
                   )
             ORDER BY cvs.collection_id, cvs.position, cvs.id;
            """, parameters, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<CollectionViewSourceProjection>>(rows.Select(row => new CollectionViewSourceProjection(
            row.Id, row.CollectionId, row.OwnerProfileId, ParseKind(row.SourceKind), row.GalleryId,
            row.RuleVersion, row.RuleJson, row.Position)).ToList());
    }

    private const string SelectSql = """
        SELECT cvs.id AS Id, cvs.collection_id AS CollectionId,
               cvs.owner_profile_id AS OwnerProfileId, cvs.source_kind AS SourceKind,
               cvs.gallery_id AS GalleryId, cvs.rule_version AS RuleVersion,
               cvs.rule_json AS RuleJson, cvs.position AS Position,
               cvs.created_at AS CreatedAt, cvs.updated_at AS UpdatedAt
          FROM collection_view_sources cvs
        """;

    private static void RequireCollectionOwner(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        Guid collectionId,
        Guid ownerProfileId,
        CancellationToken ct)
    {
        var row = connection.QuerySingleOrDefault<CollectionRow>(new CommandDefinition("""
            SELECT collection_type AS CollectionType, scope AS Scope, profile_id AS ProfileId
              FROM collections WHERE id = @collectionId;
            """, new { collectionId }, transaction, cancellationToken: ct));
        if (row is null)
        {
            throw new InvalidOperationException($"Collection '{collectionId:D}' does not exist.");
        }

        if (!string.Equals(row.CollectionType, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Personal-media sources are supported only by Custom Collections.");
        }

        if (connection.ExecuteScalar<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM profiles WHERE id = @ownerProfileId;",
                new { ownerProfileId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new InvalidOperationException($"Profile '{ownerProfileId:D}' does not exist.");
        }

        if (row.ProfileId.HasValue && row.ProfileId.Value != ownerProfileId)
        {
            throw new InvalidOperationException("A user-scoped Collection can use only its owner's personal-media sources.");
        }
    }

    private static void RequireOwnedGallery(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid galleryId,
        Guid ownerProfileId,
        CancellationToken ct)
    {
        if (connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM view_galleries
                 WHERE id = @galleryId AND owner_profile_id = @ownerProfileId;
                """, new { galleryId, ownerProfileId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new InvalidOperationException("Gallery does not exist or is not owned by the source profile.");
        }
    }

    private static void RequirePersonalSpace(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid ownerProfileId,
        CancellationToken ct)
    {
        if (connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM view_personal_spaces WHERE owner_profile_id = @ownerProfileId;
                """, new { ownerProfileId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new InvalidOperationException("View smart-rule owner does not have a Personal Space.");
        }
    }

    private static CollectionViewSource Map(SourceRow row) => new(
        row.Id, row.CollectionId, row.OwnerProfileId, ParseKind(row.SourceKind), row.GalleryId,
        row.RuleVersion.HasValue ? ViewSmartRuleDefinition.Create(row.RuleVersion.Value, row.RuleJson!) : null,
        row.Position, ParseDate(row.CreatedAt), ParseDate(row.UpdatedAt));

    private static void ValidateExclusiveSource(
        CollectionViewSourceKind kind,
        Guid? galleryId,
        ViewSmartRuleDefinition? rule)
    {
        if (kind == CollectionViewSourceKind.Gallery)
        {
            if (!galleryId.HasValue || galleryId == Guid.Empty || rule is not null)
            {
                throw new ArgumentException("A Gallery source requires only a Gallery ID.");
            }

            return;
        }
        if (galleryId.HasValue || rule is null)
        {
            throw new ArgumentException("A smart-rule source requires only a rule definition.");
        }
    }

    private static string ToStorage(CollectionViewSourceKind kind) => kind switch
    {
        CollectionViewSourceKind.Gallery => "gallery",
        CollectionViewSourceKind.SmartRule => "smart_rule",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CollectionViewSourceKind ParseKind(string value) => value switch
    {
        "gallery" => CollectionViewSourceKind.Gallery,
        "smart_rule" => CollectionViewSourceKind.SmartRule,
        _ => throw new InvalidOperationException($"Unsupported Collection View source kind '{value}'."),
    };

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value);
    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ID is required.", parameterName);
        }
    }
    private static void ValidatePosition(int position)
    {
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
    }

    private sealed class CollectionRow
    {
        public string CollectionType { get; init; } = string.Empty;
        public string Scope { get; init; } = string.Empty;
        public Guid? ProfileId { get; init; }
    }

    private sealed class SourceRow
    {
        public Guid Id { get; init; }
        public Guid CollectionId { get; init; }
        public Guid OwnerProfileId { get; init; }
        public string SourceKind { get; init; } = string.Empty;
        public Guid? GalleryId { get; init; }
        public int? RuleVersion { get; init; }
        public string? RuleJson { get; init; }
        public int Position { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;
    }
}
