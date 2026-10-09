using System.Net.Mail;
using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Authentication");

        group.MapGet("/bootstrap/status", async (IFirstPartyIdentityService identity, CancellationToken ct) =>
            Results.Ok(new AuthBootstrapStatusResponse(await identity.IsAdministratorConfiguredAsync(ct).ConfigureAwait(false))))
            .Produces<AuthBootstrapStatusResponse>()
            .RequireAuthorization(AuthPolicies.DashboardService);

        // One call per sign-in page load; nothing is cached. The Dashboard says where the visitor is (header) and
        // whether they came over HTTPS (query); every answer uses the same rules the sign-in actions enforce.
        group.MapGet("/sign-in-methods", (HttpRequest request, bool? https, IConfigurationLoader configuration,
            AuthenticationProviderConfigurationService providerConfiguration) =>
            Results.Ok(BuildSignInMethods(
                providerConfiguration.LoadWithSecrets(),
                configuration.LoadNetwork(),
                request.Headers[ClientIngressValues.ValidateHeader].ToString(),
                https == true)))
            .WithName("GetSignInMethods")
            .Produces<SignInMethodsResponse>()
            .RequireAuthorization(AuthPolicies.DashboardService);

        // Sign in again on the computer that runs Tuvima, without a password, for an account that was started with
        // just a name and email. Only a visitor on this computer (the Dashboard says so in the ingress header) is
        // ever told the account exists; everyone else gets the same plain 404 as when there is no such account.
        group.MapGet("/this-computer", async (HttpRequest request, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            var name = ComesFromThisComputer(request)
                ? await identity.GetThisComputerAccountNameAsync(ct).ConfigureAwait(false)
                : null;
            return name is null
                ? ApiErrors.NotFound("Not found.")
                : Results.Ok(new ThisComputerAccountResponse(name));
        })
        .WithName("GetThisComputerAccount")
        .Produces<ThisComputerAccountResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/this-computer", async (ThisComputerSignInRequest body, HttpRequest request,
            IFirstPartyIdentityService identity, DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            var issued = ComesFromThisComputer(request)
                ? await identity.SignInThisComputerAccountAsync(body.DeviceId, body.DeviceName, body.Client, ct).ConfigureAwait(false)
                : null;
            return issued is null
                ? ApiErrors.NotFound("Not found.")
                : Results.Ok(await ToResponseAsync(issued, projector, ct));
        })
        .WithName("SignInThisComputer")
        .Produces<AuthSessionResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/login", async (LocalLoginRequest request, IFirstPartyIdentityService identity,
            DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            var result = await identity.AuthenticatePasswordAsync(request.Email ?? string.Empty, request.Password ?? string.Empty,
                request.DeviceId, request.DeviceName, request.Client, ct, ClientIngress.Parse(request.OriginalClientIngress)).ConfigureAwait(false);

            return result.Succeeded && result.IssuedSession is not null
                ? Results.Ok(await ToResponseAsync(result.IssuedSession, projector, ct))
                : ApiErrors.Problem(
                    StatusCodes.Status401Unauthorized,
                    "Authentication failed.",
                    result.LockedOut ? "The credential is temporarily locked." : result.Error ?? "Invalid credentials.");
        })
        .Produces<AuthSessionResponse>()
        .AdmitClient<LocalLoginRequest>(
            (services, request) => IsLoginPermittedFor(services.GetRequiredService<IConfigurationLoader>(), request),
            () => ApiErrors.Problem(StatusCodes.Status401Unauthorized,
                "Authentication failed.", "This sign-in method is unavailable for this connection."))
        .RequireRateLimiting("authentication")
        .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/external-transactions", async (
            BeginExternalIdentityTransactionRequest request,
            HttpContext http,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            IRequestAuthorityResolver authorities,
            ExternalIdentityTransactionService transactions,
            CancellationToken ct) =>
        {
            var policy = providerConfiguration.LoadWithSecrets();
            if (!IsExternalSignInEnabled(policy) ||
                !IsConfiguredProvider(policy, configuration.LoadNetwork(), request.Provider, request.Issuer))
            {
                return Results.Unauthorized();
            }

            var authority = await authorities.ResolveAsync(http, ct).ConfigureAwait(false);
            if (request.Purpose == ExternalIdentityTransactionPurposes.Link && !authority.HasHumanContext)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(transactions.Begin(
                    request,
                    authority.AccountId,
                    authority.SessionId));
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).WithName("BeginExternalIdentityTransaction")
          .Produces<ExternalIdentityTransactionResponse>()
          .RequireRateLimiting("authentication")
          .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/external-session", async (
            ExternalSessionRequest request,
            IFirstPartyIdentityService identity,
            IAccountExternalLoginService externalLogins,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            ExternalIdentityTransactionService transactions,
            DashboardAuthorityProjector projector,
            CancellationToken ct) =>
        {
            var policy = providerConfiguration.LoadWithSecrets();
            var verified = transactions.Consume(
                request.TransactionTicket,
                ExternalIdentityTransactionPurposes.SignIn);
            if (verified is null || !IsConfiguredProvider(policy, configuration.LoadNetwork(), verified.Provider, verified.Issuer))
            {
                return Results.Unauthorized();
            }

            try
            {
                var linked = await externalLogins.ResolveAsync(
                    verified.Provider, verified.Issuer, verified.Subject, ct).ConfigureAwait(false);
                if (linked is null)
                {
                    return Results.Unauthorized();
                }

                await externalLogins.RecordLoginAsync(linked.Id, ct).ConfigureAwait(false);
                return Results.Ok(await ToResponseAsync(await identity.CreateExternalSessionAsync(
                    linked.AccountId, verified.Provider, request.DeviceId, request.DeviceName, request.Client, ct, ClientIngress.Parse(request.OriginalClientIngress)).ConfigureAwait(false), projector, ct));
            }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
        }).WithName("CreateExternalSession")
          .Produces<AuthSessionResponse>()
          .AdmitClient<ExternalSessionRequest>(
              (services, request) => AllowsClient(
                  services.GetRequiredService<IConfigurationLoader>().LoadNetwork(),
                  request.OriginalClientIngress, request.OriginalClientIsHttps,
                  IsExternalSignInEnabled(services.GetRequiredService<AuthenticationProviderConfigurationService>().LoadWithSecrets())),
              () => Results.Unauthorized())
          .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/external-link", async (
            LinkAccountExternalLoginRequest request,
            HttpContext http,
            IRequestAuthorityResolver authorities,
            ISelfServiceAuthorizationService decisions,
            IAccountExternalLoginService externalLogins,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            ExternalIdentityTransactionService transactions,
            CancellationToken ct) =>
        {
            var authority = await authorities.ResolveAsync(http, ct).ConfigureAwait(false);
            var decision = await decisions.EvaluateAccountAsync(
                authority, authority.AccountId.GetValueOrDefault(), ct).ConfigureAwait(false);
            if (!decision.IsAllowed || authority.AccountId is not { } accountId || authority.SessionId is not { } sessionId)
            {
                return Results.Forbid();
            }

            var policy = providerConfiguration.LoadWithSecrets();
            if (!IsExternalSignInEnabled(policy))
            {
                return Results.Forbid();
            }

            var verified = transactions.Consume(
                request.TransactionTicket,
                ExternalIdentityTransactionPurposes.Link,
                accountId,
                sessionId);
            if (verified is null || !IsConfiguredProvider(policy, configuration.LoadNetwork(), verified.Provider, verified.Issuer))
            {
                return Results.Unauthorized();
            }

            try
            {
                var linked = await externalLogins.LinkAsync(
                    accountId,
                    verified.Provider,
                    verified.Issuer,
                    verified.Subject,
                    verified.Email,
                    verified.DisplayName,
                    ct).ConfigureAwait(false);
                return Results.Ok(ProfileContractMapper.ToResponse(linked));
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.Conflict(ex.Message); }
        }).WithName("LinkExternalIdentity")
          .Produces<AccountExternalLoginDto>()
          .RequireRateLimiting("authentication")
          .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/invitations/accept", async (AcceptAccountInvitationRequest request,
            IConfigurationLoader configuration, IFirstPartyIdentityService identity, DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!AllowsClient(configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps,
                policy.PasswordSignInEnabled))
            {
                return Results.Unauthorized();
            }

            try { return Results.Ok(await ToResponseAsync(await identity.AcceptInvitationAsync(request.Token, request.Password, request.DeviceId, request.DeviceName, "Tuvima Dashboard", ct, ClientIngress.Parse(request.OriginalClientIngress)).ConfigureAwait(false), projector, ct)); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).WithName("AcceptAccountInvitation").Produces<AuthSessionResponse>().RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/session/validate", async (HttpRequest request, IFirstPartyIdentityService identity, DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            // Only the Dashboard (the service credential) may say where the browser is. Any other caller, or a
            // missing header, is treated as outside the home, so the rule fails closed.
            var isDashboard = request.HttpContext.User.HasClaim(TuvimaClaimTypes.DashboardService, "true");
            var currentIngress = isDashboard
                ? ClientIngress.Parse(request.Headers[ClientIngressValues.ValidateHeader].ToString())
                : ClientIngress.Remote;
            var session = await identity.ValidateSessionAsync(request.Headers[TuvimaAuthDefaults.SessionHeader].ToString(), true, ct, currentIngress).ConfigureAwait(false);
            if (session is not null)
            {
                return Results.Ok(await ToValidationResponseAsync(session, projector, ct));
            }

            // A home session used from outside is not revoked; it is only unusable here. Tell the Dashboard
            // apart from a dead session so the sign-in cookie is kept for when the person is home again.
            var wrongPlace = await identity.ValidateSessionAsync(request.Headers[TuvimaAuthDefaults.SessionHeader].ToString(), false, ct).ConfigureAwait(false) is not null;
            return wrongPlace
                ? Results.Json(new { reason = ClientIngressValues.SignInAgainHere }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Unauthorized();
        }).Produces<SessionValidationResponse>().RequireAuthorization(AuthPolicies.DashboardService);

        group.MapGet("/sessions", async (ClaimsPrincipal user, IFirstPartyIdentityService identity,
            TimeProvider clock, CancellationToken ct) =>
        {
            var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
            var sessions = await identity.GetSessionsAsync(accountId, ct).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            return Results.Ok(ToActiveSessionResponses(sessions, now));
        }).Produces<IReadOnlyList<DeviceSessionResponse>>().RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapDelete("/sessions/others", async (ClaimsPrincipal user, IFirstPartyIdentityService identity,
            CancellationToken ct) =>
        {
            var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
            var currentSessionId = RequiredGuidClaim(user, TuvimaClaimTypes.SessionId);
            var revoked = await identity.RevokeOtherSessionsAsync(
                accountId, currentSessionId, "user_revoked_other_sessions", ct).ConfigureAwait(false);
            return Results.Ok(new RevokeOtherSessionsResponse(revoked));
        }).WithName("RevokeOtherAuthSessions").Produces<RevokeOtherSessionsResponse>()
          .RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal user, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
            var owned = (await identity.GetSessionsAsync(accountId, ct).ConfigureAwait(false)).Any(session => session.Id == sessionId);
            if (!owned)
            {
                return Results.Forbid();
            }

            return await identity.RevokeSessionAsync(sessionId, "user_revoked", ct).ConfigureAwait(false) ? Results.NoContent() : ApiErrors.NotFound("Session not found.");
        }).WithName("RevokeAuthSession").Produces(StatusCodes.Status204NoContent).RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPost("/password/change", async (ChangePasswordRequest request, ClaimsPrincipal user, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            try
            {
                await identity.ChangePasswordAsync(RequiredGuidClaim(user, TuvimaClaimTypes.AccountId), request.CurrentPassword,
                    request.NewPassword, RequiredGuidClaim(user, TuvimaClaimTypes.SessionId), ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).WithName("ChangePassword").Produces(StatusCodes.Status204NoContent).RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPost("/password/recovery-codes", async (RegenerateRecoveryCodesRequest request, ClaimsPrincipal user, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            try
            {
                var codes = await identity.RegenerateRecoveryCodesAsync(RequiredGuidClaim(user, TuvimaClaimTypes.AccountId), request.CurrentPassword, ct).ConfigureAwait(false);
                return Results.Ok(new RecoveryCodesResponse(codes));
            }
            catch (InvalidOperationException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).Produces<RecoveryCodesResponse>().RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPost("/password/recover", async (RecoverPasswordRequest request,
            IConfigurationLoader configuration, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!AllowsClient(configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps,
                policy.PasswordSignInEnabled))
            {
                return Results.Unauthorized();
            }

            try
            {
                var codes = await identity.ResetPasswordWithRecoveryCodeAsync(request.Email, request.RecoveryCode, request.NewPassword, ct).ConfigureAwait(false);
                return Results.Ok(new RecoveryCodesResponse(codes));
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).WithName("RecoverPassword").Produces<RecoveryCodesResponse>().RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/password/reset/begin", async (BeginPasswordResetRequest request,
            IConfigurationLoader configuration, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!AllowsClient(configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps,
                policy.PasswordSignInEnabled))
            {
                return Results.Accepted(value: new BeginPasswordResetResponse(null));
            }

            var token = await identity.BeginPasswordResetAsync(request.Email, ct).ConfigureAwait(false);
            return Results.Accepted(value: new BeginPasswordResetResponse(token));
        }).WithName("BeginPasswordReset").Produces<BeginPasswordResetResponse>(StatusCodes.Status202Accepted).RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/password/reset/complete", async (ResetPasswordTokenRequest request,
            IConfigurationLoader configuration, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!AllowsClient(configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps,
                policy.PasswordSignInEnabled))
            {
                return Results.Unauthorized();
            }

            try { await identity.ResetPasswordWithTokenAsync(request.Token, request.NewPassword, ct).ConfigureAwait(false); return Results.NoContent(); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).WithName("CompletePasswordReset").Produces(StatusCodes.Status204NoContent).RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/passkeys/login/options", async (BeginPasskeyLoginRequest request, HttpContext context,
            IAccountRepository accounts, IPasskeyHandler<Account> passkeys, CancellationToken ct) =>
        {
            Account? account = null;
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                try { account = await accounts.GetByNormalizedEmailAsync(new MailAddress(request.Email.Trim()).Address.ToUpperInvariant(), ct).ConfigureAwait(false); } catch (FormatException) { }
            }
            var result = await passkeys.MakeRequestOptionsAsync(account!, context).ConfigureAwait(false);
            return Results.Ok(new PasskeyOptionsResponse(result.RequestOptionsJson, result.AssertionState ?? string.Empty));
        }).Produces<PasskeyOptionsResponse>()
          .AdmitClient<BeginPasskeyLoginRequest>(
              (services, request) => IsPasskeyPermittedFor(
                  services.GetRequiredService<IConfigurationLoader>(), request.OriginalClientIngress, request.OriginalClientIsHttps),
              () => Results.Unauthorized())
          .RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/passkeys/login/complete", async (CompletePasskeyLoginRequest request, HttpContext context,
            IConfigurationLoader configuration, IPasskeyHandler<Account> passkeys, UserManager<Account> users, IFirstPartyIdentityService identity, DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!IsPasskeyAvailable(policy, configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps))
            {
                return Results.Unauthorized();
            }

            var result = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext { HttpContext = context, CredentialJson = request.CredentialJson, AssertionState = request.State }).ConfigureAwait(false);
            if (!result.Succeeded || result.User is null || result.Passkey is null)
            {
                return Results.Unauthorized();
            }

            await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey).ConfigureAwait(false);
            return Results.Ok(await ToResponseAsync(await identity.CreatePasskeySessionAsync(result.User.Id, request.DeviceId, request.DeviceName, "Tuvima Dashboard", ct, ClientIngress.Parse(request.OriginalClientIngress)).ConfigureAwait(false), projector, ct));
        }).Produces<AuthSessionResponse>().RequireRateLimiting("authentication").RequireAuthorization(AuthPolicies.DashboardService);

        group.MapPost("/passkeys/registration/options", async (BeginPasskeyRegistrationRequest request,
            ClaimsPrincipal user, HttpContext context, IConfigurationLoader configuration,
            IAccountRepository accounts, IPasskeyHandler<Account> passkeys, CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!IsPasskeyAvailable(policy, configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps))
            {
                return Results.Unauthorized();
            }

            var account = await accounts.GetByIdAsync(RequiredGuidClaim(user, TuvimaClaimTypes.AccountId), ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException();
            var entity = new PasskeyUserEntity { Id = account.Id.ToString("D"), Name = account.Email ?? account.Id.ToString("D"), DisplayName = account.Email ?? "Tuvima account" };
            var result = await passkeys.MakeCreationOptionsAsync(entity, context).ConfigureAwait(false);
            return Results.Ok(new PasskeyOptionsResponse(result.CreationOptionsJson, result.AttestationState ?? string.Empty));
        }).Produces<PasskeyOptionsResponse>().RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPost("/passkeys/registration/complete", async (CompletePasskeyRegistrationRequest request,
            ClaimsPrincipal user, HttpContext context, IConfigurationLoader configuration,
            IAccountRepository accounts, IPasskeyHandler<Account> passkeys, UserManager<Account> users,
            CancellationToken ct) =>
        {
            var policy = configuration.LoadCore().Auth;
            if (!IsPasskeyAvailable(policy, configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps))
            {
                return Results.Unauthorized();
            }

            var account = await accounts.GetByIdAsync(RequiredGuidClaim(user, TuvimaClaimTypes.AccountId), ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException();
            var result = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext { HttpContext = context, CredentialJson = request.CredentialJson, AttestationState = request.State }).ConfigureAwait(false);
            if (!result.Succeeded || result.Passkey is null || result.UserEntity?.Id != account.Id.ToString("D"))
            {
                return ApiErrors.BadRequest("Passkey registration could not be verified.");
            }

            result.Passkey.Name = string.IsNullOrWhiteSpace(request.Name) ? "Passkey" : request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 100)];
            var saved = await users.AddOrUpdatePasskeyAsync(account, result.Passkey).ConfigureAwait(false);
            return saved.Succeeded ? Results.NoContent() : ApiErrors.BadRequest("Passkey registration could not be saved.");
        }).WithName("RegisterPasskey").Produces(StatusCodes.Status204NoContent).RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapGet("/passkeys", async (ClaimsPrincipal user, IAccountRepository accounts, UserManager<Account> users, CancellationToken ct) =>
        {
            var account = await accounts.GetByIdAsync(RequiredGuidClaim(user, TuvimaClaimTypes.AccountId), ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException();
            return Results.Ok((await users.GetPasskeysAsync(account).ConfigureAwait(false)).Select(p => new PasskeyCredentialResponse(Convert.ToBase64String(p.CredentialId), p.Name ?? "Passkey", p.CreatedAt, p.IsBackedUp)).ToList());
        }).Produces<List<PasskeyCredentialResponse>>().RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapDelete("/passkeys/{credentialId}", async (string credentialId, ClaimsPrincipal user,
            IAccountSignInMethodRepository signInMethods, AuthenticationPolicyMutationGate mutationGate,
            AuthenticationProviderConfigurationService providerConfiguration, IConfigurationLoader configuration,
            IAccountRepository accounts, IIdentityRepository identities, IAccountExternalLoginService externalLogins,
            UserManager<Account> users, CancellationToken ct) =>
        {
            byte[] id; try { id = Convert.FromBase64String(credentialId); } catch (FormatException) { return ApiErrors.BadRequest("Credential id is invalid."); }
            var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
            if (account is null)
            {
                return ApiErrors.NotFound("Account not found.");
            }

            if (!(await users.GetPasskeysAsync(account).ConfigureAwait(false)).Any(passkey => passkey.CredentialId.AsSpan().SequenceEqual(id)))
            {
                return ApiErrors.NotFound("Passkey not found.");
            }

            if (!await HasUsableAccountSignInAsync(providerConfiguration.LoadWithSecrets(), configuration.LoadNetwork(), accountId, accounts,
                   identities, externalLogins, users, excludedPasskeyCredentialId: id, ct: ct).ConfigureAwait(false))
            {
                return ApiErrors.Conflict("Add another enabled sign-in method before removing this passkey.");
            }

            var result = await signInMethods.RemovePasskeyAsync(accountId, id, ct).ConfigureAwait(false);
            return result switch
            {
                SignInMethodRemovalResult.Removed => Results.NoContent(),
                SignInMethodRemovalResult.NotFound => ApiErrors.NotFound("Passkey not found."),
                SignInMethodRemovalResult.LastSignInMethod => ApiErrors.Conflict("Add another sign-in method before removing this passkey."),
                _ => throw new ArgumentOutOfRangeException()
            };
        }).WithName("DeletePasskey").Produces(StatusCodes.Status204NoContent).RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPut("/profiles/{profileId:guid}/pin", async (Guid profileId, SetProfilePinRequest request, IFirstPartyIdentityService identity, CancellationToken ct) =>
        {
            try { await identity.SetProfilePinAsync(profileId, request.Pin, ct).ConfigureAwait(false); return Results.NoContent(); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
        }).WithName("SetProfilePin").Produces(StatusCodes.Status204NoContent).RequireAuthorization(AuthPolicies.Administrator);

        group.MapPost("/session/switch-profile", async (SwitchProfileRequest request, HttpRequest httpRequest, IFirstPartyIdentityService identity, DashboardAuthorityProjector projector, CancellationToken ct) =>
        {
            try
            {
                var result = await identity.SwitchActiveProfileAsync(
                    httpRequest.Headers[TuvimaAuthDefaults.SessionHeader].ToString(), request.ProfileId, request.Secret, ct).ConfigureAwait(false);
                return Results.Ok(await ToValidationResponseAsync(result, projector, ct));
            }
            catch (Exception ex) when (ex is ProfilePinLockedException or ProfilePinRequiredException or KeyNotFoundException or UnauthorizedAccessException)
            {
                return SwitchProfileFailure(ex);
            }
        }).Produces<SessionValidationResponse>()
          .ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .RequireRateLimiting("authentication-session").RequireAuthorization(AuthPolicies.HumanSelfService);

        group.MapPost("/intercom-token", (ClaimsPrincipal user, IntercomTokenService tokens) =>
        {
            var sessionId = RequiredGuidClaim(user, TuvimaClaimTypes.SessionId);
            var accountId = RequiredGuidClaim(user, TuvimaClaimTypes.AccountId);
            var created = tokens.Create(sessionId, accountId);
            return Results.Ok(new IntercomTokenResponse(created.Token, created.ExpiresAt));
        }).Produces<IntercomTokenResponse>().RequireRateLimiting("intercom").RequireAuthorization(AuthPolicies.HumanSelfService);

        return app;
    }

    /// <summary>How a refused profile switch is answered. A locked profile is a 429 so the Dashboard can tell the person to wait.</summary>
    internal static IResult SwitchProfileFailure(Exception exception) => exception switch
    {
        ProfilePinLockedException => Results.Problem(
            title: "Too many attempts",
            detail: "Too many attempts. Try again later.",
            statusCode: StatusCodes.Status429TooManyRequests),
        ProfilePinRequiredException => Results.Problem(
            title: "Profile PIN required",
            detail: "Enter the target profile's PIN to continue.",
            statusCode: StatusCodes.Status428PreconditionRequired),
        KeyNotFoundException notFound => ApiErrors.NotFound(notFound.Message),
        _ => Results.Unauthorized(),
    };

    private static async Task<AuthSessionResponse> ToResponseAsync(SessionIssueResult issued, DashboardAuthorityProjector projector, CancellationToken ct) => new()
    {
        SessionId = issued.Session.Id,
        SessionToken = issued.PlaintextToken,
        AccountId = issued.Account.Id,
        ActiveProfileId = issued.ActiveProfile.Id,
        DisplayName = issued.ActiveProfile.DisplayName,
        Authority = await projector.ProjectAsync(issued.Account.Id, issued.ActiveProfile.Id, issued.Session.Id, ct),
        AuthenticationMethod = issued.Session.AuthenticationMethod,
        ExpiresAt = issued.Session.ExpiresAt,
        RecoveryCodes = issued.RecoveryCodes,
    };

    private static async Task<SessionValidationResponse> ToValidationResponseAsync(SessionValidationResult result, DashboardAuthorityProjector projector, CancellationToken ct) => new()
    {
        SessionId = result.Session.Id,
        AccountId = result.Account.Id,
        ActiveProfileId = result.ActiveProfile.Id,
        DisplayName = result.ActiveProfile.DisplayName,
        Authority = await projector.ProjectAsync(result.Account.Id, result.ActiveProfile.Id, result.Session.Id, ct),
        AuthenticationMethod = result.Session.AuthenticationMethod,
        ExpiresAt = result.Session.ExpiresAt,
    };

    private static Guid RequiredGuidClaim(ClaimsPrincipal user, string type) =>
        Guid.TryParse(user.FindFirstValue(type), out var value)
            ? value
            : throw new UnauthorizedAccessException($"Required claim '{type}' is missing.");

    internal static IReadOnlyList<DeviceSessionResponse> ToActiveSessionResponses(
        IEnumerable<AuthSession> sessions,
        DateTimeOffset now) => sessions
        .Where(session => session.IsActive(now))
        .Select(session => new DeviceSessionResponse
        {
            Id = session.Id,
            AccountId = session.AccountId,
            ActiveProfileId = session.ActiveProfileId,
            DeviceId = session.DeviceId,
            DeviceName = session.DeviceName,
            Client = session.Client,
            AuthenticationMethod = session.AuthenticationMethod,
            CreatedAt = session.CreatedAt,
            LastSeenAt = session.LastSeenAt,
            ExpiresAt = session.ExpiresAt,
            RevokedAt = session.RevokedAt,
        })
        .ToList();

    /// <summary>
    /// Runs the "is this connection allowed to try this sign-in method" decision as an endpoint filter, before the
    /// handler, so the handler itself has no branch on the Dashboard-supplied client flags.
    /// </summary>
    private static RouteHandlerBuilder AdmitClient<TRequest>(
        this RouteHandlerBuilder builder,
        Func<IServiceProvider, TRequest, bool> admits,
        Func<IResult> refusal) =>
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var request = invocation.GetArgument<TRequest>(0);
            return admits(invocation.HttpContext.RequestServices, request)
                ? await next(invocation).ConfigureAwait(false)
                : refusal();
        });

    /// <summary>Only the Dashboard (service credential) may say where the browser is; a missing header counts as remote.</summary>
    private static bool ComesFromThisComputer(HttpRequest request) =>
        request.HttpContext.User.HasClaim(TuvimaClaimTypes.DashboardService, "true")
        && ClientIngress.Parse(request.Headers[ClientIngressValues.ValidateHeader].ToString()) == ClientIngress.ThisComputer
        && !WasForwarded(request);

    /// <summary>
    /// A tunnel such as cloudflared or ngrok on this computer looks like loopback, so a visitor whose request carried
    /// forwarding headers (seen by the Dashboard, which says so in a header, or by the Engine itself) is not trusted.
    /// </summary>
    private static bool WasForwarded(HttpRequest request) =>
        string.Equals(request.Headers[ClientIngressValues.ForwardedHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase)
        || request.Headers.ContainsKey("X-Forwarded-For")
        || request.Headers.ContainsKey("Forwarded");

    private static bool IsLoginPermittedFor(IConfigurationLoader configuration, LocalLoginRequest request)
    {
        var policy = configuration.LoadCore().Auth;
        return AllowsClient(configuration.LoadNetwork(), request.OriginalClientIngress, request.OriginalClientIsHttps, policy.PasswordSignInEnabled);
    }

    private static bool IsPasskeyPermittedFor(IConfigurationLoader configuration, string? originalClientIngress, bool originalClientIsHttps) =>
        IsPasskeyAvailable(configuration.LoadCore().Auth, configuration.LoadNetwork(), originalClientIngress, originalClientIsHttps);

    /// <summary>
    /// The one door rule for sign-in. A client on this computer or the home network is allowed whenever the method is
    /// enabled; a client from outside is allowed only when "who can connect" is Anywhere, and only over HTTPS.
    /// </summary>
    internal static bool AllowsClient(
        NetworkSettings network,
        string? originalClientIngress,
        bool originalClientIsHttps,
        bool methodEnabled)
    {
        var ingress = ClientIngress.Parse(originalClientIngress);
        return methodEnabled
            && ClientIngress.Rank(ingress) <= WhoCanConnectRank(network.WhoCanConnect)
            && (ingress != ClientIngress.Remote || originalClientIsHttps);
    }

    // An unknown setting is the most restrictive (fail closed), matching the Dashboard's ExposurePolicy.
    private static int WhoCanConnectRank(string? whoCanConnect) => whoCanConnect?.Trim().ToLowerInvariant() switch
    {
        WhoCanConnectModes.Anywhere => 2,
        WhoCanConnectModes.HomeNetwork => 1,
        _ => 0,
    };

    internal static bool IsPasskeyAvailable(
        AuthSettings policy,
        NetworkSettings network,
        string? originalClientIngress,
        bool originalClientIsHttps) =>
        AllowsClient(network, originalClientIngress, originalClientIsHttps,
            policy.PasskeySignInEnabled && IsCanonicalOriginReady(network));

    internal static bool IsConfiguredProvider(AuthSettings policy, NetworkSettings network, string providerId, string issuer)
    {
        var provider = policy.ExternalProviders.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.Id.Equals(providerId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null || string.IsNullOrWhiteSpace(issuer))
        {
            return false;
        }

        var configured = !string.IsNullOrWhiteSpace(provider.ClientId)
            && (provider.Kind.Equals(ExternalAuthProviderKinds.OpenIdConnect, StringComparison.OrdinalIgnoreCase)
                ? IsHttps(provider.Authority) && provider.Scopes.Contains("openid", StringComparer.Ordinal)
                : !string.IsNullOrWhiteSpace(provider.ClientSecret)
                  && IsHttps(provider.Issuer)
                  && IsHttps(provider.AuthorizationEndpoint)
                  && IsHttps(provider.TokenEndpoint)
                  && IsHttps(provider.UserInformationEndpoint));
        if (!configured || !IsCanonicalOriginReady(network))
        {
            return false;
        }

        return ExternalIssuerMatcher.Matches(provider.Issuer, provider.Authority, issuer);
    }

    internal static SignInMethodsResponse BuildSignInMethods(
        AuthSettings policy,
        NetworkSettings network,
        string? originalClientIngress,
        bool originalClientIsHttps) =>
        new(
            AllowsClient(network, originalClientIngress, originalClientIsHttps, policy.PasswordSignInEnabled),
            IsPasskeyAvailable(policy, network, originalClientIngress, originalClientIsHttps),
            AvailableExternalProviders(policy, network, originalClientIngress, originalClientIsHttps),
            AllowsClient(network, originalClientIngress, originalClientIsHttps, true));

    /// <summary>The configured sign-in providers a visitor may use from where they are.</summary>
    internal static List<AccountExternalProviderResponse> AvailableExternalProviders(
        AuthSettings policy,
        NetworkSettings network,
        string? originalClientIngress,
        bool originalClientIsHttps) =>
        AllowsClient(network, originalClientIngress, originalClientIsHttps, IsExternalSignInEnabled(policy))
            ? policy.ExternalProviders
                .Where(provider => provider.Enabled && IsConfiguredProvider(
                    policy,
                    network,
                    provider.Id,
                    provider.Kind.Equals(ExternalAuthProviderKinds.OpenIdConnect, StringComparison.OrdinalIgnoreCase)
                        ? string.IsNullOrWhiteSpace(provider.Issuer) ? provider.Authority : provider.Issuer
                        : provider.Issuer))
                .Select(provider => new AccountExternalProviderResponse(provider.Id, provider.DisplayName))
                .ToList()
            : [];

    internal static bool IsExternalSignInEnabled(AuthSettings policy) =>
        policy.ExternalSignInEnabled && policy.Mode is "Optional" or "Required";

    /// <summary>The one public address (<c>network.remote.public_hostname</c>) is set and valid.</summary>
    internal static bool IsCanonicalOriginReady(NetworkSettings network) => network.HasValidPublicAddress();

    internal static async Task<bool> HasUsableAccountSignInAsync(
        AuthSettings policy,
        NetworkSettings network,
        Guid accountId,
        IAccountRepository accounts,
        IIdentityRepository identities,
        IAccountExternalLoginService externalLogins,
        UserManager<Account> users,
        Guid? excludedExternalLoginId = null,
        byte[]? excludedPasskeyCredentialId = null,
        CancellationToken ct = default)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null || !account.IsEnabled)
        {
            return false;
        }

        var grants = (await accounts.GetGrantsAsync(accountId, ct).ConfigureAwait(false))
            .Where(grant => grant.IsEnabled)
            .ToArray();
        if (grants.Length == 0)
        {
            return false;
        }

        if (policy.PasswordSignInEnabled &&
            await identities.GetAccountCredentialAsync(accountId, AccountCredentialKind.Password, ct)
                .ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (policy.PasskeySignInEnabled && IsCanonicalOriginReady(network))
        {
            var passkeys = await users.GetPasskeysAsync(account).ConfigureAwait(false);
            if (passkeys.Any(passkey => excludedPasskeyCredentialId is null ||
                    !passkey.CredentialId.AsSpan().SequenceEqual(excludedPasskeyCredentialId)))
            {
                return true;
            }
        }

        if (IsExternalSignInEnabled(policy))
        {
            var linked = await externalLogins.GetByAccountAsync(accountId, ct).ConfigureAwait(false);
            if (linked.Any(login => login.Id != excludedExternalLoginId &&
                    IsConfiguredProvider(policy, network, login.Provider, login.Issuer)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
