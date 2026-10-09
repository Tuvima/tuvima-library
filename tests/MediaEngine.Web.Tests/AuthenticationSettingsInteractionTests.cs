using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
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
                _saveStarted.TrySetResult();
                return _throwOnSave
                    ? Task.FromException<AuthSettingsDto?>(new InvalidOperationException("simulated failure"))
                    : WaitForCancellationAsync(_saveToken);
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
    public void AuthenticationPage_RendersCanonicalOriginAndCurrentAccountTestAction()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");

        Assert.Contains("https://library.example.test", cut.Markup, StringComparison.Ordinal);
        var button = cut.FindAll("button").Single(element =>
            element.TextContent.Contains("Send test email to my account", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Contains("current signed-in account", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input[type='email']"));
    }

    [Fact]
    public void ProviderRestartNotice_AppearsOnlyWhenSavedProvidersDifferFromStartup()
    {
        var cut = Render<SecurityTab>();
        cut.WaitForElement(".security-settings__facts");
        Assert.DoesNotContain("Restart Tuvima Library to apply sign-in provider changes.", cut.Markup, StringComparison.Ordinal);
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

        Assert.Contains("Restart Tuvima Library to apply sign-in provider changes.", cut.Markup, StringComparison.Ordinal);
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

        await (Task)save.Invoke(cut.Instance, null)!;

        Assert.Equal(1, _saveCalls);
        Assert.False((bool)typeof(SecurityTab).GetField("_saving", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance)!);
        var button = cut.FindAll("button").Single(element =>
            element.TextContent.Contains("Save authentication settings", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));
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
