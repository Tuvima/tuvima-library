using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MediaEngine.Web.Services.Integration;

public static class DashboardAuthenticationEndpoints
{
    public static WebApplication MapDashboardAuthenticationEndpoints(
        this WebApplication app,
        IReadOnlyList<RegisteredExternalAuthProvider> externalProviders)
    {
        app.MapGet("/auth/login", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery, string? returnUrl) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return Results.Redirect(SafeReturnUrl(returnUrl));
            }

            var bootstrap = await identity.GetBootstrapStatusAsync(context.RequestAborted).ConfigureAwait(false);
            if (bootstrap is null)
            {
                return EngineUnavailableResult(SafeReturnUrl(returnUrl));
            }

            if (!bootstrap.AdministratorConfigured)
            {
                return Results.Redirect("/setup");
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            var deviceId = EnsureDeviceCookie(context);
            var methods = await identity.GetSignInMethodsAsync(context.RequestAborted).ConfigureAwait(false);
            // Only asked on this computer; anywhere else the Engine never says the account exists.
            var thisComputerName = await identity.GetThisComputerAccountNameAsync(context.RequestAborted).ConfigureAwait(false);
            return Results.Content(
                LoginPage(
                    tokens.RequestToken ?? string.Empty,
                    methods,
                    externalProviders,
                    deviceId,
                    SafeReturnUrl(returnUrl),
                    PasskeyOriginGate.IsPublicOrigin(context),
                    thisComputerName: thisComputerName),
                "text/html",
                Encoding.UTF8);
        }).AllowAnonymous();

        app.MapPost("/auth/login", async (HttpContext context, DashboardIdentityClient identity,
            DashboardConfigurationReader configuration, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            var invalidForm = await RefreshInvalidLoginFormAsync(
                context, antiforgery, externalProviders,
                () => identity.GetSignInMethodsAsync(context.RequestAborted)).ConfigureAwait(false);
            if (invalidForm is not null)
            {
                return invalidForm;
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var action = form["action"].ToString();
            var deviceId = EnsureDeviceCookie(context);
            var deviceName = SanitizeDeviceName(context.Request.Headers.UserAgent.ToString());
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

            if (action.Equals("bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect("/setup");
            }

            var continueOnThisComputer = action.Equals("this-computer", StringComparison.OrdinalIgnoreCase);
            var attempt = continueOnThisComputer
                ? new DashboardSessionAttempt(
                    await identity.SignInThisComputerAsync(new ThisComputerSignInRequest
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        Client = "Tuvima Library Dashboard",
                    }, context.RequestAborted).ConfigureAwait(false),
                    HttpStatusCode.OK,
                    null)
                : await identity.LoginDetailedAsync(new LocalLoginRequest
                {
                    Email = form["email"].ToString(),
                    Password = form["password"].ToString(),
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    Client = "Tuvima Library Dashboard",
                    OriginalClientIngress = context.ClientIngress(),
                    OriginalClientIsHttps = context.Request.IsHttps,
                }, context.RequestAborted).ConfigureAwait(false);

            // A right password for an account with two-step codes on: ask for the code from the authenticator app next.
            if (attempt.TwoStepToken is { } pendingToken)
            {
                return Results.Content(
                    TwoStepCodePage(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty, pendingToken, returnUrl, null),
                    "text/html", Encoding.UTF8);
            }

            var issued = attempt.Session;
            if (issued is null)
            {
                // Only one reason is worth saying out loud: a temporary password that has run out (said by the
                // Engine only after the password itself was right). Every other failure stays generic.
                var failure = continueOnThisComputer
                    ? "This computer can't continue without a password right now. Sign in with your email and password instead."
                    : attempt.Detail == TemporaryPasswords.ExpiredMessage
                        ? TemporaryPasswords.ExpiredMessage
                        : "Sign in failed. Check your credentials and try again.";
                return Results.Content(LoginFailurePage(failure), "text/html", Encoding.UTF8, StatusCodes.Status401Unauthorized);
            }

            return await FinishSignInAsync(context, issued, returnUrl).ConfigureAwait(false);
        }).AllowAnonymous();

        // The second step of a password sign-in: the code from the authenticator app, or a recovery code.
        app.MapPost("/auth/login/two-step", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.Content(LoginFailurePage("This sign-in form expired. Please start signing in again."), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var pendingToken = form["pendingToken"].ToString();
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());
            // Either box may be filled in; the recovery code box is only shown after "Use a recovery code instead".
            var code = form["code"].ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                code = form["recoveryCode"].ToString();
            }

            var attempt = await identity.CompleteTwoStepSignInAsync(new CompleteTwoStepSignInRequest
            {
                PendingToken = pendingToken,
                Code = code,
                OriginalClientIngress = context.ClientIngress(),
                OriginalClientIsHttps = context.Request.IsHttps,
            }, context.RequestAborted).ConfigureAwait(false);

            if (attempt.Session is not { } issued)
            {
                return Results.Content(
                    TwoStepCodePage(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty, pendingToken, returnUrl,
                        "That code didn't work. Check the code in your app and try again, or start signing in again."),
                    "text/html", Encoding.UTF8, StatusCodes.Status401Unauthorized);
            }

            return await FinishSignInAsync(context, issued, returnUrl).ConfigureAwait(false);
        }).AllowAnonymous();

        app.MapGet("/auth/recover", (HttpContext context, PasswordResetEmailSender emailSender, IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return Results.Redirect("/account/security");
            }

            var token = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
            return Results.Content(
                PasswordRecoveryPage(token, emailSender.IsConfigured),
                "text/html",
                Encoding.UTF8);
        }).AllowAnonymous();

        app.MapPost("/auth/recover", async (HttpContext context, DashboardIdentityClient identity,
            DashboardConfigurationReader configuration, PasswordResetEmailSender emailSender, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var action = form["action"].ToString();

            if (action.Equals("email-reset", StringComparison.OrdinalIgnoreCase))
            {
                var email = form["email"].ToString();
                var resetToken = await identity.BeginPasswordResetAsync(new BeginPasswordResetRequest(
                    email,
                    context.ClientIngress(),
                    context.Request.IsHttps), context.RequestAborted).ConfigureAwait(false);
                if (resetToken is not null)
                {
                    await emailSender.SendAsync(email, resetToken, context.RequestAborted).ConfigureAwait(false);
                }

                return Results.Content(Shell("<h1>Check your email</h1><p>If that address belongs to an eligible account, a password reset link has been sent. The link expires in 30 minutes.</p><p><a href=\"/auth/login\">Return to sign in</a></p>"), "text/html", Encoding.UTF8);
            }

            if (action.Equals("recover", StringComparison.OrdinalIgnoreCase))
            {
                var codes = await identity.RecoverAsync(new RecoverPasswordRequest
                {
                    Email = form["email"].ToString(),
                    RecoveryCode = form["recoveryCode"].ToString(),
                    NewPassword = form["newPassword"].ToString(),
                    OriginalClientIngress = context.ClientIngress(),
                    OriginalClientIsHttps = context.Request.IsHttps,
                }, context.RequestAborted).ConfigureAwait(false);
                if (codes is null)
                {
                    return Results.Content(
                        PasswordRecoveryFailurePage("Recovery failed. Check the email, code, and new password."),
                        "text/html",
                        Encoding.UTF8,
                        StatusCodes.Status400BadRequest);
                }

                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                return Results.Content(
                    RecoveryCodesPage(codes, "/auth/login", "Continue to sign in"),
                    "text/html",
                    Encoding.UTF8);
            }

            return Results.Redirect("/auth/recover");
        }).AllowAnonymous();

        app.MapGet("/auth/reset", (HttpContext context, IAntiforgery antiforgery, string? token) =>
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.Redirect("/auth/login");
            }

            var anti = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
            return Results.Content(Shell($"<h1>Choose a new password</h1><form method=\"post\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{H(anti)}\"><input type=\"hidden\" name=\"token\" value=\"{H(token)}\"><label>New password<input type=\"password\" name=\"newPassword\" minlength=\"12\" autocomplete=\"new-password\" required><small>Use at least 12 characters, and avoid common passwords or your email address.</small></label><button>Reset password</button></form>"), "text/html", Encoding.UTF8);
        }).AllowAnonymous();

        app.MapPost("/auth/reset", async (HttpContext context, DashboardConfigurationReader configuration,
            DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var ok = await identity.CompletePasswordResetAsync(new ResetPasswordTokenRequest(
                form["token"].ToString(), form["newPassword"].ToString(),
                context.ClientIngress(), context.Request.IsHttps), context.RequestAborted).ConfigureAwait(false);
            return Results.Content(ok ? Shell("<h1>Password changed</h1><p><a class=\"button\" href=\"/auth/login\">Sign in</a></p>") : LoginFailurePage("That reset link is invalid or expired."), "text/html", Encoding.UTF8, ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        }).AllowAnonymous();

        app.MapGet("/auth/invite", async (HttpContext context, IAntiforgery antiforgery, DashboardIdentityClient identity, string? code) =>
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                // Someone who was handed a code rather than a link: ask for it, then continue with the same page.
                return Results.Content(Shell(InviteCodeEntryBody(null)), "text/html", Encoding.UTF8);
            }

            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            var preview = await identity.PreviewInvitationAsync(
                new PreviewAccountInvitationRequest(code.Trim(), context.ClientIngress(), context.Request.IsHttps),
                context.RequestAborted).ConfigureAwait(false);
            if (preview is null)
            {
                return Results.Content(Shell(InviteCodeEntryBody(InvalidInvitationMessage)), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var anti = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
            return Results.Content(Shell(InvitePasswordBody(anti, EnsureDeviceCookie(context), code.Trim(), preview.Email, null)), "text/html", Encoding.UTF8);
        }).AllowAnonymous();
        app.MapPost("/auth/invite", async (HttpContext context, DashboardConfigurationReader configuration,
            DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var code = form["code"].ToString();
            var attempt = await identity.AcceptInvitationDetailedAsync(new AcceptAccountInvitationRequest(
                code, form["password"].ToString(), form["deviceId"].ToString(),
                SanitizeDeviceName(context.Request.Headers.UserAgent.ToString()),
                context.ClientIngress(), context.Request.IsHttps), context.RequestAborted).ConfigureAwait(false);
            if (attempt.Session is not { } issued)
            {
                if (attempt.Status == HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(attempt.Detail)
                    && await identity.PreviewInvitationAsync(
                        new PreviewAccountInvitationRequest(code.Trim(), context.ClientIngress(), context.Request.IsHttps),
                        context.RequestAborted).ConfigureAwait(false) is { } preview)
                {
                    // The code is still good; only the password was refused. Say why and let them try again.
                    var anti = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
                    return Results.Content(Shell(InvitePasswordBody(anti, EnsureDeviceCookie(context), code.Trim(), preview.Email, attempt.Detail)), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
                }

                return Results.Content(LoginFailurePage(InvalidInvitationMessage), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, DashboardPrincipalFactory.Create(issued, context.ClientIngress()), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = issued.ExpiresAt }).ConfigureAwait(false);
            return Results.Redirect("/");
        }).AllowAnonymous();

        app.MapGet("/auth/change-password", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!context.User.HasClaim(DashboardPrincipalFactory.PasswordChangeRequiredClaim, "true"))
            {
                return Results.Redirect("/");
            }

            var anti = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
            return Results.Content(Shell(ChangePasswordBody(anti, null)), "text/html", Encoding.UTF8);
        }).RequireAuthorization();

        app.MapPost("/auth/change-password", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            if (RejectIfTooManyAttempts(context) is { } limited)
            {
                return limited;
            }

            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            string AgainWith(string message) => Shell(ChangePasswordBody(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty, message));
            if (!string.Equals(form["newPassword"].ToString(), form["confirmPassword"].ToString(), StringComparison.Ordinal))
            {
                return Results.Content(AgainWith("The two new passwords do not match."), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var attempt = await identity.ChangeTemporaryPasswordAsync(new ChangeTemporaryPasswordRequest
            {
                CurrentPassword = form["currentPassword"].ToString(),
                NewPassword = form["newPassword"].ToString(),
                DeviceId = EnsureDeviceCookie(context),
                DeviceName = SanitizeDeviceName(context.Request.Headers.UserAgent.ToString()),
                OriginalClientIngress = context.ClientIngress(),
                OriginalClientIsHttps = context.Request.IsHttps,
            }, context.RequestAborted).ConfigureAwait(false);
            if (attempt.Session is not { } issued)
            {
                var message = !string.IsNullOrWhiteSpace(attempt.Detail) && attempt.Status != HttpStatusCode.Forbidden
                    ? attempt.Detail
                    : "Your password could not be changed. Try again, or ask your administrator for a new temporary password.";
                return Results.Content(AgainWith(message), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            // The Engine ended every old session and issued a new ordinary one; this browser takes it over.
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                DashboardPrincipalFactory.Create(issued, context.ClientIngress()),
                new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = issued.ExpiresAt }).ConfigureAwait(false);
            return Results.Redirect("/");
        }).RequireAuthorization();

        app.MapPost("/auth/passkeys/login/options", async (BeginPasskeyLoginRequest request, HttpContext context,
            DashboardConfigurationReader configuration, DashboardIdentityClient identity, CancellationToken ct) =>
        {
            if (RejectIfTooManyAttempts(context, json: true) is { } limited)
            {
                return limited;
            }

            if (!PasskeyOriginGate.IsPublicOrigin(context))
            {
                return Results.BadRequest();
            }

            return await identity.GetPasskeyLoginOptionsAsync(request.Email, context.ClientIngress(), context.Request.IsHttps, ct).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.BadRequest();
        }).AllowAnonymous();

        app.MapPost("/auth/passkeys/login/complete", async (CompletePasskeyLoginRequest request, HttpContext context,
            DashboardConfigurationReader configuration, DashboardIdentityClient identity, CancellationToken ct) =>
        {
            if (RejectIfTooManyAttempts(context, json: true) is { } limited)
            {
                return limited;
            }

            request = request with { OriginalClientIngress = context.ClientIngress(), OriginalClientIsHttps = context.Request.IsHttps };
            var issued = await identity.CompletePasskeyLoginAsync(request, ct).ConfigureAwait(false); if (issued is null)
            {
                return Results.Unauthorized();
            }

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, DashboardPrincipalFactory.Create(issued, context.ClientIngress()), new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = issued.ExpiresAt }).ConfigureAwait(false);
            return Results.Ok(issued);
        }).AllowAnonymous();

        app.MapPost("/auth/passkeys/registration/options", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery, CancellationToken ct) =>
        { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); if (!PasskeyOriginGate.IsPublicOrigin(context)) { return Results.BadRequest(); } return await identity.GetPasskeyRegistrationOptionsAsync(ct).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.BadRequest(); });

        app.MapPost("/auth/passkeys/registration/complete", async (CompletePasskeyRegistrationRequest request, HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery, CancellationToken ct) =>
        { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); return await identity.CompletePasskeyRegistrationAsync(request, ct).ConfigureAwait(false) ? Results.NoContent() : Results.BadRequest(); });

        app.MapGet("/account/security/external/{providerId}", (string providerId) =>
        {
            var provider = externalProviders.FirstOrDefault(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return Results.NotFound();
            }

            var properties = new AuthenticationProperties { RedirectUri = "/settings/account" };
            properties.Items["tuvima:external-purpose"] = ExternalIdentityTransactionPurposes.Link;
            return Results.Challenge(properties, [provider.AuthenticationScheme]);
        });


        app.MapPost("/auth/logout", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            if (Guid.TryParse(context.User.FindFirstValue("tuvima:session_id"), out var sessionId))
            {
                await identity.RevokeSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.Redirect("/auth/login");
        });


        app.MapGet("/account/security", (string? emailTest) => Results.Redirect(
            string.IsNullOrWhiteSpace(emailTest)
                ? "/settings/account"
                : $"/settings/account?emailTest={Uri.EscapeDataString(emailTest)}"));

        // "Secure your account": the this-computer-only owner adds a password. The Engine ends the old no-password
        // session and starts a normal one, so the sign-in cookie is replaced here (a circuit cannot set a cookie).
        app.MapPost("/account/secure", async (HttpContext context, DashboardIdentityClient identity, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var password = form["password"].ToString();
            if (!string.Equals(password, form["confirmPassword"].ToString(), StringComparison.Ordinal))
            {
                return Results.Content(SecureAccountFailurePage("The two passwords don't match."), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var result = await identity.SecureAccountAsync(new SecureAccountRequest
            {
                Password = password,
                DeviceId = EnsureDeviceCookie(context),
                DeviceName = SanitizeDeviceName(context.Request.Headers.UserAgent.ToString()),
                Client = "Tuvima Library Dashboard",
            }, context.RequestAborted).ConfigureAwait(false);
            if (!result.Succeeded || result.Value is not { } issued)
            {
                return Results.Content(
                    SecureAccountFailurePage(result.Failure == DashboardAccessMutationFailure.Validation
                        ? "Use at least 12 characters, and avoid common passwords or your email address."
                        : "Your account could not be secured. Nothing has changed. Try again."),
                    "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                DashboardPrincipalFactory.Create(issued, context.ClientIngress()),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    AllowRefresh = true,
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = issued.ExpiresAt,
                }).ConfigureAwait(false);

            return Results.Content(
                RecoveryCodesPage(issued.RecoveryCodes, "/settings/account", "Continue to Tuvima Library",
                    "Your account is secure. You can now sign in from other devices at home."),
                "text/html", Encoding.UTF8);
        });

        app.MapPost("/account/security", async (HttpContext context, DashboardIdentityClient identity, PasswordResetEmailSender emailSender, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var action = form["action"].ToString();
            if (action == "test-email")
            {
                var sent = await SendCurrentAccountTestEmailAsync(
                    identity, emailSender, context.RequestAborted).ConfigureAwait(false);
                return Results.Redirect($"/settings/account?emailTest={(sent ? "sent" : "failed")}");
            }
            if (action == "recovery-codes")
            {
                var codes = await identity.RegenerateRecoveryCodesAsync(context.RequestAborted).ConfigureAwait(false);
                return codes is null
                    ? Results.Content(LoginFailurePage("Recovery codes could not be created. Confirm it's you on the account page and try again."), "text/html", Encoding.UTF8, StatusCodes.Status403Forbidden)
                    : Results.Content(RecoveryCodesPage(codes, "/settings/account", "Return to Account Security"), "text/html", Encoding.UTF8);
            }
            // Password, passkey, provider and session changes live on the Account page, which can ask the person
            // to confirm it's them and shows any failure; this form only handles the two actions above.
            return Results.Redirect("/settings/account");
        });

        return app;
    }

    internal static async Task<IResult?> RefreshInvalidLoginFormAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IReadOnlyList<RegisteredExternalAuthProvider> externalProviders,
        Func<Task<SignInMethodsResponse?>>? loadMethods = null)
    {
        if (await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            return null;
        }

        // Reject the submitted credentials. The framework replaces an unreadable
        // cookie token and issues a matching request token for a new submission.
        var tokens = antiforgery.GetAndStoreTokens(context);
        var methods = loadMethods is null ? null : await loadMethods().ConfigureAwait(false);
        var form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false)
            : null;
        return Results.Content(
            LoginPage(tokens.RequestToken ?? string.Empty, methods, externalProviders,
                EnsureDeviceCookie(context), SafeReturnUrl(form?["returnUrl"].ToString()),
                PasskeyOriginGate.IsPublicOrigin(context),
                "This sign-in form expired. Please enter your details again."),
            "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    private static string EnsureDeviceCookie(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue("Tuvima.Device", out var existing) && Guid.TryParse(existing, out _))
        {
            return existing;
        }

        var value = Guid.NewGuid().ToString("D");
        context.Response.Cookies.Append("Tuvima.Device", value, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddYears(2),
        });
        return value;
    }

    private static string SafeReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)
            ? value
            : "/";

    private static string SanitizeDeviceName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Browser" : value.Length <= 100 ? value : value[..100];

    /// <summary>
    /// Shows only the sign-in methods the Engine says work here. When the Engine could not answer, falls back to the
    /// password form alone (the safe, always-working choice) rather than offering buttons that may fail.
    /// </summary>
    internal static string LoginPage(
        string token,
        SignInMethodsResponse? methods,
        IReadOnlyList<RegisteredExternalAuthProvider> registeredProviders,
        string deviceId,
        string returnUrl,
        bool atPublicOrigin,
        string? message = null,
        string? thisComputerName = null)
    {
        methods ??= new SignInMethodsResponse(true, false, [], true);
        var showPasskey = methods.Passkey && atPublicOrigin;
        var externalProviders = registeredProviders
            .Where(provider => methods.ExternalProviders.Any(allowed =>
                string.Equals(allowed.Id, provider.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var externalButtons = string.Join(
            string.Empty,
            externalProviders.Select(provider =>
                $"<p><a class=\"button\" href=\"/auth/external/{Uri.EscapeDataString(provider.Id)}?returnUrl={Uri.EscapeDataString(returnUrl)}\">Continue with {H(provider.DisplayName)}</a></p>"));
        var passkeyScript = $$$"""
              <script>
              document.getElementById('passkey-login').addEventListener('click',async()=>{const message=document.getElementById('passkey-message');try{if(!window.PublicKeyCredential||!PublicKeyCredential.parseRequestOptionsFromJSON)throw new Error('This browser does not support passkeys.');const start=await fetch('/auth/passkeys/login/options',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:(document.getElementById('signin-email')?.value)||null})});if(start.status===429)throw new Error('Too many attempts. Try again in a minute.');if(!start.ok)throw new Error('Passkey sign-in is unavailable.');const data=await start.json();const credential=await navigator.credentials.get({publicKey:PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(data.options_json))});const finish=await fetch('/auth/passkeys/login/complete',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({credential_json:JSON.stringify(credential.toJSON()),state:data.state,device_id:{{{JsonSerializer.Serialize(deviceId)}}},device_name:navigator.userAgent})});if(finish.status===429)throw new Error('Too many attempts. Try again in a minute.');if(!finish.ok)throw new Error('Passkey sign-in failed.');location.href={{{JsonSerializer.Serialize(returnUrl)}}};}catch(error){message.textContent=error.message;}});
              </script>
              """;
        var passwordForm = $"""
              <form method="post"><input type="hidden" name="__RequestVerificationToken" value="{H(token)}"><input type="hidden" name="action" value="login"><input type="hidden" name="returnUrl" value="{H(returnUrl)}">
              <label>Email<input id="signin-email" type="email" name="email" autocomplete="username" required autofocus></label>
              <label>Password<input type="password" name="password" autocomplete="current-password" required></label><button>Sign in</button></form>
              <p><a href="/auth/recover">Forgot your password?</a></p>
              """;
        var continueAs = string.IsNullOrWhiteSpace(thisComputerName)
            ? string.Empty
            : $"""
              <form method="post"><input type="hidden" name="__RequestVerificationToken" value="{H(token)}"><input type="hidden" name="action" value="this-computer"><input type="hidden" name="returnUrl" value="{H(returnUrl)}">
              <button id="continue-on-this-computer">Continue as {H(thisComputerName)}</button></form>
              <p class="supporting">This computer only. Add a password later to use Tuvima on other devices.</p>
              """;
        var form = $"""
              <p class="eyebrow">Tuvima Library</p>
              <h1>Sign in to Tuvima Library</h1>
              {(message is null ? string.Empty : $"<p class=\"error\" role=\"alert\">{H(message)}</p>")}
              {continueAs}
              {(methods.Password ? passwordForm : "<p class=\"supporting\">Password sign-in isn't available from here.</p>")}
              {(showPasskey ? "<button type=\"button\" id=\"passkey-login\">Sign in with a passkey</button><p id=\"passkey-message\" class=\"supporting\"></p>" : string.Empty)}
              {externalButtons}
              {(methods.InvitationCode ? "<p class=\"supporting\"><a href=\"/auth/invite\">Have an invitation code?</a></p>" : string.Empty)}
              {(showPasskey ? passkeyScript : string.Empty)}
              """;

        return Shell(form);
    }

    /// <summary>Signs the browser in with the session the Engine issued, then sends the person where they were going.</summary>
    private static async Task<IResult> FinishSignInAsync(HttpContext context, AuthSessionResponse issued, string returnUrl)
    {
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            DashboardPrincipalFactory.Create(issued, context.ClientIngress()),
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = issued.ExpiresAt,
                RedirectUri = returnUrl,
            }).ConfigureAwait(false);

        if (issued.PasswordChangeRequired)
        {
            return Results.Redirect(PasswordChangeRedirectMiddleware.ChangePasswordPath);
        }

        if (issued.RecoveryCodes.Count > 0)
        {
            return Results.Content(
                RecoveryCodesPage(issued.RecoveryCodes, "/", "Continue to Tuvima Library"),
                "text/html",
                Encoding.UTF8);
        }

        return Results.Redirect(returnUrl);
    }

    internal static string TwoStepCodePage(string antiforgeryToken, string pendingToken, string returnUrl, string? error) =>
        Shell($"""
            <p class="eyebrow">Tuvima Library</p>
            <h1>Enter your code</h1>
            <p class="supporting">Enter the 6-digit code from your authenticator app.</p>
            {(string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"notice\" role=\"alert\">{H(error)}</p>")}
            <form method="post" action="/auth/login/two-step"><input type="hidden" name="__RequestVerificationToken" value="{H(antiforgeryToken)}"><input type="hidden" name="pendingToken" value="{H(pendingToken)}"><input type="hidden" name="returnUrl" value="{H(returnUrl)}">
              <label>6-digit code<input name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" spellcheck="false" autofocus></label>
              <details><summary>Use a recovery code instead</summary><label>Recovery code<input name="recoveryCode" autocomplete="off" autocapitalize="characters" spellcheck="false" placeholder="XXXXX-XXXXX-XXXXX-XXXXX"></label></details>
              <button>Continue</button>
            </form>
            <p><a href="/auth/login">Start over</a></p>
            """);

    private static string PasswordRecoveryPage(string token, bool emailResetEnabled)
    {
        var emailReset = emailResetEnabled
            ? $"<h2>Email reset link</h2><p class=\"supporting\">We will send a time-limited reset link if the address belongs to an eligible account.</p><form method=\"post\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{H(token)}\"><input type=\"hidden\" name=\"action\" value=\"email-reset\"><label>Email<input type=\"email\" name=\"email\" autocomplete=\"username\" required autofocus></label><button>Email reset link</button></form>"
            : "<h2>Email reset link</h2><p class=\"supporting\">Email delivery is not configured. Use a saved recovery code, or ask the server administrator to run the local recovery command below.</p>";

        return Shell($$"""
            <p class="eyebrow">Tuvima Library</p>
            <h1>Recover your account</h1>
            <p><a href="/auth/login">Back to sign in</a></p>
            {{emailReset}}
            <h2>Use a recovery code</h2>
            <p class="supporting">Use one of the one-time recovery codes saved when the account was created.</p>
            <form method="post"><input type="hidden" name="__RequestVerificationToken" value="{{H(token)}}"><input type="hidden" name="action" value="recover"><label>Email<input type="email" name="email" autocomplete="username" required></label><label>Recovery code<input name="recoveryCode" autocomplete="off" spellcheck="false" required></label><label>New password<input type="password" name="newPassword" minlength="12" autocomplete="new-password" required><small>Use at least 12 characters, and avoid common passwords or your email address.</small></label><button>Reset with recovery code</button></form>
            <h2>Local administrator recovery</h2>
            <p class="supporting">No recovery code or email access? Open an elevated terminal on the computer running Tuvima Library and run <code>tuvima-admin auth reset-password --email you@example.com</code>. From a source checkout, run <code>dotnet run --project src/MediaEngine.Admin -- auth reset-password --email you@example.com</code>.</p>
            <p class="supporting">This recovery command is local-only: it is not an HTTP endpoint and cannot be invoked through an externally exposed Dashboard. It revokes all sessions and replaces the recovery codes.</p>
            """);
    }

    private static string PasswordRecoveryFailurePage(string message) =>
        Shell($"<h1>Account recovery failed</h1><p class=\"error\">{H(message)}</p><p><a href=\"/auth/recover\">Try again</a></p><p><a href=\"/auth/login\">Return to sign in</a></p>");

    private static string SecureAccountFailurePage(string message) =>
        Shell($"<h1>Your account is not secured yet</h1><p class=\"error\">{H(message)}</p><p><a href=\"/settings/account\">Go back</a></p>");

    private static string RecoveryCodesPage(
        IReadOnlyList<string> codes,
        string continueHref,
        string continueLabel,
        string? note = null) =>
        Shell($"<h1>Save your recovery codes</h1>{(note is null ? string.Empty : $"<p>{H(note)}</p>")}<p>Each code works once. Store them somewhere safe before continuing.</p><pre>{H(string.Join(Environment.NewLine, codes))}</pre><p><a class=\"button\" href=\"{H(continueHref)}\">{H(continueLabel)}</a></p>");

    internal static async Task<bool> SendCurrentAccountTestEmailAsync(
        DashboardIdentityClient identity,
        PasswordResetEmailSender emailSender,
        CancellationToken ct)
    {
        var account = await identity.GetAccountAsync(ct).ConfigureAwait(false);
        return account?.Email is { Length: > 0 } email
            && await emailSender.SendTestAsync(email, ct).ConfigureAwait(false);
    }
    /// <summary>
    /// Counts one anonymous sign-in attempt for the caller's address. Returns the 429 response (with
    /// <c>Retry-After</c> and a plain message) when that address has used up its allowance for the minute.
    /// </summary>
    public static IResult? RejectIfTooManyAttempts(HttpContext context, bool json = false)
    {
        var limiter = context.RequestServices.GetRequiredService<SignInAttemptLimiter>();
        var outcome = limiter.Acquire(context, out var retryAfter);
        if (outcome == SignInAttemptResult.Allowed)
        {
            return null;
        }

        if (outcome != SignInAttemptResult.TooManyAttempts)
        {
            // Not a rate limit: sign-in cannot work from here, and waiting will not change that.
            var refusal = outcome == SignInAttemptResult.UseProxyPort
                ? SignInAttemptLimiter.UseProxyPortMessage
                : SignInAttemptLimiter.AddressUnknownMessage;
            return json
                ? Results.Problem(title: "Sign-in unavailable", detail: refusal, statusCode: StatusCodes.Status403Forbidden)
                : Results.Content(LoginFailurePage(refusal), "text/html", Encoding.UTF8, StatusCodes.Status403Forbidden);
        }

        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (json)
        {
            return Results.Problem(
                title: "Too many attempts",
                detail: SignInAttemptLimiter.TooManyAttemptsMessage,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Content(
            LoginFailurePage(SignInAttemptLimiter.TooManyAttemptsMessage),
            "text/html",
            Encoding.UTF8,
            StatusCodes.Status429TooManyRequests);
    }

    private const string InvalidInvitationMessage = "That invitation code is not valid, has expired, or was already used.";

    private static string InviteCodeEntryBody(string? error) =>
        "<p class=\"eyebrow\">Tuvima Library invitation</p><h1>Enter your invitation code</h1>"
        + (string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"notice\" role=\"alert\">{H(error)}</p>")
        + "<form method=\"get\" action=\"/auth/invite\"><label>Invitation code<input name=\"code\" autocomplete=\"off\" autocapitalize=\"characters\" spellcheck=\"false\" placeholder=\"XXXXX-XXXXX\" required autofocus></label><button>Continue</button></form><p><a href=\"/auth/login\">Back to sign in</a></p>";

    private static string InvitePasswordBody(string antiforgeryToken, string deviceId, string code, string email, string? error) =>
        "<p class=\"eyebrow\">Tuvima Library invitation</p><h1>Create your sign-in</h1><p class=\"supporting\">This invitation grants access only to the profiles chosen by the server administrator.</p>"
        + (string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"notice\" role=\"alert\">{H(error)}</p>")
        + $"<form method=\"post\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{H(antiforgeryToken)}\"><input type=\"hidden\" name=\"code\" value=\"{H(code)}\"><input type=\"hidden\" name=\"deviceId\" value=\"{H(deviceId)}\">"
        + $"<label>Email<input type=\"email\" value=\"{H(email)}\" readonly aria-readonly=\"true\"></label>"
        + "<label>Choose a password<input type=\"password\" name=\"password\" minlength=\"12\" autocomplete=\"new-password\" required autofocus><small>Use at least 12 characters, and avoid common passwords or your email address.</small></label><button>Create my sign-in</button></form>";

    private static string ChangePasswordBody(string antiforgeryToken, string? error) =>
        "<p class=\"eyebrow\">Tuvima Library</p><h1>Choose a new password</h1><p class=\"supporting\">Your administrator gave you a temporary password. Choose a password of your own to continue.</p>"
        + (string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"notice\" role=\"alert\">{H(error)}</p>")
        + $"<form method=\"post\" action=\"/auth/change-password\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{H(antiforgeryToken)}\">"
        + "<label>Temporary password<input type=\"password\" name=\"currentPassword\" autocomplete=\"current-password\" required></label>"
        + "<label>New password<input type=\"password\" name=\"newPassword\" minlength=\"12\" autocomplete=\"new-password\" required><small>Use at least 12 characters, and avoid common passwords or your email address.</small></label>"
        + "<label>Type the new password again<input type=\"password\" name=\"confirmPassword\" minlength=\"12\" autocomplete=\"new-password\" required></label><button>Save my password</button></form>"
        + $"<form method=\"post\" action=\"/auth/logout\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{H(antiforgeryToken)}\"><button class=\"link-button\">Sign out</button></form>";

    private static string LoginFailurePage(string message) => Shell($"<h1>Unable to continue</h1><p>{H(message)}</p><p><a href=\"/auth/login\">Return to sign in</a></p>");
    private static IResult EngineUnavailableResult(string returnUrl) => Results.Content(
        EngineUnavailablePage(returnUrl),
        "text/html",
        Encoding.UTF8,
        StatusCodes.Status503ServiceUnavailable);
    private static string EngineUnavailablePage(string returnUrl) => Shell($"<p class=\"eyebrow\">Tuvima Library</p><h1>Engine unavailable</h1><p class=\"supporting\">Tuvima Library cannot reach the library Engine yet. Start or restart the Engine, then try again.</p><p><a class=\"button\" href=\"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}\">Try again</a></p>");
    private static string Shell(string body) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
          <meta name="theme-color" content="#0B1020">
          <meta name="description" content="Sign in to your local Tuvima Library.">
          <meta name="mobile-web-app-capable" content="yes">
          <meta name="apple-mobile-web-app-capable" content="yes">
          <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
          <meta name="apple-mobile-web-app-title" content="Tuvima Library">
          <link rel="manifest" href="/manifest.webmanifest">
          <link rel="apple-touch-icon" sizes="192x192" href="/icons/tuvima-192.png">
          <title>Tuvima Library</title>
          <style>
            :root { color-scheme: dark; font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
            * { box-sizing: border-box; }
            body { margin: 0; min-height: 100dvh; display: grid; place-items: center; padding: max(1.25rem, env(safe-area-inset-top)) max(1.25rem, env(safe-area-inset-right)) max(1.25rem, env(safe-area-inset-bottom)) max(1.25rem, env(safe-area-inset-left)); background: radial-gradient(circle at 15% 5%, #281849 0, #100b1c 38%, #08060d 75%); color: #f8f6ff; }
            main { width: min(34rem, 100%); padding: clamp(1.5rem, 5vw, 2.5rem); background: rgba(22, 16, 33, .96); border: 1px solid #58427d; border-radius: 1.25rem; box-shadow: 0 1.5rem 5rem rgba(0, 0, 0, .55); }
            h1, h2, p { margin-top: 0; }
            h1 { margin-bottom: .75rem; color: #ffffff; font-size: clamp(1.8rem, 5vw, 2.35rem); line-height: 1.1; letter-spacing: -.025em; }
            h2 { margin-top: 2rem; color: #ffffff; }
            p, td, th, summary { color: #d8d0e8; }
            .eyebrow { margin-bottom: .65rem; color: #ad8cff; font-size: .75rem; font-weight: 800; letter-spacing: .14em; text-transform: uppercase; }
            .lede { margin-bottom: 1.6rem; color: #c9bfd9; font-size: 1rem; line-height: 1.6; }
            .supporting { margin-top: .8rem; color: #c9bfd9; font-size: .9rem; line-height: 1.5; }
            form { display: grid; gap: 1rem; margin: 1.25rem 0; }
            label { display: grid; gap: .45rem; color: #f4efff; font-size: .9rem; font-weight: 700; }
            small { color: #aa9fbc; font-size: .78rem; font-weight: 500; }
            input, button, .button { width: 100%; min-height: 3rem; border-radius: .7rem; font: inherit; }
            input { padding: .78rem .9rem; border: 1px solid #65557d; outline: none; background: #0e0a16; color: #ffffff; caret-color: #b596ff; }
            input:hover { border-color: #8970ad; }
            input:focus { border-color: #a982ff; box-shadow: 0 0 0 .2rem rgba(124, 77, 255, .24); }
            button, .button { display: inline-grid; place-items: center; padding: .8rem 1rem; border: 1px solid #9d7bff; background: linear-gradient(135deg, #7040ef, #8c5cff); color: #ffffff; font-weight: 800; text-decoration: none; cursor: pointer; }
            button:hover, .button:hover { background: linear-gradient(135deg, #8051f5, #9c70ff); }
            .link-button { width: auto; min-height: 2.5rem; padding: .4rem .2rem; border: 0; background: none; color: #c6aaff; font-weight: 600; text-decoration: underline; }
            .link-button:hover { background: none; color: #ddcdff; }
            button:focus-visible, .button:focus-visible, summary:focus-visible, a:focus-visible { outline: .2rem solid rgba(181, 150, 255, .75); outline-offset: .15rem; }
            a { color: #c6aaff; }
            details { margin: 1.25rem 0; }
            summary { min-height: 3rem; display: flex; align-items: center; cursor: pointer; touch-action: manipulation; }
            table { width: 100%; border-collapse: collapse; }
            td, th { padding: .65rem; border-bottom: 1px solid #392b4c; text-align: left; }
            pre, .notice { padding: 1rem; border: 1px solid #44345c; border-radius: .7rem; background: #0d0914; color: #ece5f7; }
            pre { white-space: pre-wrap; }
            code { color: #d2bcff; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
            @media (max-width: 40rem) { body { align-items: start; padding: max(.75rem, env(safe-area-inset-top)) max(.75rem, env(safe-area-inset-right)) max(.75rem, env(safe-area-inset-bottom)) max(.75rem, env(safe-area-inset-left)); } main { margin-top: .75rem; padding: 1.35rem; border-radius: 1rem; } }
          </style>
        </head>
        <body><main>{{body}}</main><script>if ('serviceWorker' in navigator) window.addEventListener('load', function () { navigator.serviceWorker.register('/service-worker.js', { scope: '/' }); });</script></body>
        </html>
        """;
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
