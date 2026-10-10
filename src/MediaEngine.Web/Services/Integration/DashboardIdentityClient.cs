using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>The result of a sign-in style call: the new session, or the Engine's status and (for some refusals) its plain-language reason.</summary>
/// <param name="TwoStepToken">Set (with no session) when the password was right but the account also needs a code from its authenticator app.</param>
public sealed record DashboardSessionAttempt(AuthSessionResponse? Session, HttpStatusCode Status, string? Detail, string? TwoStepToken = null);

public sealed class DashboardIdentityClient(
    IHttpClientFactory clients,
    IHttpContextAccessor? contextAccessor = null,
    ILogger<DashboardIdentityClient>? logger = null,
    IngressClassifier? ingress = null,
    OpenScreenRegistry? openScreens = null,
    SignInAttemptLimiter? signInLimiter = null)
{
    private readonly object _initialAuthorityGate = new();
    private Task<DashboardAuthorityResponse?>? _initialAuthorityTask;
    private DashboardSessionAccessor? _initialAuthoritySession;
    private DashboardSessionSnapshot? _initialAuthoritySnapshot;
    private HttpClient Client => clients.CreateClient("EngineIdentity");

    public async Task<AuthBootstrapStatusResponse?> GetBootstrapStatusAsync(CancellationToken ct = default) =>
        await GetAsync<AuthBootstrapStatusResponse>("/auth/bootstrap/status", ct).ConfigureAwait(false);

    /// <summary>
    /// Asks the Engine which sign-in methods work for this visitor, from where they are. One call per page load,
    /// nothing cached. Null means the Engine could not answer.
    /// </summary>
    public async Task<SignInMethodsResponse?> GetSignInMethodsAsync(CancellationToken ct = default)
    {
        var (clientIngress, isHttps) = GetOriginalClientContext();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/sign-in-methods?https={(isHttps ? "true" : "false")}");
            request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, clientIngress);
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<SignInMethodsResponse>(cancellationToken: ct).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException exception)
        {
            logger?.LogWarning(exception, "Dashboard sign-in methods request could not reach the Engine");
            return null;
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Dashboard sign-in methods request timed out");
            return null;
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(exception, "Dashboard sign-in methods response was malformed");
            return null;
        }
        catch (NotSupportedException exception)
        {
            logger?.LogWarning(exception, "Dashboard sign-in methods response had an unsupported content type");
            return null;
        }
    }

    public async Task<AuthSessionResponse?> LoginAsync(LocalLoginRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/login", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AuthSessionResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// The name of the account that can continue without a password on this computer, or null. Only asked of a visitor on
    /// this computer who is not behind a tunnel or proxy; everyone else is never told the account exists.
    /// </summary>
    public async Task<string?> GetThisComputerAccountNameAsync(CancellationToken ct = default)
    {
        using var request = ThisComputerRequest(HttpMethod.Get);
        if (request is null)
        {
            return null;
        }

        try
        {
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ThisComputerAccountResponse>(cancellationToken: ct).ConfigureAwait(false))?.DisplayName
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger?.LogWarning(exception, "Dashboard this-computer account check could not reach the Engine");
            return null;
        }
    }

    /// <summary>Signs in the this-computer account without a password. Null when it is not offered from here.</summary>
    public async Task<AuthSessionResponse?> SignInThisComputerAsync(ThisComputerSignInRequest body, CancellationToken ct = default)
    {
        using var request = ThisComputerRequest(HttpMethod.Post);
        if (request is null)
        {
            return null;
        }

        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<AuthSessionResponse>(cancellationToken: ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger?.LogWarning(exception, "Dashboard this-computer sign-in could not reach the Engine");
            return null;
        }
    }

    // Built only for a visitor on this computer; the Engine also needs to know when a proxy or tunnel may be relaying them.
    private HttpRequestMessage? ThisComputerRequest(HttpMethod method)
    {
        var context = contextAccessor?.HttpContext;
        if (context is null || IngressClassifierExtensions.FromWireValue(CurrentIngress(context)) != IngressKind.ThisComputer)
        {
            return null;
        }

        var request = new HttpRequestMessage(method, "/auth/this-computer");
        request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, ClientIngressValues.ThisComputer);
        if (context.WasForwarded())
        {
            request.Headers.TryAddWithoutValidation(ClientIngressValues.ForwardedHeader, "true");
        }

        return request;
    }

    public async Task<SessionValidationResponse?> ValidateAsync(string sessionToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/validate");
        request.Headers.TryAddWithoutValidation(DashboardEngineAuthenticationHandler.SessionHeader, sessionToken);
        request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, CurrentIngress(contextAccessor?.HttpContext));
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Unauthorized || !response.IsSuccessStatusCode
            ? null
            : await response.Content.ReadFromJsonAsync<SessionValidationResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<DashboardAuthorityResponse?> RevalidateAuthorityAsync(
        DashboardSessionAccessor session,
        CancellationToken ct = default) =>
        (await RevalidateAuthorityDetailedAsync(session, ct).ConfigureAwait(false)).Authority;

    /// <summary>
    /// The one Engine check for an open screen: refreshes what the person may do and says whether the sign-in
    /// itself still stands. The sign-in check on <see cref="SessionRevalidatingAuthenticationStateProvider"/> uses this
    /// so each open screen costs at most one Engine call a minute.
    /// </summary>
    public async Task<AuthorityRevalidation> RevalidateAuthorityDetailedAsync(
        DashboardSessionAccessor session,
        CancellationToken ct = default)
    {
        var refresh = session.SnapshotForRefresh();
        if (string.IsNullOrWhiteSpace(refresh.Snapshot.SessionToken))
        {
            return new(null, SessionCheckStatus.NoSession);
        }

        var result = await ValidateDetailedAsync(
            refresh.Snapshot.SessionToken, session.LastIngress ?? CurrentIngress(contextAccessor?.HttpContext), ct).ConfigureAwait(false);
        if (result.Invalid)
        {
            session.ClearIfCurrent(refresh);
            return new(null, SessionCheckStatus.Revoked);
        }
        if (result.WrongPlace)
        {
            // A home session seen from outside: drop any stale capabilities but keep the sign-in cookie.
            session.ClearAuthorityIfCurrent(refresh);
            return new(null, SessionCheckStatus.WrongPlace);
        }
        if (result.Unusable)
        {
            session.ClearAuthorityIfCurrent(refresh);
            return new(null, SessionCheckStatus.Unknown);
        }
        var validated = result.Response;
        if (validated is null)
        {
            return new(null, SessionCheckStatus.Unknown);
        }

        if (!session.TrySet(refresh, validated.AccountId, validated.ActiveProfileId, validated.SessionId, validated.Authority))
        {
            return new(null, SessionCheckStatus.Unknown);
        }

        return new(validated.Authority, SessionCheckStatus.Valid);
    }

    /// <summary>Coalesces the first circuit validation used by layout and page initialization.</summary>
    public Task<DashboardAuthorityResponse?> EnsureInitialAuthorityAsync(
        DashboardSessionAccessor session, CancellationToken ct = default)
    {
        var snapshot = session.CurrentSnapshot();
        lock (_initialAuthorityGate)
        {
            if (_initialAuthorityTask is not null
                && ReferenceEquals(_initialAuthoritySession, session)
                && Equals(_initialAuthoritySnapshot, snapshot))
            {
                return _initialAuthorityTask;
            }

            var completion = new TaskCompletionSource<DashboardAuthorityResponse?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _initialAuthoritySession = session;
            _initialAuthoritySnapshot = snapshot;
            _initialAuthorityTask = completion.Task;
            _ = CompleteInitialAuthorityAsync(session, snapshot, ct, completion);
            return completion.Task;
        }
    }

    private async Task CompleteInitialAuthorityAsync(DashboardSessionAccessor session,
        DashboardSessionSnapshot snapshot, CancellationToken ct,
        TaskCompletionSource<DashboardAuthorityResponse?> completion)
    {
        try { completion.TrySetResult(await RevalidateAuthorityAsync(session, ct).ConfigureAwait(false)); }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally
        {
            lock (_initialAuthorityGate)
            {
                if (ReferenceEquals(_initialAuthoritySession, session)
                    && Equals(_initialAuthoritySnapshot, snapshot)
                    && ReferenceEquals(_initialAuthorityTask, completion.Task))
                {
                    _initialAuthorityTask = null;
                    _initialAuthoritySession = null;
                    _initialAuthoritySnapshot = null;
                }
            }
        }
    }

    private static async Task<bool> IsSignInAgainHereAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("reason", out var reason)
                && reason.GetString() == ClientIngressValues.SignInAgainHere;
        }
        catch (JsonException) { return false; } // A plain 401 has no JSON body: the session really is invalid.
        catch (NotSupportedException) { return false; } // Same: non-JSON content type.
    }

    private async Task<(SessionValidationResponse? Response, bool Invalid, bool Unusable, bool WrongPlace)> ValidateDetailedAsync(
        string sessionToken,
        string currentIngress,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/validate");
            request.Headers.TryAddWithoutValidation(DashboardEngineAuthenticationHandler.SessionHeader, sessionToken);
            request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, currentIngress);
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A home session used from outside is refused for this request only. It is not "invalid",
                // so the sign-in cookie is kept for when the person is back at home.
                var wrongPlace = await IsSignInAgainHereAsync(response, ct).ConfigureAwait(false);
                return (null, !wrongPlace, false, wrongPlace);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, false, false, false);
            }

            var validation = await response.Content
                .ReadFromJsonAsync<SessionValidationResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (validation is null)
            {
                logger?.LogWarning("Dashboard authority validation returned an empty success response.");
                return (null, false, true, false);
            }
            return (validation, false, false, false);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(exception,
                "Dashboard authority validation returned a malformed success response.");
            return (null, false, true, false);
        }
        catch (NotSupportedException exception)
        {
            logger?.LogWarning(exception,
                "Dashboard authority validation returned an unsupported success response.");
            return (null, false, true, false);
        }
        catch (HttpRequestException) { return (null, false, false, false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, false, false, false); }
    }

    public async Task<IReadOnlyList<string>?> RecoverAsync(RecoverPasswordRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/password/recover", request, ct).ConfigureAwait(false);
        var result = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<RecoveryCodesResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
        return result?.RecoveryCodes;
    }

    public async Task<string?> BeginPasswordResetAsync(BeginPasswordResetRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/password/reset/begin", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<BeginPasswordResetResponse>(cancellationToken: ct).ConfigureAwait(false))?.Token : null;
    }

    public async Task<bool> CompletePasswordResetAsync(ResetPasswordTokenRequest request, CancellationToken ct = default) =>
        (await Client.PostAsJsonAsync("/auth/password/reset/complete", request, ct).ConfigureAwait(false)).IsSuccessStatusCode;

    public async Task<AccountSelfServiceResponse?> GetAccountAsync(CancellationToken ct = default) =>
        await GetAsync<AccountSelfServiceResponse>(SelfServicePath(), ct).ConfigureAwait(false);

    public Task<AccountSelfServiceResponse?> GetSelfServiceAsync(CancellationToken ct = default) =>
        GetAsync<AccountSelfServiceResponse>(SelfServicePath(), ct);

    private string SelfServicePath()
    {
        var original = GetOriginalClientContext();
        return $"/access/self-service?originalClientIngress={Uri.EscapeDataString(original.Ingress)}&originalClientIsHttps={original.IsHttps.ToString().ToLowerInvariant()}";
    }

    public async Task<(SessionValidationResponse? Response, bool Invalid)> ValidateCookieAsync(string token, string currentIngress, CancellationToken ct = default)
    {
        var result = await ValidateDetailedAsync(token, currentIngress, ct).ConfigureAwait(false);
        return (result.Response, result.Invalid);
    }

    public Task<List<AccountAccessResponse>> GetManagedAccountsAsync(CancellationToken ct = default) =>
        GetAsync<List<AccountAccessResponse>>("/access/accounts", ct).ContinueWith(task => task.Result ?? [], ct);

    public Task<AccountAccessResponse?> GetManagedAccountAsync(Guid accountId, CancellationToken ct = default) =>
        GetAsync<AccountAccessResponse>($"/access/accounts/{accountId:D}", ct);

    /// <summary>Paired phones and TVs: every device for an administrator, only your own otherwise.</summary>
    public async Task<List<ManagedClientDeviceDto>> GetManagedDevicesAsync(CancellationToken ct = default) =>
        await GetAsync<List<ManagedClientDeviceDto>>("/access/devices", ct).ConfigureAwait(false) ?? [];

    public Task<DashboardAccessMutationResult> RevokeManagedDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/devices/{deviceId:D}", ct);

    /// <summary>Chooses (or clears, with null) whose photos a phone backs up.</summary>
    public Task<DashboardAccessMutationResult> SetManagedDeviceBackupProfileAsync(Guid deviceId, Guid? profileId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, $"/access/devices/{deviceId:D}/backup-profile",
            new SetManagedDeviceBackupProfileRequest { ProfileId = profileId }, ct);

    public async Task<List<ManagedProfileResponse>> GetManagedProfilesAsync(CancellationToken ct = default) =>
        await GetAsync<List<ManagedProfileResponse>>("/access/profiles", ct).ConfigureAwait(false) ?? [];

    public async Task<List<AccessLibraryOptionDto>> GetAccessLibrariesAsync(CancellationToken ct = default) =>
        await GetAsync<List<AccessLibraryOptionDto>>("/access/libraries", ct).ConfigureAwait(false) ?? [];

    public Task<DashboardAccessMutationResult<ManagedProfileResponse>> CreateManagedProfileResultAsync(CreateManagedProfileRequest request, CancellationToken ct = default) =>
        SendMutationAsync<CreateManagedProfileRequest, ManagedProfileResponse>(HttpMethod.Post, "/access/profiles", request, ct);

    public Task<DashboardAccessMutationResult<ManagedProfileResponse>> UpdateManagedProfileResultAsync(Guid profileId, UpdateManagedProfileRequest request, CancellationToken ct = default) =>
        SendMutationAsync<UpdateManagedProfileRequest, ManagedProfileResponse>(HttpMethod.Put, $"/access/profiles/{profileId:D}", request, ct);

    /// <summary>Removes a person. Their habits always go; <paramref name="keepPhotos"/> moves their personal photos to the Shared Library (true) or deletes them (false).</summary>
    public async Task<DashboardAccessMutationResult> DeleteManagedProfileResultAsync(Guid profileId, bool keepPhotos = true, CancellationToken ct = default)
    {
        var result = await SendMutationAsync(HttpMethod.Delete, $"/access/profiles/{profileId:D}?photos={(keepPhotos ? "move" : "delete")}", ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.ProfileId == profileId);
        }

        return result;
    }

    /// <summary>Sets a person's profile photo (JPEG, PNG or WebP up to 5 MB). Allowed for anyone who may manage that person.</summary>
    public async Task<DashboardAccessMutationResult> UploadManagedProfilePhotoResultAsync(Guid profileId, byte[] photo, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(photo);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                Path.GetExtension(fileName).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" });
            content.Add(file, "file", Path.GetFileName(fileName));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/access/profiles/{profileId:D}/avatar") { Content = content };
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? DashboardAccessMutationResult.Success()
                : await ReadMutationFailureAsync(response, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    /// <summary>Removes a person's profile photo; their colour or icon shows again.</summary>
    public Task<DashboardAccessMutationResult> RemoveManagedProfilePhotoResultAsync(Guid profileId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/profiles/{profileId:D}/avatar", ct);

    public Task<DashboardAccessMutationResult<ManagedProfileResponse>> AddHouseholdPersonResultAsync(Guid householdId, AddHouseholdPersonRequest request, CancellationToken ct = default) =>
        SendMutationAsync<AddHouseholdPersonRequest, ManagedProfileResponse>(HttpMethod.Post, $"/access/households/{householdId:D}/people", request, ct);

    public Task<DashboardAccessMutationResult<GiveOwnSignInResponse>> GiveOwnSignInResultAsync(Guid profileId, GiveOwnSignInRequest request, CancellationToken ct = default) =>
        SendMutationAsync<GiveOwnSignInRequest, GiveOwnSignInResponse>(HttpMethod.Post, $"/access/profiles/{profileId:D}/own-sign-in", request, ct);

    /// <summary>Removes a person's own sign-in and sends any screen open on it back to sign-in; the person stays.</summary>
    public async Task<DashboardAccessMutationResult> RemoveOwnSignInResultAsync(Guid accountId, CancellationToken ct = default)
    {
        var result = await SendMutationAsync(HttpMethod.Delete, $"/access/accounts/{accountId:D}/own-sign-in", ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.AccountId == accountId);
        }

        return result;
    }

    /// <summary>Sets a person's profile PIN. A server administrator can set anyone's; a household administrator only their own household's.</summary>
    public Task<DashboardAccessMutationResult> SetProfilePinResultAsync(Guid profileId, string pin, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, $"/auth/profiles/{profileId:D}/pin", new SetProfilePinRequest { Pin = pin }, ct);

    public Task<DashboardAccessMutationResult<AccountInvitationResponse>> CreateInvitationResultAsync(CreateAccountInvitationRequest request, CancellationToken ct = default) =>
        SendMutationAsync<CreateAccountInvitationRequest, AccountInvitationResponse>(HttpMethod.Post, "/access/invitations", request, ct);

    public async Task<AccountAccessResponse?> CreateManagedAccountAsync(CreateManagedAccountRequest request, CancellationToken ct = default) =>
        (await CreateManagedAccountResultAsync(request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<AccountAccessResponse>> CreateManagedAccountResultAsync(CreateManagedAccountRequest request, CancellationToken ct = default) =>
        SendMutationAsync<CreateManagedAccountRequest, AccountAccessResponse>(HttpMethod.Post, "/access/accounts", request, ct);

    public async Task<AccountAccessResponse?> UpdateManagedAccountAsync(Guid accountId, UpdateManagedAccountRequest request, CancellationToken ct = default) =>
        (await UpdateManagedAccountResultAsync(accountId, request, ct).ConfigureAwait(false)).Value;

    public async Task<DashboardAccessMutationResult<AccountAccessResponse>> UpdateManagedAccountResultAsync(Guid accountId, UpdateManagedAccountRequest request, CancellationToken ct = default)
    {
        var result = await SendMutationAsync<UpdateManagedAccountRequest, AccountAccessResponse>(HttpMethod.Put, $"/access/accounts/{accountId:D}", request, ct).ConfigureAwait(false);
        if (result.Succeeded && !request.IsEnabled)
        {
            // A disabled account's open screens go to sign-in at once instead of at the next check.
            openScreens?.CloseWhere(screen => screen.AccountId == accountId);
        }

        return result;
    }

    public async Task<bool> DeleteManagedAccountAsync(Guid accountId, CancellationToken ct = default) =>
        (await DeleteManagedAccountResultAsync(accountId, ct).ConfigureAwait(false)).Succeeded;

    public async Task<DashboardAccessMutationResult> DeleteManagedAccountResultAsync(Guid accountId, CancellationToken ct = default)
    {
        var result = await SendMutationAsync(HttpMethod.Delete, $"/access/accounts/{accountId:D}", ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.AccountId == accountId);
        }

        return result;
    }

    public Task<DashboardAccessMutationResult> ReplaceManagedAccountAccessResultAsync(Guid accountId, ReplaceAccountAccessRequest request, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, $"/access/accounts/{accountId:D}/access", request, ct);

    public Task<DashboardAccessMutationResult> SetManagedProfileGrantResultAsync(Guid accountId, Guid profileId, SetAccountProfileGrantAccessRequest request, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, $"/access/accounts/{accountId:D}/grants/{profileId:D}", request, ct);

    public Task<DashboardAccessMutationResult> SetGrantProtectionResultAsync(Guid accountId, Guid profileId, SetGrantAdminProtectionRequest request, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, $"/access/accounts/{accountId:D}/grants/{profileId:D}/admin-protection", request, ct);

    public async Task<bool> RevokeManagedProfileGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default) =>
        (await RevokeManagedProfileGrantResultAsync(accountId, profileId, ct).ConfigureAwait(false)).Succeeded;

    public async Task<DashboardAccessMutationResult> RevokeManagedProfileGrantResultAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        var result = await SendMutationAsync(HttpMethod.Delete, $"/access/accounts/{accountId:D}/grants/{profileId:D}", ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.AccountId == accountId && screen.ProfileId == profileId);
        }

        return result;
    }

    public Task<List<ApplicationPermissionDefinitionDto>> GetApplicationPermissionsAsync(CancellationToken ct = default) =>
        GetAsync<List<ApplicationPermissionDefinitionDto>>("/access/applications/permissions", ct).ContinueWith(task => task.Result ?? [], ct);

    public Task<List<ApplicationPermissionPresetDto>> GetApplicationPresetsAsync(CancellationToken ct = default) =>
        GetAsync<List<ApplicationPermissionPresetDto>>("/access/applications/presets", ct).ContinueWith(task => task.Result ?? [], ct);

    public Task<List<ApplicationResponse>> GetApplicationsAsync(CancellationToken ct = default) =>
        GetAsync<List<ApplicationResponse>>("/access/applications", ct).ContinueWith(task => task.Result ?? [], ct);

    public async Task<ApplicationResponse?> CreateApplicationAsync(CreateApplicationRequest request, CancellationToken ct = default) =>
        (await CreateApplicationResultAsync(request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationResponse>> CreateApplicationResultAsync(CreateApplicationRequest request, CancellationToken ct = default) =>
        SendMutationAsync<CreateApplicationRequest, ApplicationResponse>(HttpMethod.Post, "/access/applications", request, ct);

    public async Task<ApplicationResponse?> UpdateApplicationAsync(Guid applicationId, UpdateApplicationRequest request, CancellationToken ct = default) =>
        (await UpdateApplicationResultAsync(applicationId, request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationResponse>> UpdateApplicationResultAsync(Guid applicationId, UpdateApplicationRequest request, CancellationToken ct = default) =>
        SendMutationAsync<UpdateApplicationRequest, ApplicationResponse>(HttpMethod.Put, $"/access/applications/{applicationId:D}", request, ct);

    public async Task<bool> DeleteApplicationAsync(Guid applicationId, CancellationToken ct = default) =>
        (await DeleteApplicationResultAsync(applicationId, ct).ConfigureAwait(false)).Succeeded;

    public Task<DashboardAccessMutationResult> DeleteApplicationResultAsync(Guid applicationId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/applications/{applicationId:D}", ct);

    public async Task<ApplicationResponse?> ReplaceApplicationPermissionsAsync(Guid applicationId, SetApplicationPermissionsRequest request, CancellationToken ct = default) =>
        (await ReplaceApplicationPermissionsResultAsync(applicationId, request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationResponse>> ReplaceApplicationPermissionsResultAsync(Guid applicationId, SetApplicationPermissionsRequest request, CancellationToken ct = default) =>
        SendMutationAsync<SetApplicationPermissionsRequest, ApplicationResponse>(HttpMethod.Put, $"/access/applications/{applicationId:D}/permissions", request, ct);

    public async Task<ApplicationResponse?> SetApplicationClientBindingsAsync(Guid applicationId, SetApplicationClientBindingsRequest request, CancellationToken ct = default) =>
        (await SetApplicationClientBindingsResultAsync(applicationId, request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationResponse>> SetApplicationClientBindingsResultAsync(Guid applicationId, SetApplicationClientBindingsRequest request, CancellationToken ct = default) =>
        SendMutationAsync<SetApplicationClientBindingsRequest, ApplicationResponse>(HttpMethod.Put, $"/access/applications/{applicationId:D}/client-bindings", request, ct);

    public async Task<ApplicationCredentialIssuedResponse?> IssueApplicationCredentialAsync(Guid applicationId, CreateApplicationCredentialRequest request, CancellationToken ct = default) =>
        (await IssueApplicationCredentialResultAsync(applicationId, request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationCredentialIssuedResponse>> IssueApplicationCredentialResultAsync(Guid applicationId, CreateApplicationCredentialRequest request, CancellationToken ct = default) =>
        SendMutationAsync<CreateApplicationCredentialRequest, ApplicationCredentialIssuedResponse>(HttpMethod.Post, $"/access/applications/{applicationId:D}/credentials", request, ct);

    public async Task<ApplicationCredentialIssuedResponse?> RotateApplicationCredentialAsync(Guid applicationId, Guid credentialId, RotateApplicationCredentialRequest request, CancellationToken ct = default) =>
        (await RotateApplicationCredentialResultAsync(applicationId, credentialId, request, ct).ConfigureAwait(false)).Value;

    public Task<DashboardAccessMutationResult<ApplicationCredentialIssuedResponse>> RotateApplicationCredentialResultAsync(Guid applicationId, Guid credentialId, RotateApplicationCredentialRequest request, CancellationToken ct = default) =>
        SendMutationAsync<RotateApplicationCredentialRequest, ApplicationCredentialIssuedResponse>(HttpMethod.Post, $"/access/applications/{applicationId:D}/credentials/{credentialId:D}/rotate", request, ct);

    public async Task<bool> RevokeApplicationCredentialAsync(Guid applicationId, Guid credentialId, CancellationToken ct = default) =>
        (await RevokeApplicationCredentialResultAsync(applicationId, credentialId, ct).ConfigureAwait(false)).Succeeded;

    public Task<DashboardAccessMutationResult> RevokeApplicationCredentialResultAsync(Guid applicationId, Guid credentialId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/applications/{applicationId:D}/credentials/{credentialId:D}", ct);

    public Task<List<string>> GetApplicationWebhookEventTypesAsync(Guid applicationId, CancellationToken ct = default) =>
        GetAsync<List<string>>($"/access/applications/{applicationId:D}/webhooks/event-types", ct)
            .ContinueWith(task => task.Result ?? [], ct);

    public Task<List<ApplicationWebhookResponse>> GetApplicationWebhooksAsync(Guid applicationId, CancellationToken ct = default) =>
        GetAsync<List<ApplicationWebhookResponse>>($"/access/applications/{applicationId:D}/webhooks", ct)
            .ContinueWith(task => task.Result ?? [], ct);

    public Task<DashboardAccessMutationResult<ApplicationWebhookSecretResponse>> CreateApplicationWebhookResultAsync(
        Guid applicationId,
        SaveApplicationWebhookRequest request,
        CancellationToken ct = default) =>
        SendMutationAsync<SaveApplicationWebhookRequest, ApplicationWebhookSecretResponse>(
            HttpMethod.Post,
            $"/access/applications/{applicationId:D}/webhooks",
            request,
            ct);

    public Task<DashboardAccessMutationResult<ApplicationWebhookSecretResponse>> UpdateApplicationWebhookResultAsync(
        Guid applicationId,
        Guid webhookId,
        SaveApplicationWebhookRequest request,
        CancellationToken ct = default) =>
        SendMutationAsync<SaveApplicationWebhookRequest, ApplicationWebhookSecretResponse>(
            HttpMethod.Put,
            $"/access/applications/{applicationId:D}/webhooks/{webhookId:D}",
            request,
            ct);

    public Task<DashboardAccessMutationResult<ApplicationWebhookSecretResponse>> RotateApplicationWebhookSecretResultAsync(
        Guid applicationId,
        Guid webhookId,
        CancellationToken ct = default) =>
        SendMutationAsync<object, ApplicationWebhookSecretResponse>(
            HttpMethod.Post,
            $"/access/applications/{applicationId:D}/webhooks/{webhookId:D}/rotate",
            new { },
            ct);

    public Task<DashboardAccessMutationResult> DeleteApplicationWebhookResultAsync(
        Guid applicationId,
        Guid webhookId,
        CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/applications/{applicationId:D}/webhooks/{webhookId:D}", ct);

    public Task<GrantAdminUnlockResponse?> GetAdministratorUnlockAsync(CancellationToken ct = default) =>
        GetAsync<GrantAdminUnlockResponse>("/access/admin-unlock", ct);

    public Task<GrantAdminUnlockResponse?> UnlockAdministratorAsync(GrantAdminUnlockRequest request, CancellationToken ct = default) =>
        SendAsync<GrantAdminUnlockRequest, GrantAdminUnlockResponse>(HttpMethod.Post, "/access/admin-unlock", request, ct);

    public async Task<bool> ExitAdministratorAsync(CancellationToken ct = default)
    {
        using var response = await Client.DeleteAsync("/access/admin-unlock", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
    public async Task<AuthSessionResponse?> AcceptInvitationAsync(AcceptAccountInvitationRequest request, CancellationToken ct = default) =>
        (await AcceptInvitationDetailedAsync(request, ct).ConfigureAwait(false)).Session;

    /// <summary>Uses an invitation code to set a first password. A refused password comes back with the Engine's reason.</summary>
    public async Task<DashboardSessionAttempt> AcceptInvitationDetailedAsync(AcceptAccountInvitationRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/invitations/accept", request, ct).ConfigureAwait(false);
        return await ReadSessionAttemptAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Sign in with email and password; keeps the Engine's reason so an expired temporary password can be explained.</summary>
    public async Task<DashboardSessionAttempt> LoginDetailedAsync(LocalLoginRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/login", request, ct).ConfigureAwait(false);
        return await ReadSessionAttemptAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Looks at an invitation code without using it; <c>null</c> when it is wrong, used, expired or not allowed from here.</summary>
    public async Task<AccountInvitationPreviewResponse?> PreviewInvitationAsync(PreviewAccountInvitationRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/invitations/preview", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AccountInvitationPreviewResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Replaces a temporary password with the person's own; the Engine answers with a fresh session.</summary>
    public async Task<DashboardSessionAttempt> ChangeTemporaryPasswordAsync(ChangeTemporaryPasswordRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/password/change-temporary", request, ct).ConfigureAwait(false);
        return await ReadSessionAttemptAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>An administrator gives an existing person a new temporary password.</summary>
    public Task<DashboardAccessMutationResult<AccountAccessResponse>> SetTemporaryPasswordResultAsync(Guid accountId, SetTemporaryPasswordRequest request, CancellationToken ct = default) =>
        SendMutationAsync<SetTemporaryPasswordRequest, AccountAccessResponse>(HttpMethod.Post, $"/access/accounts/{accountId:D}/temporary-password", request, ct);

    /// <summary>Finishes a password sign-in that asked for a code from the authenticator app (or a recovery code).</summary>
    public async Task<DashboardSessionAttempt> CompleteTwoStepSignInAsync(CompleteTwoStepSignInRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/two-step/verify", request, ct).ConfigureAwait(false);
        return await ReadSessionAttemptAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Starts setting up two-step codes: the key to show as a QR code and as text.</summary>
    public Task<DashboardAccessMutationResult<TwoStepSetupResponse>> BeginTwoStepSetupResultAsync(CancellationToken ct = default) =>
        SendMutationAsync<object, TwoStepSetupResponse>(HttpMethod.Post, "/auth/two-step/setup", new { }, ct);

    /// <summary>Turns two-step codes on once the first code from the app matches; the Engine answers with new recovery codes.</summary>
    public Task<DashboardAccessMutationResult<RecoveryCodesResponse>> EnableTwoStepResultAsync(string code, CancellationToken ct = default) =>
        SendMutationAsync<EnableTwoStepRequest, RecoveryCodesResponse>(HttpMethod.Post, "/auth/two-step/enable", new EnableTwoStepRequest { Code = code }, ct);

    /// <summary>Turns two-step codes off with a current code or a recovery code.</summary>
    public Task<DashboardAccessMutationResult> DisableTwoStepResultAsync(string code, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Post, "/auth/two-step/disable", new DisableTwoStepRequest { Code = code }, ct);

    /// <summary>An administrator turns two-step codes off for someone who lost their phone and recovery codes.</summary>
    public Task<DashboardAccessMutationResult<AccountAccessResponse>> ResetAccountTwoStepResultAsync(Guid accountId, CancellationToken ct = default) =>
        SendMutationAsync<object, AccountAccessResponse>(HttpMethod.Post, $"/access/accounts/{accountId:D}/two-step/reset", new { }, ct);

    private static async Task<DashboardSessionAttempt> ReadSessionAttemptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var pending = await response.Content.ReadFromJsonAsync<TwoStepRequiredResponse>(cancellationToken: ct).ConfigureAwait(false);
            return new DashboardSessionAttempt(null, response.StatusCode, null, pending?.PendingToken);
        }

        if (response.IsSuccessStatusCode)
        {
            return new DashboardSessionAttempt(
                await response.Content.ReadFromJsonAsync<AuthSessionResponse>(cancellationToken: ct).ConfigureAwait(false),
                response.StatusCode,
                null);
        }

        string? detail = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                detail = value.GetString();
            }
        }
        catch (JsonException)
        {
            // The answer was not a problem document; the caller shows its own generic message instead.
        }

        return new DashboardSessionAttempt(null, response.StatusCode, detail);
    }

    public async Task<ExternalIdentityTransactionResponse?> BeginExternalIdentityTransactionAsync(
        BeginExternalIdentityTransactionRequest request,
        CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/external-transactions", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ExternalIdentityTransactionResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    public async Task<AccountExternalLoginDto?> LinkExternalLoginAsync(LinkAccountExternalLoginRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/external-link", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AccountExternalLoginDto>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    public async Task<List<AccountExternalLoginDto>> GetExternalLoginsAsync(CancellationToken ct = default) =>
        await GetAsync<List<AccountExternalLoginDto>>("/access/self-service/external-logins", ct).ConfigureAwait(false) ?? [];

    public async Task<bool> UnlinkExternalLoginAsync(Guid id, CancellationToken ct = default) =>
        (await UnlinkExternalLoginResultAsync(id, ct).ConfigureAwait(false)).Succeeded;

    public Task<DashboardAccessMutationResult> UnlinkExternalLoginResultAsync(Guid id, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/access/self-service/external-logins/{id:D}", ct);

    public Task<PasskeyOptionsResponse?> GetPasskeyLoginOptionsAsync(string? email, string clientIngress, bool isHttps, CancellationToken ct = default) =>
        SendPasskeyAsync<BeginPasskeyLoginRequest, PasskeyOptionsResponse>("/auth/passkeys/login/options", new(email, clientIngress, isHttps), ct);
    public Task<AuthSessionResponse?> CompletePasskeyLoginAsync(CompletePasskeyLoginRequest body, CancellationToken ct = default) =>
        SendPasskeyAsync<CompletePasskeyLoginRequest, AuthSessionResponse>("/auth/passkeys/login/complete", body, ct);
    public async Task<PasskeyOptionsResponse?> GetPasskeyRegistrationOptionsAsync(CancellationToken ct = default) =>
        (await GetPasskeyRegistrationOptionsResultAsync(ct).ConfigureAwait(false)).Value;
    public Task<DashboardAccessMutationResult<PasskeyOptionsResponse>> GetPasskeyRegistrationOptionsResultAsync(CancellationToken ct = default)
    {
        var original = GetOriginalClientContext();
        return SendMutationAsync<BeginPasskeyRegistrationRequest, PasskeyOptionsResponse>(
            HttpMethod.Post, "/auth/passkeys/registration/options", new(original.Ingress, original.IsHttps), ct);
    }
    public async Task<bool> CompletePasskeyRegistrationAsync(CompletePasskeyRegistrationRequest body, CancellationToken ct = default) =>
        (await CompletePasskeyRegistrationResultAsync(body, ct).ConfigureAwait(false)).Succeeded;
    public Task<DashboardAccessMutationResult> CompletePasskeyRegistrationResultAsync(CompletePasskeyRegistrationRequest body, CancellationToken ct = default)
    {
        var original = GetOriginalClientContext();
        body = body with { OriginalClientIngress = original.Ingress, OriginalClientIsHttps = original.IsHttps };
        return SendMutationAsync(HttpMethod.Post, "/auth/passkeys/registration/complete", body, ct);
    }
    public async Task<List<PasskeyCredentialResponse>> GetPasskeysAsync(CancellationToken ct = default) => await GetAsync<List<PasskeyCredentialResponse>>("/auth/passkeys", ct).ConfigureAwait(false) ?? [];
    public async Task<bool> RemovePasskeyAsync(string id, CancellationToken ct = default) =>
        (await RemovePasskeyResultAsync(id, ct).ConfigureAwait(false)).Succeeded;
    public Task<DashboardAccessMutationResult> RemovePasskeyResultAsync(string id, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, $"/auth/passkeys/{Uri.EscapeDataString(id)}", ct);

    private async Task<TResponse?> SendPasskeyAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    { using var request = PasskeyRequest(HttpMethod.Post, path, body); using var response = await Client.SendAsync(request, ct).ConfigureAwait(false); return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false) : default; }
    // The Engine answers only to its own loopback name and takes the passkey domain and origin from the
    // public address, so the visitor's Host and Origin are deliberately not forwarded.
    internal static HttpRequestMessage PasskeyRequest<T>(HttpMethod method, string path, T body) =>
        new(method, path) { Content = JsonContent.Create(body) };

    // Prefer the place the session was last seen; with neither that nor a request context the caller is treated as
    // remote, the stricter side.
    private bool IsRemoteCaller(string? knownIngress) =>
        IngressClassifierExtensions.FromWireValue(
            string.IsNullOrEmpty(knownIngress) ? CurrentIngress(contextAccessor?.HttpContext) : knownIngress) == IngressKind.Remote;

    // No classifier or no request means the caller cannot be placed, so it is treated as remote (fail closed).
    private string CurrentIngress(HttpContext? context) =>
        context is not null && ingress is not null
            ? ingress.Classify(context).ToWireValue()
            : ClientIngressValues.Remote;

    private (string Ingress, bool IsHttps) GetOriginalClientContext()
    {
        var context = contextAccessor?.HttpContext;
        if (context is null)
        {
            return (ClientIngressValues.Remote, false);
        }

        return (CurrentIngress(context), context.Request.IsHttps);
    }

    private async Task<DashboardAccessMutationResult<TResponse>> SendMutationAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadMutationFailureAsync<TResponse>(response, ct).ConfigureAwait(false);
            }

            try
            {
                var value = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false);
                return value is null
                    ? DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse, response.StatusCode)
                    : DashboardAccessMutationResult<TResponse>.Success(value);
            }
            catch (JsonException)
            {
                return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse, response.StatusCode);
            }
            catch (NotSupportedException)
            {
                return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse, response.StatusCode);
            }
            catch (IOException)
            {
                return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient, response.StatusCode);
            }
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    private async Task<DashboardAccessMutationResult<TResponse>> SendDeleteResponseAsync<TResponse>(
        string path,
        CancellationToken ct)
    {
        try
        {
            using var response = await Client.DeleteAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadMutationFailureAsync<TResponse>(response, ct).ConfigureAwait(false);
            }

            var value = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false);
            return value is null
                ? DashboardAccessMutationResult<TResponse>.FailureResult(
                    DashboardAccessMutationFailure.InvalidResponse, response.StatusCode)
                : DashboardAccessMutationResult<TResponse>.Success(value);
        }
        catch (JsonException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse);
        }
        catch (NotSupportedException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    private async Task<DashboardAccessMutationResult> SendMutationAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? DashboardAccessMutationResult.Success()
                : await ReadMutationFailureAsync(response, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    private async Task<DashboardAccessMutationResult> SendMutationAsync(
        HttpMethod method,
        string path,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? DashboardAccessMutationResult.Success()
                : await ReadMutationFailureAsync(response, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    private static async Task<DashboardAccessMutationResult<T>> ReadMutationFailureAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var failure = await ClassifyFailureAsync(response, ct).ConfigureAwait(false);
        var fields = failure == DashboardAccessMutationFailure.Validation
            ? await ReadValidationFieldNamesAsync(response, ct).ConfigureAwait(false)
            : [];
        return DashboardAccessMutationResult<T>.FailureResult(failure, response.StatusCode, fields);
    }

    private static async Task<DashboardAccessMutationResult> ReadMutationFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var failure = await ClassifyFailureAsync(response, ct).ConfigureAwait(false);
        var fields = failure == DashboardAccessMutationFailure.Validation
            ? await ReadValidationFieldNamesAsync(response, ct).ConfigureAwait(false)
            : [];
        return DashboardAccessMutationResult.FailureResult(failure, response.StatusCode, fields);
    }

    // A 403 can mean "not allowed" or "confirm it's you first"; only the second one has a way forward in the UI.
    private static async Task<DashboardAccessMutationFailure> ClassifyFailureAsync(HttpResponseMessage response, CancellationToken ct) =>
        await ConfirmItsYouRequests.IsConfirmItsYouRefusalAsync(response, ct).ConfigureAwait(false)
            ? DashboardAccessMutationFailure.ConfirmItsYou
            : MutationFailure(response.StatusCode);

    private static DashboardAccessMutationFailure MutationFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => DashboardAccessMutationFailure.Validation,
        HttpStatusCode.Unauthorized => DashboardAccessMutationFailure.Unauthorized,
        HttpStatusCode.Forbidden => DashboardAccessMutationFailure.Forbidden,
        HttpStatusCode.Conflict => DashboardAccessMutationFailure.Conflict,
        HttpStatusCode.NotFound => DashboardAccessMutationFailure.NotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => DashboardAccessMutationFailure.Transient,
        _ => DashboardAccessMutationFailure.Failed,
    };

    private static async Task<IReadOnlyList<string>> ReadValidationFieldNamesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return errors.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false)
            : default;
    }

    public async Task<List<DeviceSessionResponse>> GetSessionsAsync(CancellationToken ct = default) =>
        await GetAsync<List<DeviceSessionResponse>>("/auth/sessions", ct).ConfigureAwait(false) ?? [];

    public async Task<bool> RevokeSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        (await RevokeSessionResultAsync(sessionId, ct).ConfigureAwait(false)).Succeeded;

    public async Task<DashboardAccessMutationResult> RevokeSessionResultAsync(Guid sessionId, CancellationToken ct = default)
    {
        var result = await SendMutationAsync(HttpMethod.Delete, $"/auth/sessions/{sessionId:D}", ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.SessionId == sessionId);
        }

        return result;
    }

    /// <summary>Signs out every other session of the account; screens open on the kept session stay open.</summary>
    public async Task<DashboardAccessMutationResult<RevokeOtherSessionsResponse>> RevokeOtherSessionsAsync(Guid? accountId, Guid? keepSessionId, CancellationToken ct = default)
    {
        var result = await SendDeleteResponseAsync<RevokeOtherSessionsResponse>("/auth/sessions/others", ct).ConfigureAwait(false);
        // Without both ids the filter would be wider than "my other sessions", so close nothing; the minute check covers it.
        if (result.Succeeded && accountId is { } account && keepSessionId is { } keep)
        {
            openScreens?.CloseWhere(screen => screen.AccountId == account && screen.SessionId != keep);
        }

        return result;
    }

    public async Task<bool> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        (await ChangePasswordResultAsync(request, ct).ConfigureAwait(false)).Succeeded;

    public Task<DashboardAccessMutationResult> ChangePasswordResultAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Post, "/auth/password/change", request, ct);

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(CancellationToken ct = default) =>
        (await RegenerateRecoveryCodesResultAsync(ct).ConfigureAwait(false)).Value?.RecoveryCodes;

    public Task<DashboardAccessMutationResult<RecoveryCodesResponse>> RegenerateRecoveryCodesResultAsync(CancellationToken ct = default) =>
        SendMutationAsync<object, RecoveryCodesResponse>(HttpMethod.Post, "/auth/password/recovery-codes", new { }, ct);

    /// <summary>Asks the Engine whether the person signed in or confirmed recently; <c>ConfirmItsYou</c> when they did not.</summary>
    public Task<DashboardAccessMutationResult> CheckRecentSignInAsync(CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Get, "/auth/confirm/recent", ct);

    /// <summary>"Confirm it's you" with the password.</summary>
    public Task<DashboardAccessMutationResult> ConfirmItsYouAsync(ConfirmItsYouRequest request, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Post, "/auth/confirm", request, ct);

    /// <summary>Starts "Confirm it's you" with a passkey: the options the browser needs to ask for it.</summary>
    public Task<DashboardAccessMutationResult<PasskeyOptionsResponse>> GetConfirmPasskeyOptionsAsync(CancellationToken ct = default) =>
        SendMutationAsync<object, PasskeyOptionsResponse>(HttpMethod.Post, "/auth/confirm/passkey-options", new { }, ct);

    /// <summary>Turns the this-computer-only account into a normal one. The Engine answers with the new session.</summary>
    public Task<DashboardAccessMutationResult<AuthSessionResponse>> SecureAccountAsync(SecureAccountRequest request, CancellationToken ct = default) =>
        SendMutationAsync<SecureAccountRequest, AuthSessionResponse>(HttpMethod.Post, "/auth/account/secure", request, ct);

    /// <summary>The profile this browser or device always opens as for the signed-in account, or <c>null</c> when none is set.</summary>
    public async Task<Guid?> GetDeviceProfilePreferenceAsync(CancellationToken ct = default) =>
        (await GetAsync<DeviceProfilePreferenceResponse>("/auth/device-profile", ct).ConfigureAwait(false))?.ProfileId;

    public Task<DashboardAccessMutationResult> SetDeviceProfilePreferenceAsync(Guid profileId, CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Put, "/auth/device-profile", new SetDeviceProfilePreferenceRequest(profileId), ct);

    public Task<DashboardAccessMutationResult> ClearDeviceProfilePreferenceAsync(CancellationToken ct = default) =>
        SendMutationAsync(HttpMethod.Delete, "/auth/device-profile", ct);

    /// <summary>Wrong-PIN guesses allowed per minute for one target profile, from one kind of place.</summary>
    public const int ProfilePinAttemptsPerMinute = 10;

    /// <param name="request">The profile to switch to and, when it has one, its PIN.</param>
    /// <param name="knownIngress">
    /// Where the signed-in session was last seen (<see cref="DashboardSessionAccessor.LastIngress"/>). Interactive
    /// circuits often have no HttpContext, and without this the caller would be counted as remote.
    /// </param>
    public async Task<DashboardProfileSwitchResult> SwitchProfileAsync(SwitchProfileRequest request, CancellationToken ct = default, string? knownIngress = null)
    {
        // A PIN attempt (the request carries a secret) counts against the target profile, separately for remote and
        // home callers: the Engine locks a profile for remote guessers, and that lock (or a remote guess run) must
        // never use up the allowance home users need to switch into the same profile. Home sessions are not locked
        // out by the Engine, so this is what stops a quick guess run from a signed-in home device.
        if (!string.IsNullOrEmpty(request.Secret)
            && signInLimiter is not null
            && !signInLimiter.TryAcquireKey(
                $"switch-pin:{(IsRemoteCaller(knownIngress) ? "remote" : "home")}:{request.ProfileId:N}",
                ProfilePinAttemptsPerMinute,
                out _))
        {
            return new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.TooManyAttempts);
        }

        using var response = await Client.PostAsJsonAsync("/auth/session/switch-profile", request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var session = await response.Content.ReadFromJsonAsync<SessionValidationResponse>(cancellationToken: ct).ConfigureAwait(false);
            return session is null
                ? new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.Failed)
                : new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.Succeeded, session);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.PreconditionRequired => new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.PinRequired),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.Forbidden),
            HttpStatusCode.NotFound => new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.NotFound),
            HttpStatusCode.TooManyRequests => new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.TooManyAttempts),
            _ => new DashboardProfileSwitchResult(DashboardProfileSwitchStatus.Failed),
        };
    }

    public async Task<AuthSessionResponse?> CreateExternalSessionAsync(ExternalSessionRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/auth/external-session", request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AuthSessionResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    public async Task<IntercomTokenResponse?> GetIntercomTokenAsync(CancellationToken ct = default)
    {
        using var response = await Client.PostAsync("/auth/intercom-token", null, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<IntercomTokenResponse>(cancellationToken: ct).ConfigureAwait(false)
            : null;
    }

    private async Task<TResponse?> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(path, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false)
                : default;
        }
        catch (HttpRequestException exception)
        {
            logger?.LogWarning(exception, "Dashboard identity request {Path} could not reach the Engine", path);
            return default;
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Dashboard identity request {Path} timed out", path);
            return default;
        }
    }
}

public enum DashboardProfileSwitchStatus
{
    Succeeded,
    PinRequired,
    Forbidden,
    NotFound,
    TooManyAttempts,
    Failed,
}

public sealed record DashboardProfileSwitchResult(
    DashboardProfileSwitchStatus Status,
    SessionValidationResponse? Session = null);

/// <summary>How an open screen's sign-in stands after an Engine check.</summary>
public enum SessionCheckStatus
{
    /// <summary>The Engine confirmed the sign-in.</summary>
    Valid,

    /// <summary>The sign-in was revoked, expired or its account was removed or disabled.</summary>
    Revoked,

    /// <summary>A home sign-in seen from outside the home: kept, but unusable from here.</summary>
    WrongPlace,

    /// <summary>The Engine could not say (restart, throttling); nothing is concluded.</summary>
    Unknown,

    /// <summary>The screen has no sign-in to check.</summary>
    NoSession,
}

public sealed record AuthorityRevalidation(DashboardAuthorityResponse? Authority, SessionCheckStatus Status);
