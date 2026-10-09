using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

public static class ClientAuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapClientAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        var oauth = app.MapGroup("/api/v1/oauth")
            .WithTags("Client authorization")
            .AllowAnonymous();

        oauth.MapPost("/device_authorization", async (
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            ClientAuthorizationService authorization,
            IConfigurationLoader configuration,
            CancellationToken ct) =>
        {
            try
            {
                var request = await ReadDeviceAuthorizationRequestAsync(httpRequest, ct);
                var publicOrigin = PairingOrigin(httpRequest, configuration.LoadNetwork());
                httpResponse.Headers.CacheControl = "no-store";
                return Results.Ok(await authorization.BeginAsync(request, publicOrigin, ct));
            }
            catch (ArgumentException ex)
            {
                return OAuthError("invalid_request", ex.Message);
            }
            catch (JsonException)
            {
                return OAuthError("invalid_request", "The request body is invalid.");
            }
        })
        .WithName("BeginDeviceAuthorization")
        .WithSummary("Starts RFC 8628-style device-code pairing for a public native client.")
        .Produces<DeviceAuthorizationResponse>()
        .RequireRateLimiting("authentication");

        oauth.MapPost("/token", async (
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            ClientAuthorizationService authorization,
            CancellationToken ct) =>
        {
            OAuthTokenRequest request;
            try
            {
                request = await ReadTokenRequestAsync(httpRequest, ct);
            }
            catch (JsonException)
            {
                return OAuthError("invalid_request", "The request body is invalid.");
            }

            httpResponse.Headers.CacheControl = "no-store";
            httpResponse.Headers.Pragma = "no-cache";
            var result = await authorization.ExchangeAsync(request, ct);
            return result.Success is not null
                ? Results.Ok(result.Success)
                : Results.Json(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("ExchangeClientToken")
        .WithSummary("Exchanges a device code or rotating refresh token for bearer credentials.")
        .Produces<OAuthTokenResponse>()
        .Produces<OAuthErrorResponse>(StatusCodes.Status400BadRequest)
        .RequireRateLimiting("authentication");

        var pairing = app.MapGroup("/api/v1/pairing")
            .WithTags("Device pairing")
            .RequireAuthorization(AuthPolicies.DashboardInteractive);

        pairing.MapGet("/review/{userCode}", async (
            string userCode,
            ClientAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var review = await authorization.ReviewAsync(userCode, ct);
            return review is null ? ApiErrors.NotFound("The pairing code is invalid or expired.") : Results.Ok(review);
        })
        .WithName("ReviewDevicePairing")
        .Produces<PairingReviewResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        pairing.MapPost("/decision", async (
            PairingDecisionRequest request,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            CancellationToken ct) =>
        {
            try
            {
                var profileId = RequiredGuidClaim(user, TuvimaClaimTypes.ActiveProfileId);
                var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
                return await authorization.DecideAsync(request, accountId, profileId, profileId, ct)
                    ? Results.NoContent()
                    : ApiErrors.NotFound("The pairing code is invalid, expired, or already decided.");
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("DecideDevicePairing")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        var devices = app.MapGroup("/api/v1/devices")
            .WithTags("Devices")
            .RequireAuthorization(AuthPolicies.Authenticated);

        devices.MapGet("/", async (ClaimsPrincipal user, ClientAuthorizationService authorization, CancellationToken ct) =>
        {
            var profileId = RequiredGuidClaim(user, TuvimaClaimTypes.ActiveProfileId);
            var values = await authorization.GetDevicesAsync(profileId, ct);
            return Results.Ok(values.Select(ToDto));
        })
        .WithName("ListClientDevices")
        .Produces<IReadOnlyList<ClientDeviceDto>>();

        devices.MapGet("/current", async (ClaimsPrincipal user, ClientAuthorizationService authorization, CancellationToken ct) =>
        {
            var deviceId = RequiredGuidClaim(user, TuvimaClaimTypes.DeviceId);
            var device = await authorization.GetDeviceAsync(deviceId, ct);
            return device is null ? ApiErrors.NotFound("Device not found.") : Results.Ok(ToDto(device));
        })
        .WithName("GetCurrentClientDevice")
        .Produces<ClientDeviceDto>();

        devices.MapPut("/current/capabilities", async (
            ClientCapabilitiesDto request,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var deviceId = RequiredGuidClaim(user, TuvimaClaimTypes.DeviceId);
            return await authorization.UpdateCapabilitiesAsync(deviceId, request, ct)
                ? Results.NoContent()
                : ApiErrors.NotFound("Device not found or revoked.");
        })
        .WithName("UpdateClientCapabilities")
        .Produces(StatusCodes.Status204NoContent)
        .RequireClientScope(ClientApiScopes.PlaybackWrite);

        devices.MapDelete("/{deviceId:guid}", async (
            Guid deviceId,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var profileId = RequiredGuidClaim(user, TuvimaClaimTypes.ActiveProfileId);
            return await authorization.RevokeDeviceAsync(deviceId, profileId, ct)
                ? Results.NoContent()
                : ApiErrors.NotFound("Device not found or already revoked.");
        })
        .WithName("RevokeClientDevice")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<DeviceAuthorizationRequest> ReadDeviceAuthorizationRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return await request.ReadFromJsonAsync<DeviceAuthorizationRequest>(cancellationToken: ct)
                ?? throw new ArgumentException("A request body is required.");
        }

        var form = await request.ReadFormAsync(ct);
        var capabilities = string.IsNullOrWhiteSpace(form["capabilities"])
            ? new ClientCapabilitiesDto()
            : JsonSerializer.Deserialize<ClientCapabilitiesDto>(form["capabilities"]!) ?? new ClientCapabilitiesDto();
        return new DeviceAuthorizationRequest
        {
            ClientId = form["client_id"].ToString(),
            ClientName = form["client_name"].ToString(),
            ClientVersion = form["client_version"].ToString(),
            DeviceName = form["device_name"].ToString(),
            DeviceClass = form["device_class"].ToString(),
            Scope = form["scope"].ToString(),
            Capabilities = capabilities,
        };
    }

    private static async Task<OAuthTokenRequest> ReadTokenRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return await request.ReadFromJsonAsync<OAuthTokenRequest>(cancellationToken: ct)
                ?? new OAuthTokenRequest();
        }

        var form = await request.ReadFormAsync(ct);
        return new OAuthTokenRequest
        {
            GrantType = form["grant_type"].ToString(),
            ClientId = form["client_id"].ToString(),
            DeviceCode = form["device_code"].ToString(),
            RefreshToken = form["refresh_token"].ToString(),
        };
    }

    /// <summary>
    /// The origin the pairing link is built from: the public address when one is set, otherwise the
    /// Engine's own request origin. <c>X-Forwarded-*</c> headers are never trusted here; the Dashboard
    /// edge replaces the value with its own host-allow-listed origin.
    /// </summary>
    internal static string PairingOrigin(HttpRequest request, NetworkSettings network)
    {
        if (network.HasValidPublicAddress())
        {
            return network.Remote.PublicHostname!.Trim().TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host.Value}";
    }

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new OAuthErrorResponse { Error = error, ErrorDescription = description }, statusCode: StatusCodes.Status400BadRequest);

    private static Guid RequiredGuidClaim(ClaimsPrincipal user, string type) =>
        Guid.TryParse(user.FindFirstValue(type), out var value)
            ? value
            : throw new UnauthorizedAccessException($"Required claim '{type}' is missing.");

    private static ClientDeviceDto ToDto(ClientDevice device) => new()
    {
        Id = device.Id,
        ProfileId = device.ProfileId,
        DeviceName = device.DeviceName,
        DeviceClass = device.DeviceClass,
        ClientId = device.ClientId,
        ClientName = device.ClientName,
        ClientVersion = device.ClientVersion,
        Scopes = ClientAuthorizationService.SplitScopes(device.Scopes),
        CreatedAt = device.CreatedAt,
        LastSeenAt = device.LastSeenAt,
        RevokedAt = device.RevokedAt,
    };
}
