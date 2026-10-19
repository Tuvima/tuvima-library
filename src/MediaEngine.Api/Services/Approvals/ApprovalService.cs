using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Approvals;

/// <summary>Who confirmed: a person (account and profile) or an application, with the name shown in the record.</summary>
internal sealed record ApprovalActor(Guid? AccountId, Guid? ProfileId, Guid? ApplicationId, string Name);

/// <summary>An action needs an approval it does not have. Carries what the person must be shown.</summary>
internal sealed class ApprovalRequiredException(IReadOnlyList<ApprovalPreviewResponse> approvals)
    : Exception("This needs to be approved before it can be switched on.")
{
    public IReadOnlyList<ApprovalPreviewResponse> Approvals { get; } = approvals;
}

/// <summary>
/// The one approval rule (Developer tools spec D-C): nothing is switched on until a server administrator has seen, in
/// plain words, what it can do and whether anything leaves the machine, and confirmed. This service builds the previews,
/// compares a confirmation (a statement hash) with the current words, and writes the append-only record.
/// </summary>
internal sealed class ApprovalService(
    IApprovalRecordRepository records,
    IProfileRepository profiles,
    IApplicationRepository applications,
    IPermissionRegistry permissions,
    TimeProvider clock)
{
    private const string UnknownApprover = "An administrator";

    private static readonly JsonSerializerOptions StatementJson = new();

    // ── Previews ─────────────────────────────────────────────────────────────

    public ApprovalPreviewResponse PreviewPlugin(PluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var statement = ApprovalStatementBuilder.ForPlugin(registration.Manifest, registration.Trust);
        return ApprovalStatementBuilder.ToPreview(ApprovalKinds.Plugin, registration.Manifest.Id, statement);
    }

    /// <param name="modelReady">True when the model the feature reads is already on this machine.</param>
    public ApprovalPreviewResponse PreviewAiFeature(string featureKey, bool modelReady)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureKey);
        var statement = ApprovalStatementBuilder.ForAiFeature(featureKey, modelReady);
        return ApprovalStatementBuilder.ToPreview(ApprovalKinds.AiFeature, featureKey, statement);
    }

    /// <summary>The key's reach is the application's permissions as the Engine sees them now (an administrator application holds every available one).</summary>
    public ApprovalPreviewResponse PreviewCredential(ApplicationResponse application, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(application);
        var names = application.PermissionIds.Select(PermissionName);
        var statement = ApprovalStatementBuilder.ForCredential(application.Name, names, expiresAt);
        return ApprovalStatementBuilder.ToPreview(ApprovalKinds.ApplicationCredential, application.Id.ToString("D"), statement);
    }

    /// <param name="itemId">The webhook's id, or empty for one that does not exist yet.</param>
    public ApprovalPreviewResponse PreviewWebhook(Uri receiver, IEnumerable<string> eventTypes, bool allowLocalNetwork, string itemId)
    {
        var statement = ApprovalStatementBuilder.ForWebhook(receiver, eventTypes, allowLocalNetwork);
        return ApprovalStatementBuilder.ToPreview(ApprovalKinds.Webhook, itemId, statement);
    }

    /// <summary>True when the model every Local AI feature reads is already on this machine, so no download is needed first.</summary>
    public static bool IsFeatureModelReady(IModelDownloadManager downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        return downloads.GetStatus(AiModelRole.TextQuality).State is AiModelState.Ready or AiModelState.Loaded;
    }

    // ── Checking and recording ───────────────────────────────────────────────

    /// <summary>True when a record for this item already holds exactly this statement.</summary>
    public Task<bool> IsCoveredAsync(string itemKind, string itemId, string statementHash, CancellationToken ct) =>
        records.ExistsAsync(itemKind, itemId, statementHash, ct);

    /// <summary>True when the hash the caller sent is the current statement's hash.</summary>
    public static bool HashMatches(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Works out who is confirming from the request's identity.</summary>
    public async Task<ApprovalActor> ResolveActorAsync(RequestAuthority authority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.AccountId is { } accountId)
        {
            string? name = null;
            if (authority.ActiveProfileId is { } profileId)
            {
                name = (await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false))?.DisplayName;
            }

            return new ApprovalActor(accountId, authority.ActiveProfileId, authority.ApplicationId, Name(name));
        }

        if (authority.ApplicationId is { } applicationId)
        {
            var application = await applications.GetApplicationAsync(applicationId, ct).ConfigureAwait(false);
            return new ApprovalActor(null, null, applicationId, Name(application?.Name));
        }

        throw new InvalidOperationException("An approval needs a signed-in person or an application.");
    }

    /// <summary>Appends the record: who, when, and exactly what was shown. <paramref name="itemId"/> is the stored item id.</summary>
    public Task RecordAsync(ApprovalPreviewResponse preview, string itemKind, string itemId, ApprovalActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(actor);
        return records.AddAsync(
            new ApprovalRecord(
                Guid.NewGuid(),
                itemKind,
                itemId,
                preview.StatementHash,
                JsonSerializer.Serialize(preview, StatementJson),
                actor.AccountId,
                actor.ProfileId,
                actor.ApplicationId,
                actor.Name,
                clock.GetUtcNow()),
            ct);
    }

    /// <summary>The approvals for one item, newest first.</summary>
    public async Task<IReadOnlyList<ApprovalRecordResponse>> ListAsync(string itemKind, string itemId, CancellationToken ct)
    {
        var rows = await records.ListAsync(itemKind, itemId, 50, ct).ConfigureAwait(false);
        return rows.Select(row => new ApprovalRecordResponse
        {
            ApprovedByName = row.ApprovedByName,
            ApprovedAt = row.ApprovedAt,
            Statement = JsonSerializer.Deserialize<ApprovalPreviewResponse>(row.StatementJson, StatementJson) ?? new ApprovalPreviewResponse(),
            StatementHash = row.StatementHash,
        }).ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // The permission catalogue's human-readable name; an id the catalogue does not know is shown as it is.
    private string PermissionName(string permissionId) =>
        permissions.TryGet(new ApplicationPermissionId(permissionId), out var definition) ? definition.DisplayName : permissionId;

    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? UnknownApprover : name.Trim();
}
