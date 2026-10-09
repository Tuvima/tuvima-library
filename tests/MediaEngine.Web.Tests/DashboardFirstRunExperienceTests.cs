using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class DashboardFirstRunExperienceTests
{
    [Fact]
    public void FirstRunSetup_StartsWithoutAContainerLogClaimToken()
    {
        var dashboard = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");
        var setup = Read("src/MediaEngine.Web/Components/Pages/SetupPage.razor");
        var setupClient = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.Setup.cs");
        var preflight = Read("src/MediaEngine.Web/Components/Setup/SetupPreflightStage.razor");
        var engine = Read("src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs");
        var setupSessions = Read("src/MediaEngine.Api/Services/SetupSessionService.cs");

        Assert.Contains("Results.Redirect(\"/setup\")", dashboard, StringComparison.Ordinal);
        Assert.Contains("BeginSetupAsync", setup, StringComparison.Ordinal);
        Assert.Contains("Keep this Dashboard on your private network", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("Claim this server", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("[Tuvima Setup] Claim token", setup, StringComparison.Ordinal);
        Assert.Contains("X-Tuvima-Setup-Session", setupSessions, StringComparison.Ordinal);
        Assert.Contains("SuppressSessionToken", setupClient, StringComparison.Ordinal);
        Assert.Contains("await ClearSetupSessionAsync()", setup, StringComparison.Ordinal);
        Assert.Contains("!_setupSessionReadyForCircuit", setup, StringComparison.Ordinal);
        Assert.Contains("_setupSessionReadyForCircuit = true", setup, StringComparison.Ordinal);
        Assert.Contains("IsAdministratorConfiguredAsync", setupSessions, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Out.WriteLineAsync", setupSessions, StringComparison.Ordinal);
        Assert.DoesNotContain("Claim token", setupSessions, StringComparison.Ordinal);
        Assert.DoesNotContain("/bootstrap/administrator", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLoopbackRequest", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunSetup_AsksForTheServerCodeBeforeTheWizardWhenRequired()
    {
        var setup = Read("src/MediaEngine.Web/Components/Pages/SetupPage.razor");
        var codeStage = Read("src/MediaEngine.Web/Components/Setup/SetupCodeStage.razor");
        var host = Read("src/MediaEngine.Web/Components/App.razor");
        var setupClient = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.Setup.cs");

        Assert.Contains("Enter the setup code from your server", codeStage, StringComparison.Ordinal);
        Assert.Contains("docker exec -it &lt;container&gt; tuvima-admin setup code", codeStage, StringComparison.Ordinal);
        Assert.Contains("tuvima-admin setup code", codeStage, StringComparison.Ordinal);
        Assert.Contains("Setup has to be finished from your home network.", codeStage, StringComparison.Ordinal);
        Assert.Contains("<SetupCodeStage", setup, StringComparison.Ordinal);
        Assert.Contains("SetupBeginRefusalReasons.RemoteRefused", setup, StringComparison.Ordinal);
        // The ingress comes from the Dashboard's classifier through a protected root-component parameter,
        // never from anything the browser sends.
        Assert.Contains("HttpContext?.ClientIngress()", host, StringComparison.Ordinal);
        Assert.Contains("OriginalClientIngress = ClientIngress ?? ClientIngressValues.Remote", setup, StringComparison.Ordinal);
        Assert.Contains("SuppressSessionToken", setupClient, StringComparison.Ordinal);
        // The code step must not hold the wizard's _busy flag, or the first step never loads after the code is accepted.
        Assert.Contains("_codeBusy = true", setup, StringComparison.Ordinal);
        Assert.Contains("Busy=\"@_codeBusy\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("_busy = true;\n        try { await ReloadAsync(); }", setup.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Login_SeparatesUnavailableEngineFromAnExplicitFirstRun()
    {
        var dashboard = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");
        var normalized = dashboard.ReplaceLineEndings("\n");

        Assert.Contains("if (bootstrap is null)", dashboard, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status503ServiceUnavailable", dashboard, StringComparison.Ordinal);
        Assert.Contains("EngineUnavailableResult(SafeReturnUrl(returnUrl))", dashboard, StringComparison.Ordinal);
        Assert.Contains("<h1>Engine unavailable</h1>", dashboard, StringComparison.Ordinal);
        Assert.Contains(">Try again</a>", dashboard, StringComparison.Ordinal);
        Assert.Contains("if (!bootstrap.AdministratorConfigured)\n            {\n                return Results.Redirect(\"/setup\");", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginUnavailableResult_IsRetryable503WithoutSetupControls()
    {
        var method = typeof(MediaEngine.Web.Services.Integration.DashboardAuthenticationEndpoints)
            .GetMethod("EngineUnavailableResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = Assert.IsAssignableFrom<IResult>(method?.Invoke(null, ["/read?browse=books"]));
        var context = new DefaultHttpContext();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        var html = await reader.ReadToEndAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.StartsWith("text/html", context.Response.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1>Engine unavailable</h1>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/login?returnUrl=%2Fread%3Fbrowse%3Dbooks\">Try again</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/setup", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"action\" value=\"bootstrap\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupWizard_IsReversibleFocusedAndProtectsCredentialEntry()
    {
        var setup = Read("src/MediaEngine.Web/Components/Pages/SetupPage.razor");
        var administrator = Read("src/MediaEngine.Web/Components/Setup/SetupAdministratorStage.razor");
        var recoveryCodes = Read("src/MediaEngine.Web/Components/Shared/RecoveryCodesDisplay.razor");
        var preflight = Read("src/MediaEngine.Web/Components/Setup/SetupPreflightStage.razor");
        var providers = Read("src/MediaEngine.Web/Components/Setup/SetupProvidersStage.razor");
        var providerDialog = Read("src/MediaEngine.Web/Components/Shared/Providers/ProviderOnboardingDialog.razor");
        var metadataSettings = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var readiness = Read("src/MediaEngine.Web/Components/Setup/SetupReadinessStage.razor");
        var stageFooter = Read("src/MediaEngine.Web/Components/Setup/SetupStageFooter.razor");
        var workflow = Read("src/MediaEngine.Contracts/Setup/SetupContracts.cs");
        var overview = Read("src/MediaEngine.Web/Components/Settings/OverviewTab.razor");

        Assert.Contains("Confirm password", administrator, StringComparison.Ordinal);
        Assert.Contains("RecoveryCodesDisplay", administrator, StringComparison.Ordinal);
        Assert.Contains("Download .txt", recoveryCodes, StringComparison.Ordinal);
        Assert.Contains("Copy all", recoveryCodes, StringComparison.Ordinal);
        Assert.Contains("Save and check again", preflight, StringComparison.Ordinal);
        Assert.Contains("CanNavigateTo(key)", setup, StringComparison.Ordinal);
        Assert.Contains("Label=\"Back\"", stageFooter, StringComparison.Ordinal);
        Assert.Contains("SetupStageFooter", readiness, StringComparison.Ordinal);
        Assert.Contains("ProviderOnboardingDialog", providers, StringComparison.Ordinal);
        Assert.Contains("ProviderOnboardingDialog", metadataSettings, StringComparison.Ordinal);
        Assert.Contains("provider.MediaTypes.Any(ConfiguredMediaTypes.Contains)", providers, StringComparison.Ordinal);
        Assert.Contains("Set up later", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("TMDB", providers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fanart.tv", providers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ownership", providerDialog, StringComparison.Ordinal);
        Assert.Contains("RunPreflightAsync(automaticallyAdvance: true)", setup, StringComparison.Ordinal);
        Assert.Contains("LoadReadinessAsync", setup, StringComparison.Ordinal);
        Assert.Contains("Ready · later", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"local-ai\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"access\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("\"local-ai\", \"access\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Continue optional setup", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordRecovery_OffersEmailRecoveryCodesAndElevatedHostCommand()
    {
        var dashboard = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");
        var client = Read("src/MediaEngine.Web/Services/Integration/DashboardIdentityClient.cs");
        var engine = Read("src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs");
        var identity = Read("src/MediaEngine.Identity/FirstPartyIdentityService.cs");
        var publicIdentityContract = Read("src/MediaEngine.Identity/Contracts/IFirstPartyIdentityService.cs");
        var hostRecoveryContract = Read("src/MediaEngine.Identity/Contracts/IHostAdministratorRecoveryService.cs");

        Assert.DoesNotContain("reset-local-administrator", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetLocalAdministratorPassword", client, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/password/local-administrator-reset", client, StringComparison.Ordinal);
        Assert.DoesNotContain("local-administrator-reset", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetAdministratorPasswordFromHostAsync", publicIdentityContract, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/auth/recover\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/auth/recover\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/recover\">Forgot your password?</a>", dashboard, StringComparison.Ordinal);
        Assert.Contains("<h1>Recover your account</h1>", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("<details><summary>Reset password or recover administrator access</summary>", dashboard, StringComparison.Ordinal);
        Assert.Contains("tuvima-admin auth reset-password", dashboard, StringComparison.Ordinal);
        Assert.Contains("This recovery command is local-only", dashboard, StringComparison.Ordinal);
        Assert.Contains("cannot be invoked through an externally exposed Dashboard", dashboard, StringComparison.Ordinal);
        Assert.Contains("Use one of the one-time recovery codes", dashboard, StringComparison.Ordinal);
        Assert.Contains("Reset with recovery code", dashboard, StringComparison.Ordinal);
        Assert.Contains("ResetAdministratorPasswordFromHostAsync", hostRecoveryContract, StringComparison.Ordinal);
        Assert.Contains("account.IsAdministrator", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileRole.Administrator", identity, StringComparison.Ordinal);
        Assert.Contains("host_administrator_password_reset", identity, StringComparison.Ordinal);
        Assert.Contains("RevokeAccountSessionsAsync", identity, StringComparison.Ordinal);
        Assert.Contains("ReplaceRecoveryCodesAsync", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationShell_HasExplicitAccessibleDarkThemeColors()
    {
        var dashboard = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");

        Assert.Contains(":root { color-scheme: dark;", dashboard, StringComparison.Ordinal);
        Assert.Contains("body { margin: 0;", dashboard, StringComparison.Ordinal);
        Assert.Contains("background: #0e0a16; color: #ffffff", dashboard, StringComparison.Ordinal);
        Assert.Contains("label { display: grid;", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>color-scheme:dark;body", dashboard, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
