using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Personal &gt; Security &gt; Devices: where it sits in the menu, who can open it, and that the old Server entry is gone.</summary>
public sealed class DevicesNavTests
{
    [Fact]
    public void Devices_IsTheOnlyChildOfSecurity_AtTheSecurityDevicesAddress()
    {
        var personal = SettingsNav.MenuGroups.Single(group => group.Key == "personal");
        var security = personal.Nodes.Single(node => node.Section == SettingsSection.Security);

        var devices = Assert.Single(security.Children);
        Assert.Equal("Devices", devices.Label);
        Assert.Equal(SettingsSection.Security, devices.Section);
        Assert.Equal("devices", devices.Subsection);
        Assert.Equal("/settings/security/devices", devices.Route);
        // Security still opens on the sign-in page; its child is a different page.
        Assert.Equal("/settings/security", security.Route);
        Assert.Equal("/settings/security", SettingsNav.RouteFor(SettingsSection.Security));
        Assert.Equal("/settings/security/devices", SettingsNav.RouteFor(SettingsSection.Security, "devices"));
        Assert.NotNull(SettingsNav.ResolveSubsection(SettingsSection.Security, "devices"));
    }

    [Fact]
    public void ServerHasNoDevicesEntry_AndTheOldAddressesNoLongerExist()
    {
        var server = SettingsNav.MenuGroups.Single(group => group.Key == "server");

        Assert.DoesNotContain(SettingsNav.Flatten(server.Nodes), node => node.Label == "Devices");
        Assert.DoesNotContain(SettingsNav.AllItems, item => item.Slug == "devices" || item.Aliases.Contains("apps"));
        Assert.DoesNotContain(SettingsNav.GetSubsections(SettingsSection.Network), subsection => subsection.Slug == "apps");

        foreach (var segment in new[] { "devices", "apps" })
        {
            var resolution = SettingsNav.ResolveRoute(segment, administrationAllowed: true, householdAdministrator: true);
            Assert.False(resolution.IsKnownRoute);
            Assert.Equal("/not-found", resolution.CanonicalRoute);
        }

        // /settings/network/apps is no longer a Network page either, and nothing redirects it.
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Network, "apps"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Devices_IsInTheMenuAndReachableForEveryKindOfSignedInPerson(bool server, bool household)
    {
        Assert.True(SettingsNav.IsVisible(SettingsSection.Security, server, household));
        var resolution = SettingsNav.ResolveRoute("security", server, household);
        Assert.True(resolution.RequestedSectionAllowed);

        var groups = SettingsNav.FilteredMenuGroups(server, household);
        var security = SettingsNav.Flatten(groups.Single(group => group.Key == "personal").Nodes)
            .Single(node => node.Section == SettingsSection.Security && node.Subsection is null);
        Assert.Equal(["Devices"], security.Children.Select(child => child.Label));

        var location = SettingsNav.FindMenuLocation(SettingsSection.Security, "devices");
        Assert.NotNull(location);
        Assert.Equal(["Security", "Devices"], location.Trail.Select(node => node.Label));
        Assert.Equal("personal", location.Group.Key);
    }

    [Fact]
    public void Devices_ShowsInThePhonePageSelectorUnderSecurity()
    {
        var context = new MediaEngine.Web.Services.Navigation.SettingsMenuContext(
            AdministrationAllowed: false, HouseholdAdministrator: false, IsMobile: true, ReviewCount: 0, SecurityNeedsAttention: false);

        var selector = MediaEngine.Web.Services.Navigation.SettingsMenuBuilder.BuildPhoneSelector(
            MediaEngine.Web.Services.Navigation.SettingsMenuBuilder.Build(context));

        var lines = selector.Single().Options;
        var security = lines.Single(line => line.Label == "Security");
        var devices = lines.Single(line => line.Label == "Devices");
        Assert.Equal("/settings/security/devices", devices.Route);
        Assert.Equal(security.Depth + 1, devices.Depth);
    }

    [Fact]
    public void ARestrictedKidsProfileNeverReachesTheSettingsPageThatHostsDevices()
    {
        var root = RepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));

