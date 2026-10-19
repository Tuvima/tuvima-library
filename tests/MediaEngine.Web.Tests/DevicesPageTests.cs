using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Administration &gt; Devices: who can open it, where the old address goes, and what it shows.</summary>
public sealed class DevicesNavTests
{
    [Fact]
    public void Devices_IsAnAdministrationPageAfterNetwork_WithTheAppsAlias()
    {
        var item = SettingsNav.GetItem(SettingsSection.Devices);
        var server = SettingsNav.MenuGroups.Single(group => group.Key == "server");

        Assert.Equal("devices", item.Slug);
        Assert.Equal("Devices", item.Label);
        Assert.True(item.AdminOnly);
        Assert.True(item.HouseholdAdminAccess);
        Assert.Equal(SettingsMobileAvailability.Full, item.MobileAvailability);
        Assert.True(SettingsNav.MeetsLaunchContract(SettingsSection.Devices));
        Assert.Contains("apps", item.Aliases);
        // Devices stays in Server for now (it moves to Security in WP6), after the other Server pages.
        Assert.Equal(SettingsSection.Devices, server.Nodes[^1].Section);
        Assert.Equal("/settings/devices", SettingsNav.RouteFor(SettingsSection.Devices));
        Assert.DoesNotContain("apps", SettingsNav.GetSubsections(SettingsSection.Network).Select(subsection => subsection.Slug));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Devices_IsVisibleToBothKindsOfAdministratorAndNobodyElse(bool server, bool household, bool expected)
    {
        Assert.Equal(expected, SettingsNav.IsVisible(SettingsSection.Devices, server, household));
        var resolution = SettingsNav.ResolveRoute("devices", server, household);
        Assert.Equal(expected, resolution.RequestedSectionAllowed);
        if (!expected)
        {
            Assert.True(resolution.ShouldRedirect);
        }
    }

    [Fact]
    public void HouseholdAdministrator_SeesOnlyDevicesInAdministration()
    {
        var groups = SettingsNav.FilteredMenuGroups(false, true);

        Assert.Equal(["personal", "server"], groups.Select(group => group.Key));
        var server = groups.Single(group => group.Key == "server");
        Assert.Equal(["Devices"], SettingsNav.Flatten(server.Nodes).Select(node => node.Label));
        Assert.False(SettingsNav.IsVisible(SettingsSection.Network, false, true));
        Assert.False(SettingsNav.IsVisible(SettingsSection.Access, false, true));
        Assert.False(SettingsNav.ResolveRoute("access", false, true).RequestedSectionAllowed);
    }

    [Fact]
    public void MemberWithoutAdministration_NeverSeesTheAdministrationGroup()
    {
        Assert.Equal(["personal"], SettingsNav.FilteredMenuGroups(false).Select(group => group.Key));
    }

    [Theory]
    [InlineData("apps")]
    [InlineData("Apps")]
    public void TheOldAppsAddress_ResolvesToDevices_AndRedirects(string alias)
    {
        var resolution = SettingsNav.ResolveRoute(alias, true);

        Assert.Equal(SettingsSection.Devices, resolution.Section);
        Assert.Equal("/settings/devices", resolution.CanonicalRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void TheOldNetworkAppsPage_RedirectsToDevices()
    {
        Assert.Equal("/settings/devices", SettingsNav.LegacyRedirect("network", "apps"));
        Assert.Equal("/settings/devices", SettingsNav.LegacyRedirect("Network", "Apps"));
        Assert.Null(SettingsNav.LegacyRedirect("network", "remote"));
        Assert.Null(SettingsNav.LegacyRedirect("devices", null));
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
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler("GetNetworkSettingsAsync", _ => Task.FromResult<NetworkSettingsDto?>(new NetworkSettingsDto()))));
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
    }

    [Fact]
    public void ServerAdministrator_SeesAppsPairedDevicesAndSessionsGroupedByPerson()
    {
        SignIn(_handler.ServerAdminAuthority());
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-devices-panel='apps']");
        cut.WaitForElement("[data-session-person]");
        Assert.Contains("Paired devices", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Living room TV", cut.Markup, StringComparison.Ordinal);

        var people = cut.FindAll("[data-session-person]");
        Assert.Equal(2, people.Count);
        var owner = people.Single(person => person.GetAttribute("data-session-person") == _handler.OwnerAccount.ToString());
        Assert.Contains("owner@example.test", owner.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, owner.QuerySelectorAll("[data-session-row]").Length);
        Assert.Single(owner.QuerySelectorAll("[data-session-tag='this-device']"));

        var mary = people.Single(person => person.GetAttribute("data-session-person") == _handler.MaryAccount.ToString());
        Assert.Contains("Mary", mary.TextContent, StringComparison.Ordinal);
        Assert.Contains("No email", mary.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdAdministrator_SeesNoAppsPanel_ButPairedDevicesAndSessions()
    {
        SignIn(_handler.HouseholdAdminAuthority());
        var cut = Render<DevicesTab>();

        cut.WaitForElement("[data-session-person]");
        Assert.Empty(cut.FindAll("[data-devices-panel='apps']"));
        Assert.Contains("Paired devices", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Paths, path => path.StartsWith("/settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignOut_AsksFirst_ThenEndsOnlyThatSession_AndThisDeviceHasNoSignOut()
    {
        SignIn(_handler.ServerAdminAuthority());
        var dialogs = Render<AppDialogHost>();
        var cut = Render<DevicesTab>();
        cut.WaitForElement("[data-session-person]");

        var thisDevice = cut.Find($"[data-session-row='{_handler.CurrentSession}']");
        Assert.DoesNotContain("Sign out", thisDevice.TextContent, StringComparison.Ordinal);

        var tv = cut.Find($"[data-session-row='{_handler.OtherSession}']");
        await cut.InvokeAsync(() => tv.QuerySelector("button")!.Click());
        Assert.Empty(_handler.Revoked);
        cut.WaitForAssertion(() => Assert.Contains("Sign this device out?", cut.Markup + dialogs.Markup, StringComparison.Ordinal));

        var confirm = cut.FindAll("button").Concat(dialogs.FindAll("button"))
            .Last(button => button.TextContent.Trim() == "Sign out");
        await cut.InvokeAsync(() => confirm.Click());

        cut.WaitForAssertion(() => Assert.Equal([_handler.OtherSession], _handler.Revoked.ToArray()));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"[data-session-row='{_handler.OtherSession}']")));
    }

    [Fact]
    public void WhenOnlyThisDeviceIsSignedIn_ShowsTheEmptyState()
    {
        _handler.People = [_handler.OwnerOnlyThisDevice()];
        SignIn(_handler.ServerAdminAuthority());
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Nobody else is signed in", cut.Markup, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-session-person]"));
    }

    [Fact]
    public void WhenTheEngineRefusesBecauseOfTheAdministratorPin_AsksForIt()
    {
        _handler.SessionsStatus = HttpStatusCode.Forbidden;
        SignIn(_handler.HouseholdAdminAuthority());
        var cut = Render<DevicesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Enter your administrator PIN", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-session-person]"));
    }

    private void SignIn(DashboardAuthorityResponse authority) =>
        _session.Set("session", _handler.OwnerAccount, _handler.Profile, _handler.CurrentSession, authority);

    private sealed class DevicesClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    private sealed class DevicesHandler : HttpMessageHandler
    {
        public Guid OwnerAccount { get; } = Guid.NewGuid();
        public Guid MaryAccount { get; } = Guid.NewGuid();
        public Guid Profile { get; } = Guid.NewGuid();
        public Guid CurrentSession { get; } = Guid.NewGuid();
        public Guid OtherSession { get; } = Guid.NewGuid();
        public Guid MarySession { get; } = Guid.NewGuid();
        public HttpStatusCode SessionsStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<Guid> Revoked { get; } = new();
        public ConcurrentQueue<string> Paths { get; } = new();
        public List<ManagedAccountSessionsResponse>? People { get; set; }

        public DashboardAuthorityResponse ServerAdminAuthority() => new(
            OwnerAccount, Profile, true, true, 1, 1, true, true, null, 1, [],
            ["settings.administration"], ["access.manage"]);

        public DashboardAuthorityResponse HouseholdAdminAuthority() => new(
            OwnerAccount, Profile, true, true, 1, 1, false, false, null, 1, [],
            [], ["household.manage"]);

        public ManagedAccountSessionsResponse OwnerOnlyThisDevice() => new(
            OwnerAccount, "owner@example.test", "owner@example.test", null, null,
            [new DeviceSessionResponse { Id = CurrentSession, AccountId = OwnerAccount, DeviceName = "This computer", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow }]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            if (request.Method == HttpMethod.Delete && path.StartsWith("/access/sessions/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path["/access/sessions/".Length..]);
                Revoked.Enqueue(id);
                People = Current().Select(person => person with
                {
                    Sessions = person.Sessions.Where(session => session.Id != id).ToList(),
                }).Where(person => person.Sessions.Count > 0).ToList();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var response = path switch
            {
                "/access/sessions" when SessionsStatus != HttpStatusCode.OK => new HttpResponseMessage(SessionsStatus),
                "/access/sessions" => Json(Current()),
                "/access/devices" => Json(new[]
                {
                    new ManagedClientDeviceDto
                    {
                        Id = Guid.NewGuid(), DeviceName = "Living room TV", Platform = "television", ClientName = "Tuvima TV",
                        AccountId = OwnerAccount, AccountDisplayName = "owner@example.test", ProfileId = Profile, ProfileName = "Owner",
                        PairedAt = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAt = DateTimeOffset.UtcNow,
                    },
                }),
                "/access/profiles" => Json(Array.Empty<ManagedProfileResponse>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private List<ManagedAccountSessionsResponse> Current() => People ??=
        [
            new(OwnerAccount, "owner@example.test", "owner@example.test", null, null,
            [
                new DeviceSessionResponse { Id = CurrentSession, AccountId = OwnerAccount, DeviceName = "This computer", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow },
                new DeviceSessionResponse { Id = OtherSession, AccountId = OwnerAccount, DeviceName = "Living room TV", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2) },
            ]),
            new(MaryAccount, "Mary", null, null, "Mary",
            [
                new DeviceSessionResponse { Id = MarySession, AccountId = MaryAccount, DeviceName = "Mary's phone", Client = "Test", LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
            ]),
        ];

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
