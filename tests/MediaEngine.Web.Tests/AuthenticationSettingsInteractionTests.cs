using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class AuthenticationSettingsInteractionTests : AsyncBunitContext
{
    private AuthSettingsDto _settings = ReadySettings();
    private int _saveCalls;
    private RegisteredExternalProvidersSnapshot _registered = new([]);
    private TaskCompletionSource _saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken _saveToken;
    private bool _throwOnSave;
    private bool _saveSucceeds;
    private UpdateAuthSettingsRequest? _lastRequest;

    public AuthenticationSettingsInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetAuthSettingsAsync), _ => Task.FromResult<AuthSettingsDto?>(_settings));
            stub.SetHandler(nameof(IEngineApiClient.UpdateAuthSettingsAsync), args =>
            {
                _saveCalls++;
                _saveToken = (CancellationToken)args![1]!;
                _lastRequest = args[0] as UpdateAuthSettingsRequest;
                _saveStarted.TrySetResult();
                return _throwOnSave
                    ? Task.FromException<AuthSettingsDto?>(new InvalidOperationException("simulated failure"))
                    : _saveSucceeds ? Task.FromResult<AuthSettingsDto?>(_settings = Echo(_settings, _lastRequest)) : WaitForCancellationAsync(_saveToken);
            });
        }));
        Services.AddSingleton(_ => _registered);
        Services.AddSingleton<IHttpClientFactory>(new StaticHttpClientFactory());
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton(new PasswordResetDeliverySettings { Mode = "Disabled" });
        Services.AddSingleton(new DashboardConfigurationReader(Path.Combine(Path.GetTempPath(), $"tuvima-no-config-{Guid.NewGuid():N}")));
        Services.AddSingleton<PasswordResetEmailSender>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Render<AppPopoverHost>();
    }

    [Fact]
    public void AuthenticationPage_RendersCanonicalOrigin_AndTheTestEmailHasMovedToGeneral()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");

        Assert.Contains("https://library.example.test", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Send test email", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("SMTP", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input[type='email']"));
    }

    [Fact]
    public void AdvancedTab_HoldsEverySignInControlWithoutTheDoubledAuthenticationHeadings()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");

        // The page and its Advanced tab already say where this is: no "Authentication" and no "How people sign in" heading.
        Assert.Equal(["Sessions and invitations", "Status"], cut.FindAll("h3").Select(h => h.TextContent.Trim()));
        Assert.DoesNotContain("Authentication", string.Concat(cut.FindAll("h2, h3").Select(h => h.TextContent)), StringComparison.Ordinal);

        var signIn = cut.Find("[data-testid='sign-in-switches']").TextContent + cut.Find("h4").TextContent;
        Assert.Contains("Allow passwords", signIn, StringComparison.Ordinal);
        Assert.Contains("Require sign-in for everyone", signIn, StringComparison.Ordinal);
        Assert.Contains("Passkey sign-in", signIn, StringComparison.Ordinal);
        Assert.Contains("External provider sign-in", signIn, StringComparison.Ordinal);
        Assert.Contains("External providers", signIn, StringComparison.Ordinal);
        // The Authentication mode dropdown is gone; its three values live behind two switches.
        Assert.Empty(cut.FindAll("select"));
        Assert.DoesNotContain("Authentication mode", cut.Markup, StringComparison.Ordinal);

        string[] InGroup(string id) => cut.Find($"#{id}").NextElementSibling is { } group ? [group.TextContent] : [];
        var sessions = string.Concat(InGroup("security-group-sessions"));
        Assert.Contains("Session lifetime (hours)", sessions, StringComparison.Ordinal);
        Assert.Contains("Maximum active sessions per account", sessions, StringComparison.Ordinal);
        Assert.Contains("Invitation lifetime (hours)", sessions, StringComparison.Ordinal);
        Assert.Contains("Extra home network ranges", sessions, StringComparison.Ordinal);
        Assert.DoesNotContain("CIDR", sessions, StringComparison.Ordinal);

        var status = cut.Find("dl.security-settings__facts").TextContent;
        Assert.Contains("Public address", status, StringComparison.Ordinal);
        Assert.Contains("Passkeys", status, StringComparison.Ordinal);
        Assert.Contains("Recovery delivery", status, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralNotifications_OffersTheTestEmailOnceOutgoingEmailIsSetUp()
    {
        var cut = Render<NotificationsEmailTest>();
        cut.WaitForElement("[data-testid='notifications-email-test']");

        var button = cut.Find("[data-testid='notifications-email-test-send']");
        Assert.False(button.HasAttribute("disabled"));
        Assert.Contains("Send test email to my account", button.TextContent, StringComparison.Ordinal);
        Assert.Contains("current signed-in account", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralNotifications_ShowsNoTestEmailWhileOutgoingEmailIsNotConfigured()
    {
        _settings = new AuthSettingsDto();

        var cut = Render<NotificationsEmailTest>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='notifications-email-test']")));
        Assert.DoesNotContain("Send test email", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderRestartNotice_AppearsOnlyWhenSavedProvidersDifferFromStartup()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");
        Assert.DoesNotContain("Restart Library to apply sign-in provider changes.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderRestartNotice_ShownWhenSavedProviderIsNotRegistered()
    {
        _settings = ReadySettings(new ExternalAuthProviderDto
        {
            Id = "idp",
            Kind = "oidc",
            Enabled = true,
            DisplayName = "IdP",
            ClientId = "c",
            Authority = "https://idp.example",
            Scopes = ["openid"],
        });

        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");

        Assert.Contains("Restart Library to apply sign-in provider changes.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_IsSingleFlightAndComponentDisposalCancelsPendingMutation()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__actions");
        var save = typeof(SecurityTab).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var first = (Task)save.Invoke(cut.Instance, null)!;
        await _saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = (Task)save.Invoke(cut.Instance, null)!;
        await duplicate;

        Assert.Equal(1, _saveCalls);
        await cut.Instance.DisposeAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(_saveToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SaveFailure_IsContainedAndRestoresInteractiveControls()
    {
        _throwOnSave = true;
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__actions");
        var save = typeof(SecurityTab).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);
        cut.Render();

        Assert.Equal(1, _saveCalls);
        Assert.False((bool)typeof(SecurityTab).GetField("_saving", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance)!);
        var button = cut.FindAll(".security-settings__actions button").Single(element =>
            element.TextContent.Contains("Save changes", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));
        // Told once, in place, at the top of the section; never as a pop-up notice.
        Assert.Equal("alert", cut.Find("[data-security-failure]").GetAttribute("role"));
    }

    [Fact]
    public async Task SaveSuccess_ShowsTheQuietSavedLineBesideTheButton_AndNoNotice()
    {
        _saveSucceeds = true;
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__actions");
        var save = typeof(SecurityTab).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Empty(cut.FindAll(".security-settings__actions .app-saved-note__line"));

        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);
        cut.Render();

        var saved = cut.Find(".security-settings__actions [data-saved-note]");
        Assert.Equal("status", saved.GetAttribute("role"));
        Assert.Contains("Saved", saved.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-security-failure]"));
    }

    [Fact]
    public async Task SaveWithAnInvalidLifetime_ShowsTheAlertAndNeverCallsTheEngine()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__actions");
        cut.FindAll("input").Single(input => input.GetAttribute("value") == "336").Input("0");
        var save = typeof(SecurityTab).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);
        cut.Render();

        Assert.Equal(0, _saveCalls);
        Assert.NotEmpty(cut.Find("[data-security-failure]").TextContent.Trim());
    }

    [Fact]
    public void TurningOffRequireSignIn_SavesAtOnce_AndShowsTheSavedNote()
    {
        _saveSucceeds = true;
        var cut = Render<SecurityTab>();
        cut.WaitForElement("[data-testid='sign-in-switches']");

        cut.Find("input[aria-label='Require sign-in for everyone']").Change(false);

        cut.WaitForAssertion(() => Assert.Equal(1, _saveCalls));
        Assert.Equal("Local", _lastRequest!.Mode);
        Assert.True(_lastRequest.PasswordSignInEnabled);
        cut.WaitForAssertion(() =>
            Assert.Contains("Saved", cut.Find("[data-testid='sign-in-saved']").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-security-failure]"));
    }

    [Fact]
    public void TurningOnRequireSignIn_SavesTheRequiredMode()
    {
        _saveSucceeds = true;
        _settings = WithMode(ReadySettings(), "Local");
        var cut = Render<SecurityTab>();
        cut.WaitForElement("[data-testid='sign-in-switches']");

        cut.Find("input[aria-label='Require sign-in for everyone']").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(1, _saveCalls));
        Assert.Equal("Required", _lastRequest!.Mode);
    }

    [Fact]
    public void TurningOffAllowPasswords_SavesAtOnce_AndKeepsTheSignInMode()
    {
        _saveSucceeds = true;
        var cut = Render<SecurityTab>();
        cut.WaitForElement("[data-testid='sign-in-switches']");

        cut.Find("input[aria-label='Allow passwords']").Change(false);

        cut.WaitForAssertion(() => Assert.Equal(1, _saveCalls));
        Assert.False(_lastRequest!.PasswordSignInEnabled);
        Assert.True(_lastRequest.PasskeySignInEnabled);
        Assert.Equal("Required", _lastRequest.Mode);
    }

    [Fact]
    public void TurningOnLinkedSignIn_MovesTheLocalModeToOptional_AndOffMovesItBack()
    {
        _saveSucceeds = true;
        _settings = WithMode(ReadySettings(), "Local");
        var cut = Render<SecurityTab>();
        cut.WaitForElement("[data-testid='sign-in-switches']");

        cut.Find("input[aria-label='External provider sign-in']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(1, _saveCalls));
        Assert.True(_lastRequest!.ExternalSignInEnabled);
        Assert.Equal("Optional", _lastRequest.Mode);

        cut.Find("input[aria-label='External provider sign-in']").Change(false);
        cut.WaitForAssertion(() => Assert.Equal(2, _saveCalls));
        Assert.False(_lastRequest.ExternalSignInEnabled);
        Assert.Equal("Local", _lastRequest.Mode);
    }

    [Fact]
    public void TurningOffTheLastSignInMethod_ShowsAnInlineMessage_AndNeverCallsTheServer()
    {
        _settings = new AuthSettingsDto
        {
            Mode = "Required",
            PasswordSignInEnabled = true,
            PasskeySignInEnabled = false,
            InvitationLifetimeHours = 168,
            SessionLifetimeHours = 336,
            MaximumActiveSessions = 20,
        };
        var cut = Render<SecurityTab>();
        cut.WaitForElement("[data-testid='sign-in-switches']");

        cut.Find("input[aria-label='Allow passwords']").Change(false);

        Assert.Equal(0, _saveCalls);
        Assert.NotEmpty(cut.Find("[data-security-failure]").TextContent.Trim());
        Assert.True(cut.Find("input[aria-label='Allow passwords']").HasAttribute("checked"));
    }

    // What the server keeps after a save: the stored settings with the request's sign-in switches applied.
    private static AuthSettingsDto Echo(AuthSettingsDto stored, UpdateAuthSettingsRequest? request)
    {
        if (request is null)
        {
            return stored;
        }

        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(stored))!.AsObject();
        json["mode"] = request.Mode;
        json["password_sign_in_enabled"] = request.PasswordSignInEnabled;
        json["passkey_sign_in_enabled"] = request.PasskeySignInEnabled;
        json["external_sign_in_enabled"] = request.ExternalSignInEnabled;
        return System.Text.Json.JsonSerializer.Deserialize<AuthSettingsDto>(json.ToJsonString())!;
    }

    private static AuthSettingsDto WithMode(AuthSettingsDto source, string mode)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(source))!.AsObject();
        json["mode"] = mode;
        return System.Text.Json.JsonSerializer.Deserialize<AuthSettingsDto>(json.ToJsonString())!;
    }

    private static async Task<AuthSettingsDto?> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return null;
    }

    private static AuthSettingsDto ReadySettings(params ExternalAuthProviderDto[] providers) => new()
    {
        ExternalProviders = [.. providers],
        Mode = "Required",
        PasswordSignInEnabled = true,
        PasskeySignInEnabled = true,
        InvitationLifetimeHours = 168,
        SessionLifetimeHours = 336,
        MaximumActiveSessions = 20,
        PublicAddress = "https://library.example.test",
        CanonicalOriginReady = true,
        PasskeyReady = true,
        RecoveryDeliveryReady = true,
        PasswordReset = new PasswordResetDeliveryDto
        {
            Mode = "Smtp",
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            FromAddress = "library@example.test",
            Configured = true,
            Ready = true,
        },
    };

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new EmptyHandler())
        {
            BaseAddress = new Uri("https://engine.example.test"),
        };
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