        // Settings turns a Kids profile away before it draws any page, Security's Devices child included.
        Assert.Contains("_kidsBlocked = _activeProfile?.IsRestricted == true;", settings, StringComparison.Ordinal);
        Assert.True(settings.IndexOf("@if (_kidsBlocked)", StringComparison.Ordinal) < settings.IndexOf("<DevicesTab />", StringComparison.Ordinal));
        Assert.Contains("if (_kidsBlocked)", settings[settings.IndexOf("private void ResolveSectionAndNormalize()", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void NoCodeOrDocumentationStillPointsAtTheOldServerDevicesPage()
    {
        var root = RepositoryRoot();
        var webSource = Directory.EnumerateFiles(Path.Combine(root, "src", "MediaEngine.Web"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".css" or ".js")
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj" or "vendor"))
            .ToList();

        foreach (var file in webSource)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("SettingsSection.Devices", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/settings/devices", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AppsAndDevicesPanel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("LegacyRedirect", text, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

public sealed class DevicesFilterTests
{
    private static readonly Guid HomeHousehold = Guid.NewGuid();
    private static readonly Guid CabinHousehold = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Mary = Guid.NewGuid();

    [Fact]
    public void AllMatchesEverything_AndIsNotActive()
    {
        Assert.False(DevicesFilter.All.IsActive);
        Assert.True(DevicesFilter.All.Matches(Device(HomeHousehold, Anna)));
        Assert.True(DevicesFilter.All.Matches(null, Session(Anna)));
    }

    [Fact]
    public void ADeviceMatchesOnlyItsHouseholdAndProfile()
    {
        var annaAtHome = Device(HomeHousehold, Anna);

        Assert.True(new DevicesFilter(HomeHousehold).Matches(annaAtHome));
        Assert.False(new DevicesFilter(CabinHousehold).Matches(annaAtHome));
        Assert.True(new DevicesFilter(null, Anna).Matches(annaAtHome));
        Assert.False(new DevicesFilter(null, Mary).Matches(annaAtHome));
        Assert.False(new DevicesFilter(HomeHousehold, Mary).Matches(annaAtHome));
    }

    [Fact]
    public void ABrowserMatchesByThePersonsHouseholdAndTheProfileOpenInIt()
    {
        var annaSession = Session(Anna);

        Assert.True(new DevicesFilter(HomeHousehold, Anna).Matches(HomeHousehold, annaSession));
        Assert.False(new DevicesFilter(CabinHousehold).Matches(HomeHousehold, annaSession));
        Assert.False(new DevicesFilter(null, Mary).Matches(HomeHousehold, annaSession));
    }

    private static ManagedClientDeviceDto Device(Guid household, Guid profile) =>
        new() { Id = Guid.NewGuid(), HouseholdId = household, ProfileId = profile };

    private static DeviceSessionResponse Session(Guid profile) =>
        new() { Id = Guid.NewGuid(), ActiveProfileId = profile };
}

public sealed class DeviceTypeClassifierTests
{
    [Theory]
    [InlineData("mobile", "Tuvima for Android", DeviceTypeKind.Android)]
    [InlineData("mobile", "Tuvima for iOS", DeviceTypeKind.Apple)]
    [InlineData("mobile", "Tuvima for iPadOS", DeviceTypeKind.Apple)]
    [InlineData("television", "Tuvima for Roku", DeviceTypeKind.Tv)]
    [InlineData("television", "Tuvima for Android TV", DeviceTypeKind.Tv)]
    [InlineData("television", "Tuvima TV", DeviceTypeKind.Tv)]
    [InlineData("television", "Tuvima for Apple TV", DeviceTypeKind.Tv)]
    [InlineData("web", "Tuvima Dashboard", DeviceTypeKind.Browser)]
    [InlineData("automotive", "Tuvima Auto", DeviceTypeKind.Generic)]
    [InlineData("mobile", "Something else", DeviceTypeKind.Generic)]
    [InlineData("", "", DeviceTypeKind.Generic)]
    public void TheTypeComesFromThePlatformAndTheAppNameTheDeviceReported(string platform, string clientName, DeviceTypeKind expected)
    {
        Assert.Equal(expected, DeviceTypeClassifier.Classify(platform, clientName));
    }

    [Fact]
    public void ATvAppNameWinsOverItsPlatformWordsAndNullsAreGeneric()
    {
        Assert.Equal(DeviceTypeKind.Tv, DeviceTypeClassifier.Classify("mobile", "Tuvima for Roku"));
        Assert.Equal(DeviceTypeKind.Android, DeviceTypeClassifier.Classify("automotive", "Tuvima for Android Auto"));
        Assert.Equal(DeviceTypeKind.Generic, DeviceTypeClassifier.Classify(null, null));
    }

    [Fact]
    public void EveryTypeHasAWordForItsAccessibleName()
    {
        foreach (var kind in Enum.GetValues<DeviceTypeKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DeviceTypeClassifier.Label(kind)));
        }
    }
}

public sealed class DevicesPageTests : AsyncBunitContext
{
    private readonly DevicesHandler _handler = new();
    private readonly DashboardSessionAccessor _session = new();

    public DevicesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new DevicesClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton(_session);
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
    }

    [Fact]
    public void Member_SeesOnlyTheirOwnDevicesAndBrowsers_WithNoFiltersAndNoAdministratorCalls()
    {
        SignIn(DevicesScope.Member);
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-session-row]");
        Assert.Contains("Living room TV", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna's phone", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Mary's phone", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-device-row]"));
        Assert.Empty(cut.FindAll("[data-devices-filters]"));
        Assert.Empty(cut.FindAll(".signed-in-sessions__person-header"));
        Assert.Equal(2, cut.FindAll("[data-session-row]").Count);
        Assert.Contains("Signed-in browsers", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Belongs to", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Paths, path => path is "/access/sessions" or "/access/profiles" or "/access/households");
    }

    [Fact]
    public void HouseholdAdministrator_SeesTheirHouseholdWithAProfileFilterButNoHouseholdFilter()
    {
        SignIn(DevicesScope.HouseholdAdministrator);
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-devices-filter='profile']");
        cut.WaitForElement("[data-session-person]");
        Assert.Empty(cut.FindAll("[data-devices-filter='household']"));
        Assert.Equal(["All", "Owner", "Anna", "Ben"], OptionLabels(cut, "profile"));
        Assert.Equal(2, cut.FindAll("[data-device-row]").Count);
        Assert.DoesNotContain("Mary's phone", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Paths, path => path == "/access/households");
        Assert.Equal(2, cut.FindAll("[data-session-person]").Count);
    }

    [Fact]
    public void ServerAdministrator_SeesEveryonesDevicesAndBothFilters_AndNoConnectAnAppCard()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-devices-filter='household']");
        cut.WaitForElement("[data-devices-filter='profile']");
        cut.WaitForElement("[data-session-person]");
        Assert.Equal(["All", "Cabin", "Home"], OptionLabels(cut, "household"));
        Assert.Equal(["All", "Owner", "Anna", "Ben", "Mary"], OptionLabels(cut, "profile"));
        Assert.Equal(3, cut.FindAll("[data-device-row]").Count);
        Assert.Equal(3, cut.FindAll("[data-session-person]").Count);
        Assert.Empty(cut.FindAll("[data-devices-panel='apps']"));
        Assert.DoesNotContain("Address to type in the app", cut.Markup, StringComparison.Ordinal);

        var owner = cut.Find($"[data-session-person='{_handler.OwnerAccount}']");
        Assert.Contains("owner@example.test", owner.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, owner.QuerySelectorAll("[data-session-row]").Length);
        Assert.Single(owner.QuerySelectorAll("[data-session-tag='this-device']"));
        Assert.Contains("No email", cut.Find($"[data-session-person='{_handler.MaryAccount}']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAHousehold_NarrowsDevicesBrowsersAndTheProfileChoices()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-device-row]").Count));

