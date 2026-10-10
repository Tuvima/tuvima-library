using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>"Who's watching?": where sign-in sends a household, and what the picker page promises.</summary>
public sealed class ProfilePickerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData(null, "/who")]
    [InlineData("", "/who")]
    [InlineData("/", "/who")]
    [InlineData("/watch", "/who?returnUrl=%2Fwatch")]
    [InlineData("/details/work/1?tab=files", "/who?returnUrl=%2Fdetails%2Fwork%2F1%3Ftab%3Dfiles")]
    public void PickerAddress_CarriesWhereToGoNext(string? returnUrl, string expected) =>
        Assert.Equal(expected, ProfilePickerRoute.For(returnUrl));

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/watch\t//evil.example")]
    [InlineData("/watch\n/x")]
    public void PickerAddress_NeverFollowsAnotherSite(string returnUrl)
    {
        Assert.Null(ProfilePickerRoute.SafeReturnUrl(returnUrl));
        Assert.Equal("/who", ProfilePickerRoute.For(returnUrl));
    }

    [Fact]
    public void EverySignInRoute_HandsAHouseholdToThePicker()
    {
        var endpoints = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");
        var external = Read("src/MediaEngine.Web/Services/Integration/ExternalAuthenticationRegistration.cs");

        // Password sign-in, passkey sign-in and provider sign-in all check the Engine's "choose_profile" answer.
        Assert.Contains("issued.ChooseProfile ? ProfilePickerRoute.For(returnUrl) : returnUrl", endpoints, StringComparison.Ordinal);
        Assert.Contains("signedIn.choose_profile?'/who'", endpoints, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(external, "RouteThroughProfilePicker(context.Properties, issued);"));
    }

    [Fact]
    public void PickerPage_ShowsLockAndChildTags_AsksForPins_AndRemembersPerDevice()
    {
        var page = Read("src/MediaEngine.Web/Components/Pages/ProfilePickerPage.razor");

        Assert.Contains("@page \"/who\"", page, StringComparison.Ordinal);
        Assert.Contains("Who's watching?", page, StringComparison.Ordinal);
        Assert.Contains("profile.HasPin", page, StringComparison.Ordinal);
        Assert.Contains("profile.IsRestricted", page, StringComparison.Ordinal);
        Assert.Contains(">Kids<", page, StringComparison.Ordinal);
        Assert.Contains("Always open as this person on this device", page, StringComparison.Ordinal);
        Assert.Contains("Stop always opening as @preferred.DisplayName", page, StringComparison.Ordinal);
        Assert.Contains("Orchestrator.SetActiveProfileAsync(profile.Id, pin)", page, StringComparison.Ordinal);
        Assert.Contains("Set PINs on adult profiles so children can't switch into them.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerPage_UsesTheNumberPad_AndKeepsTheSameSwitchRules()
    {
        var page = Read("src/MediaEngine.Web/Components/Pages/ProfilePickerPage.razor");

        Assert.Contains("<PinPad", page, StringComparison.Ordinal);
        Assert.DoesNotContain("autocomplete=\"current-password\"", page, StringComparison.Ordinal);
        Assert.Contains("ProfileSwitchStatus.TooManyAttempts => \"Too many attempts. Try again in a minute.\"", page, StringComparison.Ordinal);
        Assert.Contains("That PIN didn't work. Try again.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PinPadStyles_AreGlobal_WithoutDeepRulesOrInlineStyles()
    {
        var pad = Read("src/MediaEngine.Web/Components/Shared/PinPad.razor");
        var css = Read("src/MediaEngine.Web/wwwroot/app.css");

        Assert.DoesNotContain(" style=\"", pad, StringComparison.Ordinal);
        Assert.Contains(".pin-pad__key", css, StringComparison.Ordinal);
        Assert.Contains("@keyframes pin-shake", css, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerStyles_AvoidDeepRulesAndInlineStyles()
    {
        var css = Read("src/MediaEngine.Web/Components/Pages/ProfilePickerPage.razor.css");
        var page = Read("src/MediaEngine.Web/Components/Pages/ProfilePickerPage.razor");

        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", css, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=\"", page, StringComparison.Ordinal);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
