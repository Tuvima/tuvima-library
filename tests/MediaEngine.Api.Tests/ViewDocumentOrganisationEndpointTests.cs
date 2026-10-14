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
using MediaEngine.Domain.Models;
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
        builder.Services.AddSingleton<IDocumentBulkRepository, DocumentBulkRepository>();
        builder.Services.AddScoped<ViewDocumentBulkService>();
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
        builder.Services.AddSingleton<IViewSmartGalleryQueryService, ViewSmartGalleryQueryService>();
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
        var savedView = (await new ViewGalleryRepository(_database).CreateAsync(new CreateViewGalleryCommand(
            blocked.ProfileId, (await _spaces.GetByOwnerAsync(blocked.ProfileId))!.Id, "Old saved view",
            ViewGalleryKind.Smart, SmartRuleJson: DocumentViewRule()))).Id;
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
            // Bulk organising, the list export and saved views are closed to them as well.
            (HttpMethod.Post, "/view/documents/bulk", JsonContent.Create(new ViewDocumentBulkRequest([document], ViewDocumentBulkOperations.MoveToTrash))),
            (HttpMethod.Post, "/view/documents/bulk", JsonContent.Create(new ViewDocumentBulkRequest([document], ViewDocumentBulkOperations.AddTags, Tags: ["Sneaky"]))),
            (HttpMethod.Get, "/view/documents/export?scope=mine", null),
            (HttpMethod.Get, "/view/documents/export", null),
            (HttpMethod.Get, "/view/preferences/documents-sort", null),
            (HttpMethod.Put, "/view/preferences/documents-sort", JsonContent.Create(new ViewDocumentsSortDto("title"))),
            (HttpMethod.Put, $"/view/galleries/{savedView:D}/shares", JsonContent.Create(new ViewGallerySharesRequest([]))),
            (HttpMethod.Post, "/view/galleries", JsonContent.Create(new ViewGalleryRequest("Saved", ViewGalleryKind.Smart, SmartRuleJson: DocumentViewRule()))),
            (HttpMethod.Get, $"/view/galleries/{savedView:D}", null),
            (HttpMethod.Put, $"/view/galleries/{savedView:D}", JsonContent.Create(new ViewGalleryRequest("Renamed", ViewGalleryKind.Smart, SmartRuleJson: DocumentViewRule()))),
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

        // A saved Documents view they made before Documents was turned off is hidden from their Galleries too.
        var galleries = await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries");
        Assert.DoesNotContain(galleries!.Owned, gallery => gallery.Id == savedView);

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

    private static string DocumentViewRule(params (string Field, string Op, string Value)[] more) =>
        ViewSmartGalleryRules.Serialize(CollectionRuleDefinition.SingleGroup(
        [
            new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "document" },
            .. more.Select(item => new CollectionRulePredicate { Field = item.Field, Op = item.Op, Value = item.Value }),
        ]));

    [Fact]
    public async Task ASavedViewIsAPrivateShelfThatFiltersByTheNewDocumentFields()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        var invoice = await AddDocument(alice, 'a');
        var letter = await AddDocument(alice, 'b');
        var bobs = await AddDocument(bob, 'c');
        _actor.Current = alice.Authority;
        using (await _client.PutAsJsonAsync($"/view/items/{invoice:D}/document-details", new UpdateViewDocumentDetailsRequest(
                   Correspondent: "Acme", DocumentType: "Invoice", Tags: ["Tax"], DocumentDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)))))
        {
        }

        using (await _client.PutAsJsonAsync($"/view/items/{letter:D}/document-details", new UpdateViewDocumentDetailsRequest(
                   Correspondent: "Globex", DocumentType: "Letter")))
        {
        }

        var catalogue = await _client.GetFromJsonAsync<ViewDocumentCatalogueDto>("/view/catalogue?scope=mine&kind=document");
        var acme = catalogue!.Correspondents.Single(item => item.Name == "Acme").Id;
        var tax = catalogue.Tags.Single().Id;
        var rule = DocumentViewRule(("tags", "all", tax.ToString("D")), ("correspondent", "eq", acme.ToString("D")), ("document_date", "within", "last_30_days"));
        var view = await PostAsync<ViewGalleryDto>("/view/galleries", new ViewGalleryRequest("Recent tax invoices", ViewGalleryKind.Smart, SmartRuleJson: rule));
        Assert.True(ViewSmartGalleryRules.IsDocumentView(view.SmartRuleJson));
        // The shelf counts what it matches right now.
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries"))!.Owned.Single(gallery => gallery.Id == view.Id).ItemCount);

        var listed = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>($"/view/assets?scope=mine&kind=document&galleryId={view.Id:D}");
        Assert.Equal(invoice, Assert.Single(listed!.Items).Id);
        // The view and the page filters narrow each other.
        var narrowed = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>($"/view/assets?scope=mine&kind=document&galleryId={view.Id:D}&docTo=2000-01-01");
        Assert.Empty(narrowed!.Items);

        // Private by default: Bob neither sees it in his list nor can open it, and it never lists his documents.
        _actor.Current = bob.Authority;
        Assert.Empty((await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries"))!.Owned);
        using var peek = await _client.GetAsync($"/view/galleries/{view.Id:D}");
        Assert.NotEqual(HttpStatusCode.OK, peek.StatusCode);
        using var peekItems = await _client.GetAsync($"/view/assets?scope=mine&kind=document&galleryId={view.Id:D}");
        Assert.NotEqual(HttpStatusCode.OK, peekItems.StatusCode);
        Assert.DoesNotContain(bobs, listed.Items.Select(item => item.Id));

        // Rename and delete from the same place.
        _actor.Current = alice.Authority;
        using var renamed = await _client.PutAsJsonAsync($"/view/galleries/{view.Id:D}",
            new ViewGalleryRequest("Tax this month", ViewGalleryKind.Smart, SmartRuleJson: view.SmartRuleJson));
        Assert.Equal("Tax this month", (await renamed.Content.ReadFromJsonAsync<ViewGalleryDto>())!.Name);
        using var bad = await _client.PostAsJsonAsync("/view/galleries", new ViewGalleryRequest("Bad", ViewGalleryKind.Smart,
            SmartRuleJson: """{"version":1,"groups":[{"match_mode":"all","join_with_previous":"and","conditions":[{"field":"media_type","op":"eq","value":"document"},{"field":"document_date","op":"within","value":"last_century"}]}]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var deleted = await _client.DeleteAsync($"/view/galleries/{view.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.NotNull(_assets.Find(invoice));
    }

    [Fact]
    public async Task ABulkEditChangesOnlyWhatThePersonOwnsAndSaysPlainlyWhatItSkipped()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        var first = await AddDocument(alice, 'a');
        var second = await AddDocument(alice, 'b');
        var bobs = await AddDocument(bob, 'c');
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            alice.LibraryId, (await _spaces.GetByOwnerAsync(alice.ProfileId))!.Id, alice.ProfileId, LocalAssetMediaKinds.Image, "Photo", null,
            [new LocalAssetFileRegistration(@"C:\p.jpg", new string('e', 64), "1.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow)]))).ItemId;
        var missing = Guid.NewGuid();
        _actor.Current = alice.Authority;

        var result = await BulkAsync(new ViewDocumentBulkRequest([first, second, bobs, photo, missing],
            ViewDocumentBulkOperations.AddTags, Tags: ["Tax"]));
        Assert.Equal((5, 2, 3, 0), (result.Requested, result.Done, result.Skipped, result.Failed));
        Assert.Equal(ViewDocumentBulkStatuses.Done, result.Items.Single(item => item.ItemId == first).Status);
        Assert.Equal(ViewDocumentBulkService.NotAvailableReason, result.Items.Single(item => item.ItemId == bobs).Reason);
        Assert.Equal(ViewDocumentBulkService.NotAvailableReason, result.Items.Single(item => item.ItemId == missing).Reason);
        Assert.Contains("documents", result.Items.Single(item => item.ItemId == photo).Reason, StringComparison.Ordinal);
        Assert.Contains("Tax", _assets.Find(first)!.Tags);
        Assert.Empty(_assets.Find(bobs)!.Tags);

        // Trash, restore and a repeat all report themselves; the file is never touched.
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.MoveToTrash))).Done);
        Assert.NotNull(_assets.Find(first)!.TrashedAt);
        var again = await BulkAsync(new ViewDocumentBulkRequest([first], ViewDocumentBulkOperations.MoveToTrash));
        Assert.Equal((0, 1), (again.Done, again.Skipped));
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.Restore))).Done);
        Assert.Null(_assets.Find(first)!.TrashedAt);

        // Mark filed only touches documents waiting in the Inbox.
        var inboxBefore = (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count;
        Assert.Equal(2, inboxBefore);
        var filed = await BulkAsync(new ViewDocumentBulkRequest([first], ViewDocumentBulkOperations.MarkFiled));
        Assert.Equal(1, filed.Done);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewDocumentInboxSummaryDto>("/view/documents/inbox-summary?scope=mine"))!.Count);
        Assert.Equal(1, (await BulkAsync(new ViewDocumentBulkRequest([first], ViewDocumentBulkOperations.MarkFiled))).Skipped);

        // Set and clear, with the person recorded as the one who chose.
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.SetCorrespondent, Correspondent: "Acme"))).Done);
        Assert.Equal("Acme", _assets.Find(second)!.DocumentInfo!.Correspondent!.Name);
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.SetCorrespondent, Clear: true))).Done);
        Assert.Null(_assets.Find(second)!.DocumentInfo!.Correspondent);
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.SetDocumentDate, DocumentDate: new DateOnly(2026, 5, 1)))).Done);
        Assert.Equal(new DateOnly(2026, 5, 1), _assets.Find(first)!.DocumentInfo!.DocumentDate);
        var taxId = (await _client.GetFromJsonAsync<ViewDocumentCatalogueDto>("/view/catalogue?scope=mine&kind=document"))!.Tags.Single(tag => tag.Name == "Tax").Id;
        Assert.Equal(2, (await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.RemoveTags, TagIds: [taxId]))).Done);
        Assert.Empty(_assets.Find(first)!.Tags);
        Assert.Empty(_assets.Find(second)!.Tags);

        // Bob cannot reach Alice's documents however he asks, and Alice's are untouched.
        _actor.Current = bob.Authority;
        var bobAttempt = await BulkAsync(new ViewDocumentBulkRequest([first, second], ViewDocumentBulkOperations.MoveToTrash));
        Assert.Equal((0, 2), (bobAttempt.Done, bobAttempt.Skipped));
        Assert.Null(_assets.Find(first)!.TrashedAt);
    }

    [Fact]
    public async Task ASharedLibraryDocumentIsSkippedWithAClearReasonEvenForAnAdministrator()
    {
        var household = Guid.NewGuid();
        var admin = await CreatePerson("StandardUser", household, householdAdmin: true);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared folder', @sourceKey, 'managed',
                        'Shared/Folders/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, libraryId = shared.LibraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        var sharedDocument = (await _assets.UpsertAsync(new LocalAssetRegistration(
            shared.LibraryId, null, null, LocalAssetMediaKinds.Document, "Shared lease", null,
            [new LocalAssetFileRegistration(@"C:\shared\s.pdf", new string('f', 64), "s.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;
        var mine = await AddDocument(admin, 'a');
        _actor.Current = admin.Authority;

        var result = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument, mine], ViewDocumentBulkOperations.MarkFiled));
        Assert.Equal(ViewDocumentBulkService.SharedReason, result.Items.Single(item => item.ItemId == sharedDocument).Reason);
        Assert.Equal(ViewDocumentBulkStatuses.Done, result.Items.Single(item => item.ItemId == mine).Status);
        var tagged = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument], ViewDocumentBulkOperations.AddTags, Tags: ["Mine now"]));
        Assert.Equal((0, 1), (tagged.Done, tagged.Skipped));
        Assert.Empty(_assets.Find(sharedDocument)!.Tags);
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(sharedDocument)!.DocumentInfo!.FilingState);
    }

    [Fact]
    public async Task ABulkEditRefusesBadRequestsAndTooManyDocuments()
    {
        var alice = await CreatePerson("StandardUser");
        var document = await AddDocument(alice, 'a');
        _actor.Current = alice.Authority;

        async Task<HttpStatusCode> Send(ViewDocumentBulkRequest request)
        {
            using var response = await _client.PostAsJsonAsync("/view/documents/bulk", request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([document], "explode")));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([], ViewDocumentBulkOperations.MoveToTrash)));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([Guid.Empty], ViewDocumentBulkOperations.MoveToTrash)));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([document], ViewDocumentBulkOperations.AddTags)));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([document], ViewDocumentBulkOperations.SetCorrespondent)));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new([document], ViewDocumentBulkOperations.SetDocumentDate)));
        var many = Enumerable.Range(0, ViewDocumentBulkOperations.MaximumItems + 1).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new(many, ViewDocumentBulkOperations.MoveToTrash)));
        var exactly = Enumerable.Range(0, ViewDocumentBulkOperations.MaximumItems).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(HttpStatusCode.OK, await Send(new(exactly, ViewDocumentBulkOperations.MoveToTrash)));
        Assert.Null(_assets.Find(document)!.TrashedAt);
    }

    [Fact]
    public async Task TheCsvExportListsOnlyWhatThePersonCanSeeAndDefusesFormulas()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        var evil = await AddDocument(alice, 'a');
        await AddDocument(alice, 'b');
        var bobs = await AddDocument(bob, 'c');
        _actor.Current = alice.Authority;
        var field = await PostAsync<ViewDocumentFieldDto>("/view/document-fields", new ViewDocumentFieldRequest("Total", "money"));
        using (await _client.PutAsJsonAsync($"/view/items/{evil:D}/document-details", new UpdateViewDocumentDetailsRequest(
                   Title: "=HYPERLINK(\"http://evil\")", DocumentDate: new DateOnly(2026, 3, 1), Correspondent: "Smith, \"Bob\" & Co", DocumentType: "Invoice",
                   ArchiveNumber: "-1+2", Tags: ["Tax", "2026"], Notes: "secret words",
                   FieldValues: [new ViewDocumentFieldValueRequest(field.Id, TextValue: "eur", NumberValue: 12.5)])))
        {
        }

        using var response = await _client.GetAsync("/view/documents/export?scope=mine&docSort=title");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.Contains(ViewEndpoints.ExportTruncatedHeader));
        Assert.StartsWith("documents-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());
        var text = System.Text.Encoding.UTF8.GetString(bytes.Skip(3).ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Title,File name,Document date,Correspondent,Type,Tags,Archive number,Total,Added", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("'=HYPERLINK(\"\"http://evil\"\")", text, StringComparison.Ordinal);
        Assert.Contains("\"Smith, \"\"Bob\"\" & Co\"", text, StringComparison.Ordinal);
        Assert.Contains("'-1+2", text, StringComparison.Ordinal);
        Assert.Contains("2026-03-01", text, StringComparison.Ordinal);
        Assert.Contains("12.50 EUR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret words", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{bobs:D}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c.pdf", text, StringComparison.Ordinal);

        // A filter narrows the export exactly as it narrows the list.
        using var narrowed = await _client.GetAsync("/view/documents/export?scope=mine&docFrom=2026-01-01&docTo=2026-06-30");
        var narrowedLines = (await narrowed.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, narrowedLines.Length);
        using var badSort = await _client.GetAsync("/view/documents/export?scope=mine&docSort=chaos");
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
    }

    [Fact]
    public async Task TheDocumentsSortIsRememberedPerProfileAndOnlyKnownSortsAreAccepted()
    {
        var alice = await CreatePerson("StandardUser");
        var bob = await CreatePerson("StandardUser");
        _actor.Current = alice.Authority;
        Assert.Null((await _client.GetFromJsonAsync<ViewDocumentsSortDto>("/view/preferences/documents-sort"))!.Sort);

        using var saved = await _client.PutAsJsonAsync("/view/preferences/documents-sort", new ViewDocumentsSortDto("Title"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(ViewDocumentSorts.Title, (await _client.GetFromJsonAsync<ViewDocumentsSortDto>("/view/preferences/documents-sort"))!.Sort);
        using var bad = await _client.PutAsJsonAsync("/view/preferences/documents-sort", new ViewDocumentsSortDto("chaos"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        _actor.Current = bob.Authority;
        Assert.Null((await _client.GetFromJsonAsync<ViewDocumentsSortDto>("/view/preferences/documents-sort"))!.Sort);

        _actor.Current = alice.Authority;
        using var cleared = await _client.PutAsJsonAsync("/view/preferences/documents-sort", new ViewDocumentsSortDto(null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await _client.GetFromJsonAsync<ViewDocumentsSortDto>("/view/preferences/documents-sort"))!.Sort);
    }

    [Fact]
    public async Task ASortedListPagesWithoutRepeatingOrSkippingDocuments()
    {
        var alice = await CreatePerson("StandardUser");
        _actor.Current = alice.Authority;
        var ids = new List<Guid>();
        foreach (var seed in "abcde")
        {
            var id = await AddDocument(alice, seed);
            ids.Add(id);
            using (await _client.PutAsJsonAsync($"/view/items/{id:D}/document-details", new UpdateViewDocumentDetailsRequest(Title: $"Title {seed}")))
            {
            }
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>(
                $"/view/assets?scope=mine&kind=document&docSort=title&limit=2{(cursor is null ? string.Empty : "&cursor=" + cursor)}");
            seen.AddRange(page!.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(ids, seen);
    }

    private async Task<ViewDocumentBulkResultDto> BulkAsync(ViewDocumentBulkRequest request)
    {
        using var response = await _client.PostAsJsonAsync("/view/documents/bulk", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ViewDocumentBulkResultDto>())!;
    }

    private async Task<T> PostAsync<T>(string route, object body)
    {
        using var response = await _client.PostAsJsonAsync(route, body);
        Assert.True(response.IsSuccessStatusCode, $"POST {route} returned {(int)response.StatusCode}.");
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