        cut.Find("select[data-devices-filter='household']").Change(_handler.CabinHousehold.ToString());

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-device-row]"));
            Assert.Contains("Mary's phone", cut.Find("[data-device-row]").TextContent, StringComparison.Ordinal);
            Assert.Single(cut.FindAll("[data-session-person]"));
            Assert.Equal(["All", "Mary"], OptionLabels(cut, "profile"));
        });

        cut.Find("select[data-devices-filter='household']").Change(string.Empty);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-device-row]").Count));
    }

    [Fact]
    public void ChoosingAProfile_NarrowsToThatPersonsDevicesAndBrowsers()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-device-row]").Count));

        cut.Find("select[data-devices-filter='profile']").Change(_handler.AnnaProfile.ToString());

        cut.WaitForAssertion(() =>
        {
            var device = Assert.Single(cut.FindAll("[data-device-row]"));
            Assert.Contains("Anna's phone", device.TextContent, StringComparison.Ordinal);
            var person = Assert.Single(cut.FindAll("[data-session-person]"));
            Assert.Equal(_handler.AnnaAccount.ToString(), person.GetAttribute("data-session-person"));
        });
    }

    [Fact]
    public void ProfileQueryValue_PreselectsTheProfileFilter_ForAnAdministrator()
    {
        SignIn(DevicesScope.ServerAdministrator);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/settings/security/devices?profile={_handler.AnnaProfile}");
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(_handler.AnnaProfile.ToString(), cut.Find("select[data-devices-filter='profile']").GetAttribute("value"));
            var device = Assert.Single(cut.FindAll("[data-device-row]"));
            Assert.Contains("Anna's phone", device.TextContent, StringComparison.Ordinal);
            var person = Assert.Single(cut.FindAll("[data-session-person]"));
            Assert.Equal(_handler.AnnaAccount.ToString(), person.GetAttribute("data-session-person"));
        });
    }

    [Fact]
    public void ProfileQueryValue_ThatIsNotAPersonTheAdministratorCanSee_LeavesTheFilterOnAll()
    {
        SignIn(DevicesScope.HouseholdAdministrator);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/settings/security/devices?profile={_handler.MaryProfile}");
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-devices-filter='profile']");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-device-row]").Count));
        Assert.Equal(string.Empty, cut.Find("select[data-devices-filter='profile']").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void ProfileQueryValue_IsIgnoredForAMember()
    {
        SignIn(DevicesScope.Member);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/settings/security/devices?profile={_handler.AnnaProfile}");
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-session-row]");
        Assert.Empty(cut.FindAll("[data-devices-filters]"));
        Assert.Single(cut.FindAll("[data-device-row]"));
    }

    [Fact]
    public void ChoosingAHouseholdThatLacksTheChosenProfile_ResetsTheProfileToAll()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-device-row]").Count));
        cut.Find("select[data-devices-filter='profile']").Change(_handler.AnnaProfile.ToString());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-device-row]")));

        cut.Find("select[data-devices-filter='household']").Change(_handler.CabinHousehold.ToString());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Mary's phone", cut.Find("[data-device-row]").TextContent, StringComparison.Ordinal);
            Assert.Equal(string.Empty, cut.Find("select[data-devices-filter='profile']").GetAttribute("value") ?? string.Empty);
        });
    }

    [Fact]
    public void AFilterThatMatchesNothing_SaysSoInsteadOfShowingAnEmptyTable()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");

        cut.Find("select[data-devices-filter='profile']").Change(_handler.BenProfile.ToString());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-device-row]"));
            Assert.Empty(cut.FindAll("[data-session-person]"));
            Assert.Contains("No paired devices match", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("No sign-ins match", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EveryDeviceRowShowsTheIconForWhatItReportedAtPairing()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-device-row]").Count));

        Assert.Equal("tv", IconKind(cut, _handler.TvDevice));
        Assert.Equal("apple", IconKind(cut, _handler.AnnaPhone));
        Assert.Equal("android", IconKind(cut, _handler.MaryPhone));
        cut.WaitForElement("[data-session-row]");
        Assert.All(cut.FindAll("[data-session-row] [data-device-type]"), icon => Assert.Equal("browser", icon.GetAttribute("data-device-type")));
    }

    [Fact]
    public async Task Member_SignsOutADeviceAndABrowser_ThroughTheirOwnActions()
    {
        SignIn(DevicesScope.Member);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-row]");

        await cut.InvokeAsync(() => cut.Find($"[data-device-row='{_handler.TvDevice}'] button").Click());
        await ConfirmSignOutAsync(cut, ".network-test-dialog__actions");
        cut.WaitForAssertion(() => Assert.Equal([$"/access/devices/{_handler.TvDevice}"], _handler.Deleted.ToArray()));

        var other = cut.Find($"[data-session-row='{_handler.OtherSession}']");
        await cut.InvokeAsync(() => other.QuerySelector("button")!.Click());
        await ConfirmSignOutAsync(cut, ".signed-in-sessions__dialog-actions");

        cut.WaitForAssertion(() => Assert.Contains($"/auth/sessions/{_handler.OtherSession}", _handler.Deleted.ToArray()));
        Assert.DoesNotContain(_handler.Deleted, path => path.StartsWith("/access/sessions/", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"[data-session-row='{_handler.OtherSession}']")));
    }

    [Fact]
    public async Task Administrator_SignOutAsksFirst_ThenEndsOnlyThatSessionThroughTheManagedAction_AndThisDeviceHasNoSignOut()
    {
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");

        var thisDevice = cut.Find($"[data-session-row='{_handler.CurrentSession}']");
        Assert.DoesNotContain("Sign out", thisDevice.TextContent, StringComparison.Ordinal);

        var tv = cut.Find($"[data-session-row='{_handler.OtherSession}']");
        await cut.InvokeAsync(() => tv.QuerySelector("button")!.Click());
        Assert.Empty(_handler.Deleted);
        await ConfirmSignOutAsync(cut, ".signed-in-sessions__dialog-actions");

        cut.WaitForAssertion(() => Assert.Equal([$"/access/sessions/{_handler.OtherSession}"], _handler.Deleted.ToArray()));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"[data-session-row='{_handler.OtherSession}']")));
    }

    [Fact]
    public void WhenOnlyThisDeviceIsSignedIn_ShowsTheEmptyState()
    {
        _handler.People = [_handler.OwnerOnlyThisDevice()];
        SignIn(DevicesScope.ServerAdministrator);
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Nobody else is signed in", cut.Markup, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-session-person]"));
    }

    [Fact]
    public void WhenAMemberIsSignedInOnlyHere_ShowsTheEmptyStateForThemselves()
    {
        _handler.OwnSessions = [_handler.ThisComputer()];
        SignIn(DevicesScope.Member);
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() => Assert.Contains("You are not signed in anywhere else", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheEngineRefusesBecauseOfTheAdministratorPin_AsksForIt()
    {
        _handler.SessionsStatus = HttpStatusCode.Forbidden;
        SignIn(DevicesScope.HouseholdAdministrator);
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Enter your administrator PIN", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-session-person]"));
    }

    // The confirmation is the "Sign out" button in the dialog's own footer, not the one on a row.
    private static async Task ConfirmSignOutAsync(IRenderedComponent<DevicesTab> cut, string dialogActions)
    {
        cut.WaitForAssertion(() => Assert.Contains("Sign this device out?", cut.Markup, StringComparison.Ordinal));
        var confirm = cut.FindAll($"{dialogActions} button").Single(button => button.TextContent.Trim() == "Sign out");
        await cut.InvokeAsync(() => confirm.Click());
    }

    private static string[] OptionLabels(IRenderedComponent<DevicesTab> cut, string filter) =>
        cut.Find($"select[data-devices-filter='{filter}']").QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToArray();

    private static string? IconKind(IRenderedComponent<DevicesTab> cut, Guid device) =>
        cut.Find($"[data-device-row='{device}'] [data-device-type]").GetAttribute("data-device-type");

    private void SignIn(DevicesScope scope)
    {
        _handler.Scope = scope;
        _session.Set("session", _handler.OwnerAccount, _handler.OwnerProfile, _handler.CurrentSession, _handler.Authority(scope));
    }

    private sealed class DevicesClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    private enum DevicesScope
    {
        Member,
        HouseholdAdministrator,
        ServerAdministrator,
    }

    /// <summary>Plays the Engine: it decides what each kind of person gets back, so the page only chooses what to show.</summary>
    private sealed class DevicesHandler : HttpMessageHandler
    {
        public Guid HomeHousehold { get; } = Guid.NewGuid();
        public Guid CabinHousehold { get; } = Guid.NewGuid();
        public Guid OwnerAccount { get; } = Guid.NewGuid();
        public Guid AnnaAccount { get; } = Guid.NewGuid();
        public Guid MaryAccount { get; } = Guid.NewGuid();
        public Guid OwnerProfile { get; } = Guid.NewGuid();
        public Guid AnnaProfile { get; } = Guid.NewGuid();
        public Guid MaryProfile { get; } = Guid.NewGuid();
        public Guid BenProfile { get; } = Guid.NewGuid();
        public Guid CurrentSession { get; } = Guid.NewGuid();
        public Guid OtherSession { get; } = Guid.NewGuid();
        public Guid AnnaSession { get; } = Guid.NewGuid();
        public Guid MarySession { get; } = Guid.NewGuid();
        public Guid TvDevice { get; } = Guid.NewGuid();
        public Guid AnnaPhone { get; } = Guid.NewGuid();
        public Guid MaryPhone { get; } = Guid.NewGuid();
        public DevicesScope Scope { get; set; }
        public HttpStatusCode SessionsStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<string> Deleted { get; } = new();
        public ConcurrentQueue<string> Paths { get; } = new();
        public List<ManagedAccountSessionsResponse>? People { get; set; }
        public List<DeviceSessionResponse>? OwnSessions { get; set; }

        public DashboardAuthorityResponse Authority(DevicesScope scope) => scope switch
        {
            DevicesScope.ServerAdministrator => new(
                OwnerAccount, OwnerProfile, true, true, 1, 1, true, true, null, 1, [],
                ["settings.administration"], ["access.manage"]),
            DevicesScope.HouseholdAdministrator => new(
                OwnerAccount, OwnerProfile, true, true, 1, 1, false, false, null, 1, [],
                [], ["household.manage"]),
            _ => new(OwnerAccount, OwnerProfile, true, true, 1, 1, false, false, null, 1, [], [], []),
        };

        public DeviceSessionResponse ThisComputer() =>
            new() { Id = CurrentSession, AccountId = OwnerAccount, ActiveProfileId = OwnerProfile, DeviceName = "This computer", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow };

        public ManagedAccountSessionsResponse OwnerOnlyThisDevice() =>
            new(OwnerAccount, "owner@example.test", "owner@example.test", HomeHousehold, null, [ThisComputer()]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Enqueue(path);
                RemoveEnded(path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            Paths.Enqueue(path);
            var response = path switch
            {
                "/access/sessions" when SessionsStatus != HttpStatusCode.OK => new HttpResponseMessage(SessionsStatus),
                "/access/sessions" => Json(ManagedSessions()),
                "/auth/sessions" => Json(MemberSessions()),
                "/access/devices" => Json(Devices()),
                "/access/profiles" => Json(Profiles()),
                "/access/households" => Json(new[]
                {
                    new HouseholdResponse(HomeHousehold, "Home", 2, 2, DateTimeOffset.UtcNow),
                    new HouseholdResponse(CabinHousehold, "Cabin", 1, 1, DateTimeOffset.UtcNow),
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private void RemoveEnded(string path)
        {
            var idText = path[(path.LastIndexOf('/') + 1)..];
            if (!Guid.TryParse(idText, out var id))
            {
                return;
            }

            if (path.StartsWith("/access/sessions/", StringComparison.Ordinal))
            {
                People = ManagedSessions().Select(person => person with
                {
                    Sessions = person.Sessions.Where(session => session.Id != id).ToList(),
                }).Where(person => person.Sessions.Count > 0).ToList();
            }
            else if (path.StartsWith("/auth/sessions/", StringComparison.Ordinal))
            {
                OwnSessions = MemberSessions().Where(session => session.Id != id).ToList();
            }
        }

        private List<DeviceSessionResponse> MemberSessions() => OwnSessions ??=
        [
            ThisComputer(),
            new() { Id = OtherSession, AccountId = OwnerAccount, ActiveProfileId = OwnerProfile, DeviceName = "Living room TV", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2) },
        ];

        private List<ManagedAccountSessionsResponse> ManagedSessions()
        {
            if (People is not null)
            {
                return People;
            }

            var owner = new ManagedAccountSessionsResponse(OwnerAccount, "owner@example.test", "owner@example.test", HomeHousehold, null, MemberSessions());
            var anna = new ManagedAccountSessionsResponse(AnnaAccount, "anna@example.test", "anna@example.test", HomeHousehold, null,
            [
                new DeviceSessionResponse { Id = AnnaSession, AccountId = AnnaAccount, ActiveProfileId = AnnaProfile, DeviceName = "Anna's laptop", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-9) },
            ]);
            var mary = new ManagedAccountSessionsResponse(MaryAccount, "Mary", null, CabinHousehold, "Mary",
            [
                new DeviceSessionResponse { Id = MarySession, AccountId = MaryAccount, ActiveProfileId = MaryProfile, DeviceName = "Mary's phone", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
            ]);
            return Scope switch
            {
                DevicesScope.ServerAdministrator => [owner, anna, mary],
                _ => [owner, anna],
            };
        }

        private List<ManagedClientDeviceDto> Devices()
        {
            var tv = Device(TvDevice, "Living room TV", "television", "Tuvima for Roku", OwnerAccount, OwnerProfile, "Owner", HomeHousehold);
            var anna = Device(AnnaPhone, "Anna's phone", "mobile", "Tuvima for iOS", AnnaAccount, AnnaProfile, "Anna", HomeHousehold);
            var mary = Device(MaryPhone, "Mary's phone", "mobile", "Tuvima for Android", MaryAccount, MaryProfile, "Mary", CabinHousehold);
            return Scope switch
            {
                DevicesScope.Member => [tv],
                DevicesScope.HouseholdAdministrator => [tv, anna],
                _ => [tv, anna, mary],
            };
        }

        private List<ManagedProfileResponse> Profiles()
        {
            var owner = new ManagedProfileResponse(OwnerProfile, "Owner", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var anna = new ManagedProfileResponse(AnnaProfile, "Anna", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var ben = new ManagedProfileResponse(BenProfile, "Ben", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var mary = new ManagedProfileResponse(MaryProfile, "Mary", "#fff", null, DateTimeOffset.UtcNow, CabinHousehold);
            return Scope == DevicesScope.ServerAdministrator ? [owner, anna, ben, mary] : [owner, anna, ben];
        }

        private static ManagedClientDeviceDto Device(Guid id, string name, string platform, string client, Guid account, Guid profile, string profileName, Guid household) =>
            new()
            {
                Id = id,
                DeviceName = name,
                Platform = platform,
                ClientName = client,
                AccountId = account,
                AccountDisplayName = $"{profileName.ToLowerInvariant()}@example.test",
                ProfileId = profile,
                ProfileName = profileName,
                HouseholdId = household,
                PairedAt = DateTimeOffset.UtcNow.AddDays(-3),
                LastSeenAt = DateTimeOffset.UtcNow,
            };

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
