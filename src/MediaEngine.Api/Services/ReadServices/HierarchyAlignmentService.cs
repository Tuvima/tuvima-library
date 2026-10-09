using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class HierarchyAlignmentService(IDatabaseConnection db, IHydrationPipelineService pipeline) : IHierarchyAlignmentService
{
    public Task<MembershipPreviewEnvelope?> PreviewAsync(Guid entityId, MembershipPreviewRequest request, CancellationToken ct) => BuildMembershipPreviewAsync(entityId, request, db, ct);
    public Task<MembershipPreviewEnvelope?> ApplyAsync(Guid entityId, MembershipPreviewRequest request, CancellationToken ct) => ApplyMembershipChangeAsync(entityId, request, db, pipeline, ct);
    public Task<MembershipPreviewEnvelope?> ApplyRetailIdentityAsync(Guid entityId, MembershipPreviewRequest request, HierarchyIdentityMutation identityMutation, CancellationToken ct) => ApplyRetailIdentityAsync(entityId, request, identityMutation, db, ct);
    private static string NormalizeEditorMediaType(string? mediaType) => (mediaType ?? string.Empty).Trim() switch { "Book" => "Books", "Comic" => "Comics", "" => "Books", var value => value };
    internal static async Task<MembershipPreviewEnvelope?> ApplyRetailIdentityAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        HierarchyIdentityMutation identityMutation,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identityMutation);

        // Re-plan under the write lock. The preview is intentionally performed
        // first only to give a fast, side-effect-free conflict result; the
        // transaction repeats planning against the current state before it
        // mutates either membership or retail identity artifacts.
        var preview = await BuildMembershipPreviewAsync(entityId, request, db, ct);
        if (preview is null)
        {
            return null;
        }

        return await db.ExecuteWriteAsync<MembershipPreviewEnvelope?>((conn, tx, innerCt) =>
        {
            var entityRow = conn.QueryFirstOrDefault<MembershipEntityRow>("""
                SELECT LOWER(HEX(w.id)) AS WorkIdValue,
                       w.media_type     AS MediaType,
                       w.work_kind      AS WorkKind,
                       CASE WHEN w.parent_work_id IS NULL THEN NULL ELSE LOWER(HEX(w.parent_work_id)) END AS ParentWorkIdValue,
                       w.ordinal        AS Ordinal,
                       w.parent_key     AS ParentKey,
                       LOWER(HEX(COALESCE(gp.id, p.id, w.id))) AS RootWorkIdValue
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.id = @entityId
                LIMIT 1;
                """, new { entityId }, tx);
            if (entityRow is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(identityMutation.ExpectedIdentityRevision))
            {
                var revisionEntityId = identityMutation.IdentityRevisionEntityId ?? entityRow.WorkId;
                var currentRevision = conn.QueryFirstOrDefault<string>("""
                    SELECT value FROM canonical_values
                    WHERE entity_id = @revisionEntityId AND key = @identityRevisionKey
                    ORDER BY last_scored_at DESC
                    LIMIT 1;
                    """, new { revisionEntityId, identityRevisionKey = MetadataFieldConstants.IdentityRevision }, tx) ?? "<none>";
                if (!string.Equals(currentRevision, identityMutation.ExpectedIdentityRevision, StringComparison.Ordinal))
                {
                    var currentPath = BuildMembershipPath(conn, entityRow.WorkId, tx, innerCt);
                    return new MembershipPreviewEnvelope(
                        "conflict", currentPath, currentPath, false, false, false,
                        entityRow.WorkId, entityRow.RootWorkId, entityRow.ParentWorkId,
                        "The retail match changed while this item was open.",
                        "Reload the item and review the selected match again.");
                }
            }

            var plan = ResolveMembershipPlan(entityRow, request);
            var finalized = FinalizeMembershipPlan(conn, tx, plan, request, applyChanges: true, innerCt);
            if (!finalized.CanApply && string.Equals(finalized.Action, "conflict", StringComparison.OrdinalIgnoreCase))
            {
                return finalized;
            }

            var finalizedMutation = RemapParentScopedRetailIdentity(identityMutation, finalized.TargetRootEntityId, plan.MediaType);
            ApplyRetailIdentityMutation(
                conn,
                tx,
                finalizedMutation,
                innerCt);
            return finalized with { Applied = true };
        }, ct);
    }

    private static HierarchyIdentityMutation RemapParentScopedRetailIdentity(
        HierarchyIdentityMutation mutation,
        Guid targetRootEntityId,
        string mediaType)
    {
        if (!Enum.TryParse<MediaType>(mediaType, true, out var parsedMediaType)
            || targetRootEntityId == Guid.Empty)
        {
            return mutation;
        }

        bool IsTargetRootScoped(string key) => ClaimScopeCatalog.IsParentScoped(key, parsedMediaType);
        return mutation with
        {
            Claims = mutation.Claims.Select(item => IsTargetRootScoped(item.Key) ? item with { EntityId = targetRootEntityId } : item).ToList(),
            CanonicalValues = mutation.CanonicalValues.Select(item => IsTargetRootScoped(item.Key) ? item with { EntityId = targetRootEntityId } : item).ToList(),
            BridgeIds = mutation.BridgeIds.Select(item => IsTargetRootScoped(item.Key) ? item with { EntityId = targetRootEntityId } : item).ToList(),
            StaleArtifacts = mutation.StaleArtifacts.Select(item => IsTargetRootScoped(item.Key) ? item with { EntityId = targetRootEntityId } : item).ToList(),
            ExternalIdentifierMutations = (mutation.ExternalIdentifierMutations ?? [])
                .SelectMany(item => SplitExternalIdentifierMutation(item, targetRootEntityId, IsTargetRootScoped))
                .ToList(),
        };
    }

    private static IEnumerable<HierarchyExternalIdentifierMutation> SplitExternalIdentifierMutation(
        HierarchyExternalIdentifierMutation mutation,
        Guid targetRootEntityId,
        Func<string, bool> isTargetRootScoped)
    {
        var targets = mutation.KeysToRemove
            .Concat(mutation.Replacements.Keys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(key => isTargetRootScoped(key) ? targetRootEntityId : mutation.EntityId);
        foreach (var target in targets)
        {
            yield return new HierarchyExternalIdentifierMutation(
                target.Key,
                mutation.KeysToRemove.Where(key => target.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList(),
                mutation.Replacements
                    .Where(pair => target.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal static async Task<MembershipPreviewEnvelope?> BuildMembershipPreviewAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var entityRow = conn.QueryFirstOrDefault<MembershipEntityRow>("""
            SELECT LOWER(HEX(w.id)) AS WorkIdValue,
                   w.media_type     AS MediaType,
                   w.work_kind      AS WorkKind,
                   CASE WHEN w.parent_work_id IS NULL THEN NULL ELSE LOWER(HEX(w.parent_work_id)) END AS ParentWorkIdValue,
                   w.ordinal        AS Ordinal,
                   w.parent_key     AS ParentKey,
                   LOWER(HEX(COALESCE(gp.id, p.id, w.id))) AS RootWorkIdValue
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @entityId
            LIMIT 1;
            """, new { entityId });

        if (entityRow is null)
        {
            return null;
        }

        var plan = ResolveMembershipPlan(entityRow, request);
        return await FinalizeMembershipPreviewAsync(conn, plan, request, ct);
    }

    internal static async Task<MembershipPreviewEnvelope?> ApplyMembershipChangeAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        IDatabaseConnection db,
        IHydrationPipelineService pipeline,
        CancellationToken ct)
    {
        var preview = await BuildMembershipPreviewAsync(entityId, request, db, ct);
        if (preview is null)
        {
            return null;
        }

        if (!preview.CanApply || string.Equals(preview.Action, "none", StringComparison.OrdinalIgnoreCase))
        {
            return preview with { Applied = false };
        }

        // The Stage 2 pipeline enqueue below must happen after the write lock is
        // released — IHydrationPipelineService may persist through its own
        // repository path, which could try to acquire the same non-reentrant write
        // lock. The media type is captured from inside the transaction body and the
        // enqueue call is hoisted outside ExecuteWriteAsync to avoid that
        // deadlock risk.
        string? finalizedMediaType = null;
        var finalized = await db.ExecuteWriteAsync<MembershipPreviewEnvelope?>((conn, tx, innerCt) =>
        {
            var entityRow = conn.QueryFirstOrDefault<MembershipEntityRow>("""
                SELECT LOWER(HEX(w.id)) AS WorkIdValue,
                       w.media_type     AS MediaType,
                       w.work_kind      AS WorkKind,
                       CASE WHEN w.parent_work_id IS NULL THEN NULL ELSE LOWER(HEX(w.parent_work_id)) END AS ParentWorkIdValue,
                       w.ordinal        AS Ordinal,
                       w.parent_key     AS ParentKey,
                       LOWER(HEX(COALESCE(gp.id, p.id, w.id))) AS RootWorkIdValue
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.id = @entityId
                LIMIT 1;
                """, new { entityId }, tx);

            if (entityRow is null)
            {
                return null;
            }

            var plan = ResolveMembershipPlan(entityRow, request);
            finalizedMediaType = plan.MediaType;
            return FinalizeMembershipPlan(conn, tx, plan, request, applyChanges: true, innerCt);
        }, ct);

        if (finalized is null)
        {
            return null;
        }

        if (finalized.Stage2TargetEntityId.HasValue)
        {
            await QueueRetailParentStage2Async(pipeline, finalized.Stage2TargetEntityId.Value, finalizedMediaType!, request, ct);
        }

        return finalized with { Applied = true };
    }

    private static MembershipSuggestionSelection? GetRequestSuggestion(
        IReadOnlyDictionary<string, MembershipSuggestionSelection> suggestions,
        string key) =>
        suggestions.TryGetValue(key, out var suggestion) ? suggestion : null;

    private static void UpsertRetailIdentity(
        SqliteConnection conn,
        SqliteTransaction tx,
        Guid workId,
        MembershipSuggestionSelection suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion.ExternalIdKey) && !string.IsNullOrWhiteSpace(suggestion.ExternalIdValue))
        {
            conn.Execute(
                """
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value, provider_id, created_at)
                VALUES (@id, @entityId, @idType, @idValue, @providerId, @createdAt)
                ON CONFLICT(entity_id, id_type) DO UPDATE SET
                    id_value = excluded.id_value,
                    provider_id = excluded.provider_id;
                """,
                new
                {
                    id = Guid.NewGuid(),
                    entityId = workId,
                    idType = suggestion.ExternalIdKey,
                    idValue = suggestion.ExternalIdValue,
                    providerId = ResolveProviderId(suggestion.ProviderName)?.ToString(),
                    createdAt = DateTimeOffset.UtcNow.ToString("o"),
                },
                tx);

            UpsertExternalIdentifiersJson(conn, tx, workId, suggestion.ExternalIdKey, suggestion.ExternalIdValue);
            if (string.Equals(suggestion.ExternalIdKey, BridgeIdKeys.TvdbId, StringComparison.OrdinalIgnoreCase))
            {
                UpsertCanonicalValues(conn, tx, workId,
                    new Dictionary<string, string?> { [BridgeIdKeys.TvdbId] = suggestion.ExternalIdValue });
            }
        }

        if (!string.IsNullOrWhiteSpace(suggestion.ProviderItemId) && !string.IsNullOrWhiteSpace(suggestion.ProviderName))
        {
            UpsertExternalIdentifiersJson(conn, tx, workId, $"{suggestion.ProviderName}_item_id", suggestion.ProviderItemId);
        }
    }

    private static void UpsertExternalIdentifiersJson(
        SqliteConnection conn,
        SqliteTransaction tx,
        Guid workId,
        string key,
        string value)
    {
        var currentJson = conn.QueryFirstOrDefault<string?>(
            "SELECT external_identifiers FROM works WHERE id = @workId LIMIT 1;",
            new { workId },
            tx);

        Dictionary<string, string> current;
        try
        {
            current = string.IsNullOrWhiteSpace(currentJson)
                ? new(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(currentJson!) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            current = new(StringComparer.OrdinalIgnoreCase);
        }

        current[key] = value;

        conn.Execute(
            "UPDATE works SET external_identifiers = @json WHERE id = @workId;",
            new { json = JsonSerializer.Serialize(current), workId },
            tx);
    }

    private static Guid? ResolveProviderId(string? providerName)
    {
        var normalized = (providerName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "tvdb" => WellKnownProviders.Tvdb,
            "tmdb" => WellKnownProviders.Tmdb,
            "musicbrainz" => WellKnownProviders.MusicBrainz,
            "apple_music" => WellKnownProviders.AppleApi,
            "apple_books" => WellKnownProviders.AppleApi,
            "open_library" => WellKnownProviders.OpenLibrary,
            "comicvine" => WellKnownProviders.ComicVine,
            "comic_vine" => WellKnownProviders.ComicVine,
            _ => null,
        };
    }

    private static async Task QueueRetailParentStage2Async(
        IHydrationPipelineService pipeline,
        Guid workId,
        string mediaType,
        MembershipPreviewRequest request,
        CancellationToken ct)
    {
        var selected = request.SelectedSuggestions?
            .Values
            .FirstOrDefault(suggestion =>
                string.Equals(suggestion.Source, "retail", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(suggestion.ExternalIdKey)
                && !string.IsNullOrWhiteSpace(suggestion.ExternalIdValue));

        if (selected is null
            || !Enum.TryParse<MediaType>(mediaType, true, out var parsedMediaType))
        {
            return;
        }

        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = selected.Label,
        };

        hints[selected.ExternalIdKey!] = selected.ExternalIdValue!;

        await pipeline.EnqueueAsync(new HarvestRequest
        {
            EntityId = workId,
            EntityType = EntityType.Work,
            MediaType = parsedMediaType,
            Hints = hints,
            SkipRetailStage = true,
            IsUserResolution = true,
            SuppressReviewCreation = true,
        }, ct);
    }

    private static MembershipPlan ResolveMembershipPlan(
        MembershipEntityRow entityRow,
        MembershipPreviewRequest request)
    {
        var mediaType = NormalizeEditorMediaType(entityRow.MediaType);
        var fields = request.FieldValues ?? new(StringComparer.OrdinalIgnoreCase);
        var targets = request.SelectedTargetIds ?? new(StringComparer.OrdinalIgnoreCase);
        var workKind = entityRow.WorkKind.Trim().ToLowerInvariant();

        if (mediaType == "TV")
        {
            if (string.Equals(workKind, "child", StringComparison.OrdinalIgnoreCase))
            {
                var showName = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "show_name"));
                var seasonNumber = ParseNavigatorOrdinal(GetRequestValue(fields, "season_number"), null);
                var episodeNumber = ParseNavigatorOrdinal(GetRequestValue(fields, "episode_number"), entityRow.Ordinal);
                return new MembershipPlan(
                    Action: "move_child",
                    MediaType: mediaType,
                    CurrentEntityId: entityRow.WorkId,
                    CurrentParentEntityId: entityRow.ParentWorkId,
                    CurrentRootEntityId: entityRow.RootWorkId,
                    CurrentOrdinal: entityRow.Ordinal,
                    RequestedTitle: StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "episode_title")),
                    RequestedParentLabel: showName,
                    RequestedSecondaryLabel: seasonNumber?.ToString(CultureInfo.InvariantCulture),
                    RequestedParentKey: BuildHierarchyParentKey(showName),
                    RequestedOrdinal: episodeNumber,
                    SelectedPrimaryTargetId: GetRequestTarget(targets, "show"),
                    SelectedSecondaryTargetId: GetRequestTarget(targets, "season"));
            }

            if (string.Equals(workKind, "parent", StringComparison.OrdinalIgnoreCase) && entityRow.ParentWorkId.HasValue)
            {
                var seasonNumber = ParseNavigatorOrdinal(GetRequestValue(fields, "season_number"), entityRow.Ordinal);
                return new MembershipPlan(
                    Action: "move_season",
                    MediaType: mediaType,
                    CurrentEntityId: entityRow.WorkId,
                    CurrentParentEntityId: entityRow.ParentWorkId,
                    CurrentRootEntityId: entityRow.RootWorkId,
                    CurrentOrdinal: entityRow.Ordinal,
                    RequestedTitle: null,
                    RequestedParentLabel: null,
                    RequestedSecondaryLabel: seasonNumber?.ToString(CultureInfo.InvariantCulture),
                    RequestedParentKey: null,
                    RequestedOrdinal: seasonNumber,
                    SelectedPrimaryTargetId: null,
                    SelectedSecondaryTargetId: null);
            }

            var renamedShowName = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "show_name"));
            return new MembershipPlan(
                Action: "rename_container",
                MediaType: mediaType,
                CurrentEntityId: entityRow.WorkId,
                CurrentParentEntityId: entityRow.ParentWorkId,
                CurrentRootEntityId: entityRow.RootWorkId,
                CurrentOrdinal: entityRow.Ordinal,
                RequestedTitle: renamedShowName,
                RequestedParentLabel: renamedShowName,
                RequestedSecondaryLabel: null,
                RequestedParentKey: BuildHierarchyParentKey(renamedShowName),
                RequestedOrdinal: entityRow.Ordinal,
                SelectedPrimaryTargetId: null,
                SelectedSecondaryTargetId: null);
        }

        if (mediaType == "Music")
        {
            if (string.Equals(workKind, "child", StringComparison.OrdinalIgnoreCase))
            {
                var artist = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "artist"), GetRequestValue(fields, "album_artist"));
                var album = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "album"));
                var trackNumber = ParseNavigatorOrdinal(GetRequestValue(fields, "track_number"), entityRow.Ordinal);
                return new MembershipPlan(
                    Action: "move_child",
                    MediaType: mediaType,
                    CurrentEntityId: entityRow.WorkId,
                    CurrentParentEntityId: entityRow.ParentWorkId,
                    CurrentRootEntityId: entityRow.RootWorkId,
                    CurrentOrdinal: entityRow.Ordinal,
                    RequestedTitle: StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "title")),
                    RequestedParentLabel: album,
                    RequestedSecondaryLabel: artist,
                    RequestedParentKey: BuildHierarchyParentKey(artist, album),
                    RequestedOrdinal: trackNumber,
                    SelectedPrimaryTargetId: GetRequestTarget(targets, "album"),
                    SelectedSecondaryTargetId: null);
            }

            var renamedArtist = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "artist"), GetRequestValue(fields, "album_artist"));
            var renamedAlbum = StringHelpers.FirstNonBlankOr(string.Empty, GetRequestValue(fields, "album"));
            return new MembershipPlan(
                Action: "rename_container",
                MediaType: mediaType,
                CurrentEntityId: entityRow.WorkId,
                CurrentParentEntityId: entityRow.ParentWorkId,
                CurrentRootEntityId: entityRow.RootWorkId,
                CurrentOrdinal: entityRow.Ordinal,
                RequestedTitle: renamedAlbum,
                RequestedParentLabel: renamedAlbum,
                RequestedSecondaryLabel: renamedArtist,
                RequestedParentKey: BuildHierarchyParentKey(renamedArtist, renamedAlbum),
                RequestedOrdinal: entityRow.Ordinal,
                SelectedPrimaryTargetId: null,
                SelectedSecondaryTargetId: null);
        }

        // Books, audiobooks, comic issues/volumes, and movies all use the same
        // one-level series placement model when their current metadata supplies
        // a series value.  The physical edition and media asset remain attached
        // to this leaf; only its structural work placement is updated below.
        if (mediaType is "Books" or "Audiobooks" or "Comics" or "Movies"
            && !string.Equals(workKind, "parent", StringComparison.OrdinalIgnoreCase))
        {
            var series = GetRequestValue(fields, MetadataFieldConstants.Series);
            if (!string.IsNullOrWhiteSpace(series))
            {
                var author = StringHelpers.FirstNonBlankOr(
                    string.Empty,
                    GetRequestValue(fields, MetadataFieldConstants.Author),
                    GetRequestValue(fields, "creator"),
                    GetRequestValue(fields, MetadataFieldConstants.Director));
                var position = ParseNavigatorOrdinal(
                    GetRequestValue(fields, MetadataFieldConstants.SeriesPosition),
                    entityRow.Ordinal);

                return new MembershipPlan(
                    Action: "move_child",
                    MediaType: mediaType,
                    CurrentEntityId: entityRow.WorkId,
                    CurrentParentEntityId: entityRow.ParentWorkId,
                    CurrentRootEntityId: entityRow.RootWorkId,
                    CurrentOrdinal: entityRow.Ordinal,
                    RequestedTitle: GetRequestValue(fields, MetadataFieldConstants.Title),
                    RequestedParentLabel: series,
                    RequestedSecondaryLabel: author,
                    RequestedParentKey: BuildHierarchyParentKey(author, series),
                    RequestedOrdinal: position,
                    SelectedPrimaryTargetId: GetRequestTarget(targets, "series"),
                    SelectedSecondaryTargetId: null);
            }
        }

        return new MembershipPlan(
            Action: "none",
            MediaType: mediaType,
            CurrentEntityId: entityRow.WorkId,
            CurrentParentEntityId: entityRow.ParentWorkId,
            CurrentRootEntityId: entityRow.RootWorkId,
            CurrentOrdinal: entityRow.Ordinal,
            RequestedTitle: null,
            RequestedParentLabel: null,
            RequestedSecondaryLabel: null,
            RequestedParentKey: null,
            RequestedOrdinal: entityRow.Ordinal,
            SelectedPrimaryTargetId: null,
            SelectedSecondaryTargetId: null);
    }

    private static Task<MembershipPreviewEnvelope> FinalizeMembershipPreviewAsync(
        SqliteConnection conn,
        MembershipPlan plan,
        MembershipPreviewRequest request,
        CancellationToken ct) =>
        Task.FromResult(FinalizeMembershipPlan(conn, null, plan, request, applyChanges: false, ct));

    private static MembershipPreviewEnvelope FinalizeMembershipPlan(
        SqliteConnection conn,
        SqliteTransaction? tx,
        MembershipPlan plan,
        MembershipPreviewRequest request,
        bool applyChanges,
        CancellationToken ct)
    {
        var currentPath = BuildMembershipPath(conn, plan.CurrentEntityId, tx, ct);

        if (string.Equals(plan.Action, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new MembershipPreviewEnvelope("none", currentPath, currentPath, false, false, false, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "No structural change is needed.", null);
        }

        if (string.Equals(plan.Action, "rename_container", StringComparison.OrdinalIgnoreCase))
        {
            var currentKey = conn.QueryFirstOrDefault<string>("SELECT parent_key FROM works WHERE id = @workId LIMIT 1;", new { workId = plan.CurrentEntityId }, tx);
            if (string.IsNullOrWhiteSpace(plan.RequestedParentKey) || string.Equals(currentKey, plan.RequestedParentKey, StringComparison.OrdinalIgnoreCase))
            {
                return new MembershipPreviewEnvelope("none", currentPath, currentPath, false, false, false, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "No container identity change was detected.", null);
            }

            if (applyChanges)
            {
                conn.Execute("UPDATE works SET parent_key = @parentKey WHERE id = @workId;", new { parentKey = plan.RequestedParentKey, workId = plan.CurrentEntityId }, tx);
                UpsertContainerIdentity(conn, tx!, plan);
            }

            return new MembershipPreviewEnvelope("rename_container", currentPath, BuildRenamedContainerPath(plan), false, true, applyChanges, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "The container identity will be updated.", null);
        }

        if (string.Equals(plan.Action, "move_season", StringComparison.OrdinalIgnoreCase))
        {
            if (!plan.CurrentParentEntityId.HasValue || !plan.RequestedOrdinal.HasValue || plan.RequestedOrdinal == plan.CurrentOrdinal)
            {
                return new MembershipPreviewEnvelope("none", currentPath, currentPath, false, false, false, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "No season move is needed.", null);
            }

            var conflictId = FindChildByOrdinal(conn, plan.CurrentParentEntityId.Value, plan.RequestedOrdinal.Value, tx);
            if (conflictId.HasValue && conflictId.Value != plan.CurrentEntityId)
            {
                return new MembershipPreviewEnvelope("conflict", currentPath, $"Season {plan.RequestedOrdinal.Value}", false, false, false, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "The requested season number is already occupied.", "Another owned season already uses that season number.");
            }

            if (applyChanges)
            {
                conn.Execute(
                    "UPDATE works SET ordinal = @ordinal, parent_key = @parentKey WHERE id = @workId;",
                    new
                    {
                        ordinal = plan.RequestedOrdinal.Value,
                        parentKey = BuildHierarchyParentKey(plan.RequestedParentLabel, $"S{plan.RequestedOrdinal.Value:D2}"),
                        workId = plan.CurrentEntityId,
                    },
                    tx);
            }

            return new MembershipPreviewEnvelope("move_season", currentPath, $"Season {plan.RequestedOrdinal.Value}", false, true, applyChanges, plan.CurrentEntityId, plan.CurrentRootEntityId, plan.CurrentParentEntityId, "The season will move to the requested position.", null);
        }

        var resolvedTarget = ResolveMembershipMoveTarget(conn, tx, plan, request, applyChanges, ct);
        if (!resolvedTarget.CanApply)
        {
            return new MembershipPreviewEnvelope(resolvedTarget.Action, currentPath, resolvedTarget.TargetPath, resolvedTarget.RequiresNewTarget, false, false, plan.CurrentEntityId, plan.CurrentRootEntityId, resolvedTarget.TargetParentEntityId, resolvedTarget.Message, resolvedTarget.ConflictMessage);
        }

        var sourceParentWillBeEmpty = plan.CurrentParentEntityId is { } sourceParentId
            && resolvedTarget.TargetParentEntityId != sourceParentId
            && conn.QuerySingle<int>("SELECT COUNT(*) FROM works WHERE parent_work_id = @sourceParentId AND id <> @currentEntityId;",
                new { sourceParentId, currentEntityId = plan.CurrentEntityId }, tx) == 0;

        if (applyChanges && resolvedTarget.TargetParentEntityId.HasValue)
        {
            conn.Execute(
                "UPDATE works SET parent_work_id = @parentId, ordinal = @ordinal WHERE id = @workId;",
                new { parentId = resolvedTarget.TargetParentEntityId.Value, ordinal = (object?)plan.RequestedOrdinal ?? DBNull.Value, workId = plan.CurrentEntityId },
                tx);
        }

        var targetRootEntityId = ResolveRootWorkId(conn, resolvedTarget.TargetParentEntityId ?? plan.CurrentEntityId, tx, ct);
        return new MembershipPreviewEnvelope(resolvedTarget.Action, currentPath, resolvedTarget.TargetPath, resolvedTarget.RequiresNewTarget, true, applyChanges, plan.CurrentEntityId, targetRootEntityId, resolvedTarget.TargetParentEntityId, resolvedTarget.Message, null, resolvedTarget.Stage2TargetEntityId, sourceParentWillBeEmpty);
    }

    private static ResolvedMoveTarget ResolveMembershipMoveTarget(
        SqliteConnection conn,
        SqliteTransaction? tx,
        MembershipPlan plan,
        MembershipPreviewRequest request,
        bool applyChanges,
        CancellationToken ct)
    {
        Guid? targetParentId = null;
        Guid? stage2TargetId = null;
        var requiresNewTarget = false;
        string targetPath;
        var selectedSuggestions = request.SelectedSuggestions ?? new(StringComparer.OrdinalIgnoreCase);

        if (plan.MediaType == "TV")
        {
            var retailShowSuggestion = GetRequestSuggestion(selectedSuggestions, "show");
            if (plan.SelectedPrimaryTargetId.HasValue
                && !IsCompatibleParent(conn, plan.SelectedPrimaryTargetId.Value, plan.MediaType, parentWorkId: null, tx))
            {
                return IncompatibleTarget("show");
            }

            Guid? showId;
            if (retailShowSuggestion?.ExternalIdKey == BridgeIdKeys.TvdbId
                && !string.IsNullOrWhiteSpace(retailShowSuggestion.ExternalIdValue))
            {
                var verifiedId = retailShowSuggestion.ExternalIdValue;
                var byIdentity = FindTvdbShowById(conn, verifiedId, tx);
                var byName = FindParentByKey(conn, plan.MediaType, plan.RequestedParentKey, tx);
                if (byName.HasValue && byName != byIdentity)
                {
                    return new ResolvedMoveTarget("conflict", null, BuildTelevisionTargetPath(plan), false, false,
                            "A different local show already uses this name.",
                            "Resolve the existing show's TheTVDB identity before moving the episode.");
                }
                if (plan.SelectedPrimaryTargetId.HasValue && plan.SelectedPrimaryTargetId != byIdentity)
                {
                    return IncompatibleTarget("show");
                }
                showId = byIdentity;
            }
            else
            {
                showId = plan.SelectedPrimaryTargetId ?? FindParentByKey(conn, plan.MediaType, plan.RequestedParentKey, tx);
            }
            if (!showId.HasValue)
            {
                requiresNewTarget = true;
                if (retailShowSuggestion is null)
                {
                    return new ResolvedMoveTarget("move_child", null, BuildTelevisionTargetPath(plan), true, false, "Select an identified show before moving the episode.", "Choose an existing local show or use Search Retail.");
                }

                if (!applyChanges)
                {
                    return new ResolvedMoveTarget("move_child", null, BuildTelevisionTargetPath(plan), true, true, "The episode will move into a retail-matched show and season.", null);
                }

                showId = InsertParentWork(conn, tx!, plan.MediaType, plan.RequestedParentKey!, null, null);
                UpsertCanonicalValues(conn, tx!, showId.Value, new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["show_name"] = plan.RequestedParentLabel });
                UpsertRetailIdentity(conn, tx!, showId.Value, retailShowSuggestion);
                stage2TargetId = showId.Value;
            }

            var seasonNumber = ParseNavigatorOrdinal(plan.RequestedSecondaryLabel, null);
            if (plan.RequestedOrdinal is null)
            {
                return new ResolvedMoveTarget("conflict", null, string.Empty, false, false, "An episode number is required for TV membership moves.", "Episode number is required.");
            }

            if (plan.SelectedSecondaryTargetId.HasValue
                && !IsCompatibleParent(conn, plan.SelectedSecondaryTargetId.Value, plan.MediaType, showId, tx))
            {
                return IncompatibleTarget("season");
            }

            targetParentId = plan.SelectedSecondaryTargetId;
            if (!targetParentId.HasValue && seasonNumber.HasValue)
            {
                targetParentId = FindChildByOrdinal(conn, showId.Value, seasonNumber.Value, tx);
                if (!targetParentId.HasValue)
                {
                    requiresNewTarget = true;
                    if (!applyChanges)
                    {
                        return new ResolvedMoveTarget("move_child", null, BuildTelevisionTargetPath(plan), true, true, "The episode will move into a new season.", null);
                    }

                    targetParentId = InsertParentWork(conn, tx!, plan.MediaType, BuildHierarchyParentKey(plan.RequestedParentLabel, $"S{seasonNumber.Value:D2}"), showId.Value, seasonNumber);
                    UpsertCanonicalValues(conn, tx!, targetParentId.Value, new Dictionary<string, string?> { ["season_number"] = seasonNumber.Value.ToString(CultureInfo.InvariantCulture) });
                }
            }

            targetParentId ??= showId;
            targetPath = BuildTelevisionTargetPath(plan);
        }
        else if (plan.MediaType == "Music")
        {
            var retailAlbumSuggestion = GetRequestSuggestion(selectedSuggestions, "album");
            if (plan.SelectedPrimaryTargetId.HasValue
                && !IsCompatibleParent(conn, plan.SelectedPrimaryTargetId.Value, plan.MediaType, parentWorkId: null, tx))
            {
                return IncompatibleTarget("album");
            }

            targetParentId = plan.SelectedPrimaryTargetId ?? FindParentByKey(conn, plan.MediaType, plan.RequestedParentKey, tx);
            if (!targetParentId.HasValue)
            {
                requiresNewTarget = true;
                if (retailAlbumSuggestion is null)
                {
                    return new ResolvedMoveTarget("move_child", null, BuildMusicTargetPath(plan), true, false, "Select an identified album before moving the track.", "Choose an existing local album or use Search Retail.");
                }

                if (!applyChanges)
                {
                    return new ResolvedMoveTarget("move_child", null, BuildMusicTargetPath(plan), true, true, "The track will move into a retail-matched album.", null);
                }

                targetParentId = InsertParentWork(conn, tx!, plan.MediaType, plan.RequestedParentKey!, null, null);
                UpsertCanonicalValues(conn, tx!, targetParentId.Value, new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["album"] = plan.RequestedParentLabel, ["artist"] = plan.RequestedSecondaryLabel });
                UpsertRetailIdentity(conn, tx!, targetParentId.Value, retailAlbumSuggestion);
                stage2TargetId = targetParentId.Value;
            }

            targetPath = BuildMusicTargetPath(plan);
        }
        else
        {
            var retailSeriesSuggestion = GetRequestSuggestion(selectedSuggestions, "series");
            if (plan.SelectedPrimaryTargetId.HasValue
                && !IsCompatibleParent(conn, plan.SelectedPrimaryTargetId.Value, plan.MediaType, parentWorkId: null, tx))
            {
                return IncompatibleTarget("series");
            }

            targetParentId = plan.SelectedPrimaryTargetId ?? FindParentByKey(conn, plan.MediaType, plan.RequestedParentKey, tx);
            if (!targetParentId.HasValue)
            {
                requiresNewTarget = true;
                if (!applyChanges)
                {
                    return new ResolvedMoveTarget("move_child", null, BuildSeriesTargetPath(plan), true, true, $"The {GetSeriesLeafLabel(plan.MediaType).ToLowerInvariant()} will move into a new series.", null);
                }

                targetParentId = InsertParentWork(conn, tx!, plan.MediaType, plan.RequestedParentKey!, null, null);
                UpsertCanonicalValues(conn, tx!, targetParentId.Value, new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["series"] = plan.RequestedParentLabel, ["author"] = plan.RequestedSecondaryLabel });
                if (retailSeriesSuggestion is not null)
                {
                    UpsertRetailIdentity(conn, tx!, targetParentId.Value, retailSeriesSuggestion);
                    stage2TargetId = targetParentId.Value;
                }
            }

            targetPath = BuildSeriesTargetPath(plan);
        }

        if (targetParentId.HasValue && plan.RequestedOrdinal.HasValue)
        {
            var conflictId = FindChildByOrdinal(conn, targetParentId.Value, plan.RequestedOrdinal.Value, tx);
            if (conflictId.HasValue && conflictId.Value != plan.CurrentEntityId)
            {
                return new ResolvedMoveTarget("conflict", targetParentId, targetPath, requiresNewTarget, false, "The requested position is already occupied.", "Another owned item already uses that ordinal.");
            }
        }

        if (targetParentId == plan.CurrentParentEntityId && plan.RequestedOrdinal == plan.CurrentOrdinal)
        {
            return new ResolvedMoveTarget("none", targetParentId, targetPath, requiresNewTarget, false, "No structural change is needed.", null);
        }

        return new ResolvedMoveTarget("move_child", targetParentId, targetPath, requiresNewTarget, true, "The item will move to the selected membership target.", null, stage2TargetId);
    }

    private static ResolvedMoveTarget IncompatibleTarget(string targetKind) =>
        new(
            "conflict",
            null,
            string.Empty,
            false,
            false,
            $"The selected {targetKind} is not compatible with this item.",
            "Choose a matching container from this media type.");

    private static bool IsCompatibleParent(
        SqliteConnection conn,
        Guid parentId,
        string mediaType,
        Guid? parentWorkId,
        SqliteTransaction? tx) =>
        conn.QueryFirstOrDefault<long>("""
            SELECT 1
            FROM works
            WHERE id = @parentId
              AND media_type = @mediaType
              AND work_kind = 'parent'
              AND ((@parentWorkId IS NULL AND parent_work_id IS NULL) OR parent_work_id = @parentWorkId)
            LIMIT 1;
            """, new { parentId, mediaType, parentWorkId }, tx) == 1;

    private static string BuildMembershipPath(SqliteConnection conn, Guid entityId, SqliteTransaction? tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = conn.QueryFirstOrDefault<MembershipPathRow>("""
            SELECT w.media_type AS MediaType,
                   w.work_kind AS WorkKind,
                   w.ordinal AS Ordinal,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('title', 'episode_title', 'album', 'series') ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'episode_title' THEN 1 ELSE 2 END LIMIT 1), w.parent_key) AS LeafLabel,
                   CASE WHEN p.id IS NULL THEN 0 ELSE 1 END AS HasParent,
                   p.ordinal AS ParentOrdinal,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id = p.id AND key IN ('title', 'show_name', 'album', 'series') ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 ELSE 2 END LIMIT 1), p.parent_key) AS ParentLabel,
                   CASE WHEN gp.id IS NULL THEN 0 ELSE 1 END AS HasRoot,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id = gp.id AND key IN ('title', 'show_name', 'album', 'series') ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 ELSE 2 END LIMIT 1), gp.parent_key) AS RootLabel
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @entityId
            LIMIT 1;
            """, new { entityId }, tx);
        if (row is null)
        {
            return "Item";
        }

        var path = new List<string>();
        if (row.HasRoot)
        {
            path.Add(StringHelpers.FirstNonBlankOr("Item", row.RootLabel, "Container"));
        }
        if (row.HasParent)
        {
            path.Add(string.Equals(row.MediaType, "TV", StringComparison.OrdinalIgnoreCase) && row.HasRoot
                ? $"Season {row.ParentOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                : StringHelpers.FirstNonBlankOr("Item", row.ParentLabel, "Container"));
        }

        path.Add(BuildMembershipLeafLabel(row));
        return string.Join(" / ", path);
    }

    private static string BuildMembershipLeafLabel(MembershipPathRow row)
    {
        if (string.Equals(row.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(row.WorkKind, "child", StringComparison.OrdinalIgnoreCase)
                ? $"Episode {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                : row.HasParent
                    ? $"Season {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                    : StringHelpers.FirstNonBlankOr("Item", row.LeafLabel, "Show");
        }
        if (string.Equals(row.MediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.WorkKind, "child", StringComparison.OrdinalIgnoreCase))
        {
            return $"Track {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        }

        return StringHelpers.FirstNonBlankOr("Item", row.LeafLabel, row.WorkKind, "Item");
    }

    private static Guid ResolveRootWorkId(SqliteConnection conn, Guid entityId, SqliteTransaction? tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rootId = conn.QueryFirstOrDefault<Guid?>("""
            SELECT COALESCE(gp.id, p.id, w.id) AS RootWorkId
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @entityId
            LIMIT 1;
            """, new { entityId }, tx);

        return rootId ?? entityId;
    }

    private static Guid? FindParentByKey(SqliteConnection conn, string mediaType, string? parentKey, SqliteTransaction? tx)
    {
        if (string.IsNullOrWhiteSpace(parentKey))
        {
            return null;
        }

        return conn.QueryFirstOrDefault<Guid?>("SELECT id FROM works WHERE media_type = @mediaType AND work_kind = 'parent' AND parent_key = @parentKey LIMIT 1;", new { mediaType, parentKey }, tx);
    }

    private static Guid? FindTvdbShowById(SqliteConnection conn, string tvdbId, SqliteTransaction? tx) =>
        conn.QueryFirstOrDefault<Guid?>("""
            SELECT w.id FROM works w
            WHERE w.media_type = 'TV' AND w.work_kind = 'parent' AND w.parent_work_id IS NULL
              AND (EXISTS (SELECT 1 FROM bridge_ids b
                           WHERE b.entity_id = w.id AND b.id_type = @idType AND b.id_value = @tvdbId)
                   OR EXISTS (SELECT 1 FROM canonical_values c
                              WHERE c.entity_id = w.id AND c.key = @idType AND c.value = @tvdbId))
            LIMIT 1;
            """, new { idType = BridgeIdKeys.TvdbId, tvdbId }, tx);

    private static Guid? FindChildByOrdinal(SqliteConnection conn, Guid parentWorkId, int ordinal, SqliteTransaction? tx)
    {
        return conn.QueryFirstOrDefault<Guid?>("SELECT id FROM works WHERE parent_work_id = @parentWorkId AND ordinal = @ordinal LIMIT 1;", new { parentWorkId, ordinal }, tx);
    }

    private static Guid InsertParentWork(SqliteConnection conn, SqliteTransaction tx, string mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal)
    {
        var workId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO works
                (id, collection_id, media_type, work_kind, parent_work_id, ordinal, is_catalog_only, parent_key, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'parent', @parentWorkId, @ordinal, 0, @parentKey, 'pending');
            """,
            new { id = workId, mediaType, parentWorkId = grandparentWorkId, ordinal, parentKey },
            tx);
        return workId;
    }

    private static void UpsertContainerIdentity(SqliteConnection conn, SqliteTransaction tx, MembershipPlan plan) =>
        UpsertCanonicalValues(
            conn,
            tx,
            plan.CurrentEntityId,
            plan.MediaType switch
            {
                "TV" => new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["show_name"] = plan.RequestedParentLabel },
                "Music" => new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["album"] = plan.RequestedParentLabel, ["artist"] = plan.RequestedSecondaryLabel },
                "Comics" or "Books" or "Audiobooks" => new Dictionary<string, string?> { ["title"] = plan.RequestedParentLabel, ["series"] = plan.RequestedParentLabel, ["author"] = plan.RequestedSecondaryLabel },
                _ => new Dictionary<string, string?>(),
            });

    private static void UpsertCanonicalValues(SqliteConnection conn, SqliteTransaction tx, Guid entityId, IReadOnlyDictionary<string, string?> values)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            conn.Execute(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at, is_conflicted)
                VALUES (@entityId, @key, @value, @lastScoredAt, 0)
                ON CONFLICT(entity_id, key) DO UPDATE SET
                    value = excluded.value,
                    last_scored_at = excluded.last_scored_at,
                    is_conflicted = 0;
                """,
                new { entityId, key, value, lastScoredAt = now },
                tx);
        }

    }

    private static void ApplyRetailIdentityMutation(
        SqliteConnection conn,
        SqliteTransaction tx,
        HierarchyIdentityMutation mutation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Retail confirmations are decided by the user-manual provider. Ensure
        // that FK target exists in the same write when a fresh database has not
        // recorded a manual decision yet.
        conn.Execute("""
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@id, 'user_manual', '1.0', 1);
            """, new { id = WellKnownProviders.UserManual }, tx);

        foreach (var artifact in mutation.StaleArtifacts
                     .Where(item => item.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(item.Key))
                     .Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new { entityId = artifact.EntityId, key = artifact.Key };
            conn.Execute("DELETE FROM canonical_values WHERE entity_id = @entityId AND key = @key;", parameters, tx);
            conn.Execute("DELETE FROM metadata_claims WHERE entity_id = @entityId AND claim_key = @key;", parameters, tx);
            conn.Execute("DELETE FROM bridge_ids WHERE entity_id = @entityId AND id_type = @key;", parameters, tx);
        }

        foreach (var claim in mutation.Claims.Where(item =>
                     item.EntityId != Guid.Empty &&
                     item.ProviderId != Guid.Empty &&
                     !string.IsNullOrWhiteSpace(item.Key) &&
                     !string.IsNullOrWhiteSpace(item.Value)))
        {
            ct.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO metadata_claims
                    (id, entity_id, provider_id, decision_source_provider_id, observation_set_id,
                     claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current, superseded_at)
                VALUES
                    (@id, @entityId, @providerId, @decisionSourceProviderId, NULL,
                     @key, @value, @confidence, @claimedAt, @isUserLocked, 1, NULL);
                """, new
            {
                id = Guid.NewGuid(),
                entityId = claim.EntityId,
                providerId = claim.ProviderId,
                decisionSourceProviderId = claim.DecisionSourceProviderId,
                key = claim.Key,
                value = claim.Value,
                confidence = claim.Confidence,
                claimedAt = claim.ClaimedAt.ToString("O"),
                isUserLocked = claim.IsUserLocked ? 1 : 0,
            }, tx);
        }

        foreach (var canonical in mutation.CanonicalValues.Where(item =>
                     item.EntityId != Guid.Empty &&
                     !string.IsNullOrWhiteSpace(item.Key) &&
                     !string.IsNullOrWhiteSpace(item.Value)))
        {
            ct.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO canonical_values
                    (entity_id, key, value, last_scored_at, is_conflicted, winning_provider_id, needs_review)
                VALUES
                    (@entityId, @key, @value, @lastScoredAt, 0, @winningProviderId, @needsReview)
                ON CONFLICT(entity_id, key) DO UPDATE SET
                    value = excluded.value,
                    last_scored_at = excluded.last_scored_at,
                    is_conflicted = 0,
                    winning_provider_id = excluded.winning_provider_id,
                    needs_review = excluded.needs_review;
                """, new
            {
                entityId = canonical.EntityId,
                key = canonical.Key,
                value = canonical.Value,
                lastScoredAt = canonical.LastScoredAt.ToString("O"),
                winningProviderId = canonical.WinningProviderId,
                needsReview = canonical.NeedsReview ? 1 : 0,
            }, tx);
        }

        foreach (var bridgeId in mutation.BridgeIds.Where(item =>
                     item.EntityId != Guid.Empty &&
                     !string.IsNullOrWhiteSpace(item.Key) &&
                     !string.IsNullOrWhiteSpace(item.Value)))
        {
            ct.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value, provider_id, created_at)
                VALUES (@id, @entityId, @idType, @idValue, @providerId, @createdAt)
                ON CONFLICT(entity_id, id_type) DO UPDATE SET
                    id_value = excluded.id_value,
                    provider_id = excluded.provider_id;
                """, new
            {
                id = Guid.NewGuid(),
                entityId = bridgeId.EntityId,
                idType = bridgeId.Key,
                idValue = bridgeId.Value,
                providerId = bridgeId.ProviderName,
                createdAt = bridgeId.CreatedAt.ToString("O"),
            }, tx);
        }

        foreach (var externalIdentifiers in mutation.ExternalIdentifierMutations ?? [])
        {
            ApplyExternalIdentifierMutation(conn, tx, externalIdentifiers, ct);
        }
    }

    private static void ApplyExternalIdentifierMutation(
        SqliteConnection conn,
        SqliteTransaction tx,
        HierarchyExternalIdentifierMutation mutation,
        CancellationToken ct)
    {
        if (mutation.EntityId == Guid.Empty)
        {
            return;
        }
        ct.ThrowIfCancellationRequested();
        var currentJson = conn.QueryFirstOrDefault<string?>(
            "SELECT external_identifiers FROM works WHERE id = @workId LIMIT 1;",
            new { workId = mutation.EntityId }, tx);
        Dictionary<string, string> identifiers;
        try
        {
            identifiers = string.IsNullOrWhiteSpace(currentJson)
                ? new(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(currentJson)
                    ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            identifiers = new(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var key in mutation.KeysToRemove)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                identifiers.Remove(key);
            }
        }

        foreach (var (key, value) in mutation.Replacements)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                identifiers[key] = value;
            }
        }

        conn.Execute(
            "UPDATE works SET external_identifiers = @identifiers WHERE id = @workId;",
            new { workId = mutation.EntityId, identifiers = JsonSerializer.Serialize(identifiers) }, tx);
    }

    private static string BuildTelevisionTargetPath(MembershipPlan plan)
    {
        var show = string.IsNullOrWhiteSpace(plan.RequestedParentLabel) ? "Show" : plan.RequestedParentLabel;
        var season = ParseNavigatorOrdinal(plan.RequestedSecondaryLabel, null) is int seasonNumber ? $"Season {seasonNumber}" : "Season";
        var episode = plan.RequestedOrdinal.HasValue ? $"Episode {plan.RequestedOrdinal.Value}" : "Episode";
        return $"{show} / {season} / {episode}";
    }

    private static string BuildMusicTargetPath(MembershipPlan plan)
    {
        var artist = string.IsNullOrWhiteSpace(plan.RequestedSecondaryLabel) ? "Artist" : plan.RequestedSecondaryLabel;
        var album = string.IsNullOrWhiteSpace(plan.RequestedParentLabel) ? "Album" : plan.RequestedParentLabel;
        var track = plan.RequestedOrdinal.HasValue ? $"Track {plan.RequestedOrdinal.Value}" : "Track";
        return $"{artist} / {album} / {track}";
    }

    private static string BuildSeriesTargetPath(MembershipPlan plan)
    {
        var series = string.IsNullOrWhiteSpace(plan.RequestedParentLabel) ? "Series" : plan.RequestedParentLabel;
        var leaf = plan.RequestedOrdinal.HasValue ? $"{GetSeriesLeafLabel(plan.MediaType)} {plan.RequestedOrdinal.Value}" : GetSeriesLeafLabel(plan.MediaType);
        return string.IsNullOrWhiteSpace(plan.RequestedSecondaryLabel)
            ? $"{series} / {leaf}"
            : $"{plan.RequestedSecondaryLabel} / {series} / {leaf}";
    }

    private static string BuildRenamedContainerPath(MembershipPlan plan) =>
        string.IsNullOrWhiteSpace(plan.RequestedSecondaryLabel)
            ? plan.RequestedParentLabel ?? "Container"
            : $"{plan.RequestedSecondaryLabel} / {plan.RequestedParentLabel}";

    private static string GetSeriesLeafLabel(string mediaType) =>
        mediaType switch
        {
            "Comics" => "Issue",
            "Audiobooks" => "Audiobook",
            _ => "Book",
        };

    private static string BuildHierarchyParentKey(params string?[] parts)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (!first)
            {
                sb.Append('|');
            }

            sb.Append(NormalizeHierarchyPart(part));
            first = false;
        }

        return sb.ToString();
    }

    private static string NormalizeHierarchyPart(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var prevSpace = false;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                prevSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? FormatParentKeyFallback(string? parentKey) =>
        string.IsNullOrWhiteSpace(parentKey)
            ? null
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(parentKey.Replace('|', ' '));

    private static string BuildDelimitedLabel(params string?[] values)
    {
        var parts = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join(" • ", parts);
    }

    private static int? ToInt(long? value) =>
        value.HasValue && value.Value >= int.MinValue && value.Value <= int.MaxValue ? (int)value.Value : null;

    private static int? ParseNavigatorOrdinal(string? raw, int? fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var digits = new string(raw.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : fallback;
    }

    private static string? GetRequestValue(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static Guid? GetRequestTarget(IReadOnlyDictionary<string, Guid?> targets, string key) =>
        targets.TryGetValue(key, out var value) && value.HasValue && value.Value != Guid.Empty ? value.Value : null;

    private sealed class MembershipEntityRow
    {
        public string WorkIdValue { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string WorkKind { get; set; } = string.Empty;
        public string? ParentWorkIdValue { get; set; }
        public int? Ordinal { get; set; }
        public string? ParentKey { get; set; }
        public string RootWorkIdValue { get; set; } = string.Empty;
        public Guid WorkId => Guid.Parse(WorkIdValue);
        public Guid? ParentWorkId => string.IsNullOrWhiteSpace(ParentWorkIdValue) ? null : Guid.Parse(ParentWorkIdValue);
        public Guid RootWorkId => Guid.Parse(RootWorkIdValue);
    }
    private sealed class MembershipPathRow
    {
        public string MediaType { get; set; } = string.Empty;
        public string WorkKind { get; set; } = string.Empty;
        public int? Ordinal { get; set; }
        public string? LeafLabel { get; set; }
        public bool HasParent { get; set; }
        public int? ParentOrdinal { get; set; }
        public string? ParentLabel { get; set; }
        public bool HasRoot { get; set; }
        public string? RootLabel { get; set; }
    }
    private sealed record MembershipPlan(string Action, string MediaType, Guid CurrentEntityId, Guid? CurrentParentEntityId, Guid CurrentRootEntityId, int? CurrentOrdinal, string? RequestedTitle, string? RequestedParentLabel, string? RequestedSecondaryLabel, string? RequestedParentKey, int? RequestedOrdinal, Guid? SelectedPrimaryTargetId, Guid? SelectedSecondaryTargetId);
    private sealed record ResolvedMoveTarget(string Action, Guid? TargetParentEntityId, string TargetPath, bool RequiresNewTarget, bool CanApply, string Message, string? ConflictMessage, Guid? Stage2TargetEntityId = null);
}
