using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Official Stash-box plugin's own settings (Videos spec D21, D29, D50): which stash-box servers are used and in what order,
/// each server's account key, and the separate "sends a fingerprint of your file" permission. For administrators only. No
/// response ever carries a key, and a key is checked against its server before it is kept.
/// </summary>
public static class StashBoxEndpoints
{
    public const string Disclosure =
        "This contacts a stash-box server and is only ever used for items in the Vault. It sends the video's title. "
        + "It sends a fingerprint of your file only if you turn that permission on. Your account key is kept on this machine and never shown again.";

    public const string FingerprintText = "Sends a fingerprint of your file";

    public static IEndpointRouteBuilder MapStashBoxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/stashbox").WithTags("Stash-box");

        // The plugin belongs to the Vault, so it is not there for anyone whose Vault mode is off, administrators and apps included:
        // the pre-binding gate answers "not found" before a body or an id is read (Videos spec D17, D45).
        group.WithMetadata(VaultGatedRoute.Instance);

        group.MapGet("", async (IStashBoxSettings settings, CancellationToken ct) =>
            Results.Ok(ToDto(await settings.GetAsync(ct))))
            .WithName("GetStashBoxSettings")
            .WithSummary("The Stash-box servers (in the order they are tried), their connection state and the fingerprint permission. Never a key.")
            .Produces<StashBoxSettingsDto>()
            .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        group.MapPost("/servers", async (StashBoxAddServerRequest request, IStashBoxSettings settings, StashBoxClient client, CancellationToken ct) =>
        {
            if (!StashBoxAddress.TryNormalize(request.Address, out var address))
            {
                return ApiErrors.BadRequest("That address cannot be used. It must start with https:// (http:// is only allowed for a server on this computer) and have no sign-in details in it.");
            }

            var server = await settings.AddServerAsync(request.Name, address.AbsoluteUri, ct);
            if (server is null)
            {
                return ApiErrors.Conflict("That server is already in the list, or the list is full.");
            }

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                var test = await client.TestAsync(address, request.ApiKey.Trim(), ct);
                if (test.Connected)
                {
                    await settings.SetKeyAsync(server.Id, request.ApiKey, ct);
                    await settings.SetConnectionAsync(server.Id, StashBoxConnectionStates.Connected, ct);
                }
                else
                {
                    return Results.Ok(new StashBoxAddServerResultDto { Server = ToDto(server), Test = ToDto(test) });
                }
            }

            var saved = (await settings.GetAsync(ct)).Servers.First(s => s.Id == server.Id);
            return Results.Ok(new StashBoxAddServerResultDto { Server = ToDto(saved) });
        })
        .WithName("AddStashBoxServer")
        .WithSummary("Adds a stash-box server by address (and optionally its key, which is checked first).")
        .Produces<StashBoxAddServerResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapDelete("/servers/{serverId}", async (string serverId, IStashBoxSettings settings, CancellationToken ct) =>
            await settings.RemoveServerAsync(serverId, ct) ? Results.NoContent() : ApiErrors.NotFound("There is no such server, or it cannot be removed."))
            .WithName("RemoveStashBoxServer")
            .WithSummary("Removes an added server and its key. The default server cannot be removed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapPut("/servers/order", async (StashBoxOrderRequest request, IStashBoxSettings settings, CancellationToken ct) =>
        {
            await settings.SetOrderAsync(request.ServerIds ?? [], ct);
            return Results.Ok(ToDto(await settings.GetAsync(ct)));
        })
        .WithName("SetStashBoxServerOrder")
        .WithSummary("Sets the order the servers are tried in.")
        .Produces<StashBoxSettingsDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapPut("/servers/{serverId}/key", async (string serverId, StashBoxKeyRequest request, IStashBoxSettings settings, StashBoxClient client, CancellationToken ct) =>
        {
            var server = (await settings.GetAsync(ct)).Servers.FirstOrDefault(s => s.Id == serverId);
            if (server is null || !StashBoxAddress.TryNormalize(server.Address, out var address))
            {
                return ApiErrors.NotFound("There is no such server.");
            }

            var key = request.ApiKey?.Trim() ?? string.Empty;
            if (key.Length < 8 || key.Length > 512 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return ApiErrors.BadRequest("That does not look like an account key.");
            }

            // Checked first, kept only when the server accepts it (like the other plugins' Test and Save).
            var test = await client.TestAsync(address, key, ct);
            if (test.Connected)
            {
                await settings.SetKeyAsync(serverId, key, ct);
                await settings.SetConnectionAsync(serverId, StashBoxConnectionStates.Connected, ct);
            }

            return Results.Ok(ToDto(test));
        })
        .WithName("SaveStashBoxServerKey")
        .WithSummary("Checks an account key against its server and keeps it only if the server accepts it. The key is never returned.")
        .Produces<StashBoxTestResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapDelete("/servers/{serverId}/key", async (string serverId, IStashBoxSettings settings, CancellationToken ct) =>
        {
            if ((await settings.GetAsync(ct)).Servers.All(s => s.Id != serverId))
            {
                return ApiErrors.NotFound("There is no such server.");
            }

            await settings.RemoveKeyAsync(serverId, ct);
            return Results.NoContent();
        })
        .WithName("RemoveStashBoxServerKey")
        .WithSummary("Removes a server's account key. The library then behaves as if its lookup were None.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapPost("/test", async (StashBoxTestRequest request, IStashBoxSettings settings, StashBoxClient client, CancellationToken ct) =>
        {
            Uri? address = null;
            string? key = request.ApiKey?.Trim();
            var useStored = false;
            if (!string.IsNullOrWhiteSpace(request.ServerId))
            {
                var server = (await settings.GetAsync(ct)).Servers.FirstOrDefault(s => s.Id == request.ServerId);
                if (server is null || !StashBoxAddress.TryNormalize(server.Address, out address))
                {
                    return ApiErrors.NotFound("There is no such server.");
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    key = (await settings.GetCredentialAsync(server.Id, ct))?.Key;
                    useStored = true;
                }
            }
            else if (!StashBoxAddress.TryNormalize(request.Address, out address))
            {
                return ApiErrors.BadRequest("That address cannot be used. It must start with https:// (http:// is only allowed for a server on this computer).");
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Ok(ToDto(new StashBoxTestResult(false, StashBoxConnectionStates.NotConnected, "Enter the account key first.")));
            }

            var test = await client.TestAsync(address!, key, ct);
            if (useStored && test.Status != "unavailable")
            {
                await settings.SetConnectionAsync(request.ServerId!, test.Status, ct);
            }

            return Results.Ok(ToDto(test));
        })
        .WithName("TestStashBoxConnection")
        .WithSummary("Test connection: checks an address and key (typed, or a saved server's own key) and saves nothing but the connection state.")
        .Produces<StashBoxTestResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        group.MapPut("/fingerprint-permission", async (StashBoxFingerprintPermissionRequest request, IStashBoxSettings settings, CancellationToken ct) =>
        {
            await settings.SetFingerprintPermissionAsync(request.Allowed, ct);
            return Results.Ok(ToDto(await settings.GetAsync(ct)));
        })
        .WithName("SetStashBoxFingerprintPermission")
        .WithSummary("Turns \"sends a fingerprint of your file\" on or off. Off until an administrator turns it on.")
        .Produces<StashBoxSettingsDto>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        return app;
    }

    private static StashBoxSettingsDto ToDto(StashBoxSettingsSnapshot snapshot) => new()
    {
        Servers = snapshot.Servers.Select(ToDto).ToList(),
        FingerprintPermission = snapshot.FingerprintPermission,
        FingerprintPermissionText = FingerprintText,
        Disclosure = Disclosure,
    };

    private static StashBoxServerDto ToDto(StashBoxServer server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        Address = server.Address,
        IsDefault = server.Id == StashBoxSettings.DefaultServerId,
        Connection = server.Connection,
    };

    private static StashBoxTestResultDto ToDto(StashBoxTestResult result) => new()
    {
        Connected = result.Connected,
        Status = result.Status,
        Message = result.Message,
    };
}
