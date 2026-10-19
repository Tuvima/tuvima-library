using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared.Providers;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ProviderOnboardingDialogTests : AsyncBunitContext
{
    private const string KeyPage = "https://example.test/api-keys";

    private readonly List<string> _calls = [];
    private readonly List<(string Name, ProviderCredentialWriteRequest Request, string? Session)> _writes = [];
    private ProviderCredentialOperationResultDto _outcome = Success();

    public ProviderOnboardingDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.SaveProviderCredentialsAsync), args => Record("save", args, session: false));
            stub.SetHandler(nameof(IEngineApiClient.SaveSetupProviderCredentialsAsync), args => Record("setup-save", args, session: true));
            stub.SetHandler(nameof(IEngineApiClient.TestProviderCredentialsAsync), args => Record("test", args, session: false));
        }));
    }

    [Fact]
    public void ThreeStepCatalogue_RendersOneScreenWithoutNumberedProgress()
    {
        var cut = RenderDialog(Catalogue());

        Assert.Empty(cut.FindAll(".provider-onboarding__progress"));
        Assert.Single(cut.FindAll(".provider-onboarding__step"));
        Assert.Empty(cut.FindAll("button:contains('Back')"));
        Assert.Empty(cut.FindAll("button:contains('Continue')"));
        Assert.Equal(["Open the key page.", "Copy the key.", "Paste it below."],
            cut.FindAll(".provider-onboarding__instructions li").Select(item => item.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void KeyLink_UsesTheConfiguredAddressAndOpensInANewTab()
    {
        var cut = RenderDialog(Catalogue());

        var link = Assert.Single(cut.FindAll("a[href='" + KeyPage + "']"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("Open the key page", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryUserSuppliedCredentialIsOnTheOneScreen()
    {
        var catalogue = Catalogue();
        catalogue.Onboarding!.Credentials.Add(new ProviderCredentialFieldDto { Key = "pin", Label = "Subscriber PIN", Ownership = "user_supplied" });
        catalogue.Onboarding.Credentials.Add(new ProviderCredentialFieldDto { Key = "client", Label = "Hidden client id", Ownership = "application_managed" });

        var cut = RenderDialog(catalogue);

        Assert.Contains("Service key", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Subscriber PIN", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden client id", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingKey_ShowsThePlainFailureAndDoesNotClose()
    {
        _outcome = new ProviderCredentialOperationResultDto
        {
            Success = false,
            Status = "invalid_credential",
            Message = "The service did not accept this key.",
        };
        var dialog = new DialogSpy();
        var cut = RenderDialog(Catalogue(), dialog);

        EnterKey(cut, "wrong-key");
        await cut.Find("button:contains('Test')").ClickAsync();

        Assert.Contains("The service did not accept this key.", cut.Markup, StringComparison.Ordinal);
        Assert.Null(dialog.Result);
        Assert.Empty(cut.FindAll("button:contains('Done')"));
        Assert.NotEmpty(cut.FindAll("button:contains('Test')"));
    }

    [Fact]
    public async Task GoodKey_SavesThroughTheEngineAndShowsSuccessWithDone()
    {
        var dialog = new DialogSpy();
        var cut = RenderDialog(Catalogue(), dialog);

        EnterKey(cut, "good-key");
        await cut.Find("button:contains('Test')").ClickAsync();

        Assert.Equal(["save"], _calls);
        Assert.Equal("good-key", Assert.Single(_writes).Request.Credentials["api_key"]);
        Assert.Contains("Key saved", cut.Markup, StringComparison.Ordinal);
        Assert.Null(dialog.Result);

        await cut.Find("button:contains('Done')").ClickAsync();

        var result = Assert.IsType<ProviderOnboardingDialogResult>(dialog.Result!.Data);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FromSetup_TheSetupSaveActionIsCalledWithTheSession()
    {
        var cut = RenderDialog(Catalogue(), setupSession: "session-1");

        EnterKey(cut, "good-key");
        await cut.Find("button:contains('Test')").ClickAsync();

        Assert.Equal(["setup-save"], _calls);
        var write = Assert.Single(_writes);
        Assert.Equal("example", write.Name);
        Assert.Equal("session-1", write.Session);
    }

    [Fact]
    public async Task TestOperation_ChecksTheKeyWithoutSavingIt()
    {
        var cut = RenderDialog(Catalogue(), operation: ProviderOnboardingOperation.Test);

        EnterKey(cut, "good-key");
        await cut.Find("button:contains('Test connection')").ClickAsync();

        Assert.Equal(["test"], _calls);
    }

    private object? Record(string call, object?[]? args, bool session)
    {
        _calls.Add(call);
        _writes.Add(((string)args![0]!, (ProviderCredentialWriteRequest)args[1]!, session ? (string?)args[2] : null));
        return Task.FromResult<ProviderCredentialOperationResultDto?>(_outcome);
    }

    private static void EnterKey(IRenderedComponent<ProviderOnboardingDialog> cut, string value) =>
        cut.Find("input").Input(value);

    private IRenderedComponent<ProviderOnboardingDialog> RenderDialog(
        ProviderCatalogueDto catalogue,
        DialogSpy? dialog = null,
        string? setupSession = null,
        ProviderOnboardingOperation operation = ProviderOnboardingOperation.Connect) =>
        Render<ProviderOnboardingDialog>(parameters => parameters
            .Add(component => component.Catalogue, catalogue)
            .Add(component => component.SetupSession, setupSession)
            .Add(component => component.Operation, operation)
            .AddCascadingValue<IAppDialogContext>(dialog ?? new DialogSpy()));


    private static ProviderCredentialOperationResultDto Success() => new()
    {
        Success = true,
        Status = "success",
        Message = "The provider credentials were verified and saved.",
    };

    private static ProviderCatalogueDto Catalogue() => new()
    {
        Name = "example",
        DisplayName = "Example Service",
        Onboarding = new ProviderOnboardingDto
        {
            Credentials = [new ProviderCredentialFieldDto { Key = "api_key", Label = "Service key", Required = true, Ownership = "user_supplied" }],
            Steps =
            [
                new ProviderOnboardingStepDto
                {
                    Id = "account",
                    Title = "Account",
                    Instructions = ["Open the key page."],
                    Action = new ProviderOnboardingActionDto { Kind = "external_link", Label = "Open the key page", Url = KeyPage },
                },
                new ProviderOnboardingStepDto
                {
                    Id = "credential",
                    Title = "Get credential",
                    Instructions = ["Copy the key.", "Paste it below."],
                    Action = new ProviderOnboardingActionDto { Kind = "credential_entry", Label = "Enter API key" },
                    CredentialKeys = ["api_key"],
                },
                new ProviderOnboardingStepDto
                {
                    Id = "connect",
                    Title = "Connect",
                    Action = new ProviderOnboardingActionDto { Kind = "credential_probe", Label = "Verify & connect" },
                    CredentialKeys = ["api_key"],
                },
            ],
        },
    };


    private sealed class DialogSpy : IAppDialogContext
    {
        public AppDialogResult? Result { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public AppDialogOptions Options { get; } = new();
        public string? PresentationClass => null;
        public void Close(AppDialogResult? result = null) => Result = result ?? AppDialogResult.Ok(new object());
        public void Cancel() => Result = AppDialogResult.Cancel();
        public Task CloseAsync(AppDialogResult? result = null) { Close(result); return Task.CompletedTask; }
        public Task CancelAsync() { Cancel(); return Task.CompletedTask; }
        public Task SetOptionsAsync(AppDialogOptions options) => Task.CompletedTask;
        public Task SetPresentationClassAsync(string? value) => Task.CompletedTask;
        public void SetCloseGuard(Func<Task<bool>>? guard) { }
        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
