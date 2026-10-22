using System.Security.Cryptography;
using System.Text.Json;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Services.Webhooks;

internal sealed class ApplicationWebhookService(
    ApplicationWebhookRepository repository,
    IApplicationRepository applications,
    IApplicationEventRepository events,
    IApplicationEventDeliveryAuthorizer authorization,
    ApplicationEventRegistry eventTypes,
    WebhookDestinationPolicy destinations,
    IDataProtectionProvider protection,
    TimeProvider clock,
    ApprovalService approvals)
{
    public IReadOnlyList<string> EventTypes => eventTypes.ListAvailableTypes();

    public async Task<IReadOnlyList<ApplicationWebhookResponse>> ListAsync(Guid applicationId, CancellationToken ct)
    {
        await RequireApplicationAsync(applicationId, ct);
        var failures = await repository.ConsecutiveFailuresAsync(ct);
        return (await repository.ListAsync(applicationId, ct))
            .Select(webhook => Map(webhook, failures.GetValueOrDefault(webhook.Id)))
            .ToList();
    }

    public const int DefaultAttemptCount = 6;
    public const int MaximumAttemptCount = 50;

    /// <summary>A webhook's most recent delivery attempts, newest first. Defaults to the last six; at most fifty.</summary>
    public async Task<IReadOnlyList<ApplicationWebhookAttemptResponse>> ListAttemptsAsync(Guid applicationId, Guid webhookId, int? limit, CancellationToken ct)
    {
        await GetAsync(applicationId, webhookId, ct);
        var attempts = await repository.ListAttemptsAsync(webhookId, Math.Clamp(limit ?? DefaultAttemptCount, 1, MaximumAttemptCount), ct);
        return attempts.Select(WebhookAttempts.Map).ToList();
    }

    /// <summary>The webhook, once the application is known to be one that can have webhooks and to own it.</summary>
    internal async Task<ApplicationWebhook> GetAsync(Guid applicationId, Guid webhookId, CancellationToken ct)
    {
        await RequireApplicationAsync(applicationId, ct);
        return await RequireWebhookAsync(applicationId, webhookId, ct);
    }

    /// <summary>
    /// Creates or updates a webhook. A webhook that will be switched on needs the person to have seen where events go
    /// (the approval rule): a new one always, an existing one when it was off or its address, events or private-network
    /// setting changed, unless its current statement is already recorded. Otherwise <see cref="ApprovalRequiredException"/>
    /// carries what to show. Nothing is saved until the approval is covered.
    /// </summary>
    public async Task<ApplicationWebhookSecretResponse> SaveAsync(Guid applicationId, Guid? id,
        SaveApplicationWebhookRequest request, RequestAuthority authority, CancellationToken ct)
    {
        await RequireApplicationAsync(applicationId, ct);
        if (request.EventTypes is null || request.EventTypes.Count is < 1 or > 32
            || request.EventTypes.Any(type => string.IsNullOrWhiteSpace(type) || !eventTypes.TryGet(type, out _)))
        {
            throw new ArgumentException("Select one or more supported event types.");
        }

        var types = request.EventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (request.IsEnabled && !await authorization.CanSubscribeAsync(applicationId, types, ct))
        {
            throw new ArgumentException("The application needs events.subscribe and read access for every selected event type.");
        }

        var uri = WebhookDestinationPolicy.Parse(request.Url, request.AllowLocalNetwork);
        await destinations.ResolveApprovedAsync(uri, request.AllowLocalNetwork, ct);
        var webhook = id.HasValue ? await RequireWebhookAsync(applicationId, id.Value, ct) : new ApplicationWebhook
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            CreatedAt = clock.GetUtcNow(),
        };
        if (id.HasValue && request.ExpectedVersion != webhook.Version)
        {
            throw new InvalidOperationException("The webhook changed. Reload before saving.");
        }

        // The approval rule. Decided against what is stored now, before anything is changed.
        var preview = approvals.PreviewWebhook(uri, types, request.AllowLocalNetwork, id?.ToString("D") ?? "");
        var needsApproval = request.IsEnabled
            && (!id.HasValue || !webhook.IsEnabled || ReceiverChanged(webhook, uri, types, request.AllowLocalNetwork));
        var confirmedNow = false;
        if (needsApproval
            && !(id.HasValue && await approvals.IsCoveredAsync(ApprovalKinds.Webhook, webhook.Id.ToString("D"), preview.StatementHash, ct)))
        {
            if (!ApprovalService.HashMatches(request.ApprovalHash, preview.StatementHash))
            {
                throw new ApprovalRequiredException([preview]);
            }

            confirmedNow = true;
        }

        ApprovalActor? actor = null;
        if (confirmedNow)
        {
            actor = await approvals.ResolveActorAsync(authority, ct);
            if (id.HasValue)
            {
                // The webhook already exists, so its approval is recorded before the change is saved.
                await approvals.RecordAsync(preview, ApprovalKinds.Webhook, webhook.Id.ToString("D"), actor, ct);
            }
        }

        webhook.Url = uri.AbsoluteUri;
        webhook.EventTypesJson = JsonSerializer.Serialize(types);
        webhook.IsEnabled = request.IsEnabled;
        webhook.AllowLocalNetwork = request.AllowLocalNetwork;
        webhook.UpdatedAt = clock.GetUtcNow();
        webhook.LastStatus = request.IsEnabled ? "Waiting for new events" : "Disabled";
        webhook.LastEventSequence = (await events.GetBoundsAsync(ct)).LatestSequence ?? 0;
        var secret = id.HasValue ? null : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (secret is not null)
        {
            webhook.SecretCiphertext = Protector(webhook.Id).Protect(secret);
        }

        await repository.SaveAsync(webhook, !id.HasValue, ct);
        if (confirmedNow && !id.HasValue)
        {
            // A new webhook has no id until now. If its approval cannot be recorded it is removed again, so no
            // receiver is ever switched on without a record.
            var recorded = false;
            try
            {
                await approvals.RecordAsync(
                    preview with { ItemId = webhook.Id.ToString("D") }, ApprovalKinds.Webhook, webhook.Id.ToString("D"), actor!, ct);
                recorded = true;
            }
            finally
            {
                if (!recorded)
                {
                    await repository.DeleteAsync(applicationId, webhook.Id, CancellationToken.None);
                }
            }
        }

        if (id.HasValue)
        {
            webhook.Version++;
        }

        return new(Map(webhook), secret);
    }

    private static bool ReceiverChanged(ApplicationWebhook stored, Uri receiver, IReadOnlyList<string> types, bool allowLocalNetwork) =>
        !string.Equals(stored.Url, receiver.AbsoluteUri, StringComparison.Ordinal)
        || stored.AllowLocalNetwork != allowLocalNetwork
        || !(JsonSerializer.Deserialize<string[]>(stored.EventTypesJson) ?? []).Order(StringComparer.Ordinal).SequenceEqual(types, StringComparer.Ordinal);

    public async Task<ApplicationWebhookSecretResponse> RotateAsync(Guid applicationId, Guid id, CancellationToken ct)
    {
        await RequireApplicationAsync(applicationId, ct);
        var webhook = await RequireWebhookAsync(applicationId, id, ct);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        webhook.SecretCiphertext = Protector(id).Protect(secret);
        webhook.UpdatedAt = clock.GetUtcNow();
        webhook.LastStatus = "Signing secret rotated; previous pending deliveries cancelled";
        await repository.SaveAsync(webhook, false, ct);
        webhook.Version++;
        return new(Map(webhook), secret);
    }

    public async Task DeleteAsync(Guid applicationId, Guid id, CancellationToken ct)
    {
        await RequireApplicationAsync(applicationId, ct);
        await RequireWebhookAsync(applicationId, id, ct);
        await repository.DeleteAsync(applicationId, id, ct);
    }

    internal string RevealSecret(ApplicationWebhook webhook) => Protector(webhook.Id).Unprotect(webhook.SecretCiphertext);
    private IDataProtector Protector(Guid id) => protection.CreateProtector("Tuvima.ApplicationWebhooks.v1", id.ToString("D"));

    private async Task RequireApplicationAsync(Guid applicationId, CancellationToken ct)
    {
        var application = await applications.GetApplicationAsync(applicationId, ct)
            ?? throw new KeyNotFoundException("Application not found.");
        if (application.ApplicationType is not (ApplicationType.ServerIntegration or ApplicationType.Automation))
        {
            throw new ArgumentException("Webhooks require a server integration or automation application.");
        }
    }

    private async Task<ApplicationWebhook> RequireWebhookAsync(Guid applicationId, Guid id, CancellationToken ct)
    {
        var webhook = await repository.FindAsync(id, ct);
        return webhook?.ApplicationId == applicationId ? webhook : throw new KeyNotFoundException("Webhook not found.");
    }

    internal static ApplicationWebhookResponse Map(ApplicationWebhook webhook, int consecutiveFailures = 0) => new(
        webhook.Id, webhook.ApplicationId, webhook.Url,
        JsonSerializer.Deserialize<string[]>(webhook.EventTypesJson) ?? [], webhook.AllowLocalNetwork,
        webhook.IsEnabled, webhook.Version, webhook.LastStatus, webhook.LastAttemptAt, webhook.LastSuccessAt, consecutiveFailures);
}
