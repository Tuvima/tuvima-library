using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Registers each signed-in Dashboard circuit with <see cref="OpenScreenRegistry"/> and removes it when the circuit ends.</summary>
public sealed class OpenScreenCircuitHandler(
    OpenScreenRegistry registry,
    AuthenticationStateProvider authentication) : CircuitHandler
{
    private IDisposable? _registration;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var user = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        var token = user.FindFirstValue(DashboardEngineAuthenticationHandler.SessionTokenClaim);
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var screen = new OpenScreen(
            Guid.NewGuid(),
            ParseGuid(user.FindFirstValue("tuvima:account_id")),
            ParseGuid(user.FindFirstValue("tuvima:active_profile_id")),
            ParseGuid(user.FindFirstValue("tuvima:session_id")),
            OpenScreenRegistry.HashToken(token),
            user.FindFirstValue(DashboardPrincipalFactory.ClientIngressClaim) ?? MediaEngine.Contracts.Authentication.ClientIngressValues.Remote);
        _registration = registry.Register(screen, () =>
        {
            (authentication as SessionRevalidatingAuthenticationStateProvider)?.SignOutScreen();
        });
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return Task.CompletedTask;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
