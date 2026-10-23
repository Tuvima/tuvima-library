using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Profiles &gt; Households: a server administrator adds, renames and removes households; the Engine's refusal is shown as it is.</summary>
public sealed class HouseholdsTabTests : AsyncBunitContext
{
    private const string PasswordSentence = "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.";
    private const string EngineRefusal = "This household still has people or sign-ins, so it can't be removed. Nothing was changed.";

    private static readonly Guid HomeId = Guid.NewGuid();
    private static readonly Guid CabinId = Guid.NewGuid();

    private readonly List<RecordedRequest> _requests = [];
    private readonly List<HouseholdResponse> _households = [];
    private HttpStatusCode _deleteStatus = HttpStatusCode.NoContent;
    private bool _passwordRequired;
    private string? _passwordCode = "secure_administrators_first";
    private string _passwordDetail = PasswordSentence;
    private bool _confirmRemoval = true;
    private int _confirmCount;

    public HouseholdsTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(new HouseholdHandler(this)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        var dialogs = DispatchProxy.Create<IAppDialogService, ConfirmSpy>();
        ((ConfirmSpy)(object)dialogs).Owner = this;
        Services.AddScoped(_ => dialogs);
        _households.AddRange([
            new HouseholdResponse(HomeId, "Home", 3, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(CabinId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
        ]);
    }

    private class ConfirmSpy : DispatchProxy
    {
        public HouseholdsTabTests? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "ShowMessageBoxAsync")
            {
                Owner!._confirmCount++;
                return Task.FromResult<bool?>(Owner._confirmRemoval);
            }

            return null;
        }
    }

