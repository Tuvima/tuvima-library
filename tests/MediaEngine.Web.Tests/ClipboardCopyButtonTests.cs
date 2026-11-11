using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Copy buttons (server address, invitation code and link, temporary password). The browser only has a clipboard on https
/// and localhost, so on a plain-http address it throws: the person gets a plain message and the page keeps working.
/// </summary>
public sealed class ClipboardCopyButtonTests : AsyncBunitContext
{
    private const string Address = "http://192.168.1.20:5016";

    public ClipboardCopyButtonTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    private void ClipboardThrows(Exception exception) =>
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(exception);

    private IRenderedComponent<GeneralNameAddressPanel> RenderAppsPage()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ => Task.FromResult<NetworkAppAddressesDto?>(new() { AtHome = Address }));
            stub.SetHandler(nameof(IEngineApiClient.GetServerIdentityAsync), _ => Task.FromResult<MediaEngine.Contracts.Setup.SetupServerIdentityDto?>(new("Library", "library", true)));
        }));
        Services.AddSingleton(new AppStoreLinks());
        var cut = Render<GeneralNameAddressPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-apps-address='at-home']")));
        return cut;
    }

    // ---- General > Name and address -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CopyingTheServerAddress_WhereTheBrowserHasNoClipboard_SaysSoInsteadOfBreakingThePage()
    {
        ClipboardThrows(new JSException("Cannot read properties of undefined (reading 'writeText')"));
        var cut = RenderAppsPage();

        cut.Find("[data-apps-address='at-home'] button").Click();

        Assert.Equal(["Couldn't copy — select the address and copy it yourself."], Toasts);
        Assert.NotEmpty(cut.FindAll("[data-apps-address='at-home']"));
    }

    [Fact]
    public void CopyingTheServerAddress_WhenTheBrowserIsGone_SaysNothingAndDoesNotThrow()
    {
        ClipboardThrows(new JSDisconnectedException("The circuit has closed."));
        var cut = RenderAppsPage();

        cut.Find("[data-apps-address='at-home'] button").Click();

        Assert.Empty(Toasts);
        Assert.NotEmpty(cut.FindAll("[data-apps-address='at-home']"));
    }

    [Fact]
    public void CopyingTheServerAddress_ThatWorks_SaysItWasCopied()
    {
        var cut = RenderAppsPage();

        cut.Find("[data-apps-address='at-home'] button").Click();

        Assert.Equal(["Address copied."], Toasts);
    }

    // ---- Invitations ---------------------------------------------------------------------------------------------------------

    private IRenderedComponent<InvitationShare> RenderInvitation() =>
        Render<InvitationShare>(parameters => parameters
            .Add(share => share.Code, "ABCDE-FGHIJ")
            .Add(share => share.ExpiresAt, DateTimeOffset.UtcNow.AddDays(1))
            .Add(share => share.PublicAddress, "https://tuvima.example.com"));

    [Fact]
    public void CopyingAnInvitationCodeOrLink_WhereTheBrowserHasNoClipboard_SaysSoForEach()
    {
        ClipboardThrows(new JSException("Cannot read properties of undefined (reading 'writeText')"));
        var cut = RenderInvitation();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Copy code", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Copy link", StringComparison.Ordinal)).Click();

        Assert.Equal(
            ["Couldn't copy — select the code and copy it yourself.", "Couldn't copy — select the link and copy it yourself."],
            Toasts);
        Assert.Contains("ABCDE-FGHIJ", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyingAnInvitationLink_ThatWorks_CopiesTheLinkAndSaysSo()
    {
        var cut = RenderInvitation();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Copy link", StringComparison.Ordinal)).Click();

        var call = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("https://tuvima.example.com/auth/invite?code=ABCDE-FGHIJ", call.Arguments[0]);
        Assert.Equal(["Link copied."], Toasts);
    }

    [Fact]
    public void CopyingAnInvitation_WhenTheBrowserIsGone_DoesNotThrow()
    {
        ClipboardThrows(new JSDisconnectedException("The circuit has closed."));
        var cut = RenderInvitation();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Copy code", StringComparison.Ordinal)).Click();

        Assert.Empty(Toasts);
    }

    // ---- Temporary password --------------------------------------------------------------------------------------------------

    [Fact]
    public void CopyingAPassword_WhereTheBrowserHasNoClipboard_SaysSoAndKeepsThePasswordOnScreen()
    {
        ClipboardThrows(new JSException("Cannot read properties of undefined (reading 'writeText')"));
        var cut = Render<TemporaryPasswordShare>(parameters => parameters.Add(share => share.Password, "correct-horse-battery"));

        cut.Find("button").Click();

        Assert.Equal(["Couldn't copy — select the password and copy it yourself."], Toasts);
        Assert.Contains("correct-horse-battery", cut.Find("code").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyingAPassword_ThatWorks_SaysItWasCopied()
    {
        var cut = Render<TemporaryPasswordShare>(parameters => parameters.Add(share => share.Password, "correct-horse-battery"));

        cut.Find("button").Click();

        Assert.Equal(["Password copied."], Toasts);
    }
}
