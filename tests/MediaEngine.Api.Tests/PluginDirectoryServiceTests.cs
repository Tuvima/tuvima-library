using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plugin directory (Developer tools spec D-F): a saved local copy, a last-checked time, Check now, an off switch, and
/// a request only when an administrator opens Available plugins on an old copy or presses Check now.
/// </summary>
public sealed class PluginDirectoryServiceTests : IDisposable
{
    private const string GitHubUrl = "https://raw.githubusercontent.com/Tuvima/tuvima-library/main/docs/reference/approved-plugins.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-directory-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly TuvimaDataPaths _paths;
    private readonly StepClock _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly SpyHandler _handler = new();

    public PluginDirectoryServiceTests()
    {
        var configRoot = Path.Combine(_root, "config");
        _loader = new ConfigurationDirectoryLoader(configRoot, new ConfigFolderSecretsStore(configRoot));
        _paths = new TuvimaDataPaths(Path.Combine(_root, "data"));
        SaveSettings(enabled: true);
    }

    [Fact]
    public async Task OpeningAvailablePlugins_WithNoSavedCopy_FetchesTheListAndWritesTheSavedCopy()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One", trust: "verified")));
        var service = NewService();

        var directory = await service.GetAsync();

        Assert.Equal(1, _handler.Requests);
        Assert.Equal("fresh", directory.Status);
        Assert.Equal(_clock.GetUtcNow(), directory.LastCheckedAt);
        Assert.Equal(_clock.GetUtcNow(), directory.LastSuccessAt);
        Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
        Assert.True(File.Exists(service.SavedCopyPath));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(service.SavedCopyPath));
        Assert.Equal(GitHubUrl, saved.RootElement.GetProperty("source_url").GetString());
        Assert.Equal("acme.one", saved.RootElement.GetProperty("list").GetProperty("plugins")[0].GetProperty("id").GetString());
        Assert.False(File.Exists(service.SavedCopyPath + ".tmp"));
    }

    [Fact]
    public async Task TheRequest_IsAPlainGetWithNoPersonOrLibraryDataInIt()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));

        await NewService().GetAsync(installedIds: ["acme.one"]);

        var request = Assert.Single(_handler.Seen);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubUrl, request.RequestUri!.ToString());
        Assert.Null(request.Content);
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(request.Headers, header => header.Key is "Cookie" or "Referer" or "X-Tuvima-Library");
    }

    [Fact]
    public async Task AListThatCameBackFromARedirectAwayFromGitHub_IsNotAccepted()
    {
        _handler.AnswerAfterRedirectTo("https://evil.example.net/approved-plugins.json", ListJson(Entry("acme.one", "One")));

        var directory = await NewService().GetAsync();

        Assert.Equal("unavailable", directory.Status);
        Assert.Empty(directory.Plugins);
    }

    [Fact]
    public async Task AListThatCameBackFromARedirectWithinGitHub_IsAccepted()
    {
        _handler.AnswerAfterRedirectTo("https://raw.githubusercontent.com/Tuvima/tuvima-library/main/other.json", ListJson(Entry("acme.one", "One")));

        var directory = await NewService().GetAsync();

        Assert.Equal("fresh", directory.Status);
        Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
    }

    [Fact]
    public async Task ReadingTheDirectory_AsASignedInAdministrator_MayCheckGitHub()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var administrator = new RequestAuthority(
            PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(), SessionId: Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true);

        var directory = await PluginEndpoints.ReadDirectoryAsync(
            new DefaultHttpContext(), new FixedAuthorityResolver(administrator), NewService(), [], CancellationToken.None);

        Assert.Equal(1, _handler.Requests);
        Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
    }

    [Fact]
    public async Task ReadingTheDirectory_AsAnOutsideApplication_NeverMakesARequest_AndShowsTheSavedCopyOnly()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();
        _handler.Reset();
        _clock.Advance(TimeSpan.FromDays(3)); // the copy is old enough that an administrator's read would check again
        var application = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);

        var directory = await PluginEndpoints.ReadDirectoryAsync(
            new DefaultHttpContext(), new FixedAuthorityResolver(application), service, [], CancellationToken.None);

        Assert.Equal(0, _handler.Requests);
        Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
    }

    [Fact]
    public async Task WhenTheEngineIsOffline_TheSavedCopyIsShownWithItsDate()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();
        var copyTime = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromDays(3));
        _handler.Throw(new HttpRequestException("No route to host."));
        var directory = await service.CheckNowAsync();

        Assert.Equal("saved_copy", directory.Status);
        Assert.Equal(copyTime, directory.LastSuccessAt);
        Assert.Equal(_clock.GetUtcNow(), directory.LastCheckedAt);
        Assert.Equal("Couldn't reach the plugin directory; showing the copy from 20 October 2026.", directory.Message);
        Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
    }

    [Fact]
    public async Task WhenTheEngineIsOfflineAndThereIsNoCopy_ItSaysSoPlainly()
    {
        _handler.Throw(new HttpRequestException("No route to host."));

        var directory = await NewService().GetAsync();

        Assert.Equal("unavailable", directory.Status);
        Assert.Empty(directory.Plugins);
        Assert.Contains("no saved copy", directory.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheSwitchIsOff_NoRequestIsMade_AndTheSavedCopyIsStillShown()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();
        var copyTime = _clock.GetUtcNow();
        _handler.Reset();
        SaveSettings(enabled: false);
        _clock.Advance(TimeSpan.FromDays(10));

        var opened = await service.GetAsync();
        var checkedNow = await service.CheckNowAsync();

        Assert.Equal(0, _handler.Requests);
        foreach (var directory in new[] { opened, checkedNow })
        {
            Assert.Equal("disabled", directory.Status);
            Assert.False(directory.Enabled);
            Assert.Equal("Plugin directory is switched off.", directory.Message);
            Assert.Equal(copyTime, directory.LastSuccessAt);
            Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
        }
    }

    [Fact]
    public async Task WhenTheSwitchIsOffAndThereIsNoCopy_NoRequestIsMadeAndTheListIsEmpty()
    {
        SaveSettings(enabled: false);

        var directory = await NewService().GetAsync();

        Assert.Equal(0, _handler.Requests);
        Assert.Equal("disabled", directory.Status);
        Assert.Empty(directory.Plugins);
        Assert.False(File.Exists(NewService().SavedCopyPath));
    }

    [Fact]
    public async Task ACopyChecked_LessThanADayAgo_IsShownWithoutAnotherRequest()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();

        _clock.Advance(TimeSpan.FromHours(23));
        await service.GetAsync();
        await service.GetAsync();

        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task ACopyChecked_MoreThanADayAgo_IsCheckedAgainWhenAvailablePluginsIsOpened()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();

        _clock.Advance(TimeSpan.FromHours(25));
        _handler.Answer(ListJson(Entry("acme.one", "One"), Entry("acme.two", "Two")));
        var directory = await service.GetAsync();

        Assert.Equal(2, _handler.Requests);
        Assert.Equal(["acme.one", "acme.two"], directory.Plugins.Select(plugin => plugin.Id));
        Assert.Equal(_clock.GetUtcNow(), directory.LastSuccessAt);
    }

    [Fact]
    public async Task CheckNow_AsksAgainEvenWhenTheCopyIsFresh()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();

        await service.CheckNowAsync();

        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task AfterAFailedCheckWithACopy_TheNextVisitWaitsADayBeforeTryingAgain()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();
        _clock.Advance(TimeSpan.FromHours(30));
        _handler.Throw(new HttpRequestException("Offline."));
        await service.GetAsync();
        Assert.Equal(2, _handler.Requests);

        _clock.Advance(TimeSpan.FromHours(2));
        var directory = await service.GetAsync();

        Assert.Equal(2, _handler.Requests);
        Assert.Equal("saved_copy", directory.Status);
    }

    [Fact]
    public async Task AFailedFirstCheck_IsNotRetriedOnEveryVisit_ButIsAfterAFewMinutes()
    {
        _handler.Throw(new HttpRequestException("Offline."));
        var service = NewService();
        await service.GetAsync();
        await service.GetAsync();
        Assert.Equal(1, _handler.Requests);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await service.GetAsync();

        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task ABadAnswer_NeverReplacesTheSavedCopy()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();

        foreach (var bad in new[] { "not json at all", "{\"schema_version\":\"1.0\",\"plugins\":[]}", "{\"schema_version\":\"2.0\"}", "[]" })
        {
            _handler.Answer(bad);
            var directory = await service.CheckNowAsync();
            Assert.Equal("saved_copy", directory.Status);
            Assert.Equal("acme.one", Assert.Single(directory.Plugins).Id);
        }

        _handler.Respond(HttpStatusCode.NotFound, "");
        Assert.Equal("saved_copy", (await service.CheckNowAsync()).Status);
    }

    [Fact]
    public async Task AnAddressThatIsNotGitHub_IsRefusedWithoutARequest()
    {
        SaveSettings(enabled: true, url: "https://example.com/plugins.json");

        var directory = await NewService().GetAsync();

        Assert.Equal(0, _handler.Requests);
        Assert.Equal("invalid_source", directory.Status);
    }

    [Fact]
    public async Task AMalformedEntry_IsSkippedAndCounted_AndNeverFailsTheList()
    {
        _handler.Answer("""
            {
              "schema_version": "2.0",
              "updated_at": "2026-10-01T00:00:00Z",
              "plugins": [
                { "id": "acme.good", "name": "Good", "kind": "declarative", "trust_level": "verified" },
                "just a string",
                { "id": "", "name": "No id", "kind": "declarative" },
                { "id": "acme.noname", "kind": "declarative" },
                { "id": "acme.nokind", "name": "No kind" },
                { "id": "acme.oddkind", "name": "Odd kind", "kind": "dotnet-assembly" },
                { "id": "acme.good", "name": "Duplicate", "kind": "declarative" },
                { "id": "has space", "name": "Spaces", "kind": "declarative" },
                { "id": "acme.fine", "name": "Fine", "kind": "service", "permissions": ["network.http", 7, null], "package_url": "http://insecure.example/x.zip", "sha256": "not-a-checksum" }
              ]
            }
            """);

        var directory = await NewService().GetAsync();

        Assert.Equal(["acme.fine", "acme.good"], directory.Plugins.Select(plugin => plugin.Id));
        Assert.Equal(7, directory.SkippedCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), directory.UpdatedAt);
        var fine = directory.Plugins.Single(plugin => plugin.Id == "acme.fine");
        Assert.Equal(["network.http"], fine.Permissions);
        Assert.Equal(["Connects to the internet"], fine.AllowedTo);
        Assert.Null(fine.PackageUrl);
        Assert.Null(fine.Sha256);
    }

    [Fact]
    public async Task AnEntryThatDeclaresWhereItConnects_ShowsOneLinePerHost_AndPreviewsThem()
    {
        _handler.Answer("""
            {
              "schema_version": "2.0",
              "plugins": [
                { "id": "acme.lore", "name": "Lore", "kind": "service", "permissions": ["network.http", "media.read"],
                  "network": [
                    { "host": "www.wikidata.org", "sends": "Item IDs of Universes", "max_requests_per_minute": 60 },
                    { "host": "*.fandom.com", "sends": "Requests for a wiki's page list" }
                  ] }
              ]
            }
            """);
        var service = NewService();

        var entry = Assert.Single((await service.GetAsync()).Plugins);
        var preview = await service.PreviewInstallAsync("acme.lore");

        Assert.Equal(["www.wikidata.org", "*.fandom.com"], entry.Network.Select(destination => destination.Host));
        Assert.Equal("Requests for a wiki's page list", entry.Network[1].Sends);
        Assert.Equal(
            ["Contacts www.wikidata.org: Item IDs of Universes", "Contacts *.fandom.com: Requests for a wiki's page list", "Reads your media files"],
            entry.AllowedTo);
        Assert.DoesNotContain("Connects to the internet", preview.CanDo);
        Assert.Contains("Contacts *.fandom.com: Requests for a wiki's page list", preview.CanDo);
        Assert.Equal("www.wikidata.org, *.fandom.com", preview.SendsDataTo);
    }

    [Theory]
    [InlineData("""{ "id": "acme.bad", "name": "Bad", "kind": "service", "permissions": ["network.http"], "network": [ { "host": "https://api.example.com/x", "sends": "Titles" } ] }""")]
    [InlineData("""{ "id": "acme.bad", "name": "Bad", "kind": "service", "permissions": ["network.http"], "network": [ { "host": "*", "sends": "Titles" } ] }""")]
    [InlineData("""{ "id": "acme.bad", "name": "Bad", "kind": "service", "permissions": ["network.http"], "network": [ { "host": "api.example.com", "sends": "" } ] }""")]
    [InlineData("""{ "id": "acme.bad", "name": "Bad", "kind": "service", "permissions": ["network.http"], "network": "api.example.com" }""")]
    [InlineData("""{ "id": "acme.bad", "name": "Bad", "kind": "service", "permissions": ["media.read"], "network": [ { "host": "api.example.com", "sends": "Titles" } ] }""")]
    public async Task AnEntryWithAnUnreadableOrUnpermittedNetworkList_IsSkipped_NotShownAsSmaller(string entryJson)
    {
        _handler.Answer($$"""{ "schema_version": "2.0", "plugins": [ {{entryJson}} ] }""");

        var directory = await NewService().GetAsync();

        Assert.Empty(directory.Plugins);
        Assert.Equal(1, directory.SkippedCount);
    }

    [Theory]
    [InlineData("verified", "verified")]
    [InlineData("official", "official")]
    [InlineData("community", "community")]
    [InlineData("Verified ", "verified")]
    [InlineData("trusted-by-me", "community")]
    [InlineData("", "community")]
    [InlineData(null, "community")]
    public async Task AnUnknownOrMissingTrustLevel_IsCommunityAndNotVerified(string? trust, string expected)
    {
        var trustField = trust is null ? "" : $", \"trust_level\": \"{trust}\"";
        _handler.Answer($$"""{ "schema_version": "2.0", "plugins": [ { "id": "acme.one", "name": "One", "kind": "declarative"{{trustField}} } ] }""");

        var entry = Assert.Single((await NewService().GetAsync()).Plugins);

        Assert.Equal(expected, entry.TrustLevel);
        Assert.Equal(expected == "community", entry.TrustWarning is not null);
    }

    [Fact]
    public async Task PluginsNotSeenBefore_AreNew_UntilTheyAreMarkedSeen()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One"), Entry("acme.two", "Two")));
        var service = NewService();

        var first = await service.GetAsync();
        Assert.Equal(2, first.NewCount);
        Assert.All(first.Plugins, plugin => Assert.True(plugin.IsNew));

        var seen = await service.MarkSeenAsync();
        Assert.Equal(0, seen.NewCount);
        Assert.All(seen.Plugins, plugin => Assert.False(plugin.IsNew));

        _clock.Advance(TimeSpan.FromDays(2));
        _handler.Answer(ListJson(Entry("acme.one", "One"), Entry("acme.two", "Two"), Entry("acme.three", "Three")));
        var later = await service.GetAsync();

        Assert.Equal(1, later.NewCount);
        Assert.Equal(["acme.three"], later.Plugins.Where(plugin => plugin.IsNew).Select(plugin => plugin.Id));
    }

    [Fact]
    public async Task APluginAlreadyInstalled_IsMarkedInstalled_AndIsNotCountedAsAvailable()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One"), Entry("acme.two", "Two")));

        var directory = await NewService().GetAsync(installedIds: ["ACME.ONE"]);

        Assert.True(directory.Plugins.Single(plugin => plugin.Id == "acme.one").IsInstalled);
        Assert.False(directory.Plugins.Single(plugin => plugin.Id == "acme.two").IsInstalled);
        Assert.Equal(1, directory.NewCount);
    }

    [Fact]
    public async Task InstallingIsReportedAsNotSupportedYet()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));

        Assert.False((await NewService().GetAsync()).InstallSupported);
        Assert.False(PluginDirectoryService.InstallSupported);
    }

    [Fact]
    public async Task TheSavedCopy_SurvivesARestart()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        await NewService().GetAsync();
        _handler.Reset();

        var afterRestart = await NewService().GetAsync();

        Assert.Equal(0, _handler.Requests);
        Assert.Equal("acme.one", Assert.Single(afterRestart.Plugins).Id);
    }

    [Fact]
    public async Task ACopyKeptFromAnotherAddress_IsNotShownForTheCurrentOne()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        await NewService().GetAsync();
        SaveSettings(enabled: false, url: "https://raw.githubusercontent.com/Someone/else/main/list.json");

        var directory = await NewService().GetAsync();

        Assert.Empty(directory.Plugins);
        Assert.Null(directory.LastSuccessAt);
    }

    [Fact]
    public void SetEnabled_ChangesOnlyTheSwitch()
    {
        var before = _loader.LoadCore();
        before.ServerName = "Keep this name";
        _loader.SaveCore(before);

        NewService().SetEnabled(false);

        var after = _loader.LoadCore();
        Assert.False(after.PluginCatalog.Enabled);
        Assert.Equal(GitHubUrl, after.PluginCatalog.ApprovedPluginsUrl);
        Assert.Equal("Keep this name", after.ServerName);
    }

    [Fact]
    public async Task TheInstallPreview_ForAnUnverifiedPlugin_CarriesTheWarningTheKindAndThePermissions()
    {
        _handler.Answer($$"""
            { "schema_version": "2.0", "plugins": [
              { "id": "acme.sketchy", "name": "Sketchy", "kind": "service", "permissions": ["network.http", "media.read"] },
              { "id": "acme.checked", "name": "Checked", "kind": "declarative", "trust_level": "verified", "permissions": ["storage.plugin"] }
            ] }
            """);
        var service = NewService();
        await service.GetAsync();

        var unverified = await service.PreviewInstallAsync("ACME.sketchy");
        var verified = await service.PreviewInstallAsync("acme.checked");

        Assert.Equal(ApprovalKinds.Plugin, unverified.Kind);
        Assert.Equal("acme.sketchy", unverified.ItemId);
        Assert.Equal("Sketchy", unverified.Title);
        Assert.Contains("Tuvima hasn't checked this plugin. Only install plugins from people you trust.", unverified.Notes);
        Assert.Contains("This plugin runs as a separate program and talks to the Library through its published contract. Only turn on plugins you trust.", unverified.Notes);
        Assert.DoesNotContain(unverified.Notes, note => note.Contains("without a sandbox", StringComparison.Ordinal));
        Assert.Contains("Connects to the internet", unverified.CanDo);
        Assert.Contains("Reads your media files", unverified.CanDo);
        Assert.Contains("Runs as: separate program", unverified.CanDo);
        Assert.True(unverified.SendsDataOut);
        Assert.Equal("the internet", unverified.SendsDataTo);
        Assert.NotEmpty(unverified.StatementHash);

        Assert.DoesNotContain(verified.Notes, note => note.Contains("hasn't checked", StringComparison.Ordinal));
        Assert.Contains("Verified", verified.Notes);
        Assert.False(verified.SendsDataOut);
        Assert.NotEqual(unverified.StatementHash, verified.StatementHash);
    }

    [Fact]
    public async Task TheInstallPreview_ForAPluginNotInTheList_IsNotFound_AndMakesNoRequest()
    {
        var service = NewService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.PreviewInstallAsync("acme.unknown"));

        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task TheInstallPreview_WorksFromTheSavedCopy_EvenWithTheSwitchOff()
    {
        _handler.Answer(ListJson(Entry("acme.one", "One")));
        var service = NewService();
        await service.GetAsync();
        _handler.Reset();
        SaveSettings(enabled: false);

        var preview = await service.PreviewInstallAsync("acme.one");

        Assert.Equal("One", preview.Title);
        Assert.Equal(0, _handler.Requests);
    }

    private PluginDirectoryService NewService() => new(
        new SingleClientFactory(_handler), _loader, _paths, NullLogger<PluginDirectoryService>.Instance, _clock);

    private void SaveSettings(bool enabled, string url = GitHubUrl)
    {
        var core = _loader.LoadCore();
        core.PluginCatalog.Enabled = enabled;
        core.PluginCatalog.ApprovedPluginsUrl = url;
        _loader.SaveCore(core);
    }

    private static string Entry(string id, string name, string trust = "community") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "author": "Acme", "description": "Does a thing.", "version": "1.0.0", "kind": "declarative", "trust_level": "{{trust}}", "permissions": [], "capabilities": ["metadata"], "minimum_library_version": "1.0.0" }""";

    private static string ListJson(params string[] entries) =>
        $$"""{ "schema_version": "2.0", "updated_at": "2026-10-20T00:00:00Z", "plugins": [ {{string.Join(", ", entries)}} ] }""";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Counts every request that would have left the machine and answers with what the test says.</summary>
    private sealed class SpyHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _answer = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        public int Requests { get; private set; }

        public List<HttpRequestMessage> Seen { get; } = [];

        public void Answer(string body) => Respond(HttpStatusCode.OK, body);

        /// <summary>Answers as if the request was redirected on to <paramref name="finalAddress"/> before this body came back.</summary>
        public void AnswerAfterRedirectTo(string finalAddress, string body) =>
            _answer = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalAddress),
            };

        public void Respond(HttpStatusCode status, string body) =>
            _answer = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Throw(Exception exception) => _answer = () => throw exception;

        public void Reset()
        {
            Requests = 0;
            Seen.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Seen.Add(request);
            return Task.FromResult(_answer());
        }
    }
}
