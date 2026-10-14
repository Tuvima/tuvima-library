using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The real View routes, wired to a real data store and a signed-in person who can be swapped between calls. These
/// tests cover who may use the Documents routes and who may rename or delete a shared name.
/// </summary>
public sealed class ViewDocumentOrganisationEndpointTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-routes-{Guid.NewGuid():N}.db");
    private readonly string _thumbnailRoot = Path.Combine(Path.GetTempPath(), $"tuvima-view-thumbs-{Guid.NewGuid():N}");
    private readonly ActorBox _actor = new();
    private DatabaseConnection _database = null!;
    private LocalAssetRepository _assets = null!;
    private ViewPersonalSpaceRepository _spaces = null!;
    private ViewProfileRepository _policies = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _policies = new ViewProfileRepository(_database);
        Directory.CreateDirectory(_thumbnailRoot);

        (_app, _client) = await StartAsync(StandIns);
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(IReadOnlyCollection<Type> stubs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IAuthorizationHandler, AllowHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<ILocalAssetRepository>(_assets);
        builder.Services.AddSingleton<IViewCatalogueRepository, ViewCatalogueRepository>();
        builder.Services.AddSingleton<IDocumentFilingRepository, DocumentFilingRepository>();
        builder.Services.AddSingleton<IDocumentRuleRepository, DocumentRuleRepository>();
        builder.Services.AddSingleton<IMediaOperationRepository, MediaOperationRepository>();
        builder.Services.AddSingleton<DocumentRulesQueue>();
        builder.Services.AddSingleton<ViewDocumentRuleService>();
        builder.Services.AddSingleton<IViewProfileRepository>(_policies);
        builder.Services.AddSingleton<IViewPersonalSpaceRepository>(_spaces);
        builder.Services.AddSingleton<IViewGalleryRepository, ViewGalleryRepository>();
        builder.Services.AddSingleton<IViewSharedLibraryRepository, ViewSharedLibraryRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<IViewScopeStore, ViewScopePersistenceService>();
        builder.Services.AddSingleton<IViewScopeResolver, ViewScopeResolver>();
        builder.Services.AddSingleton<IViewResourceStore, ViewResourcePersistenceService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
        builder.Services.AddScoped<IViewResourceAuthorizationService, ViewResourceAuthorizationService>();
        builder.Services.AddSingleton<IViewAssetQueryBackend, ViewAssetQueryService>();
        builder.Services.AddScoped<IViewQueryOrchestrator, ViewQueryOrchestrator>();
        builder.Services.AddScoped<IViewRequestProfileContext, HttpViewRequestProfileContext>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new SwitchableAuthorityResolver(_actor));
        builder.Services.AddSingleton(new ViewThumbnailService(
            new AssetPathService(_thumbnailRoot), NullLogger<ViewThumbnailService>.Instance));
        builder.Services.AddSingleton((ViewStorageService)RuntimeHelpers.GetUninitializedObject(typeof(ViewStorageService)));
        builder.Services.AddScoped<ViewFolderService>();
        foreach (var stub in stubs)
        {
            builder.Services.AddSingleton(stub, stub.IsInterface
                ? typeof(DispatchProxy).GetMethods()
                    .Single(method => method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(stub, typeof(UnexpectedUse)).Invoke(null, null)!
                : RuntimeHelpers.GetUninitializedObject(stub));
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapViewEndpoints();
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return (app, new HttpClient { BaseAddress = address });
    }

    // The services other View routes name. Building any request builds every route, so each has to exist; these
    // stand-ins throw if a request this test makes ever reaches them.
    private static readonly Type[] StandIns =
    [
        typeof(IAccountAccessDecisionService),
        typeof(IConfigurationLoader),
        typeof(MediaEngine.Identity.Contracts.IProfileService),
        typeof(IViewOtherPeopleService),
        typeof(ViewDiscoveryService),
        typeof(ViewLibraryService),
        typeof(ViewSharedContributionService),
        typeof(ViewSharedSourceService),
        typeof(ViewSourceIndexingHostedService),
        typeof(MediaEngine.Api.Services.Settings.ServerFolderBrowserService),
    ];

    public class UnexpectedUse : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name} was not expected in this test.");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        if (Directory.Exists(_thumbnailRoot))
        {
            Directory.Delete(_thumbnailRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AProfileWithoutDocumentsIsRefusedOnEveryDocumentsRoute()
    {
        var blocked = await CreatePerson("RestrictedProfile");
        var document = await AddDocument(blocked, 'a');
        _actor.Current = blocked.Authority;
        var itemRoute = $"/view/items/{document:D}";
        var anyId = Guid.NewGuid().ToString("D");
        var body = JsonContent.Create(new UpdateViewDocumentDetailsRequest(Title: "x"));

        var refused = new (HttpMethod Method, string Route, HttpContent? Body)[]
        {
            (HttpMethod.Get, "/view/assets?scope=mine&kind=document", null),
            (HttpMethod.Get, $"{itemRoute}?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/content?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/thumbnail?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/preview?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/text?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/document-details?scope=mine", null),
            (HttpMethod.Put, $"{itemRoute}/document-details", body),
            (HttpMethod.Put, $"{itemRoute}/tags", JsonContent.Create(new UpdateLocalAssetTagsRequest(["Sneaky"]))),
            (HttpMethod.Get, "/view/catalogue", null),
            (HttpMethod.Get, "/view/catalogue?scope=mine&kind=document", null),
            (HttpMethod.Post, "/view/tags", JsonContent.Create(new ViewCatalogueEntryRequest("Tag"))),
            (HttpMethod.Put, $"/view/tags/{anyId}", JsonContent.Create(new ViewCatalogueEntryRequest("Tag"))),
            (HttpMethod.Delete, $"/view/tags/{anyId}", null),
            (HttpMethod.Post, "/view/correspondents", JsonContent.Create(new ViewCatalogueEntryRequest("Someone"))),
            (HttpMethod.Put, $"/view/correspondents/{anyId}", JsonContent.Create(new ViewCatalogueEntryRequest("Someone"))),
            (HttpMethod.Delete, $"/view/correspondents/{anyId}", null),
            (HttpMethod.Post, "/view/document-types", JsonContent.Create(new ViewCatalogueEntryRequest("Letter"))),
            (HttpMethod.Put, $"/view/document-types/{anyId}", JsonContent.Create(new ViewCatalogueEntryRequest("Letter"))),
            (HttpMethod.Delete, $"/view/document-types/{anyId}", null),
            (HttpMethod.Post, "/view/document-fields", JsonContent.Create(new ViewDocumentFieldRequest("Field", "text"))),
            (HttpMethod.Put, $"/view/document-fields/{anyId}", JsonContent.Create(new ViewDocumentFieldRequest("Field", "text"))),
            (HttpMethod.Delete, $"/view/document-fields/{anyId}", null),
            // The Inbox and the auto-filing rules are closed to the same profiles.
            (HttpMethod.Get, "/view/documents/inbox-summary?scope=mine", null),
            (HttpMethod.Get, "/view/documents/inbox-summary", null),
            (HttpMethod.Post, $"{itemRoute}/inbox/file", JsonContent.Create(new FileViewDocumentFromInboxRequest(AcceptSuggestions: true))),
            (HttpMethod.Get, "/view/document-rules", null),
            (HttpMethod.Post, "/view/document-rules", JsonContent.Create(new SaveViewDocumentRuleRequest("Rule"))),
            (HttpMethod.Put, $"/view/document-rules/{anyId}", JsonContent.Create(new SaveViewDocumentRuleRequest("Rule"))),
            (HttpMethod.Delete, $"/view/document-rules/{anyId}", null),
            (HttpMethod.Put, "/view/document-rules/order", JsonContent.Create(new ReorderViewDocumentRulesRequest([]))),
            (HttpMethod.Post, "/view/document-rules/test", JsonContent.Create(new SaveViewDocumentRuleRequest("Rule"))),
            (HttpMethod.Post, "/view/document-rules/apply", JsonContent.Create(new { })),
        };
        foreach (var (method, route, content) in refused)
        {
            using var request = new HttpRequestMessage(method, route) { Content = content };
            using var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {route} should be refused for a profile without Documents, but returned {(int)response.StatusCode}.");
        }

        // Folders still opens, but never lists a document.
        var folders = await _client.GetFromJsonAsync<ViewFolderPageDto>("/view/folders?scope=mine");
        Assert.NotNull(folders);
        Assert.DoesNotContain(folders!.Items, item => item.MediaKind == LocalAssetMediaKinds.Document);

        // Their photos and tag type-ahead keep working: only documents are closed off.
        using var suggestions = await _client.GetAsync("/view/tag-suggestions");
        Assert.Equal(HttpStatusCode.OK, suggestions.StatusCode);
        using var listing = await _client.GetAsync("/view/assets?scope=mine");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var page = await listing.Content.ReadFromJsonAsync<ViewAssetTimelinePageDto>();
        Assert.DoesNotContain(page!.Items, item => item.MediaKind == LocalAssetMediaKinds.Document);
    }

    [Fact]
    public async Task APersonWithDocumentsCanFileADocumentAndFindItByTheNewDetails()
    {
        var alice = await CreatePerson("StandardUser");
        var document = await AddDocument(alice, 'a');
        _actor.Current = alice.Authority;

        var field = await PostAsync<ViewDocumentFieldDto>("/view/document-fields", new ViewDocumentFieldRequest("Status", "choice", ["Open", "Closed"]));
        using var saved = await _client.PutAsJsonAsync($"/view/items/{document:D}/document-details", new UpdateViewDocumentDetailsRequest(
            Title: "Water bill", DocumentDate: new DateOnly(2026, 3, 1), Correspondent: "City Water", DocumentType: "Bill",
            Notes: "Pay by April", Tags: ["Home"], FieldValues: [new ViewDocumentFieldValueRequest(field.Id, TextValue: "open")]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var info = await saved.Content.ReadFromJsonAsync<LocalAssetDocumentInfoDto>();
        Assert.Equal("City Water", info!.Correspondent!.Name);
        Assert.Equal("Open", Assert.Single(info.FieldValues!).TextValue);

        var catalogue = await _client.GetFromJsonAsync<ViewDocumentCatalogueDto>("/view/catalogue?scope=mine&kind=document");
        var correspondent = Assert.Single(catalogue!.Correspondents);
        Assert.True(correspondent.CanManage);
        var tag = Assert.Single(catalogue.Tags);

        var byTag = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>(
            $"/view/assets?scope=mine&kind=document&tag={tag.Id:D}&correspondent={correspondent.Id:D}&docFrom=2026-01-01&docTo=2026-12-31");
        Assert.Equal(document, Assert.Single(byTag!.Items).Id);
        var none = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>(
            $"/view/assets?scope=mine&kind=document&tag={tag.Id:D}&docFrom=2027-01-01");
        Assert.Empty(none!.Items);
        var bySearch = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>("/view/assets?scope=mine&kind=document&q=water");
        Assert.Single(bySearch!.Items);
        using var badField = await _client.GetAsync("/view/assets?scope=mine&field=not-a-field");
        Assert.Equal(HttpStatusCode.BadRequest, badField.StatusCode);

        // A duplicate name, in any case, is refused with the reason.
        using var duplicate = await _client.PostAsJsonAsync("/view/correspondents", new ViewCatalogueEntryRequest("city water"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonManagesTheirOwnRulesTriesThemOnTheirOwnDocumentsAndQueuesThemAgain()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        var mine = await AddDocument(alice, 'a');
        var other = await AddDocument(alice, 'b');
        var bobs = await AddDocument(bob, 'c');
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                UPDATE local_items SET primary_file_name = CASE id WHEN @mine THEN 'invoice-water.pdf'
                                                                    WHEN @bobs THEN 'invoice-bobs.pdf' ELSE primary_file_name END
                 WHERE id IN (@mine, @bobs);
                """, new { mine, bobs });
        }

        _actor.Current = alice.Authority;
        var tag = await PostAsync<ViewTagDto>("/view/tags", new ViewCatalogueEntryRequest("Invoices"));
        var correspondent = await PostAsync<ViewCorrespondentDto>("/view/correspondents", new ViewCatalogueEntryRequest("Water Co"));
        var rule = new SaveViewDocumentRuleRequest("Invoices", true, "all", true, ViewDocumentRuleScopes.Mine,
            [new ViewDocumentRuleConditionDto(ViewDocumentRuleConditionKinds.FilenameContains, "INVOICE")],
            [new ViewDocumentRuleActionDto(ViewDocumentRuleActionKinds.AddTag, tag.Id),
             new ViewDocumentRuleActionDto(ViewDocumentRuleActionKinds.SetCorrespondent, correspondent.Id)]);

        // The try-out lists only Alice's matching document: never Bob's, even though Bob's file matches too.
        var tested = await PostAsync<ViewDocumentRuleTestDto>("/view/document-rules/test", rule);
        Assert.Equal(mine, Assert.Single(tested.Matches).ItemId);
        Assert.DoesNotContain(tested.Matches, match => match.ItemId == other || match.ItemId == bobs);
        Assert.False(tested.HasMore);

        var created = await PostAsync<ViewDocumentRuleDto>("/view/document-rules", rule);
        Assert.Equal(ViewDocumentRuleScopes.Mine, created.Scope);
        Assert.Equal("Invoices", created.Actions.Single(action => action.Kind == ViewDocumentRuleActionKinds.AddTag).DisplayName);
        var second = await PostAsync<ViewDocumentRuleDto>("/view/document-rules", rule with { Name = "Second" });
        var list = await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules");
        Assert.Equal([created.Id, second.Id], list!.Rules.Select(item => item.Id).ToArray());
        Assert.False(list.CanManageShared);

        using var reorder = await _client.PutAsJsonAsync("/view/document-rules/order", new ReorderViewDocumentRulesRequest([second.Id, created.Id]));
        Assert.Equal(HttpStatusCode.NoContent, reorder.StatusCode);
        Assert.Equal([second.Id, created.Id],
            (await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules"))!.Rules.Select(item => item.Id).ToArray());
        using var strangers = await _client.PutAsJsonAsync("/view/document-rules/order", new ReorderViewDocumentRulesRequest([Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.NotFound, strangers.StatusCode);

        using var renamed = await _client.PutAsJsonAsync($"/view/document-rules/{created.Id:D}", rule with { Name = "Renamed", Enabled = false });
        Assert.Equal("Renamed", (await renamed.Content.ReadFromJsonAsync<ViewDocumentRuleDto>())!.Name);

        // Applying queues Alice's documents only (two), once per click.
        var applied = await PostAsync<ViewDocumentRulesApplyResultDto>("/view/document-rules/apply", new { });
        Assert.Equal(2, applied.Queued);
        // Queueing runs in the background so the click returns at once; it finishes moments later.
        for (var attempt = 0; attempt < 100 && CountOperations(MediaOperationType.ViewDocumentApplyRules) < 2; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, CountOperations(MediaOperationType.ViewDocumentApplyRules));

        // Bob sees none of it and cannot touch it.
        _actor.Current = bob.Authority;
        Assert.Empty((await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules"))!.Rules);
        using var bobPut = await _client.PutAsJsonAsync($"/view/document-rules/{created.Id:D}", rule);
        Assert.Equal(HttpStatusCode.NotFound, bobPut.StatusCode);
        using var bobDelete = await _client.DeleteAsync($"/view/document-rules/{created.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, bobDelete.StatusCode);
        // Bob cannot point a rule at Alice's private names, however he found the ids.
        using var guessed = await _client.PostAsJsonAsync("/view/document-rules", rule);
        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);

        _actor.Current = alice.Authority;
        using var deleted = await _client.DeleteAsync($"/view/document-rules/{created.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task OnlyAHouseholdAdministratorMakesSharedLibraryRulesAndRulesAreCheckedBeforeTheyAreSaved()
    {
        var household = Guid.NewGuid();
        var member = await CreatePerson("StandardUser", household);
        var admin = await CreatePerson("StandardUser", household, householdAdmin: true);
        _actor.Current = member.Authority;
        var correspondent = await PostAsync<ViewCorrespondentDto>("/view/correspondents", new ViewCatalogueEntryRequest("Water Co"));
        var field = await PostAsync<ViewDocumentFieldDto>("/view/document-fields", new ViewDocumentFieldRequest("Cost", "money"));
        ViewDocumentRuleConditionDto[] conditions = [new(ViewDocumentRuleConditionKinds.TextContains, "water")];
        ViewDocumentRuleActionDto[] actions = [new(ViewDocumentRuleActionKinds.SetCorrespondent, correspondent.Id)];

        using var memberShared = await _client.PostAsJsonAsync("/view/document-rules",
            new SaveViewDocumentRuleRequest("Shared", Scope: ViewDocumentRuleScopes.Shared, Conditions: conditions, Actions: actions));
        Assert.Equal(HttpStatusCode.Forbidden, memberShared.StatusCode);

        // Bad rules are refused with a reason: no conditions, nothing to do, an unknown kind, a value that does not fit.
        foreach (var bad in new[]
                 {
                     new SaveViewDocumentRuleRequest("No conditions", Actions: actions),
                     new SaveViewDocumentRuleRequest("Does nothing", Conditions: conditions),
                     new SaveViewDocumentRuleRequest("", Conditions: conditions, Actions: actions),
                     new SaveViewDocumentRuleRequest("Odd kind", Conditions: [new("regex", ".*")], Actions: actions),
                     new SaveViewDocumentRuleRequest("Odd file kind", Conditions: [new(ViewDocumentRuleConditionKinds.FileKind, "video")], Actions: actions),
                     new SaveViewDocumentRuleRequest("Odd source", Conditions: [new(ViewDocumentRuleConditionKinds.Source, Guid.NewGuid().ToString("D"))], Actions: actions),
                     new SaveViewDocumentRuleRequest("Bad mode", MatchMode: "some", Conditions: conditions, Actions: actions),
                     new SaveViewDocumentRuleRequest("Bad amount", Conditions: conditions,
                         Actions: [new(ViewDocumentRuleActionKinds.SetField, field.Id, "twelve")]),
                     new SaveViewDocumentRuleRequest("Two correspondents", Conditions: conditions,
                         Actions: [.. actions, .. actions]),
                 })
        {
            using var refused = await _client.PostAsJsonAsync("/view/document-rules", bad);
            Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, $"'{bad.Name}' should be refused but returned {(int)refused.StatusCode}.");
        }

        using var fine = await _client.PostAsJsonAsync("/view/document-rules", new SaveViewDocumentRuleRequest(
            "Cost", Conditions: conditions, Actions: [new(ViewDocumentRuleActionKinds.SetField, field.Id, "12.50 eur")]));
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);

        _actor.Current = admin.Authority;
        var adminCorrespondent = await PostAsync<ViewCorrespondentDto>("/view/correspondents", new ViewCatalogueEntryRequest("Household Co"));
        var shared = await PostAsync<ViewDocumentRuleDto>("/view/document-rules", new SaveViewDocumentRuleRequest(
            "Shared rule", Scope: ViewDocumentRuleScopes.Shared, FilesAutomatically: true,
            Conditions: conditions, Actions: [new(ViewDocumentRuleActionKinds.SetCorrespondent, adminCorrespondent.Id)]));
        Assert.Equal(ViewDocumentRuleScopes.Shared, shared.Scope);
        var list = await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules");
        Assert.True(list!.CanManageShared);
        Assert.Equal(shared.Id, Assert.Single(list.Rules).Id);
        // The member cannot see the administrator's rule.
        _actor.Current = member.Authority;
        Assert.DoesNotContain(shared.Id, (await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules"))!.Rules.Select(item => item.Id));
    }

    [Fact]
    public async Task TheInboxCountsFilesAndRefusesOtherPeoplesDocuments()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        var first = await AddDocument(alice, 'a');
        var second = await AddDocument(alice, 'b');
        var bobs = await AddDocument(bob, 'c');
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                UPDATE doc_item_details SET suggestions_json = @json WHERE item_id = @first;
                """, new { first, json = """{"document_date":"2026-03-01","date_from_file":false,"correspondent":"City Water","document_type":"Bill","tags":["Home"]}""" });
        }

        _actor.Current = alice.Authority;
        Assert.Equal(2, (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count);
        var listed = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>("/view/assets?scope=mine&kind=document&filing=inbox");
        Assert.Equal(2, listed!.Items.Count);
        Assert.Equal("City Water", listed.Items.Single(item => item.Id == first).DocumentInfo!.Suggestions!.Correspondent);

        // Bob cannot file Alice's document, and a stranger's id looks like any other missing item.
        _actor.Current = bob.Authority;
        using var refused = await _client.PostAsJsonAsync($"/view/items/{first:D}/inbox/file", new FileViewDocumentFromInboxRequest(true));
        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(first)!.DocumentInfo!.FilingState);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count);
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(bobs)!.DocumentInfo!.FilingState);

        _actor.Current = alice.Authority;
        using var filed = await _client.PostAsJsonAsync($"/view/items/{first:D}/inbox/file", new FileViewDocumentFromInboxRequest(true));
        Assert.Equal(HttpStatusCode.OK, filed.StatusCode);
        var info = await filed.Content.ReadFromJsonAsync<LocalAssetDocumentInfoDto>();
        Assert.Equal(ViewDocumentFilingStates.Filed, info!.FilingState);
        Assert.Equal("City Water", info.Correspondent!.Name);
        Assert.Equal(new DateOnly(2026, 3, 1), info.DocumentDate);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count);

        using var plain = await _client.PostAsJsonAsync($"/view/items/{second:D}/inbox/file", new FileViewDocumentFromInboxRequest());
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(0, (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count);
    }

    [Fact]
    public async Task OnlyTheCreatorOrAnAdministratorMayRenameOrDeleteAName()
    {
        var household = Guid.NewGuid();
        var otherHousehold = Guid.NewGuid();
        var alice = await CreatePerson("StandardUser", household);
        var bob = await CreatePerson("StandardUser", household);
        var dave = await CreatePerson("StandardUser", household, householdAdmin: true);
        var carol = await CreatePerson("StandardUser", otherHousehold, householdAdmin: true);
        var admin = await CreatePerson("Administrator", otherHousehold, serverAdmin: true);
        foreach (var (person, seed) in new[] { (bob, 'b'), (dave, 'd'), (carol, 'c'), (admin, 'e') })
        {
            person.Document = await AddDocument(person, seed);
        }

        // Alice makes three names; the others use them on their own documents, which is how they come to see them.
        async Task<Guid> AliceMakes(string name)
        {
            _actor.Current = alice.Authority;
            return (await PostAsync<ViewTagDto>("/view/tags", new ViewCatalogueEntryRequest(name))).Id;
        }

        async Task Use(Person person, string name)
        {
            _actor.Current = person.Authority;
            using var response = await _client.PutAsJsonAsync($"/view/items/{person.Document:D}/tags", new UpdateLocalAssetTagsRequest([name]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        async Task<HttpStatusCode> Delete(Person person, Guid id)
        {
            _actor.Current = person.Authority;
            using var response = await _client.DeleteAsync($"/view/tags/{id:D}");
            return response.StatusCode;
        }

        var first = await AliceMakes("First");
        await Use(bob, "First");
        await Use(carol, "First");
        Assert.Equal(HttpStatusCode.Forbidden, await Delete(bob, first));
        Assert.Equal(HttpStatusCode.Forbidden, await Delete(carol, first));
        _actor.Current = bob.Authority;
        using (var rename = await _client.PutAsJsonAsync($"/view/tags/{first:D}", new ViewCatalogueEntryRequest("Hijacked")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, await Delete(alice, first));

        var second = await AliceMakes("Second");
        await Use(admin, "Second");
        Assert.Equal(HttpStatusCode.NoContent, await Delete(admin, second));

        var third = await AliceMakes("Third");
        await Use(dave, "Third");
        Assert.Equal(HttpStatusCode.NoContent, await Delete(dave, third));

        // Removing a name never removes the document that carried it.
        Assert.NotNull(_assets.Find(bob.Document));
        Assert.Empty(_assets.Find(bob.Document)!.Tags);

        // Someone who cannot see a name cannot touch it, and learns nothing by trying.
        var fourth = await AliceMakes("Fourth");
        Assert.Equal(HttpStatusCode.NotFound, await Delete(bob, fourth));
        _actor.Current = bob.Authority;
        using var hidden = await _client.GetAsync("/view/catalogue");
        var seen = await hidden.Content.ReadFromJsonAsync<ViewDocumentCatalogueDto>();
        Assert.DoesNotContain(seen!.Tags, tag => tag.Name == "Fourth");
        using var attach = await _client.PutAsJsonAsync($"/view/items/{bob.Document:D}/tags",
            new UpdateLocalAssetTagsRequest([], [fourth]));
        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);
    }

    private async Task<T> PostAsync<T>(string route, object body)
    {
        using var response = await _client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed class Person(Guid profileId, Guid libraryId, RequestAuthority authority)
    {
        public Guid ProfileId { get; } = profileId;
        public Guid LibraryId { get; } = libraryId;
        public RequestAuthority Authority { get; } = authority;
        public Guid Document { get; set; }
    }

    private async Task<Person> CreatePerson(
        string role, Guid? household = null, bool householdAdmin = false, bool serverAdmin = false)
    {
        var profileId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            if (household is { } householdId)
            {
                connection.Execute("""
                    INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@householdId, 'Home', @now);
                    """, new { householdId, now = DateTimeOffset.UtcNow });
            }

            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
                VALUES (@profileId, @name, '#7C4DFF', @role, @now, @household);
                """, new { profileId, name = $"Profile {profileId:N}", role, now = DateTimeOffset.UtcNow, household });
        }

        await _spaces.CreateAsync(profileId, libraryId);
        var authority = new RequestAuthority(
            PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: profileId, SessionId: Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true,
            AccountIsAdministrator: serverAdmin, GrantAdminEnabled: serverAdmin,
            ActiveProfileIsRestricted: role == "RestrictedProfile",
            AccountHouseholdId: household, AccountIsHouseholdAdmin: householdAdmin);
        return new Person(profileId, libraryId, authority);
    }

    private int CountOperations(string operationType)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_operations WHERE operation_type = @operationType;", new { operationType });
    }

    private async Task<Guid> AddDocument(Person person, char seed)
    {
        var space = await _spaces.GetByOwnerAsync(person.ProfileId);
        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space!.Id, person.ProfileId, LocalAssetMediaKinds.Document, $"Document {seed}", null,
            [new LocalAssetFileRegistration($@"C:\docs\{seed}.pdf", new string(seed, 64), $"{seed}.pdf", "application/pdf",
                1024, DateTimeOffset.UtcNow)]))).ItemId;
    }

    private sealed class ActorBox
    {
        public RequestAuthority Current { get; set; } = new(PrincipalKind.Anonymous, false);
    }

    private sealed class SwitchableAuthorityResolver(ActorBox actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(actor.Current);
    }

    private sealed class AllowEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
            AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "view-routes-test")], Scheme.Name)),
                Scheme.Name)));
    }
}
