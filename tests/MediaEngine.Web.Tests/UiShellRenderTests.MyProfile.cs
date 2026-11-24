using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// My profile inside Settings (profile-and-household D3, D4): a member reaches every preference from My profile &gt; Preferences without
/// the person-page chrome, and Access &amp; safety opens only for an administrator.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly Guid MyProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private void UseMyProfileApi() =>
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel> { new(MyProfileId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
                Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults((Guid)args![0]!)));
        }));

    private void SignInAsMember()
    {
        var authority = ShellAuthority(false, [], []);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", authority.AccountId, authority.ActiveProfileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));
    }

    [Fact]
    public void MyProfilePreferences_ForAMember_OffersWatchingListeningReadingAndHome_WithNoPersonPageChrome()
    {
        UseMyProfileApi();
        SignInAsMember();

        var host = RenderSettingsWithHosts("overview", "preferences");

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-profile-preferences]")));
        Assert.Equal(["Watching", "Listening", "Reading", "Home"],
            host.FindAll(".profile-detail__section-switch .app-segmented-control__item").Select(item => item.TextContent.Trim()));
        Assert.NotEmpty(host.FindAll("section#playback"));
        // The person-page frame (back link, second header card) is not drawn under My profile.
        Assert.Empty(host.FindAll("[data-profile-section='detail']"));
        Assert.Empty(host.FindAll(".profile-detail__back"));
        Assert.Empty(host.FindAll(".profile-detail__header"));

        foreach (var (section, id) in new[] { ("Listening", "listening"), ("Reading", "reading"), ("Home", "home"), ("Watching", "playback") })
        {
            host.FindAll(".profile-detail__section-switch .app-segmented-control__item").Single(item => item.TextContent.Trim() == section).Click();
            host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll($"section#{id}")));
        }
    }

    [Fact]
    public void MyProfileWatching_EndsWithOnThisDevice_AfterTheChoicesThatFollowThePerson()
    {
        UseMyProfileApi();
        SignInAsMember();

        var host = RenderSettingsWithHosts("overview", "preferences");

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-playback-section='on-this-device']")));
        var sections = host.FindAll("[data-playback-section]").Select(section => section.GetAttribute("data-playback-section")).ToList();
        Assert.Equal("on-this-device", sections[^1]);
        Assert.True(sections.IndexOf("subtitles") < sections.IndexOf("on-this-device"));
        Assert.Contains("On this device", host.Find("[data-playback-section='on-this-device']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void MyProfileAccessAndSafety_ForAMember_IsNotOpened_AndTheProfileTabShowsInstead()
    {
        UseMyProfileApi();
        SignInAsMember();

        var host = RenderSettingsWithHosts("overview", "access");

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-settings-overview]")));
        Assert.Empty(host.FindAll("[data-profile-section='detail']"));
    }

    [Fact]
    public void MyProfileAccessAndSafety_ForAnAdministrator_ShowsTheAccessTabOfTheirOwnProfile_WithNoPersonPageChrome()
    {
        UseMyProfileApi();
        SignInAsHouseholdAdministrator();

        var host = RenderSettingsWithHosts("overview", "access");

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-profile-section='detail'][data-profile-tab='access']")));
        Assert.Empty(host.FindAll(".profile-detail__back"));
        Assert.Empty(host.FindAll(".profile-detail__header"));
    }
}
