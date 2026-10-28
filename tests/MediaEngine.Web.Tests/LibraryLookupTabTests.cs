using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A library's Lookup tab (Library pages WP7, D15): the optional personal YouTube key, kept with the existing provider credentials actions
// and never shown again, the four plain steps to get one, and the privacy line.
public sealed class LibraryLookupTabTests : AsyncBunitContext
{
    private readonly List<ProviderCredentialWriteRequest> _saved = [];
    private readonly List<ProviderCredentialWriteRequest> _tested = [];
    private int _removed;
    private bool _configured;
    private bool _catalogueMissing;
    private ProviderCredentialOperationResultDto _saveResult = new() { Success = true, Status = "valid", Message = "The provider credentials were verified and saved." };

    public LibraryLookupTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetProviderCatalogueAsync), _ =>
                Task.FromResult<IReadOnlyList<ProviderCatalogueDto>>(_catalogueMissing ? [] : [Youtube(_configured)]));
            stub.SetHandler(nameof(IEngineApiClient.SaveProviderCredentialsAsync), args =>
            {
                _saved.Add((ProviderCredentialWriteRequest)args![1]!);
                return Task.FromResult<ProviderCredentialOperationResultDto?>(_saveResult);
            });
            stub.SetHandler(nameof(IEngineApiClient.TestProviderCredentialsAsync), args =>
            {
                _tested.Add((ProviderCredentialWriteRequest)args![1]!);
                return Task.FromResult<ProviderCredentialOperationResultDto?>(_saveResult);
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveProviderCredentialsAsync), _ =>
            {
                _removed++;
                return Task.FromResult<ProviderCredentialOperationResultDto?>(new ProviderCredentialOperationResultDto { Success = true, Status = "removed" });
            });
        }));
    }

    private static ProviderCatalogueDto Youtube(bool configured) => new()
    {
        Name = "youtube",
        DisplayName = "YouTube",
        Onboarding = new ProviderOnboardingDto
        {
            Credentials = [new ProviderCredentialFieldDto { Key = "api_key", Label = "YouTube Data API key (optional)", Configured = configured }],
        },
    };

    private static LibraryFolderDto Library() => new() { Id = "77777777-7777-4777-8777-777777777777", Name = "YouTube", KindKey = "youtube", Area = "watch", MediaTypes = ["Videos"] };

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<LibraryLookupTab> Open(bool readOnly = false)
    {
        var cut = Render<LibraryLookupTab>(parameters => parameters.Add(x => x.Library, Library()).Add(x => x.ReadOnly, readOnly));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading lookup", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<LibraryLookupTab> cut, string label) =>
        cut.FindAll("button").Single(button => Text(button) == label);

    [Fact]
    public void TheTabShowsFourPlainSteps_ALinkToTheGoogleConsole_AndThePrivacyLine()
    {
        var cut = Open();

        Assert.Equal(4, cut.FindAll("[data-testid=lookup-key-steps] li").Count);
        Assert.Contains("How do I get a key?", cut.Markup, StringComparison.Ordinal);
        var link = cut.FindAll("a").Single(anchor => Text(anchor) == "Open the Google Cloud console");
        Assert.StartsWith("https://console.cloud.google.com/", link.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Equal("Only video IDs are sent to YouTube.", Text(cut.Find("[data-testid=lookup-privacy]")));
    }

    [Fact]
    public void WithNoKey_TheTabSaysItIsUsingTheSharedKey_AndRemoveIsNotOffered()
    {
        var cut = Open();

        Assert.Equal("Using the shared key", Text(cut.Find("[data-testid=lookup-key-state]")));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Remove");
    }

    [Fact]
    public void WithAKeySaved_TheTabSaysItIsUsingYourKey_TheFieldIsMasked_AndNothingIsRevealed()
    {
        _configured = true;

        var cut = Open();

        Assert.Equal("Using your key", Text(cut.Find("[data-testid=lookup-key-state]")));
        var field = cut.Find("input[type=password]");
        Assert.Equal("", field.GetAttribute("value") ?? string.Empty);
        Assert.Contains(cut.FindAll("button"), button => Text(button) == "Remove");
    }

    [Fact]
    public void SavingAKey_SendsItThroughTheProviderCredentials_ThenClearsTheFieldAndUsesYourKey()
    {
        var cut = Open();

        cut.Find("input[type=password]").Input("AIzaSyExampleKey123");
        Button(cut, "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal("Using your key", Text(cut.Find("[data-testid=lookup-key-state]"))));
        var request = Assert.Single(_saved);
        Assert.Equal("AIzaSyExampleKey123", request.Credentials["api_key"]);
        Assert.DoesNotContain("AIzaSyExampleKey123", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedKey_ShowsThePlainReason_AndKeepsUsingTheSharedKey()
    {
        _saveResult = new ProviderCredentialOperationResultDto { Success = false, Status = "invalid_credential", Message = "YouTube did not accept this key." };
        var cut = Open();

        cut.Find("input[type=password]").Input("not-a-real-key");
        Button(cut, "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains("YouTube did not accept this key.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Using the shared key", Text(cut.Find("[data-testid=lookup-key-state]")));
    }

    [Fact]
    public void TestKey_ChecksTheTypedKeyWithoutSavingIt()
    {
        var cut = Open();

        cut.Find("input[type=password]").Input("AIzaSyExampleKey123");
        Button(cut, "Test key").Click();

        cut.WaitForAssertion(() => Assert.Contains("YouTube accepted the key.", cut.Markup, StringComparison.Ordinal));
        Assert.Single(_tested);
        Assert.Empty(_saved);
    }

    [Fact]
    public void RemovingTheKey_ReturnsToTheSharedKey()
    {
        _configured = true;
        var cut = Open();

        Button(cut, "Remove").Click();

        cut.WaitForAssertion(() => Assert.Equal("Using the shared key", Text(cut.Find("[data-testid=lookup-key-state]"))));
        Assert.Equal(1, _removed);
    }

    [Fact]
    public void OnAPhone_TheTabIsReadOnly_ItShowsTheStateAndTheStepsButNoField()
    {
        _configured = true;

        var cut = Open(readOnly: true);

        Assert.Equal("Using your key", Text(cut.Find("[data-testid=lookup-key-state]")));
        Assert.Empty(cut.FindAll("input"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) is "Save" or "Remove" or "Test key");
        Assert.Equal(4, cut.FindAll("[data-testid=lookup-key-steps] li").Count);
    }

    [Fact]
    public void WhenTheProviderCannotBeRead_TheTabShowsAnErrorWithTryAgain()
    {
        _catalogueMissing = true;
        var cut = Render<LibraryLookupTab>(parameters => parameters.Add(x => x.Library, Library()));

        cut.WaitForAssertion(() => Assert.Contains("Lookup settings could not be loaded", cut.Markup, StringComparison.Ordinal));
        _catalogueMissing = false;
        cut.FindAll("button").Single(button => Text(button) == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Contains("Using the shared key", cut.Markup, StringComparison.Ordinal));
    }
}
