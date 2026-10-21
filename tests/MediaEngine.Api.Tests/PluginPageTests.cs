using System.Text.Json;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins.Pages;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginPageTests : IDisposable
{
    private const string Id = "test.pages";

    private static readonly string[] Permissions = [PluginPermissionIds.PluginPages, PluginPermissionIds.PluginRecords, PluginPermissionIds.NetworkHttp];

    private const string Pages = """
        [
          { "key": "channels", "title": "Channels", "type": "list", "audience": "everyone", "menu": { "label": "Channels" },
            "collection": "channels", "fields": { "title": "name" }, "columns": [ { "key": "number", "label": "Number" } ] },
          { "key": "guide", "title": "Guide", "type": "guide", "audience": "adults", "menu": { "label": "Guide" },
            "collection": "guide", "fields": { "group": "channel", "title": "show" } },
          { "key": "tiles", "title": "Tiles", "type": "grid", "audience": "everyone", "collection": "tiles" },
          { "key": "secret", "title": "Secret", "type": "list", "collection": "channels", "menu": { "label": "Secret" } },
          { "key": "request", "title": "Request", "type": "form", "audience": "everyone", "menu": { "label": "Request" },
            "form": { "submit_label": "Send", "fields": [
              { "key": "title", "label": "Title", "kind": "text", "required": true },
              { "key": "copies", "label": "Copies", "kind": "number" },
              { "key": "urgent", "label": "Urgent", "kind": "yes_no" } ] } },
          { "key": "about", "title": "About", "type": "detail", "audience": "everyone", "collection": "info", "columns": [ { "key": "version", "label": "Version" } ] }
        ]
        """;

    private readonly HostServicesHarness _host;
    private readonly PluginPageService _pages;

    private static readonly PluginPageViewer Admin = new(true, false);
    private static readonly PluginPageViewer Adult = new(false, false);
    private static readonly PluginPageViewer Child = new(false, true);

    public PluginPageTests() : this(null)
    {
    }

    private PluginPageTests(IReadOnlyList<string>? permissions)
    {
        var plugin = new HostServicesTestPlugin(
            Id,
            permissions ?? Permissions,
            "Pages plugin",
            [new PluginNetworkDestination { Host = "images.example.com", Sends = "Asks for pictures" }],
            Pages);
        _host = new HostServicesHarness(null, plugin);
        _pages = new PluginPageService(_host.Catalog, _host.Contexts, NullLogger<PluginPageService>.Instance, _host.Clock);
        _host.Enable(Id);
    }

    [Fact]
    public void Menu_ListsOnlyPagesWithAMenuEntry_ThatTheViewersAudienceAllows()
    {
        Assert.Equal(["Channels", "Guide", "Secret", "Request"], _pages.Menu(Admin).Entries.Select(e => e.Label));
        Assert.Equal(["Channels", "Guide", "Request"], _pages.Menu(Adult).Entries.Select(e => e.Label));
        Assert.Equal(["Channels", "Request"], _pages.Menu(Child).Entries.Select(e => e.Label));
    }

    [Fact]
    public void GuideTimeline_IsNotOfferedToPhones_ButListsAreAndFormsAre()
    {
        var entries = _pages.Menu(Admin).Entries.ToDictionary(e => e.PageKey);

        Assert.False(entries["guide"].PhoneSupported);
        Assert.True(entries["channels"].PhoneSupported);
        Assert.True(entries["request"].PhoneSupported);
    }

    [Fact]
    public async Task APageTheViewerMayNotSee_AnswersLikeAPageThatDoesNotExist()
    {
        Assert.NotNull(await _pages.GetAsync(Admin, Id, "secret", default));
        Assert.Null(await _pages.GetAsync(Adult, Id, "secret", default));
        Assert.Null(await _pages.GetAsync(Child, Id, "guide", default));
        Assert.Null(await _pages.GetAsync(Admin, Id, "missing", default));
        Assert.Null(await _pages.GetAsync(Admin, "no.such.plugin", "channels", default));
    }

    [Fact]
    public async Task APluginThatIsSwitchedOff_ShowsNoMenuEntriesAndNoPages()
    {
        _host.Disable(Id);

        Assert.Empty(_pages.Menu(Admin).Entries);
        Assert.Null(await _pages.GetAsync(Admin, Id, "channels", default));
    }

    [Fact]
    public async Task APluginWithoutThePagesPermission_ShowsNothing()
    {
        using var other = new PluginPageTests([PluginPermissionIds.PluginRecords, PluginPermissionIds.NetworkHttp]);

        Assert.Empty(other._pages.Menu(Admin).Entries);
        Assert.Null(await other._pages.GetAsync(Admin, Id, "channels", default));
    }

    [Fact]
    public async Task ListPage_ShowsTheRecordsOfThePlugin_WithDeclaredColumns()
    {
        var records = _host.RecordStoreFor(Id);
        await records.PutAsync("channels", "b", """{"name":"Beta","number":2,"extra":"hidden"}""");
        await records.PutAsync("channels", "a", """{"name":"Alpha","number":1}""");

        var page = (await _pages.GetAsync(Adult, Id, "channels", default))!;

        Assert.Equal("list", page.PageType);
        Assert.Equal(["Alpha", "Beta"], page.Items.Select(i => i.Title));
        Assert.Equal("1", page.Items[0].Fields["number"]);
        Assert.DoesNotContain("extra", page.Items[1].Fields.Keys);
    }

    [Fact]
    public async Task GuidePage_OrdersByChannelThenStart_AndDropsItemsWithoutAValidTimeRange()
    {
        var records = _host.RecordStoreFor(Id);
        await records.PutAsync("guide", "1", """{"channel":"News","show":"Late","starts_at":"2026-10-21T20:00:00Z","ends_at":"2026-10-21T21:00:00Z"}""");
        await records.PutAsync("guide", "2", """{"channel":"News","show":"Early","starts_at":"2026-10-21T18:00:00Z","ends_at":"2026-10-21T19:00:00Z"}""");
        await records.PutAsync("guide", "3", """{"channel":"Films","show":"Backwards","starts_at":"2026-10-21T22:00:00Z","ends_at":"2026-10-21T21:00:00Z"}""");
        await records.PutAsync("guide", "4", """{"channel":"Films","show":"No end","starts_at":"2026-10-21T22:00:00Z"}""");

        var page = (await _pages.GetAsync(Adult, Id, "guide", default))!;

        Assert.Equal(["Early", "Late"], page.Items.Select(i => i.Title));
        Assert.All(page.Items, item => Assert.Equal("News", item.Group));
    }

    [Fact]
    public async Task Pictures_AreOnlyShownFromHttpsHostsThePluginDeclared()
    {
        var records = _host.RecordStoreFor(Id);
        await records.PutAsync("tiles", "ok", """{"title":"Ok","image_url":"https://images.example.com/a.jpg"}""");
        await records.PutAsync("tiles", "other", """{"title":"Other","image_url":"https://tracker.example.net/a.jpg"}""");
        await records.PutAsync("tiles", "plain", """{"title":"Plain","image_url":"http://images.example.com/a.jpg"}""");

        var page = (await _pages.GetAsync(Adult, Id, "tiles", default))!;

        Assert.Equal("https://images.example.com/a.jpg", page.Items.Single(i => i.Title == "Ok").ImageUrl);
        Assert.Null(page.Items.Single(i => i.Title == "Other").ImageUrl);
        Assert.Null(page.Items.Single(i => i.Title == "Plain").ImageUrl);
    }

    [Fact]
    public async Task DetailPage_ShowsOneRecord()
    {
        await _host.RecordStoreFor(Id).PutAsync("info", "main", """{"title":"Pages plugin","version":"1.4"}""");

        var page = (await _pages.GetAsync(Adult, Id, "about", default))!;

        Assert.Equal("1.4", Assert.Single(page.Items).Fields["version"]);
    }

    [Fact]
    public async Task Form_SavesATypedRecord_AndRefusesWhatDoesNotFit()
    {
        var saved = await _pages.SubmitAsync(Adult, Id, "request", new Dictionary<string, string> { ["title"] = " Dune ", ["copies"] = "2", ["urgent"] = "true", ["stranger"] = "x" }, default);
        Assert.Equal(PluginPageSubmitOutcome.Saved, saved.Outcome);

        var stored = await _host.RecordStoreFor(Id).GetAsync("form-request", saved.RecordKey!);
        using var json = JsonDocument.Parse(stored!.Json);
        Assert.Equal("Dune", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("copies").GetDouble());
        Assert.True(json.RootElement.GetProperty("urgent").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("stranger", out _));

        Assert.Equal(PluginPageSubmitOutcome.Invalid, (await _pages.SubmitAsync(Adult, Id, "request", new Dictionary<string, string>(), default)).Outcome);
        Assert.Equal(PluginPageSubmitOutcome.Invalid, (await _pages.SubmitAsync(Adult, Id, "request", new Dictionary<string, string> { ["title"] = "x", ["copies"] = "many" }, default)).Outcome);
        Assert.Equal(PluginPageSubmitOutcome.NotFound, (await _pages.SubmitAsync(Adult, Id, "channels", new Dictionary<string, string>(), default)).Outcome);
    }

    [Theory]
    [InlineData("""[{ "key": "Bad Key", "title": "T", "type": "list", "collection": "c" }]""")]
    [InlineData("""[{ "key": "a", "title": "T", "type": "carousel", "collection": "c" }]""")]
    [InlineData("""[{ "key": "a", "title": "T", "type": "list" }]""")]
    [InlineData("""[{ "key": "a", "title": "T", "type": "list", "collection": "c", "audience": "nobody" }]""")]
    [InlineData("""[{ "key": "a", "title": "T", "type": "form", "form": { "fields": [] } }]""")]
    [InlineData("""[{ "key": "a", "title": "T", "type": "list", "collection": "c" }, { "key": "a", "title": "U", "type": "list", "collection": "c" }]""")]
    public void ADeclarationThatCannotBeUnderstood_IsLeftOutAndReported(string pagesJson)
    {
        var manifest = new PluginManifest { Id = "x", Name = "X", Pages = JsonDocument.Parse(pagesJson).RootElement.Clone() };

        var (pages, problems) = PluginPageDeclarations.Read(manifest);

        Assert.True(pages.Count <= 1);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void APageDefaultsToAdministratorsOnly()
    {
        var manifest = new PluginManifest { Id = "x", Name = "X", Pages = JsonDocument.Parse("""[{ "key": "a", "title": "T", "type": "list", "collection": "c" }]""").RootElement.Clone() };

        Assert.Equal(PluginPageAudience.Administrators, PluginPageDeclarations.Read(manifest).Pages.Single().Audience);
    }

    [Fact]
    public void WideningAPagesAudience_ChangesTheApprovalFingerprint_SoItIsAskedAgain()
    {
        PluginManifest With(string audience) => new()
        {
            Id = "x",
            Name = "X",
            Permissions = [PluginPermissionIds.PluginPages, PluginPermissionIds.PluginRecords],
            Pages = JsonDocument.Parse($$"""[{ "key": "a", "title": "Secrets", "type": "list", "collection": "c", "audience": "{{audience}}" }]""").RootElement.Clone(),
        };

        var admins = ApprovalStatementBuilder.ForPlugin(With("administrators"), PluginTrustLevel.Private);
        var everyone = ApprovalStatementBuilder.ForPlugin(With("everyone"), PluginTrustLevel.Private);

        Assert.NotEqual(ApprovalStatementBuilder.Hash(admins), ApprovalStatementBuilder.Hash(everyone));
        Assert.Contains("Shows the page 'Secrets' to everyone, including child profiles", everyone.CanDo);
    }

    [Fact]
    public async Task AFormKeepsOnlyAFixedNumberOfSubmissions()
    {
        var records = _host.RecordStoreFor(Id);
        for (var i = 0; i < PluginPageService.MaxSubmissionsPerForm; i++)
        {
            await records.PutAsync("form-request", $"submission-{i:D5}", """{"title":"x"}""");
        }

        var result = await _pages.SubmitAsync(Adult, Id, "request", new Dictionary<string, string> { ["title"] = "one more" }, default);

        Assert.Equal(PluginPageSubmitOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task APictureAddressWithALoginInIt_IsNotShown()
    {
        await _host.RecordStoreFor(Id).PutAsync("tiles", "a", """{"title":"A","image_url":"https://user:pw@images.example.com/a.jpg"}""");

        var page = (await _pages.GetAsync(Adult, Id, "tiles", default))!;

        Assert.Null(page.Items.Single().ImageUrl);
    }

    [Fact]
    public void AFormAlwaysSavesIntoItsOwnCollection()
    {
        var manifest = new PluginManifest
        {
            Id = "x",
            Name = "X",
            Pages = JsonDocument.Parse("""[{ "key": "f", "title": "F", "type": "form", "collection": "settings", "form": { "fields": [ { "key": "a", "label": "A" } ] } }]""").RootElement.Clone(),
        };

        Assert.Equal("form-f", PluginPageDeclarations.Read(manifest).Pages.Single().Collection);
    }

    public void Dispose() => _host.Dispose();
}
