using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed class ViewSharedContributionService(
    IDatabaseConnection database,
    ILocalAssetRepository assets,
    IViewProfileRepository policies,
    ViewSharedTransferService transfers,
    IAuthorizationEvaluator authorization,
    IViewSharedContributionQueue queue)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ViewSharedContributionPreviewDto> PreviewAsync(
        RequestAuthority authority,
        ViewSharedContributionPreviewRequest request,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: false, ct);
        await RequireSubmitAsync(actorProfileId, ct);
        return PreviewOwned(actorProfileId, request, ct);
    }

    private ViewSharedContributionPreviewDto PreviewOwned(
        Guid actorProfileId,
        ViewSharedContributionPreviewRequest request,
        CancellationToken ct)
    {
        var itemIds = NormalizeItems(request.ItemIds);
        var previews = new List<ViewSharedTransferPreviewDto>(itemIds.Count);
        foreach (var itemId in itemIds)
        {
            var item = assets.Find(itemId, ct) ?? throw new KeyNotFoundException("A selected View item was not found.");
            if (item.OwnerProfileId != actorProfileId)
            {
                throw new KeyNotFoundException("A selected View item was not found.");
            }

            var preview = transfers.Preview(itemId, request.DestinationKind, request.FolderName, ct);
            if (!preview.AlreadyShared)
            {
                previews.Add(preview);
            }
        }

        if (previews.Count == 0)
        {
            throw new InvalidOperationException("Every selected item is already in the Shared Library.");
        }

        return BuildPreview(previews, request.DestinationKind, request.FolderName);
    }

    public async Task<ViewSharedContributionDto> SubmitAsync(
        RequestAuthority authority,
        ViewSharedContributionSubmitRequest request,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: false, ct);
        return await SubmitCoreAsync(actorProfileId, request, requireSubmitPermission: true, ct);
    }

    private async Task<ViewSharedContributionDto> SubmitCoreAsync(
        Guid actorProfileId,
        ViewSharedContributionSubmitRequest request,
        bool requireSubmitPermission,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
        {
            throw new ArgumentException("An idempotency key of 1 to 100 characters is required.");
        }

        var previewRequest = new ViewSharedContributionPreviewRequest(
            request.ItemIds, request.DestinationKind, request.FolderName);
        if (requireSubmitPermission)
        {
            await RequireSubmitAsync(actorProfileId, ct);
        }

        var preview = PreviewOwned(actorProfileId, previewRequest, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(preview.PreviewRevision),
                Encoding.UTF8.GetBytes(request.PreviewRevision ?? string.Empty)))
        {
            throw new InvalidOperationException("The selected files changed after preview. Review the contribution again.");
        }

        var existing = FindByIdempotency(actorProfileId, request.IdempotencyKey.Trim(), ct);
        if (existing.HasValue)
        {
            return await GetRequiredForProfileAsync(actorProfileId, existing.Value, false, ct);
        }

        var contributionId = Guid.NewGuid();
        var contributorName = GetProfileName(actorProfileId, ct);
        var now = DateTimeOffset.UtcNow;
        await database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            foreach (var itemPreview in preview.Items)
            {
                var alreadyPending = connection.ExecuteScalar<int>(new CommandDefinition("""
                    SELECT COUNT(*)
                      FROM view_shared_contribution_items ci
                      JOIN view_shared_contributions c ON c.id = ci.contribution_id
                     WHERE ci.item_id = @itemId AND c.submitted_by_profile_id = @actorProfileId
                       AND c.status = 'pending';
                    """, new { itemId = itemPreview.ItemId, actorProfileId }, transaction, cancellationToken: token));
                if (alreadyPending > 0)
                {
                    throw new InvalidOperationException("A selected item already has a pending Shared Library contribution.");
                }
            }
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_shared_contributions
                    (id, submitted_by_profile_id, submitted_by_name, status, destination_kind,
                     destination_label, note, revision, idempotency_key, submitted_at, updated_at)
                VALUES (@contributionId, @actorProfileId, @contributorName, 'pending', @destinationKind,
                        @destinationLabel, @note, 1, @idempotencyKey, @now, @now);
                """, new
            {
                contributionId,
                actorProfileId,
                contributorName,
                destinationKind = preview.DestinationKind,
                destinationLabel = preview.DestinationLabel,
                note = NullIfWhiteSpace(request.Note),
                idempotencyKey = request.IdempotencyKey.Trim(),
                now,
            }, transaction, cancellationToken: token));

            for (var index = 0; index < preview.Items.Count; index++)
            {
                var itemPreview = preview.Items[index];
                connection.Execute(new CommandDefinition("""
                    INSERT INTO view_shared_contribution_items
                        (id, contribution_id, item_id, original_profile_id, original_profile_name,
                         position, operation, source_manifest_json, execution_state, updated_at)
                    VALUES (@id, @contributionId, @itemId, @actorProfileId, @contributorName,
                            @position, @operation, @manifest, 'waiting', @now);
                    """, new
                {
                    id = Guid.NewGuid(),
                    contributionId,
                    itemId = itemPreview.ItemId,
                    actorProfileId,
                    contributorName,
                    position = index,
                    operation = itemPreview.Operation,
                    manifest = JsonSerializer.Serialize(itemPreview, JsonOptions),
                    now,
                }, transaction, cancellationToken: token));
            }
            InsertEvent(connection, transaction, contributionId, actorProfileId, contributorName,
                "submitted", $"{preview.Items.Count} item(s) submitted.", now, token);
            return true;
        }, ct);
        return await GetRequiredForProfileAsync(actorProfileId, contributionId, false, ct);
    }

    public async Task<ViewSharedContributionDto> AddDirectAsync(
        RequestAuthority authority, ViewSharedDirectAddRequest request, CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: true, ct);
        var policy = await policies.GetPolicyAsync(actorProfileId, ct);
        if (!policy.ViewEnabled || !policy.ReviewSharedLibraryContributions)
        {
            throw new UnauthorizedAccessException("Shared Library curator access is required.");
        }

        var preview = PreviewOwned(actorProfileId,
            new ViewSharedContributionPreviewRequest(request.ItemIds, request.DestinationKind, request.FolderName), ct);
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey.Trim();
        var pending = await SubmitCoreAsync(actorProfileId, new ViewSharedContributionSubmitRequest(
            request.ItemIds, request.DestinationKind, request.FolderName, request.Note,
            preview.PreviewRevision, key), requireSubmitPermission: false, ct);
        if (pending.Status != "pending")
        {
            return pending;
        }

        await UpdateDecisionAsync(pending.Id, pending.Revision, "accepted", actorProfileId,
            GetProfileName(actorProfileId, ct), "Added directly by a Shared Library curator.",
            pending.DestinationKind, pending.DestinationLabel, ct);
        await queue.EnqueueAsync(pending.Id, ct);
        return await GetRequiredForProfileAsync(actorProfileId, pending.Id, true, ct);
    }

    public async Task<ViewSharedContributionPageDto> ListAsync(
        RequestAuthority authority, string mode, string? status, int offset, int limit,
        CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!string.Equals(mode, "mine", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "review", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Contribution mode must be mine or review.", nameof(mode));
        }

        var review = string.Equals(mode, "review", StringComparison.OrdinalIgnoreCase);
        var actorProfileId = await RequireActorAsync(authority, curator: review, ct);
        var policy = await policies.GetPolicyAsync(actorProfileId, ct);
        if (review && !policy.ReviewSharedLibraryContributions)
        {
            throw new UnauthorizedAccessException("Shared Library curator access is required.");
        }

        var normalizedStatus = NormalizeStatusFilter(status);
        using var connection = database.CreateConnection();
        var ids = connection.Query<Guid>(new CommandDefinition("""
            SELECT id FROM view_shared_contributions
             WHERE ((@review = 1 AND EXISTS (
                        -- A reviewer only sees what people in their own household submitted.
                        SELECT 1 FROM profiles submitter JOIN profiles reviewer ON reviewer.household_id = submitter.household_id
                         WHERE submitter.id = view_shared_contributions.submitted_by_profile_id
                           AND reviewer.id = @actorProfileId))
                    OR submitted_by_profile_id = @actorProfileId)
               AND (@status IS NULL OR status = @status)
             ORDER BY CASE status WHEN 'pending' THEN 0 ELSE 1 END, submitted_at DESC
             LIMIT @take OFFSET @offset;
            """, new { review = review ? 1 : 0, actorProfileId, status = normalizedStatus, take = limit + 1, offset },
            cancellationToken: ct)).ToList();
        var hasMore = ids.Count > limit;
        if (hasMore)
        {
            ids.RemoveAt(ids.Count - 1);
        }

        var rows = new List<ViewSharedContributionDto>(ids.Count);
        foreach (var id in ids)
        {
            rows.Add(Read(connection, id, ct)!);
        }

        return new(rows, offset, hasMore, policy.SubmitToSharedLibrary,
            policy.ReviewSharedLibraryContributions);
    }

    public async Task<ViewSharedContributionDto> GetRequiredAsync(
        RequestAuthority authority, Guid contributionId, bool requireReview = false,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: requireReview, ct);
        return await GetRequiredForProfileAsync(actorProfileId, contributionId, requireReview, ct);
    }

    private async Task<ViewSharedContributionDto> GetRequiredForProfileAsync(
        Guid actorProfileId, Guid contributionId, bool requireReview, CancellationToken ct)
    {
        var policy = await policies.GetPolicyAsync(actorProfileId, ct);
        using var connection = database.CreateConnection();
        var contribution = Read(connection, contributionId, ct) ?? throw new KeyNotFoundException();
        var canReview = policy.ReviewSharedLibraryContributions
            && SubmitterIsInHousehold(connection, contribution.SubmittedByProfileId, actorProfileId);
        if ((requireReview && !canReview)
            || (!canReview && contribution.SubmittedByProfileId != actorProfileId))
        {
            throw new KeyNotFoundException("This contribution is unavailable.");
        }

        return contribution;
    }

    /// <summary>True when the person who submitted (when known) lives in the same household as the reviewer.</summary>
    private static bool SubmitterIsInHousehold(System.Data.IDbConnection connection, Guid? submitterProfileId, Guid reviewerProfileId) =>
        submitterProfileId is { } submitter
        && connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM profiles s JOIN profiles r ON r.household_id = s.household_id
             WHERE s.id = @submitter AND r.id = @reviewerProfileId;
            """, new { submitter, reviewerProfileId }) > 0;

    public async Task<ViewSharedContributionDto> CancelAsync(
        RequestAuthority authority, Guid contributionId, int expectedRevision,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: false, ct);
        var current = await GetRequiredForProfileAsync(actorProfileId, contributionId, false, ct);
        if (current.SubmittedByProfileId != actorProfileId)
        {
            throw new UnauthorizedAccessException("Only the contributor can cancel this submission.");
        }

        if (current.Status != "pending")
        {
            throw new InvalidOperationException("Only a pending contribution can be cancelled.");
        }

        await UpdateDecisionAsync(contributionId, expectedRevision, "cancelled", actorProfileId,
            current.SubmittedByName, null, null, null, ct);
        return await GetRequiredForProfileAsync(actorProfileId, contributionId, false, ct);
    }

    public async Task<ViewSharedContributionDto> DecideAsync(
        RequestAuthority authority, Guid contributionId, ViewSharedContributionDecisionRequest request,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: true, ct);
        var current = await GetRequiredForProfileAsync(actorProfileId, contributionId, true, ct);
        if (current.Status != "pending")
        {
            throw new InvalidOperationException("This contribution is no longer pending.");
        }

        var decision = request.Decision.Trim().ToLowerInvariant();
        if (decision is not ("accepted" or "declined"))
        {
            throw new ArgumentException("Decision must be accepted or declined.");
        }

        if (decision == "accepted")
        {
            if (current.SubmittedByProfileId is not { } contributorId)
            {
                throw new InvalidOperationException("The original contributor is no longer available.");
            }

            await RequireSubmitAsync(contributorId, ct);
            foreach (var contributionItem in current.Items)
            {
                var asset = assets.Find(contributionItem.ItemId, ct);
                if (asset?.OwnerProfileId != contributorId)
                {
                    throw new InvalidOperationException("A submitted item changed ownership and must be submitted again.");
                }
            }
        }
        var kind = (request.DestinationKind ?? current.DestinationKind).Trim().ToLowerInvariant();
        if (kind is not ("timeline" or "folder"))
        {
            throw new ArgumentException("Destination must be timeline or folder.");
        }

        var label = request.DestinationKind is null ? current.DestinationLabel : NullIfWhiteSpace(request.FolderName);
        if (kind == "folder" && string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("A Shared folder name is required.");
        }

        if (kind == "timeline")
        {
            label = null;
        }

        var curatorName = GetProfileName(actorProfileId, ct);
        await UpdateDecisionAsync(contributionId, request.ExpectedRevision, decision, actorProfileId,
            curatorName, request.Reason, kind, label, ct);
        if (decision == "accepted")
        {
            await queue.EnqueueAsync(contributionId, ct);
        }

        return await GetRequiredForProfileAsync(actorProfileId, contributionId, true, ct);
    }

    public async Task<ViewSharedContributionDto> RetryAsync(
        RequestAuthority authority, Guid contributionId, int expectedRevision,
        CancellationToken ct = default)
    {
        var actorProfileId = await RequireActorAsync(authority, curator: true, ct);
        var current = await GetRequiredForProfileAsync(actorProfileId, contributionId, true, ct);
        if (current.Status != "accepted")
        {
            throw new InvalidOperationException("Only an accepted contribution can be retried.");
        }

        if (current.Revision != expectedRevision)
        {
            throw new InvalidOperationException("This contribution changed. Reload it and try again.");
        }

        await queue.EnqueueAsync(contributionId, ct);
        return await GetRequiredForProfileAsync(actorProfileId, contributionId, true, ct);
    }

    public IReadOnlyList<Guid> GetRecoverableIds(CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        return connection.Query<Guid>(new CommandDefinition("""
            SELECT DISTINCT c.id
              FROM view_shared_contributions c
              JOIN view_shared_contribution_items ci ON ci.contribution_id = c.id
             WHERE c.status = 'accepted'
               AND ci.execution_state IN ('waiting', 'transferring', 'cleanup_pending', 'needs_attention', 'failed');
            """, cancellationToken: ct)).ToList();
    }

    public async Task ProcessAsync(Guid contributionId, CancellationToken ct = default)
    {
        using var readConnection = database.CreateConnection();
        var batch = Read(readConnection, contributionId, ct)!;
        var actorProfileId = readConnection.QuerySingle<Guid?>(new CommandDefinition(
            "SELECT decided_by_profile_id FROM view_shared_contributions WHERE id = @contributionId;",
            new { contributionId }, cancellationToken: ct))
            ?? throw new InvalidOperationException("The accepting curator is unavailable.");
        foreach (var item in batch.Items.Where(value => value.ExecutionState is "waiting" or "failed" or "needs_attention" or "cleanup_pending"))
        {
            await SetItemStateAsync(item.Id, "transferring", null, ct);
            try
            {
                var result = await transfers.ExecuteAsync(item.ItemId, actorProfileId,
                    batch.DestinationKind, batch.DestinationLabel, item.Id, ct);
                await SetItemStateAsync(item.Id, result.State, null, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or InvalidDataException or KeyNotFoundException)
            {
                await SetItemStateAsync(item.Id, "needs_attention", exception.Message, CancellationToken.None);
            }
        }
    }

    private async Task RequireSubmitAsync(Guid profileId, CancellationToken ct)
    {
        var policy = await policies.GetPolicyAsync(profileId, ct);
        if (!policy.ViewEnabled || !policy.SubmitToSharedLibrary)
        {
            throw new UnauthorizedAccessException("Permission to submit to the Shared Library is required.");
        }
    }

    private async Task<Guid> RequireActorAsync(RequestAuthority authority, bool curator, CancellationToken ct)
    {
        if (!authority.HasHumanContext || authority.ActiveProfileId is not { } profileId
            || curator && (authority.PrincipalKind != PrincipalKind.Human || !authority.IsEffectiveAdministrator))
        {
            throw new UnauthorizedAccessException("Trusted View authority is required.");
        }

        var requirement = new AuthorizationRequirement(
            authority.HasApplicationContext ? ApplicationPermissionIds.ViewUpload : null,
            AccountFeatureId.View, RequiresHumanContext: true, RequiresAdministrator: curator,
            RequiresAdministratorSurfaceUnlock: curator);
        if (!(await authorization.EvaluateAsync(authority, requirement, null, ct)).IsAllowed)
        {
            throw new UnauthorizedAccessException("Trusted View authority is required.");
        }

        var policy = await policies.GetPolicyAsync(profileId, ct);
        if (!policy.ViewEnabled || (curator ? !policy.ReviewSharedLibraryContributions : !policy.SubmitToSharedLibrary))
        {
            throw new UnauthorizedAccessException(curator
                ? "Shared Library curator access is required."
                : "Permission to submit to the Shared Library is required.");
        }

        return profileId;
    }

    private async Task UpdateDecisionAsync(Guid id, int revision, string status, Guid actorId,
        string actorName, string? reason, string? destinationKind, string? destinationLabel,
        CancellationToken ct)
    {
        var changed = await database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var affected = connection.Execute(new CommandDefinition("""
                UPDATE view_shared_contributions
                   SET status = @status, revision = revision + 1, decided_at = @now,
                       decided_by_profile_id = @actorId, decided_by_name = @actorName,
                       decision_reason = @reason,
                       destination_kind = COALESCE(@destinationKind, destination_kind),
                       destination_label = CASE WHEN @destinationKind IS NULL THEN destination_label ELSE @destinationLabel END,
                       updated_at = @now
                 WHERE id = @id AND status = 'pending' AND revision = @revision;
                """, new
            {
                id,
                revision,
                status,
                now = DateTimeOffset.UtcNow,
                actorId,
                actorName,
                reason = NullIfWhiteSpace(reason),
                destinationKind,
                destinationLabel = NullIfWhiteSpace(destinationLabel)
            },
                transaction, cancellationToken: token));
            if (affected > 0)
            {
                InsertEvent(connection, transaction, id, actorId, actorName, status,
                    NullIfWhiteSpace(reason), DateTimeOffset.UtcNow, token);
            }

            return affected;
        }, ct);
        if (changed == 0)
        {
            throw new InvalidOperationException("This contribution changed. Reload it and try again.");
        }
    }

    private Task SetItemStateAsync(Guid id, string state, string? error, CancellationToken ct) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var contributionId = connection.QuerySingle<Guid>(new CommandDefinition(
                "SELECT contribution_id FROM view_shared_contribution_items WHERE id = @id;",
                new { id }, transaction, cancellationToken: token));
            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                UPDATE view_shared_contribution_items
                   SET execution_state = @state, error = @error, updated_at = @now
                 WHERE id = @id;
                UPDATE view_shared_contributions SET updated_at = @now WHERE id = @contributionId;
                """, new { id, contributionId, state, error, now }, transaction, cancellationToken: token));
            InsertEvent(connection, transaction, contributionId, null, "Tuvima Library",
                $"transfer_{state}", error, now, token);
            return true;
        }, ct);

    private Guid? FindByIdempotency(Guid profileId, string key, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.QuerySingleOrDefault<Guid?>(new CommandDefinition("""
            SELECT id FROM view_shared_contributions
             WHERE submitted_by_profile_id = @profileId AND idempotency_key = @key;
            """, new { profileId, key }, cancellationToken: ct));
    }

    private string GetProfileName(Guid profileId, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.QuerySingleOrDefault<string>(new CommandDefinition(
            "SELECT display_name FROM profiles WHERE id = @profileId;", new { profileId }, cancellationToken: ct))
            ?? throw new KeyNotFoundException("The profile was not found.");
    }

    private static ViewSharedContributionDto? Read(System.Data.IDbConnection connection, Guid id, CancellationToken ct)
    {
        var row = connection.QuerySingleOrDefault<ContributionRow>(new CommandDefinition("""
            SELECT id AS Id, submitted_by_profile_id AS SubmittedByProfileId,
                   submitted_by_name AS SubmittedByName, status AS Status,
                   destination_kind AS DestinationKind, destination_label AS DestinationLabel,
                   note AS Note, revision AS Revision, submitted_at AS SubmittedAt,
                   decided_at AS DecidedAt, decided_by_name AS DecidedByName,
                   decision_reason AS DecisionReason
              FROM view_shared_contributions WHERE id = @id;
            """, new { id }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var items = connection.Query<ContributionItemRow>(new CommandDefinition("""
            SELECT sci.id AS Id, sci.item_id AS ItemId, COALESCE(li.title, li.primary_file_name) AS Title,
                   li.media_kind AS MediaKind, sci.operation AS Operation,
                   sci.execution_state AS ExecutionState, sci.error AS Error
              FROM view_shared_contribution_items sci
              JOIN local_items li ON li.id = sci.item_id
             WHERE sci.contribution_id = @id ORDER BY sci.position;
            """, new { id }, cancellationToken: ct)).Select(value => new ViewSharedContributionItemDto(
                value.Id, value.ItemId, value.Title ?? "Untitled", value.MediaKind, value.Operation,
                value.ExecutionState, value.Error)).ToList();
        var events = connection.Query<ContributionEventRow>(new CommandDefinition("""
            SELECT id AS Id, actor_name AS ActorName, event_type AS EventType,
                   detail AS Detail, occurred_at AS OccurredAt
              FROM view_shared_contribution_events
             WHERE contribution_id = @id ORDER BY occurred_at, id;
            """, new { id }, cancellationToken: ct)).Select(value => new ViewSharedContributionEventDto(
                value.Id, value.ActorName, value.EventType, value.Detail, value.OccurredAt)).ToList();
        return new(row.Id, row.SubmittedByProfileId, row.SubmittedByName, row.Status,
            row.DestinationKind, row.DestinationLabel, row.Note, row.Revision,
            row.SubmittedAt, row.DecidedAt, row.DecidedByName, row.DecisionReason, items, events);
    }

    private static ViewSharedContributionPreviewDto BuildPreview(
        IReadOnlyList<ViewSharedTransferPreviewDto> items, string destinationKind, string? folderName)
    {
        var kind = destinationKind.Trim().ToLowerInvariant();
        var label = kind == "folder" ? folderName?.Trim() : null;
        var material = string.Join('|', items.OrderBy(value => value.ItemId).Select(value =>
            $"{value.ItemId:N}:{value.Operation}:{value.FileCount}:{value.TotalBytes}:{value.DestinationRoot}"));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return new(revision, items, items.Sum(value => value.FileCount), items.Sum(value => value.TotalBytes),
            items.Count(value => value.Operation == "move"), items.Count(value => value.Operation == "copy"), kind, label);
    }

    private static IReadOnlyList<Guid> NormalizeItems(IReadOnlyList<Guid>? values)
    {
        var result = values?.Where(value => value != Guid.Empty).Distinct().ToList() ?? [];
        if (result.Count is < 1 or > 100)
        {
            throw new ArgumentException("Select between 1 and 100 View items.");
        }

        return result;
    }

    private static string? NormalizeStatusFilter(string? value)
    {
        var normalized = NullIfWhiteSpace(value)?.ToLowerInvariant();
        if (normalized is not null && normalized is not ("pending" or "accepted" or "declined" or "cancelled"))
        {
            throw new ArgumentException("Unsupported contribution status.");
        }

        return normalized;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void InsertEvent(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        Guid contributionId, Guid? actorId, string actorName, string eventType, string? detail,
        DateTimeOffset occurredAt, CancellationToken ct) => connection.Execute(new CommandDefinition("""
            INSERT INTO view_shared_contribution_events
                (id, contribution_id, actor_profile_id, actor_name, event_type, detail, occurred_at)
            VALUES (@id, @contributionId, @actorId, @actorName, @eventType, @detail, @occurredAt);
            """, new { id = Guid.NewGuid(), contributionId, actorId, actorName, eventType, detail, occurredAt },
            transaction, cancellationToken: ct));

    private sealed class ContributionRow
    {
        public Guid Id { get; init; }
        public Guid? SubmittedByProfileId { get; init; }
        public string SubmittedByName { get; init; } = "";
        public string Status { get; init; } = "";
        public string DestinationKind { get; init; } = "";
        public string? DestinationLabel { get; init; }
        public string? Note { get; init; }
        public int Revision { get; init; }
        public DateTimeOffset SubmittedAt { get; init; }
        public DateTimeOffset? DecidedAt { get; init; }
        public string? DecidedByName { get; init; }
        public string? DecisionReason { get; init; }
    }

    private sealed class ContributionItemRow
    {
        public Guid Id { get; init; }
        public Guid ItemId { get; init; }
        public string? Title { get; init; }
        public string MediaKind { get; init; } = "";
        public string Operation { get; init; } = "";
        public string ExecutionState { get; init; } = "";
        public string? Error { get; init; }
    }

    private sealed class ContributionEventRow
    {
        public Guid Id { get; init; }
        public string ActorName { get; init; } = "";
        public string EventType { get; init; } = "";
        public string? Detail { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
    }
}
