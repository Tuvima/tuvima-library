using Bunit;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using MediaEngine.Web.Services.Navigation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>Server &gt; General (settings-and-setup-simplified D10): one page, no tabs, four panels.</summary>
public sealed class GeneralPageTests : AsyncBunitContext
{
    public GeneralPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(new AppStoreLinks());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetServerIdentityAsync),
                _ => Task.FromResult<SetupServerIdentityDto?>(new("Shaya's Library", "shayas-library", true)));
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync),
                _ => Task.FromResult<NetworkAppAddressesDto?>(new() { AtHome = "http://192.168.1.20:5016" }));
            stub.SetHandler(nameof(IEngineApiClient.GetServerGeneralAsync),
                _ => Task.FromResult<ServerGeneralSettingsDto?>(new ServerGeneralSettingsDto(DisplayLanguage: "en-US")));
            stub.SetHandler(nameof(IEngineApiClient.GetNotificationsAsync),
                _ => Task.FromResult<NotificationListResponse?>(new NotificationListResponse()));
            stub.SetHandler(nameof(IEngineApiClient.GetNotificationSettingsAsync),
                _ => Task.FromResult<NotificationSettingsResponse?>(new NotificationSettingsResponse()));
            stub.SetHandler(nameof(IEngineApiClient.GetAuthSettingsAsync),
                _ => Task.FromResult<AuthSettingsDto?>(new AuthSettingsDto
                {
                    PasswordReset = new PasswordResetDeliveryDto { Mode = "Smtp", SmtpHost = "smtp.example.test", SmtpPort = 587, FromAddress = "library@example.test", Configured = true, Ready = true },
                }));
        }));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton<IHttpClientFactory>(new StaticClientFactory());
        Services.AddSingleton(new MediaEngine.Domain.Configuration.PasswordResetDeliverySettings { Mode = "Disabled" });
        Services.AddSingleton(new DashboardConfigurationReader(Path.Combine(Path.GetTempPath(), $"tuvima-no-config-{Guid.NewGuid():N}")));
        Services.AddSingleton<PasswordResetEmailSender>();
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
        Services.AddSingleton<PlayingNowState>();
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton(provider => new UIOrchestratorService(
            provider.GetRequiredService<IEngineApiClient>(),
            provider.GetRequiredService<UniverseStateContainer>(),
            new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), provider.GetRequiredService<IEngineApiClient>()),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton(provider => new IngestionLiveDashboardState(provider.GetRequiredService<IEngineApiClient>(), provider.GetRequiredService<UIOrchestratorService>(), provider.GetRequiredService<UniverseStateContainer>()));
        Services.AddSingleton<SettingsAttentionState>();
    }

    [Fact]
    public void General_ShowsRightNow_ThenNameAndAddress_ThenLanguageAndRegion_AndNoNotifications()
    {
        var cut = Render<GeneralPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='general-name-address']")));
        var markup = cut.Find("[data-testid='general-page']").InnerHtml;
        var name = markup.IndexOf("general-name-address", StringComparison.Ordinal);
        var language = markup.IndexOf("Language and region", StringComparison.Ordinal);
        Assert.True(name >= 0 && name < language, "Name and address, then Language and region.");
        Assert.Empty(cut.FindAll("[data-testid='general-notifications']"));
        Assert.Contains("Name and address", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Notifications_IsItsOwnTab_AndCarriesTheTestEmailThatWasOnSignInMethods()
    {
        var cut = Render<GeneralPage>(parameters => parameters.Add(page => page.Subsection, "notifications"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='notifications-email-test-send']")));
        Assert.Contains("Notifications", cut.Find("#general-notifications-heading").TextContent, StringComparison.Ordinal);
        Assert.Contains("Send test email to my account", cut.Find("[data-testid='notifications-email-test-send']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='general-name-address']"));
    }

    [Fact]
    public void TheQrCodeAndAppLinks_NowLiveOnGeneral()
    {
        var cut = Render<GeneralPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-qr-payload]")));
        Assert.Equal("http://192.168.1.20:5016", cut.Find("[data-qr-payload]").GetAttribute("data-qr-payload"));
        Assert.Contains("Get the app", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPage_RoutesGeneralToThisPage_AndTheProfileAppsPageIsGone()
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web"));
        var settingsPage = LocalizedSource.ReadAllText(Path.Combine(web, "Components", "Pages", "Settings.razor"));

        Assert.Contains("<GeneralPage", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("OverviewAppsPage", settingsPage, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(web, "Components", "Settings", "Overview", "OverviewAppsPage.razor")));
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Overview, "apps"));
    }

    private sealed class StaticClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://engine.example.test") };
    }
}
