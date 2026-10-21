using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>
/// Counts the integrations that need a look, for the Settings menu (Developer tools spec D-D, D-W). A webhook that failed five
/// deliveries in a row while switched on is Broken (the menu row shows red); three in a row, or a key that is unused for 90
/// days or close to expiring, is Needs attention. Anything switched off is never counted.
/// </summary>
internal sealed class IntegrationHealthService(
    ApplicationAdministrationService applications,
    ApplicationWebhookRepository webhooks,
    TimeProvider clock)
{
    public async Task<IntegrationHealthResponse> GetAsync(RequestAuthority authority, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var apps = await applications.GetApplicationsAsync(authority, ct).ConfigureAwait(false);
        var failures = await webhooks.ConsecutiveFailuresAsync(ct).ConfigureAwait(false);
        var enabledApps = apps.Where(app => app.IsEnabled).Select(app => app.Id).ToHashSet();

        var keys = apps
            .Where(app => app.IsEnabled)
            .Count(app => app.Credentials.Any(credential => credential.RevokedAt is null
                && (IntegrationHealthRules.KeyUnused(credential.CreatedAt, credential.LastUsedAt, now)
                    || IntegrationHealthRules.KeyNearExpiry(credential.ExpiresAt, now))));

        var broken = 0;
        var attention = 0;
        foreach (var webhook in await webhooks.ListAsync(null, ct).ConfigureAwait(false))
        {
            if (!webhook.IsEnabled || !enabledApps.Contains(webhook.ApplicationId))
            {
                continue;
            }

            var count = failures.GetValueOrDefault(webhook.Id);
            if (count >= IntegrationHealthRules.WebhookBrokenFailures)
            {
                broken++;
            }
            else if (count >= IntegrationHealthRules.WebhookAttentionFailures)
            {
                attention++;
            }
        }

        return new IntegrationHealthResponse(broken, attention, keys);
    }
}
