using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A person's page inside Household (profile-and-household D12): titled with the person's name, one link back to Household,
/// the tabs Profile, Preferences, Access &amp; safety and Activity, and your own id opens My profile instead.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly Guid PersonPageSamId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid PersonPageHouseholdId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private void SignInAsHouseholdAdministratorWithAHousehold()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel> { new(MyProfileId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
                Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults((Guid)args![0]!)));
            // The Activity tab draws the shared Activity page, which reads an empty list here.
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityAsync), _ =>
                Task.FromResult(PersonalActivityOutcome<PersonalActivityPageDto>.Ok(new PersonalActivityPageDto([], null, false))));
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityPeriodsAsync), _ =>
                Task.FromResult(PersonalActivityOutcome<PersonalActivityPeriodsDto>.Ok(new PersonalActivityPeriodsDto([]))));
        }));
        var authority = ShellAuthority(false, [], ["household.manage"]);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", authority.AccountId, authority.ActiveProfileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new HouseholdPeopleHandler(new DelayedLockHandler(authority, session))));
    }

    private IRenderedComponent<IComponent> RenderPersonPage(Guid personId, string? tab = null) =>
        Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "household");
            builder.AddAttribute(5, nameof(Settings.Subsection), personId.ToString("D"));
            if (tab is not null)
            {
                builder.AddAttribute(6, nameof(Settings.Detail), tab);
            }

            builder.CloseComponent();
        });

    [Theory]
    [InlineData(null)]
    [InlineData("preferences")]
    [InlineData("access")]
    [InlineData("activity")]
    public void PersonsPage_IsTitledWithTheirName_HasOneBackLinkToHousehold_AndTheFourTabs(string? tab)
    {
        SignInAsHouseholdAdministratorWithAHousehold();

        var cut = RenderPersonPage(PersonPageSamId, tab);

        cut.WaitForAssertion(() => Assert.Equal("Sam", cut.Find(".app-page-header__title").TextContent.Trim()));
        var back = cut.FindAll("[data-testid='settings-back-link']").Single();
        Assert.Equal("Household", back.TextContent.Trim());
        Assert.Equal("/settings/household", back.GetAttribute("href"));
        // Nothing else on the page links back to Household, and the title area does not name the old page.
        Assert.Empty(cut.FindAll(".profile-detail__back"));
        Assert.DoesNotContain("Manage Profiles", cut.Find(".app-page-header").TextContent, StringComparison.OrdinalIgnoreCase);

        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal(["Profile", "Preferences", "Access & safety", "Activity"], tabs.Select(item => item.TextContent.Trim()).ToArray());
        Assert.Equal(
        [
            $"/settings/household/{PersonPageSamId:D}",
            $"/settings/household/{PersonPageSamId:D}/preferences",
            $"/settings/household/{PersonPageSamId:D}/access",
            $"/settings/household/{PersonPageSamId:D}/activity",
        ], tabs.Select(item => item.GetAttribute("href")!).ToArray());
        var selected = tabs.Single(item => item.GetAttribute("aria-selected") == "true");
        Assert.Equal(tab is null ? "/settings/household/" + PersonPageSamId.ToString("D") : $"/settings/household/{PersonPageSamId:D}/{tab}", selected.GetAttribute("href"));
    }

    [Fact]
    public void PersonsActivityTab_ShowsThatPersonsActivity_InTheSamePlaceTheViewActivityRowWas()
    {
        SignInAsHouseholdAdministratorWithAHousehold();

        var cut = RenderPersonPage(PersonPageSamId, "activity");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='activity']")));
        Assert.Equal($"profile:{PersonPageSamId:D}", cut.FindComponent<MediaEngine.Web.Components.Settings.ActivityPage>().Instance.Scope);
        Assert.DoesNotContain("View activity", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void YourOwnId_UnderHousehold_OpensMyProfile_AndDrawsNoPersonPage()
    {
        SignInAsHouseholdAdministratorWithAHousehold();

        var cut = RenderPersonPage(MyProfileId);

        cut.WaitForAssertion(() => Assert.EndsWith("/settings/overview", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-profile-section='detail']"));
    }

    [Fact]
    public void ANoLongerExistingPerson_ShowsTheNoLongerExistsState_WithTheOneLinkBackToHousehold()
    {
        SignInAsHouseholdAdministratorWithAHousehold();

        var cut = RenderPersonPage(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-unavailable='gone']")));
        Assert.Contains("no longer exists", cut.Find(".app-page-state__title").TextContent);
        Assert.Equal("Household", cut.Find(".app-page-header__title").TextContent.Trim());
    }

    // The household's people, as the Engine lists them for an administrator; everything else is answered as the shared handler does.
    private sealed class HouseholdPeopleHandler(DelayedLockHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/access/profiles")
            {
                var people = new[]
                {
                    new ManagedProfileResponse(MyProfileId, "Test User", "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(-3), PersonPageHouseholdId, false,
                        IsServerAdmin: false, IsHouseholdAdmin: true),
                    new ManagedProfileResponse(PersonPageSamId, "Sam", "#2EC98B", null, DateTimeOffset.UtcNow, PersonPageHouseholdId, false,
                        IsServerAdmin: false, IsHouseholdAdmin: false),
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(people) });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
