using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>Upload plugin (Plugins and Integrations screens spec D5, WP2b): the warning, a refused file and a good one.</summary>
public sealed class PluginUploadDialogTests : AsyncBunitContext
{
    private PluginInstallOutcome _outcome = new(new PluginInstallResponse { PluginId = "acme.up", Name = "Up", Message = "Up was added as a Private plugin." });
    private string? _uploadedName;

    public PluginUploadDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.UploadPluginAsync), args =>
            {
                _uploadedName = (string)args![1]!;
                return Task.FromResult(_outcome);
            }));
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(
                serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: new DashboardSessionAccessor()),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: new DashboardSessionAccessor()));
    }

    [Fact]
    public void WhenClosed_NothingIsDrawn()
    {
        var cut = Render<PluginUploadDialog>();

        Assert.Empty(cut.FindAll("[data-testid=plugin-upload-dialog]"));
    }

    [Fact]
    public void WhenOpen_ShowsTheWarning_AndUploadStaysOffUntilAFileIsChosen()
    {
        var cut = Render<PluginUploadDialog>(p => p.Add(c => c.Open, true));

        Assert.Contains("Only upload plugins you trust. A plugin can read and change your library.", cut.Markup);
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload").HasAttribute("disabled"));
    }

    [Fact]
    public void ARefusedFile_ShowsTheEnginesReason_AndNothingIsReportedUploaded()
    {
        _outcome = new PluginInstallOutcome(null, "Only plugins described entirely in JSON can be uploaded.");
        var uploaded = false;
        var cut = Render<PluginUploadDialog>(p => p.Add(c => c.Open, true).Add(c => c.OnUploaded, _ => uploaded = true));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("zip", "p.zip"));

        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload").HasAttribute("disabled")));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload").Click();

        cut.WaitForAssertion(() => Assert.Contains("entirely in JSON", cut.Find("[data-testid=plugin-upload-problem]").TextContent));
        Assert.False(uploaded);
        Assert.Equal("p.zip", _uploadedName);
    }

    [Fact]
    public void AGoodFile_ReportsTheEnginesSentence_AndCloses()
    {
        string? message = null;
        var closed = false;
        var cut = Render<PluginUploadDialog>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.OnUploaded, text => message = text)
            .Add(c => c.OnClosed, () => closed = true));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("zip", "p.zip"));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload").HasAttribute("disabled")));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Upload").Click();

        cut.WaitForAssertion(() => Assert.Equal("Up was added as a Private plugin.", message));
        Assert.True(closed);
    }

    private sealed class SessionOnlyAuthentication : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
