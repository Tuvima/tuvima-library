using System.Net.Http.Json;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- NSFW mode and Vault libraries (/api/v1/nsfw, /api/v1/vault/libraries) --------------------------------
    // Every call goes through the shared envelope helpers and then reads the failure they recorded, so a refusal is told
    // apart without a raw HTTP call: 404 (this profile cannot use it), 428 (a PIN is needed or was wrong), 429 (too many
    // tries) and a plain-language 400/409. PINs travel only in request bodies; nothing here logs a body, and calls that carry
    // a PIN do not log failures as warnings.

    private const string NsfwStatusPath = "/api/v1/nsfw/status";
    private const string VaultLibrariesPath = "/api/v1/vault/libraries";

    public async Task<VaultCall<NsfwStatusDto>> GetNsfwStatusAsync(CancellationToken ct = default)
    {
        var status = await GetAsync<NsfwStatusDto>("GET /api/v1/nsfw/status", NsfwStatusPath, logAsWarning: false, ct: ct);
        // A refusal, an unreachable Engine and an answer that is not one of the two modes a profile can use look the same here on
        // purpose: in each case nothing about NSFW mode is drawn.
        return status is { Mode: "pin" or "allowed" } ? VaultCall<NsfwStatusDto>.Ok(status) : VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable);
    }

    public async Task<VaultCall<NsfwStatusDto>> UnlockNsfwAsync(string? pin, CancellationToken ct = default)
    {
        var status = await PostAsync<NsfwUnlockRequest, NsfwStatusDto>(
            "POST /api/v1/nsfw/unlock", "/api/v1/nsfw/unlock", new NsfwUnlockRequest { Pin = pin }, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<bool>> LockNsfwAsync(CancellationToken ct = default) =>
        await PostAsync("POST /api/v1/nsfw/lock", "/api/v1/nsfw/lock", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<bool>> KeepNsfwAliveAsync(CancellationToken ct = default) =>
        await PostAsync("POST /api/v1/nsfw/keepalive", "/api/v1/nsfw/keepalive", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<bool>> SetNsfwPinAsync(NsfwSetPinRequest request, CancellationToken ct = default) =>
        await PutAsync("PUT /api/v1/nsfw/pin", "/api/v1/nsfw/pin", request, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<NsfwStatusDto>> SetProfileNsfwAccessAsync(Guid profileId, SetNsfwAccessRequest request, CancellationToken ct = default)
    {
        var status = await PutAsync<SetNsfwAccessRequest, NsfwStatusDto>(
            "PUT /api/v1/profiles/{id}/nsfw-access", $"/api/v1/profiles/{profileId:D}/nsfw-access", request, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<bool>> ResetProfileNsfwPinAsync(Guid profileId, ResetNsfwPinRequest request, CancellationToken ct = default)
    {
        const string endpoint = "DELETE /api/v1/profiles/{id}/nsfw-pin";
        try
        {
            // The Engine reads the administrator's PIN from the request body, which the shared DELETE envelope cannot carry.
            using var message = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/profiles/{profileId:D}/nsfw-pin")
            {
                Content = JsonContent.Create(request),
            };
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct, logAsWarning: false);
                return FailedVaultCall<bool>();
            }

            ClearFailure(endpoint);
            return VaultCall<bool>.Ok(true);
        }
        catch (OperationCanceledException) { return FailedVaultCall<bool>(); }
        catch (Exception ex)
        {
            RecordExceptionFailure(endpoint, ex, logAsWarning: false);
            return FailedVaultCall<bool>();
        }
    }

    public async Task<VaultCall<IReadOnlyList<VaultLibraryDto>>> GetVaultLibrariesAsync(CancellationToken ct = default)
    {
        var libraries = await GetAsync<List<VaultLibraryDto>>("GET /api/v1/vault/libraries", VaultLibrariesPath, logAsWarning: false, ct: ct);
        return libraries is null
            ? VaultCall<IReadOnlyList<VaultLibraryDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultLibraryDto>>.Ok(libraries);
    }

    public async Task<VaultCall<VaultLibraryDto>> CreateVaultLibraryAsync(CreateVaultLibraryRequest request, CancellationToken ct = default)
    {
        var created = await PostAsync<CreateVaultLibraryRequest, VaultLibraryDto>(
            "POST /api/v1/vault/libraries", VaultLibrariesPath, request, logAsWarning: false, ct: ct);
        return created is null ? FailedVaultCall<VaultLibraryDto>() : VaultCall<VaultLibraryDto>.Ok(created);
    }

    public async Task<VaultCall<VaultLibraryDto>> UpdateVaultLibraryAsync(string libraryId, UpdateVaultLibraryRequest request, CancellationToken ct = default)
    {
        var updated = await PutAsync<UpdateVaultLibraryRequest, VaultLibraryDto>(
            "PUT /api/v1/vault/libraries/{id}", $"{VaultLibrariesPath}/{Uri.EscapeDataString(libraryId)}", request, logAsWarning: false, ct: ct);
        return updated is null ? FailedVaultCall<VaultLibraryDto>() : VaultCall<VaultLibraryDto>.Ok(updated);
    }

    public async Task<VaultCall<bool>> RemoveVaultLibraryAsync(string libraryId, RemoveVaultLibraryRequest request, CancellationToken ct = default) =>
        await PostAsync(
            "POST /api/v1/vault/libraries/{id}/remove", $"{VaultLibrariesPath}/{Uri.EscapeDataString(libraryId)}/remove", request, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    /// <summary>Reads the failure the shared envelope just recorded and names it in the terms the Vault screens use.</summary>
    private VaultCall<T> FailedVaultCall<T>()
    {
        var error = LastError ?? string.Empty;
        return LastStatusCode switch
        {
            404 => VaultCall<T>.Of(VaultOutcome.NotAvailable),
            428 when error.Contains("Administrator PIN required", StringComparison.OrdinalIgnoreCase) => VaultCall<T>.Of(VaultOutcome.AdministratorPinNotSet),
            428 => VaultCall<T>.Of(VaultOutcome.PinRequired),
            429 => VaultCall<T>.Of(VaultOutcome.TooManyAttempts),
            403 when error.Contains("Confirm it's you", StringComparison.OrdinalIgnoreCase) => VaultCall<T>.Of(VaultOutcome.ConfirmItsYou),
            400 or 409 => VaultCall<T>.Of(VaultOutcome.Rejected, PlainVaultReason(error)),
            _ => VaultCall<T>.Of(VaultOutcome.Failed),
        };
    }

    // The recorded error reads "HTTP 400: Title Detail Trace: id"; the screen wants only the sentence the Engine wrote.
    private static string? PlainVaultReason(string recorded)
    {
        var colon = recorded.IndexOf(':', StringComparison.Ordinal);
        var reason = colon >= 0 ? recorded[(colon + 1)..] : recorded;
        var trace = reason.IndexOf(" Trace:", StringComparison.Ordinal);
        if (trace >= 0)
        {
            reason = reason[..trace];
        }

        reason = reason.Trim();
        foreach (var title in (string[])["Invalid request.", "Conflict."])
        {
            if (reason.StartsWith(title, StringComparison.Ordinal))
            {
                reason = reason[title.Length..].Trim();
            }
        }

        return reason.Length == 0 ? null : reason;
    }
}
