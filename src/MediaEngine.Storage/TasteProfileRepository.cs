using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Owns the profile-specific taste projection, its interaction input, and the
/// atomic publication of the profile together with AI provenance.
/// </summary>
public sealed class TasteProfileRepository : ITasteProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = MediaEngineJson.Web;
    private readonly IDatabaseConnection _db;

    public TasteProfileRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<TasteProfile?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var json = conn.QueryFirstOrDefault<string>(
            "SELECT profile_json FROM user_taste_profiles WHERE user_id = @userId LIMIT 1;",
            new { userId });
        if (json is null)
        {
            return Task.FromResult<TasteProfile?>(null);
        }

        try
        {
            return Task.FromResult(JsonSerializer.Deserialize<TasteProfile>(json, JsonOptions));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Stored taste profile for user {userId} contains malformed JSON.",
                ex);
        }
    }

    public Task<IReadOnlyList<TasteSignal>> GetSignalsAsync(
        Guid userId,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Taste signal limit must be between 1 and 1000.");
        }

        using var conn = _db.CreateConnection();
        // The borrowed audiobook year is worked out once for the profile's played audiobook files, not once per row
        // (and never from the whole library's credit list). The limit below is not applied here; a longer list is fine.
        var relatedBookYearCte = MediaDateSql.RelatedBookOriginalYearCte(
            "related_book_year",
            """
            -- readiness: candidates for the borrowed year only; the read's own rows carry the Ready filter
            SELECT candidate_work.id AS work_id, candidate_state.asset_id AS asset_id
            FROM user_states candidate_state
            JOIN media_assets candidate_asset ON candidate_asset.id = candidate_state.asset_id
            JOIN editions candidate_edition ON candidate_edition.id = candidate_asset.edition_id
            JOIN works candidate_work ON candidate_work.id = candidate_edition.work_id
            WHERE candidate_state.user_id = @userId
              AND LOWER(COALESCE(candidate_work.media_type, '')) LIKE '%audio%'
            """,
            "rby_");
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gp.id, p.id, w.id)",
            "us.asset_id",
            "w.media_type",
            MediaDateSql.RelatedBookYearLookup("related_book_year", "w.id", "us.asset_id"));
        // user_states is the only persisted interaction source keyed by profile.
        // Metadata rating claims are deliberately excluded because they have no
        // profile id and therefore cannot be attributed to the requested user.
        var interactions = conn.Query<InteractionRow>(
            $"""
            WITH {relatedBookYearCte}
            SELECT us.asset_id AS AssetId,
                   us.progress_pct AS ProgressPct,
                   us.last_accessed AS LastAccessed,
                   e.work_id AS WorkId,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   w.media_type AS MediaType,
                   {displayYearSql} AS ReleaseYear
            FROM user_states us
            INNER JOIN media_assets a ON a.id = us.asset_id
            INNER JOIN editions e ON e.id = a.edition_id
            INNER JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE us.user_id = @userId
            ORDER BY us.last_accessed DESC
            LIMIT @limit;
            """,
            new { userId, limit }).AsList();
        if (interactions.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<TasteSignal>>([]);
        }

        var arrayRows = conn.Query<ArrayRow>(
            """
            SELECT DISTINCT cva.entity_id AS EntityId,
                            cva.key AS Key,
                            cva.ordinal AS Ordinal,
                            cva.value AS Value
            FROM user_states us
            INNER JOIN media_assets a ON a.id = us.asset_id
            INNER JOIN editions e ON e.id = a.edition_id
            INNER JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            INNER JOIN canonical_value_arrays cva
                ON cva.entity_id IN (us.asset_id, e.work_id, COALESCE(gp.id, p.id, w.id))
            WHERE us.user_id = @userId
              AND cva.key IN ('genre', 'vibe', 'mood')
            ORDER BY cva.entity_id, cva.key, cva.ordinal;
            """,
            new { userId }).AsList();

        var arraysByEntity = arrayRows
            .GroupBy(row => row.EntityId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var signals = interactions.Select(row =>
        {
            var scopes = new[] { row.WorkId, row.RootWorkId, row.AssetId }.Distinct().ToArray();
            var metadataArrays = scopes
                .SelectMany(scope => arraysByEntity.GetValueOrDefault(scope) ?? [])
                .ToList();
            var genres = metadataArrays
                .Where(value => value.Key.Equals("genre", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var moods = metadataArrays
                .Where(value => value.Key.Equals("vibe", StringComparison.OrdinalIgnoreCase)
                                || value.Key.Equals("mood", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new TasteSignal(
                row.AssetId,
                row.ProgressPct,
                DateTimeOffset.Parse(row.LastAccessed),
                row.MediaType,
                int.TryParse(row.ReleaseYear, out var releaseYear) ? releaseYear : null,
                genres,
                moods);
        }).ToList();

        return Task.FromResult<IReadOnlyList<TasteSignal>>(signals);
    }

    public Task<AiFeatureWriteResult> ReplaceAiProfileAsync(
        TasteProfilePersistenceRequest request,
        CancellationToken ct = default)
    {
        Validate(request);
        ct.ThrowIfCancellationRequested();

        var build = request.BuildResult;
        var profileJson = build.Profile is null
            ? null
            : JsonSerializer.Serialize(build.Profile, JsonOptions);
        var outputFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            profileJson ?? $"insufficient:{build.Reason}")));
        var status = build.Status == TasteProfileBuildStatus.InsufficientData
            ? AiFeatureStatus.InsufficientData
            : request.Confidence < request.PublishThreshold
                ? AiFeatureStatus.ReviewRequired
                : request.Confidence < request.ReviewThreshold
                    ? AiFeatureStatus.ReviewRequired
                    : AiFeatureStatus.Published;
        var publishes = build.Status == TasteProfileBuildStatus.Generated
                        && request.Confidence >= request.PublishThreshold;
        var publishedFields = publishes ? new[] { "taste_profile" } : [];
        var publishedValues = publishes
            ? new Dictionary<string, IReadOnlyList<string>> { ["taste_profile"] = [profileJson!] }
            : new Dictionary<string, IReadOnlyList<string>>();
        var now = DateTimeOffset.UtcNow;

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var existing = conn.QueryFirstOrDefault<ArtifactRow>(
                """
                SELECT status AS Status,
                       input_fingerprint AS InputFingerprint,
                       output_fingerprint AS OutputFingerprint,
                       attempts AS Attempts
                FROM ai_feature_artifacts
                WHERE entity_id = @userId AND feature_key = @featureKey
                LIMIT 1;
                """,
                new { userId = build.UserId, featureKey = request.FeatureKey },
                transaction: tx);
            var currentProfileJson = conn.QueryFirstOrDefault<string>(
                "SELECT profile_json FROM user_taste_profiles WHERE user_id = @userId LIMIT 1;",
                new { userId = build.UserId },
                transaction: tx);
            var expectedProfileMatches = publishes
                ? string.Equals(currentProfileJson, profileJson, StringComparison.Ordinal)
                : currentProfileJson is null;
            if (existing is not null
                && existing.Attempts == 0
                && string.Equals(existing.Status, status.ToString(), StringComparison.Ordinal)
                && string.Equals(existing.InputFingerprint, build.InputFingerprint, StringComparison.Ordinal)
                && string.Equals(existing.OutputFingerprint, outputFingerprint, StringComparison.Ordinal)
                && expectedProfileMatches)
            {
                return new AiFeatureWriteResult(status, publishedFields, [], IsUnchanged: true);
            }

            if (publishes)
            {
                conn.Execute(
                    """
                    INSERT INTO user_taste_profiles (user_id, profile_json, summary, updated_at)
                    VALUES (@userId, @profileJson, @summary, @updatedAt)
                    ON CONFLICT(user_id) DO UPDATE SET
                        profile_json = excluded.profile_json,
                        summary = excluded.summary,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        userId = build.UserId,
                        profileJson,
                        summary = build.Profile!.Summary,
                        updatedAt = build.Profile.LastUpdatedAt.ToString("O"),
                    },
                    transaction: tx);
            }
            else
            {
                conn.Execute(
                    "DELETE FROM user_taste_profiles WHERE user_id = @userId;",
                    new { userId = build.UserId },
                    transaction: tx);
            }

            conn.Execute(
                """
                INSERT INTO ai_feature_artifacts
                    (entity_id, feature_key, source_provider_id, status, confidence,
                     model_id, prompt_version, input_fingerprint, output_fingerprint,
                     attempts, next_retry_at, last_error, outcome_reason,
                     published_fields_json, protected_fields_json, published_values_json, updated_at)
                VALUES
                    (@EntityId, @FeatureKey, @ProviderId, @Status, @Confidence,
                     @ModelId, @PromptVersion, @InputFingerprint, @OutputFingerprint,
                     0, NULL, NULL, @OutcomeReason,
                     @PublishedFieldsJson, '[]', @PublishedValuesJson, @UpdatedAt)
                ON CONFLICT(entity_id, feature_key) DO UPDATE SET
                    source_provider_id = excluded.source_provider_id,
                    status = excluded.status,
                    confidence = excluded.confidence,
                    model_id = excluded.model_id,
                    prompt_version = excluded.prompt_version,
                    input_fingerprint = excluded.input_fingerprint,
                    output_fingerprint = excluded.output_fingerprint,
                    attempts = 0,
                    next_retry_at = NULL,
                    last_error = NULL,
                    outcome_reason = excluded.outcome_reason,
                    published_fields_json = excluded.published_fields_json,
                    protected_fields_json = '[]',
                    published_values_json = excluded.published_values_json,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    EntityId = build.UserId,
                    FeatureKey = request.FeatureKey,
                    ProviderId = request.ProviderId,
                    Status = status.ToString(),
                    Confidence = build.Status == TasteProfileBuildStatus.Generated
                        ? request.Confidence
                        : (double?)null,
                    request.ModelId,
                    request.PromptVersion,
                    build.InputFingerprint,
                    OutputFingerprint = outputFingerprint,
                    OutcomeReason = build.Reason,
                    PublishedFieldsJson = JsonSerializer.Serialize(publishedFields),
                    PublishedValuesJson = JsonSerializer.Serialize(publishedValues),
                    UpdatedAt = now.ToString("O"),
                },
                transaction: tx);

            return new AiFeatureWriteResult(status, publishedFields, [], IsUnchanged: false);
        }, ct);
    }

    private static void Validate(TasteProfilePersistenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.BuildResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FeatureKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BuildResult.InputFingerprint);
        if (request.BuildResult.Status == TasteProfileBuildStatus.Generated
            && request.BuildResult.Profile?.UserId != request.BuildResult.UserId)
        {
            throw new ArgumentException("Generated taste profile must match the build-result user.", nameof(request));
        }

        if (request.BuildResult.Status == TasteProfileBuildStatus.InsufficientData
            && (request.BuildResult.Profile is not null || string.IsNullOrWhiteSpace(request.BuildResult.Reason)))
        {
            throw new ArgumentException("Insufficient-data outcome requires a reason and no generated profile.", nameof(request));
        }

        if (request.Confidence is < 0 or > 1
            || request.PublishThreshold is < 0 or > 1
            || request.ReviewThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Confidence thresholds must be between zero and one.");
        }
    }

    private sealed class InteractionRow
    {
        public Guid AssetId { get; init; }
        public double ProgressPct { get; init; }
        public string LastAccessed { get; init; } = string.Empty;
        public Guid WorkId { get; init; }
        public Guid RootWorkId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? ReleaseYear { get; init; }
    }

    private sealed class ArrayRow
    {
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public int Ordinal { get; init; }
        public string Value { get; init; } = string.Empty;
    }

    private sealed class ArtifactRow
    {
        public string Status { get; init; } = string.Empty;
        public string? InputFingerprint { get; init; }
        public string? OutputFingerprint { get; init; }
        public int Attempts { get; init; }
    }
}
