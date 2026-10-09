using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static class NetworkEndpoints
{
    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/settings/network")
            .WithTags("Network & Remote Access");

        settings.MapGet("", (IConfigurationLoader configuration) =>
            Results.Ok(NetworkContractMapper.ToContract(configuration.LoadNetwork())))
            .WithName("GetNetworkSettings")
            .WithSummary("Return desired local, remote, and network-streaming settings.")
            .Produces<NetworkSettingsDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkStatusRead);

        settings.MapPut("", async (
            NetworkSettingsDto request,
            IConfigurationLoader configuration,
            RemoteAccessReadinessService readiness,
            SecureAccountGate secureAccount,
            ClaimsPrincipal user,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(user, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var current = configuration.LoadNetwork();
            var proposed = NetworkContractMapper.ToStorage(request);
            // Until the account has a password or passkey, Tuvima stays on this computer: no wider door, no app access.
            if ((SecureAccountGate.Raises(current.WhoCanConnect, proposed.WhoCanConnect)
                    || (proposed.NativeAppAccess.Enabled && !current.NativeAppAccess.Enabled))
                && await secureAccount.IsLockedAsync(ct).ConfigureAwait(false))
            {
                return SecureAccountGate.Refusal();
            }

            // The strict public-address rule applies when saving, so a hand-edited older value never stops startup.
            if (!string.IsNullOrWhiteSpace(proposed.Remote.PublicHostname)
                && !PublicAddress.IsValid(proposed.Remote.PublicHostname))
            {
                return ApiErrors.BadRequest(PublicAddress.RuleMessage);
            }
            // App access rides on the verified secure path: anything below "Anywhere" turns it off too.
            if (!proposed.AllowsInternet)
            {
                proposed.NativeAppAccess.Enabled = false;
            }
            if (proposed.Local.Port != current.Local.Port)
            {
                return ApiErrors.Conflict("Use the Change Port action so Tuvima can check the new port before saving it.");
            }

            try
            {
                if (proposed.AllowsInternet)
                {
                    var result = await readiness.EvaluateAsync(proposed.Remote, ct).ConfigureAwait(false);
                    if (!result.Ready)
                    {
                        var blockers = string.Join(" ", result.Checks
                            .Where(check => check.Status == "failed")
                            .Select(check => check.Detail));
                        return ApiErrors.Conflict($"Anywhere was not enabled. {blockers}");
                    }
                }
                configuration.SaveNetwork(proposed);
                return Results.Ok(NetworkContractMapper.ToContract(proposed));
            }
            catch (ConfigValidationException ex)
            {
                return ApiErrors.BadRequest(string.Join(" ", ex.ValidationMessages));
            }
        })
        .WithName("UpdateNetworkSettings")
        .WithSummary("Save desired network settings that do not move the active listener.")
        .Produces<NetworkSettingsDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        var network = app.MapGroup("/network")
            .WithTags("Network & Remote Access");

        network.MapGet("/status", (NetworkStatusService status) => Results.Ok(status.GetStatus()))
            .WithName("GetNetworkRuntimeStatus")
            .WithSummary("Return observed connectivity state without mixing it into configuration.")
            .Produces<NetworkRuntimeStatusDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkStatusRead);

        network.MapGet("/readiness", async (
            IConfigurationLoader configuration,
            RemoteAccessReadinessService readiness,
            CancellationToken ct) =>
            Results.Ok(await readiness.EvaluateAsync(configuration.LoadNetwork().Remote, ct).ConfigureAwait(false)))
            .WithName("GetRemoteAccessReadiness")
            .WithSummary("Verify authentication and a secure remote path before remote access can be enabled.")
            .Produces<RemoteAccessReadinessDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkStatusRead);

        network.MapPost("/tests/local", async (INetworkDiagnosticsService diagnostics, CancellationToken ct) =>
            Results.Ok(await diagnostics.TestLocalAsync(ct)))
            .WithName("TestLocalNetworkConnection")
            .Produces<NetworkTestResultDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/tests/remote", async (INetworkDiagnosticsService diagnostics, CancellationToken ct) =>
            Results.Ok(await diagnostics.TestRemoteAsync(ct)))
            .WithName("TestRemoteNetworkConnection")
            .Produces<NetworkTestResultDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/bandwidth-test", async (INetworkDiagnosticsService diagnostics, CancellationToken ct) =>
            Results.Ok(await diagnostics.TestBandwidthAsync(ct)))
            .WithName("TestNetworkUploadBandwidth")
            .WithSummary("Run an intentional upload measurement when an external measurement target is configured.")
            .Produces<NetworkBandwidthStatusDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/port-change/check", async (
            PortAvailabilityRequest request,
            INetworkDiagnosticsService diagnostics,
            CancellationToken ct) => Results.Ok(await diagnostics.CheckPortAvailabilityAsync(request.Port, ct)))
            .WithName("CheckNetworkPortAvailability")
            .Produces<PortAvailabilityResultDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/port-change/apply", async (
            PortAvailabilityRequest request,
            INetworkDiagnosticsService diagnostics,
            IConfigurationLoader configuration,
            RouterPortMappingCoordinator routerMappings,
            SecureAccountGate secureAccount,
            CancellationToken ct) =>
        {
            var availability = await diagnostics.CheckPortAvailabilityAsync(request.Port, ct);
            if (!availability.Available)
            {
                return ApiErrors.Conflict(availability.Message);
            }

            var current = configuration.LoadNetwork();
            current.Local.Port = request.Port;
            try
            {
                configuration.SaveNetwork(current);
                // Opening a router port lets others in, so it waits until the account has a password or passkey.
                if (current.Remote.AutomaticRouterConfiguration
                    && current.Remote.ConnectionMode == NetworkConnectionModes.DirectOnly
                    && !await secureAccount.IsLockedAsync(ct))
                {
                    await routerMappings.EnsureMappingAsync(ct);
                }

                return Results.Ok(new PortAvailabilityResultDto
                {
                    Port = request.Port,
                    Available = true,
                    RestartRequired = true,
                    Message = $"Port {request.Port} passed validation and was saved. The current address remains active until the Dashboard restarts.",
                });
            }
            catch (ConfigValidationException ex)
            {
                return ApiErrors.BadRequest(string.Join(" ", ex.ValidationMessages));
            }
        })
        .WithName("ApplyNetworkPortChange")
        .Produces<PortAvailabilityResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/router/renew", async (
            RouterPortMappingCoordinator routerMappings,
            NetworkStatusService status,
            CancellationToken ct) =>
        {
            await routerMappings.EnsureMappingAsync(ct);
            return Results.Ok(status.GetStatus());
        })
        .RequireSecuredAccount()
        .WithName("RenewNetworkRouterMapping")
        .WithSummary("Renew or recreate the Tuvima-owned router mapping now.")
        .Produces<NetworkRuntimeStatusDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        network.MapPost("/reset", async (
            IConfigurationLoader configuration,
            SecureAccountGate secureAccount,
            CancellationToken ct) =>
        {
            var defaults = new NetworkSettings();
            if (SecureAccountGate.Raises(configuration.LoadNetwork().WhoCanConnect, defaults.WhoCanConnect)
                && await secureAccount.IsLockedAsync(ct).ConfigureAwait(false))
            {
                return SecureAccountGate.Refusal();
            }

            configuration.SaveNetwork(defaults);
            return Results.Ok(NetworkContractMapper.ToContract(defaults));
        })
        .WithName("ResetNetworkSettings")
        .WithSummary("Reset only network settings to local-first, remote-disabled defaults.")
        .Produces<NetworkSettingsDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite);

        return app;
    }
}
