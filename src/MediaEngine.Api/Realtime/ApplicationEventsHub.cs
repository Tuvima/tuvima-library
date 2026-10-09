using System.Security.Claims;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MediaEngine.Api.Realtime;

[Authorize]
public sealed class ApplicationEventsHub(
    IRequestAuthorityResolver authorityResolver,
    ApplicationEventSubscriptionAuthorizer authorization,
    ApplicationEventDispatcher dispatcher) : Hub
{
    public async Task<ApplicationEventSubscriptionResult> Subscribe(
        ApplicationEventSubscriptionRequest request,
        CancellationToken ct = default)
    {
        var http = Context.GetHttpContext() ?? throw new HubException("Missing request context.");
        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        var types = request.EventTypes?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var libraries = request.LibraryIds?.Where(value => value != Guid.Empty).ToHashSet() ?? [];
        var consent = http.User.FindAll(TuvimaClaimTypes.Scope)
            .Select(claim => new ApplicationPermissionId(claim.Value)).ToHashSet();
        if (!await authorization.CanSubscribeAsync(authority, consent, types, libraries, ct).ConfigureAwait(false))
        {
            throw new HubException("The application is not authorized for the requested event subscription.");
        }

        return await dispatcher.SubscribeAsync(new(
            Context.ConnectionId,
            authority.ApplicationId!.Value,
            ClaimGuid(http.User, TuvimaClaimTypes.ApplicationCredentialId),
            ClaimGuid(http.User, TuvimaClaimTypes.TokenId),
            authority,
            consent,
            types.ToHashSet(StringComparer.Ordinal),
            libraries,
            Context), request.AfterEventId, ct).ConfigureAwait(false);
    }

    private static Guid? ClaimGuid(ClaimsPrincipal user, string type) =>
        Guid.TryParse(user.FindFirstValue(type), out var id) && id != Guid.Empty ? id : null;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        dispatcher.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