    [Fact]
    public void ServerAdministrator_SeesEachHouseholdWithItsPeopleAndSignInCounts()
    {
        UseSession(serverAdministrator: true);

        var cut = Render<HouseholdsTab>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));
        var items = cut.FindAll(".households-tab__item");
        // Sorted by name.
        Assert.Contains("Cabin", items[0].TextContent);
        Assert.Contains("1 person · 1 sign-in", items[0].TextContent);
        Assert.Contains("Home", items[1].TextContent);
        Assert.Contains("3 people · 1 sign-in", items[1].TextContent);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Add household");
    }

    [Fact]
    public void NotAServerAdministrator_IsToldItIsNotAvailable_AndNothingIsRequested()
    {
        UseSession(serverAdministrator: false, householdAdministrator: true);

        var cut = Render<HouseholdsTab>();

        Assert.Contains("Not available", cut.Markup);
        Assert.Empty(cut.FindAll(".households-tab__item"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Add household");
        Assert.Empty(_requests);
    }

    [Fact]
    public void AddHousehold_PostsTheName_AndShowsTheNewHousehold()
    {
        UseSession(serverAdministrator: true);
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add household").Click();
        cut.Find("[data-households-add] input").Input("Lake house");
        cut.FindAll("[data-households-add] button").Single(button => button.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Contains("Lake house was added.", cut.Markup));
        var post = Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == "/access/households");
        Assert.Equal("Lake house", JsonSerializer.Deserialize<CreateHouseholdRequest>(post.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Name);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".households-tab__item").Count));
    }

    [Fact]
    public void AddQueryFlag_OpensTheNameBoxStraightAway()
    {
        UseSession(serverAdministrator: true);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/profiles/households?add=1");

        var cut = Render<HouseholdsTab>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-households-add]")));
    }

    [Fact]
    public void Rename_PutsTheNewName_AndShowsIt()
    {
        UseSession(serverAdministrator: true);
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.Find("button[aria-label='Rename Cabin']").Click();
        cut.Find(".households-tab__item input").Input("Lake cabin");
        cut.FindAll(".households-tab__item button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains("Renamed to Lake cabin.", cut.Markup));
        var put = Assert.Single(_requests, request => request.Method == HttpMethod.Put);
        Assert.Equal($"/access/households/{CabinId:D}", put.Path);
        Assert.Equal("Lake cabin", JsonSerializer.Deserialize<RenameHouseholdRequest>(put.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Name);
    }

    [Fact]
    public void Remove_AsksFirst_ThenDeletesTheHousehold()
    {
        UseSession(serverAdministrator: true);
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.Find("button[aria-label='Remove Cabin']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Cabin was removed.", cut.Markup));
        Assert.Equal(1, _confirmCount);
        Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/households/{CabinId:D}");
    }

    [Fact]
    public void Remove_WhenTheAdministratorChangesTheirMind_SendsNothing()
    {
        UseSession(serverAdministrator: true);
        _confirmRemoval = false;
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.Find("button[aria-label='Remove Home']").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _confirmCount));
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public void Remove_RefusedByTheEngine_ShowsItsSentenceAsItIs_AndKeepsTheHousehold()
    {
        UseSession(serverAdministrator: true);
        _deleteStatus = HttpStatusCode.Conflict;
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.Find("button[aria-label='Remove Home']").Click();

        cut.WaitForAssertion(() => Assert.Equal(EngineRefusal, cut.Find("[data-households-error]").TextContent));
        Assert.Equal(2, cut.FindAll(".households-tab__item").Count);
        Assert.DoesNotContain("was removed", cut.Markup);
    }

    [Fact]
    public void AddHousehold_WithoutAPassword_ShowsTheEnginesSentence_AndAButtonToSetOneInSecurity()
    {
        UseSession(serverAdministrator: true);
        _passwordRequired = true;
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));
        Assert.Empty(cut.FindAll("[data-households-set-password]"));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add household").Click();
        cut.Find("[data-households-add] input").Input("Lake house");
        cut.FindAll("[data-households-add] button").Single(button => button.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Equal(PasswordSentence, cut.Find("[data-households-error]").TextContent));
        Assert.Equal(2, cut.FindAll(".households-tab__item").Count);
        var setPassword = cut.Find("[data-households-set-password] button");
        Assert.Equal("Set an email and password", setPassword.TextContent.Trim());

        setPassword.Click();

        Assert.EndsWith("/settings/security", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AddHousehold_OffersTheSetPasswordButtonForTheEnginesCode_WhateverTheSentenceSays()
    {
        UseSession(serverAdministrator: true);
        _passwordRequired = true;
        _passwordDetail = "Give yourself a password before adding another household.";
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add household").Click();
        cut.Find("[data-households-add] input").Input("Lake house");
        cut.FindAll("[data-households-add] button").Single(button => button.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Equal(_passwordDetail, cut.Find("[data-households-error]").TextContent));
        Assert.Equal("Set an email and password", cut.Find("[data-households-set-password] button").TextContent.Trim());
    }

    [Fact]
    public void AddHousehold_RefusedWithTheSameWordsButNoCode_DoesNotOfferTheSetPasswordButton()
    {
        UseSession(serverAdministrator: true);
        _passwordRequired = true;
        _passwordCode = null;
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add household").Click();
        cut.Find("[data-households-add] input").Input("Lake house");
        cut.FindAll("[data-households-add] button").Single(button => button.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Equal(PasswordSentence, cut.Find("[data-households-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-households-set-password]"));
    }

    [Fact]
    public void OtherRefusals_DoNotOfferTheSetPasswordButton()
    {
        UseSession(serverAdministrator: true);
        _passwordRequired = false;
        _deleteStatus = HttpStatusCode.Conflict;
        var cut = Render<HouseholdsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".households-tab__item").Count));

        cut.Find("button[aria-label='Remove Home']").Click();

        cut.WaitForAssertion(() => Assert.Equal(EngineRefusal, cut.Find("[data-households-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-households-set-password]"));
    }

    private void UseSession(bool serverAdministrator, bool householdAdministrator = false)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        string[] actions = serverAdministrator ? ["access.manage"] : householdAdministrator ? ["household.manage"] : [];
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, serverAdministrator, serverAdministrator, null, 1, [],
            serverAdministrator ? ["settings.administration"] : [], actions));
        Services.AddScoped(_ => session);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class HouseholdHandler(HouseholdsTabTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add(new RecordedRequest(request.Method, path, body));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._households) };
            }

            if (request.Method == HttpMethod.Post && path == "/access/households" && owner._passwordRequired)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new { title = "Conflict.", status = 409, detail = owner._passwordDetail, code = owner._passwordCode }),
                };
            }

            if (request.Method == HttpMethod.Post && path == "/access/households")
            {
                var name = JsonSerializer.Deserialize<CreateHouseholdRequest>(body, options)!.Name;
                var created = new HouseholdResponse(Guid.NewGuid(), name, 0, 0, DateTimeOffset.UtcNow);
                owner._households.Add(created);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(created) };
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/access/households/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path["/access/households/".Length..]);
                var name = JsonSerializer.Deserialize<RenameHouseholdRequest>(body, options)!.Name;
                var index = owner._households.FindIndex(household => household.Id == id);
                owner._households[index] = owner._households[index] with { Name = name };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._households[index]) };
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/access/households/", StringComparison.Ordinal))
            {
                if (owner._deleteStatus == HttpStatusCode.Conflict)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(new { title = "Conflict.", status = 409, detail = EngineRefusal }),
                    };
                }

                var id = Guid.Parse(path["/access/households/".Length..]);
                owner._households.RemoveAll(household => household.Id == id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }
}
