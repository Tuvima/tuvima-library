using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class AiFeaturePersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CanonicalValueRepository _canonicals;
    private readonly CanonicalValueArrayRepository _arrays;
    private readonly TasteProfileRepository _tasteProfiles;
    private readonly MetadataClaimRepository _claims;

    public AiFeaturePersistenceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ai_features_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _canonicals = new CanonicalValueRepository(_db);
        _arrays = new CanonicalValueArrayRepository(_db);
        _tasteProfiles = new TasteProfileRepository(_db);
        _claims = new MetadataClaimRepository(_db);
    }

    [Fact]
    public async Task ReplaceAiFeature_IsAtomicIdempotentAndProvenanceAware()
    {
        var entityId = Guid.NewGuid();
        var request = Request(
            entityId,
            arrays: new Dictionary<string, IReadOnlyList<string>>
            {
                ["themes"] = ["survival", "ecology"],
                ["mood"] = ["tense"],
                ["content_warnings"] = ["violence"],
            },
            scalars: new Dictionary<string, string?>
            {
                ["tldr"] = "A survival story.",
                ["setting"] = "Arrakis",
            });

        var first = await _canonicals.ReplaceAiFeatureAsync(request);
        var second = await _canonicals.ReplaceAiFeatureAsync(request);

        Assert.Equal(AiFeatureStatus.ReviewRequired, first.Status);
        Assert.False(first.IsUnchanged);
        Assert.True(second.IsUnchanged);
        Assert.Equal(["survival", "ecology"], (await _arrays.GetValuesAsync(entityId, "themes")).Select(value => value.Value));

        var scalarValues = await _canonicals.GetByEntityAsync(entityId);
        var tldr = Assert.Single(scalarValues, value => value.Key == "tldr");
        Assert.Equal(WellKnownProviders.AiProvider, tldr.WinningProviderId);
        Assert.True(tldr.NeedsReview);

        var state = await _canonicals.GetAiFeatureStateAsync(entityId, "description_intelligence");
        Assert.NotNull(state);
        Assert.Equal("text_quality", state.ModelId);
        Assert.Equal("description-intelligence-v1", state.PromptVersion);
        Assert.Equal("input-1", state.InputFingerprint);
        Assert.Contains("themes", state.PublishedFields);

        var replacement = request with
        {
            ArrayValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["themes"] = ["identity"],
                ["mood"] = [],
                ["content_warnings"] = [],
            },
            ScalarValues = new Dictionary<string, string?>
            {
                ["tldr"] = null,
                ["setting"] = "Caladan",
            },
            InputFingerprint = "input-2",
        };
        await _canonicals.ReplaceAiFeatureAsync(replacement);

        Assert.Equal(["identity"], (await _arrays.GetValuesAsync(entityId, "themes")).Select(value => value.Value));
        Assert.Empty(await _arrays.GetValuesAsync(entityId, "mood"));
        Assert.DoesNotContain(await _canonicals.GetByEntityAsync(entityId), value => value.Key == "tldr");
    }

    [Fact]
    public async Task ReplaceAiFeature_DoesNotOverrideManualOrTrustedValues()
    {
        var entityId = Guid.NewGuid();
        await _arrays.SetValuesAsync(entityId, "themes",
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "manually curated" },
        ]);
        await _canonicals.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = entityId,
                Key = "setting",
                Value = "Trusted setting",
                LastScoredAt = DateTimeOffset.UtcNow,
                WinningProviderId = WellKnownProviders.Wikidata,
            },
        ]);

        var result = await _canonicals.ReplaceAiFeatureAsync(Request(
            entityId,
            arrays: new Dictionary<string, IReadOnlyList<string>> { ["themes"] = ["generated"] },
            scalars: new Dictionary<string, string?> { ["setting"] = "Generated setting" }));

        Assert.Equal(AiFeatureStatus.Protected, result.Status);
        Assert.Equal(["setting", "themes"], result.ProtectedFields.OrderBy(value => value));
        Assert.Equal("manually curated", Assert.Single(await _arrays.GetValuesAsync(entityId, "themes")).Value);
        Assert.Equal(
            "Trusted setting",
            Assert.Single(await _canonicals.GetByEntityAsync(entityId), value => value.Key == "setting").Value);
    }

    [Fact]
    public async Task ReplaceAiFeature_ProtectsArrayEditedAfterAiPublishedIt()
    {
        var entityId = Guid.NewGuid();
        var request = Request(
            entityId,
            arrays: new Dictionary<string, IReadOnlyList<string>>
            {
                ["themes"] = ["survival", "ecology"],
            },
            scalars: new Dictionary<string, string?>());

        await _canonicals.ReplaceAiFeatureAsync(request);
        await _arrays.SetValuesAsync(entityId, "themes",
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "user-curated" },
        ]);

        var result = await _canonicals.ReplaceAiFeatureAsync(request);

        Assert.False(result.IsUnchanged);
        Assert.Equal(AiFeatureStatus.Protected, result.Status);
        Assert.Contains("themes", result.ProtectedFields);
        Assert.Equal("user-curated", Assert.Single(await _arrays.GetValuesAsync(entityId, "themes")).Value);
    }

    [Fact]
    public async Task ReplaceAiFeature_GatesLowConfidenceOutputWithoutPublishing()
    {
        var entityId = Guid.NewGuid();
        var result = await _canonicals.ReplaceAiFeatureAsync(Request(
            entityId,
            arrays: new Dictionary<string, IReadOnlyList<string>> { ["themes"] = ["uncertain"] },
            scalars: new Dictionary<string, string?> { ["series_position"] = "2" },
            confidence: 0.60,
            publishThreshold: 0.80));

        Assert.Equal(AiFeatureStatus.ReviewRequired, result.Status);
        Assert.Empty(await _arrays.GetValuesAsync(entityId, "themes"));
        Assert.Empty(await _canonicals.GetByEntityAsync(entityId));
    }

    [Fact]
    public async Task TasteProfileRepository_RoundTripsCurrentProfileShape()
    {
        var userId = Guid.NewGuid();
        var profile = new TasteProfile
        {
            UserId = userId,
            GenreDistribution = new Dictionary<string, double> { ["science fiction"] = 0.75 },
            EraPreferences = new Dictionary<string, double> { ["2020s"] = 1.0 },
            MediaTypeMix = new Dictionary<string, double> { ["Books"] = 1.0 },
            MoodPreferences = new Dictionary<string, double> { ["hopeful"] = 0.5 },
            Summary = "A hopeful science-fiction reader.",
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        var build = new TasteProfileBuildResult(
            TasteProfileBuildStatus.Generated,
            userId,
            profile,
            SignalCount: 3,
            InputFingerprint: "taste-input");
        var first = await _tasteProfiles.ReplaceAiProfileAsync(TasteRequest(build));
        var second = await _tasteProfiles.ReplaceAiProfileAsync(TasteRequest(build));
        var saved = await _tasteProfiles.GetAsync(userId);
        var state = await _canonicals.GetAiFeatureStateAsync(userId, "taste_profile");

        Assert.NotNull(saved);
        Assert.False(first.IsUnchanged);
        Assert.True(second.IsUnchanged);
        Assert.Equal(profile.Summary, saved.Summary);
        Assert.Equal(0.75, saved.GenreDistribution["science fiction"]);
        Assert.Equal(profile.LastUpdatedAt, saved.LastUpdatedAt);
        Assert.Equal(AiFeatureStatus.ReviewRequired, state?.Status);
        Assert.Contains("taste_profile", state!.PublishedFields);
    }

    [Fact]
    public async Task TasteProfileRepository_PersistsInsufficientOutcomeAndRemovesStaleProjectionAtomically()
    {
        var userId = Guid.NewGuid();
        var generated = new TasteProfile
        {
            UserId = userId,
            Summary = "Existing profile",
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        await _tasteProfiles.ReplaceAiProfileAsync(TasteRequest(new TasteProfileBuildResult(
            TasteProfileBuildStatus.Generated,
            userId,
            generated,
            SignalCount: 3,
            InputFingerprint: "with-signals")));

        var outcome = new TasteProfileBuildResult(
            TasteProfileBuildStatus.InsufficientData,
            userId,
            Profile: null,
            SignalCount: 1,
            InputFingerprint: "one-signal",
            Reason: "At least 3 profile interactions are required; found 1.");
        var result = await _tasteProfiles.ReplaceAiProfileAsync(TasteRequest(outcome));
        var state = await _canonicals.GetAiFeatureStateAsync(userId, "taste_profile");

        Assert.Equal(AiFeatureStatus.InsufficientData, result.Status);
        Assert.Null(await _tasteProfiles.GetAsync(userId));
        Assert.Equal(AiFeatureStatus.InsufficientData, state?.Status);
        Assert.Equal(outcome.Reason, state?.OutcomeReason);
    }

    [Fact]
    public async Task TasteProfileRepository_RollsBackProfileWhenProvenanceWriteFails()
    {
        var userId = Guid.NewGuid();
        var profile = new TasteProfile
        {
            UserId = userId,
            Summary = "Must roll back",
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        var request = TasteRequest(new TasteProfileBuildResult(
            TasteProfileBuildStatus.Generated,
            userId,
            profile,
            SignalCount: 3,
            InputFingerprint: "rollback"));
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                """
                CREATE TRIGGER reject_taste_provenance
                BEFORE INSERT ON ai_feature_artifacts
                WHEN NEW.feature_key = 'taste_profile'
                BEGIN
                    SELECT RAISE(ABORT, 'test provenance failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => _tasteProfiles.ReplaceAiProfileAsync(request));

        Assert.Null(await _tasteProfiles.GetAsync(userId));
        Assert.Null(await _canonicals.GetAiFeatureStateAsync(userId, "taste_profile"));
    }

    [Fact]
    public async Task TasteSignals_AreStrictlyScopedToRequestedProfile()
    {
        var includedProfile = Guid.NewGuid();
        var excludedProfile = Guid.NewGuid();
        var includedAsset = SeedTasteAsset(includedProfile, "Books", 72, "science fiction", "hopeful", 2020);
        SeedTasteAsset(excludedProfile, "Movies", 100, "horror", "bleak", 1980);

        var signals = await _tasteProfiles.GetSignalsAsync(includedProfile, 50);

        var signal = Assert.Single(signals);
        Assert.Equal(includedAsset, signal.AssetId);
        Assert.Equal("Books", signal.MediaType);
        Assert.Equal(2020, signal.ReleaseYear);
        Assert.Equal(["science fiction"], signal.Genres);
        Assert.Equal(["hopeful"], signal.Moods);
    }

    [Fact]
    public async Task RecordAiFeatureFailure_RetriesThenQuarantinesPoisonWork()
    {
        var entityId = Guid.NewGuid();
        var request = new AiFeatureFailureRequest(
            entityId,
            "vibe",
            WellKnownProviders.AiProvider,
            "text_quality",
            "vibe-v1",
            "input",
            "malformed model output",
            MaxAttempts: 3,
            InitialRetryDelay: TimeSpan.Zero);

        var first = await _canonicals.RecordAiFeatureFailureAsync(request);
        var second = await _canonicals.RecordAiFeatureFailureAsync(request);
        var third = await _canonicals.RecordAiFeatureFailureAsync(request);

        Assert.Equal(AiFeatureStatus.RetryPending, first.Status);
        Assert.Equal(AiFeatureStatus.RetryPending, second.Status);
        Assert.Equal(AiFeatureStatus.Poisoned, third.Status);
        Assert.Equal(3, third.Attempts);
        Assert.False(third.CanAttempt(DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task RecordAiFeatureFailure_UnavailableModelNeverConsumesPoisonBudget()
    {
        var request = new AiFeatureFailureRequest(
            Guid.NewGuid(),
            "vibe",
            WellKnownProviders.AiProvider,
            "text_quality",
            "vibe-v1",
            "input",
            "The requested AI model is unavailable.",
            MaxAttempts: 3,
            InitialRetryDelay: TimeSpan.Zero,
            Category: BackgroundJobOutcomeCategory.UnavailableCapability);

        AiFeatureState? state = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            state = await _canonicals.RecordAiFeatureFailureAsync(request);
        }

        Assert.NotNull(state);
        Assert.Equal(AiFeatureStatus.RetryPending, state.Status);
        Assert.Equal(0, state.Attempts);
        Assert.Equal(BackgroundJobOutcomeCategory.UnavailableCapability, state.LastOutcomeCategory);
    }

    [Fact]
    public async Task RecoverUnavailableCapabilityFailures_ReleasesOnlyModelUnavailablePoisonRows()
    {
        var unavailableId = Guid.NewGuid();
        var contentFailureId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO ai_feature_artifacts
                    (entity_id, feature_key, source_provider_id, status, model_id,
                     prompt_version, input_fingerprint, attempts, last_outcome_category,
                     last_error, updated_at)
                VALUES
                    (@unavailableId, 'vibe', @providerId, 'Poisoned', 'text_quality',
                     'vibe-v1', 'unavailable', 3, 'UnavailableCapability',
                     'The requested AI model is unavailable.', @now),
                    (@contentFailureId, 'vibe', @providerId, 'Poisoned', 'text_quality',
                     'vibe-v1', 'content', 3, 'ContentFailure',
                     'The model returned malformed JSON.', @now);
                """,
                new
                {
                    unavailableId,
                    contentFailureId,
                    providerId = WellKnownProviders.AiProvider,
                    now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        Assert.Equal(1, await _canonicals.RecoverUnavailableCapabilityFailuresAsync());

        var unavailable = await _canonicals.GetAiFeatureStateAsync(unavailableId, "vibe");
        var contentFailure = await _canonicals.GetAiFeatureStateAsync(contentFailureId, "vibe");
        Assert.Equal(AiFeatureStatus.RetryPending, unavailable?.Status);
        Assert.Equal(0, unavailable?.Attempts);
        Assert.Equal(AiFeatureStatus.Poisoned, contentFailure?.Status);
        Assert.Equal(3, contentFailure?.Attempts);
        Assert.Equal(0, await _canonicals.RecoverUnavailableCapabilityFailuresAsync());
    }

    [Fact]
    public async Task MetadataClaimRepository_RoundTripsObservationAndDecisionSourcesSeparately()
    {
        var entityId = Guid.NewGuid();
        await _claims.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                ProviderId = WellKnownProviders.Wikidata,
                DecisionSourceProviderId = WellKnownProviders.AiProvider,
                ClaimKey = "author_qid",
                ClaimValue = "Q42::Douglas Adams",
                Confidence = 0.75,
                ClaimedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var claim = Assert.Single(await _claims.GetByEntityAsync(entityId));
        Assert.Equal(WellKnownProviders.Wikidata, claim.ProviderId);
        Assert.Equal(WellKnownProviders.AiProvider, claim.DecisionSourceProviderId);
    }

    [Fact]
    public void CurrentEpochStartup_AddsDecisionSourceColumnToExistingClaimsTable()
    {
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DROP TABLE metadata_claims;");
            conn.Execute(
                """
                CREATE TABLE metadata_claims (
                    id BLOB NOT NULL PRIMARY KEY,
                    entity_id BLOB NOT NULL,
                    provider_id BLOB NOT NULL REFERENCES metadata_providers(id),
                    claim_key TEXT NOT NULL,
                    claim_value TEXT NOT NULL,
                    confidence REAL NOT NULL DEFAULT 1.0,
                    claimed_at TEXT NOT NULL,
                    is_user_locked INTEGER NOT NULL DEFAULT 0
                );
                """);
        }

        _db.RunStartupChecks();

        using var verify = _db.CreateConnection();
        var columns = verify.Query<string>(
            "SELECT name FROM pragma_table_info('metadata_claims');").ToList();
        Assert.Contains("decision_source_provider_id", columns);
    }

    [Fact]
    public async Task EnrichmentCandidateQuery_RecognizesArrayBackedFields()
    {
        var entityId = Guid.NewGuid();
        await _canonicals.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = entityId,
                Key = "description",
                Value = "Description",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);

        Assert.Contains(entityId, await _canonicals.GetEntitiesNeedingEnrichmentAsync("description", "themes", 10));
        await _arrays.SetValuesAsync(entityId, "themes",
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "identity" },
        ]);
        Assert.DoesNotContain(entityId, await _canonicals.GetEntitiesNeedingEnrichmentAsync("description", "themes", 10));
    }

    [Fact]
    public async Task MalformedStoredProvenance_FailsClearly()
    {
        var entityId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO ai_feature_artifacts
                    (entity_id, feature_key, source_provider_id, status, published_fields_json, protected_fields_json, updated_at)
                VALUES
                    (@entityId, 'vibe', @providerId, 'Published', '{bad json', '[]', @updatedAt);
                """,
                new
                {
                    entityId,
                    providerId = WellKnownProviders.AiProvider,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _canonicals.GetAiFeatureStateAsync(entityId, "vibe"));
        Assert.Contains("malformed field metadata JSON", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    private static AiFeatureWriteRequest Request(
        Guid entityId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> arrays,
        IReadOnlyDictionary<string, string?> scalars,
        double confidence = ClaimConfidence.AiDescription,
        double publishThreshold = 0.65) =>
        new(
            entityId,
            "description_intelligence",
            arrays,
            scalars,
            WellKnownProviders.AiProvider,
            confidence,
            publishThreshold,
            ReviewThreshold: 0.75,
            ModelId: "text_quality",
            PromptVersion: "description-intelligence-v1",
            InputFingerprint: "input-1");

    private static TasteProfilePersistenceRequest TasteRequest(TasteProfileBuildResult build) => new(
        build,
        FeatureKey: "taste_profile",
        ProviderId: WellKnownProviders.AiProvider,
        Confidence: ClaimConfidence.AiDescription,
        PublishThreshold: 0.65,
        ReviewThreshold: 0.75,
        ModelId: "text_fast",
        PromptVersion: "taste-profile-v1");

    private Guid SeedTasteAsset(
        Guid profileId,
        string mediaType,
        double progress,
        string genre,
        string mood,
        int releaseYear)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@workId, @mediaType);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @path);
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed)
            VALUES (@profileId, @assetId, @progress, @lastAccessed);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@workId, 'release_year', @releaseYear, @lastScoredAt);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@workId, 'genre', 0, @genre), (@workId, 'mood', 0, @mood);
            """,
            new
            {
                workId,
                mediaType,
                editionId,
                assetId,
                contentHash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.media",
                profileId,
                progress,
                lastAccessed = DateTimeOffset.UtcNow.ToString("O"),
                releaseYear = releaseYear.ToString(),
                lastScoredAt = DateTimeOffset.UtcNow.ToString("O"),
                genre,
                mood,
            });
        return assetId;
    }
}
