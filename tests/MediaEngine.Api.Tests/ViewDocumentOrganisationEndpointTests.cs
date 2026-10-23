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
using MediaEngine.Domain.Configuration;
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
public sealed partial class ViewDocumentOrganisationEndpointTests : IAsyncLifetime
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

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(
        IReadOnlyCollection<Type> stubs, Action<IServiceCollection>? configure = null)
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
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(_ => new AllowEvaluator(() => DenyViewAccess));
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

        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapViewEndpoints();
        app.MapViewDiscoveryEndpoints();
        app.MapViewClientEndpoints();
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
        typeof(ViewTrashDeletionPlanner),
        typeof(ViewTrashDeletionService),
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
            (HttpMethod.Get, "/view/assets/timeline-index?scope=mine&kind=document", null),
            (HttpMethod.Get, $"{itemRoute}?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/content?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/thumbnail?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/preview?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/text?scope=mine", null),
            (HttpMethod.Get, $"{itemRoute}/document-details?scope=mine", null),
            (HttpMethod.Put, $"{itemRoute}/document-details", body),
            (HttpMethod.Put, $"{itemRoute}/tags", JsonContent.Create(new UpdateLocalAssetTagsRequest(["Sneaky"]))),
            // Every other action that answers with the item (and so its notes, fields and suggestions) is closed too.
            (HttpMethod.Put, $"{itemRoute}/description", JsonContent.Create(new UpdateLocalAssetDescriptionRequest("Sneaky"))),
            (HttpMethod.Put, $"{itemRoute}/captured-at", JsonContent.Create(new UpdateLocalAssetCapturedAtRequest(DateTimeOffset.UtcNow))),
            (HttpMethod.Put, $"{itemRoute}/people", JsonContent.Create(new UpdateLocalAssetPeopleRequest([]))),
            (HttpMethod.Put, $"{itemRoute}/location", JsonContent.Create(new UpdateLocalAssetLocationRequest(1, 1))),
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
            // A single document is answered like an id nobody has (404) so its existence is never confirmed; everything
            // that is not about one item (and the document-details save, which is refused as a whole feature) is 403.
            var expected = route.StartsWith(itemRoute, StringComparison.Ordinal)
                && !route.Contains("/inbox/", StringComparison.Ordinal)
                && !(method == HttpMethod.Put && route.EndsWith("/document-details", StringComparison.Ordinal))
                ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
            Assert.True(response.StatusCode == expected,
                $"{method} {route} should be {(int)expected} for a profile without Documents, but returned {(int)response.StatusCode}.");
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
    public async Task ASharedLibraryDocumentIsSkippedInBulkWithAClearReasonExceptMarkFiledForAnAdministrator()
    {
        var household = Guid.NewGuid();
        var admin = await CreatePerson("StandardUser", household, householdAdmin: true);
        var member = await CreatePerson("StandardUser", household);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var sharedDocument = await AddSharedDocument(shared.LibraryId, "Shared lease", 'f');
        var mine = await AddDocument(admin, 'a');

        // Another household member cannot file it, however they ask.
        _actor.Current = member.Authority;
        var memberAttempt = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument], ViewDocumentBulkOperations.MarkFiled));
        Assert.Equal(ViewDocumentBulkService.SharedFilingReason, Assert.Single(memberAttempt.Items).Reason);
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(sharedDocument)!.DocumentInfo!.FilingState);

        // An administrator still cannot edit it in bulk, but can mark it filed to empty the Shared Inbox.
        _actor.Current = admin.Authority;
        var tagged = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument], ViewDocumentBulkOperations.AddTags, Tags: ["Mine now"]));
        Assert.Equal((0, 1), (tagged.Done, tagged.Skipped));
        Assert.Equal(ViewDocumentBulkService.SharedReason, Assert.Single(tagged.Items).Reason);
        Assert.Empty(_assets.Find(sharedDocument)!.Tags);
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(sharedDocument)!.DocumentInfo!.FilingState);

        var result = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument, mine], ViewDocumentBulkOperations.MarkFiled));
        Assert.Equal(ViewDocumentBulkStatuses.Done, result.Items.Single(item => item.ItemId == sharedDocument).Status);
        Assert.Equal(ViewDocumentBulkStatuses.Done, result.Items.Single(item => item.ItemId == mine).Status);
        Assert.Equal(ViewDocumentFilingStates.Filed, _assets.Find(sharedDocument)!.DocumentInfo!.FilingState);

        // The other bulk operations still skip a Shared document even once it is filed.
        var trash = await BulkAsync(new ViewDocumentBulkRequest([sharedDocument], ViewDocumentBulkOperations.MoveToTrash));
        Assert.Equal(ViewDocumentBulkService.SharedReason, Assert.Single(trash.Items).Reason);
        Assert.Null(_assets.Find(sharedDocument)!.TrashedAt);
    }

    [Fact]
    public async Task AnItemInAReadOnlyFolderCannotBeMovedToTrash_ButOneAlreadyInTrashCanBeRestored()
    {
        var alice = await CreatePerson("StandardUser");
        var managed = await AddItem(alice, '1', LocalAssetMediaKinds.Image, "Managed");
        var linked = await AddLinkedItem(alice, '2', "Linked");
        var livePhoto = await AddLivePhotoWithOneLinkedFile(alice, '3');
        _actor.Current = alice.Authority;

        // A managed item goes to Trash as before.
        using var trashed = await _client.PostAsync($"/view/items/{managed:D}/trash", null);
        Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);
        Assert.NotNull(_assets.Find(managed)!.TrashedAt);

        // A read-only item, and a Live Photo with one file in a read-only folder, are refused and left alone.
        foreach (var id in new[] { linked, livePhoto })
        {
            using var refused = await _client.PostAsync($"/view/items/{id:D}/trash", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await refused.Content.ReadAsStringAsync();
            Assert.Contains("read-only folder", problem, StringComparison.Ordinal);
            Assert.Contains(ViewDocumentBulkReasonCodes.ReadOnlySource, problem, StringComparison.Ordinal);
            Assert.Null(_assets.Find(id)!.TrashedAt);
        }

        // Archive stays available for a read-only item, and an item that is already in Trash can be restored.
        using var archived = await _client.PostAsync($"/view/items/{linked:D}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        await _assets.SetLifecycleStateAsync(linked, LocalAssetLifecycleState.Trashed);
        using var restored = await _client.PostAsync($"/view/items/{linked:D}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Null(_assets.Find(linked)!.TrashedAt);
    }

    [Fact]
    public async Task ABulkMoveToTrashSkipsDocumentsInAReadOnlyFolderAndTrashesTheOthers()
    {
        var alice = await CreatePerson("StandardUser");
        var managed = await AddDocument(alice, 'a');
        var linked = await AddLinkedItem(alice, 'b', "Linked letter", LocalAssetMediaKinds.Document);
        _actor.Current = alice.Authority;

        var result = await BulkAsync(new ViewDocumentBulkRequest([managed, linked], ViewDocumentBulkOperations.MoveToTrash));

        Assert.Equal((1, 1, 0), (result.Done, result.Skipped, result.Failed));
        var skipped = result.Items.Single(item => item.ItemId == linked);
        Assert.Equal(ViewDocumentBulkStatuses.Skipped, skipped.Status);
        Assert.Equal(ViewDocumentBulkReasonCodes.ReadOnlySource, skipped.ReasonCode);
        Assert.Equal(ViewDocumentBulkService.ReadOnlySourceReason, skipped.Reason);
        Assert.Null(result.Items.Single(item => item.ItemId == managed).ReasonCode);
        Assert.NotNull(_assets.Find(managed)!.TrashedAt);
        Assert.Null(_assets.Find(linked)!.TrashedAt);

        // Restore still works for a read-only document that is already in Trash.
        await _assets.SetLifecycleStateAsync(linked, LocalAssetLifecycleState.Trashed);
        var restore = await BulkAsync(new ViewDocumentBulkRequest([linked], ViewDocumentBulkOperations.Restore));
        Assert.Equal(1, restore.Done);
        Assert.Null(_assets.Find(linked)!.TrashedAt);
    }

    [Fact]
    public async Task CanTrashIsTrueOnlyForTheOwnersManagedPersonalItems()
    {
        var household = Guid.NewGuid();
        var alice = await CreatePerson("StandardUser", household);
        var bob = await CreatePerson("StandardUser", household);
        var managed = await AddItem(alice, '1', LocalAssetMediaKinds.Image, "Managed");
        var linked = await AddLinkedItem(alice, '2', "Linked");
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var sharedPhoto = await AddSharedPhoto(shared.LibraryId, '6');
        _actor.Current = alice.Authority;

        Assert.True((await _client.GetFromJsonAsync<LocalAssetDto>($"/view/items/{managed:D}?scope=mine"))!.CanTrash);
        Assert.False((await _client.GetFromJsonAsync<LocalAssetDto>($"/view/items/{linked:D}?scope=mine"))!.CanTrash);
        Assert.False((await _client.GetFromJsonAsync<LocalAssetDto>($"/view/items/{sharedPhoto:D}?scope=shared"))!.CanTrash);

        // The timeline page says the same, item by item.
        var page = (await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>("/view/assets?scope=mine"))!;
        Assert.True(page.Items.Single(item => item.Id == managed).CanTrash);
        Assert.False(page.Items.Single(item => item.Id == linked).CanTrash);

        // Someone else's item is never trashable by the viewer, whatever the data store says about the item itself.
        var item = _assets.Find(managed)!;
        Assert.True(ViewItemCapabilities.ForViewer(item, alice.ProfileId).CanTrash);
        Assert.False(ViewItemCapabilities.ForViewer(item, bob.ProfileId).CanTrash);
        Assert.False(ViewItemCapabilities.ForViewer(item, null).CanTrash);
    }

    [Fact]
    public async Task OnlyAHouseholdOrServerAdministratorFilesADocumentInTheSharedInbox()
    {
        var household = Guid.NewGuid();
        var admin = await CreatePerson("StandardUser", household, householdAdmin: true);
        var serverAdmin = await CreatePerson("StandardUser", household, serverAdmin: true);
        var member = await CreatePerson("StandardUser", household);
        var child = await CreatePerson("RestrictedProfile", household);
        var childWithDocuments = await CreatePerson("RestrictedProfile", household);
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(childWithDocuments.ProfileId, restricted: true) with { DocumentsEnabled = true });
        var otherHousehold = await CreatePerson("StandardUser", Guid.NewGuid(), householdAdmin: true);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var first = await AddSharedDocument(shared.LibraryId, "Shared first", '5');
        var second = await AddSharedDocument(shared.LibraryId, "Shared second", '6');
        var third = await AddSharedDocument(shared.LibraryId, "Shared third", '7');
        var request = new FileViewDocumentFromInboxRequest(AcceptSuggestions: false);

        async Task<HttpStatusCode> FileAsync(Person person, Guid id)
        {
            _actor.Current = person.Authority;
            using var response = await _client.PostAsJsonAsync($"/view/items/{id:D}/inbox/file", request);
            return response.StatusCode;
        }

        bool IsFiled(Guid id) => _assets.Find(id)!.DocumentInfo!.FilingState == ViewDocumentFilingStates.Filed;

        // Everyone else sees the Shared Inbox read-only; nothing changes.
        Assert.Equal(HttpStatusCode.Forbidden, await FileAsync(member, first));
        Assert.Equal(HttpStatusCode.Forbidden, await FileAsync(childWithDocuments, first));
        Assert.Equal(HttpStatusCode.Forbidden, await FileAsync(child, first));
        Assert.Equal(HttpStatusCode.NotFound, await FileAsync(otherHousehold, first));
        Assert.False(IsFiled(first));

        // The same rule tells the Dashboard whether to offer File on a Shared Inbox card.
        Assert.False(ViewSharedInboxAccess.CanFile(member.Authority));
        Assert.False(ViewSharedInboxAccess.CanFile(child.Authority));
        Assert.False(ViewSharedInboxAccess.CanFile(childWithDocuments.Authority));
        Assert.True(ViewSharedInboxAccess.CanFile(admin.Authority));
        Assert.True(ViewSharedInboxAccess.CanFile(serverAdmin.Authority));

        Assert.Equal(HttpStatusCode.OK, await FileAsync(admin, first));
        Assert.True(IsFiled(first));
        Assert.Equal(HttpStatusCode.OK, await FileAsync(serverAdmin, second));
        Assert.True(IsFiled(second));
        // A second tap changes nothing and is not an error; a photo or an unknown id is not an Inbox document.
        Assert.Equal(HttpStatusCode.OK, await FileAsync(admin, first));
        Assert.Equal(HttpStatusCode.NotFound, await FileAsync(admin, Guid.NewGuid()));
        Assert.False(IsFiled(third));
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

    [Fact]
    public async Task TheTimelineIndexNeverCountsOrSearchesDocumentsForAProfileWithoutThem()
    {
        var household = Guid.NewGuid();
        var blocked = await CreatePerson("RestrictedProfile", household);
        var allowed = await CreatePerson("StandardUser", household);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        await AddSharedDocument(shared.LibraryId, "Zebra lease", '5');
        await AddItem(blocked, 'd', LocalAssetMediaKinds.Document, "Zebra passport");
        await AddItem(blocked, '1', LocalAssetMediaKinds.Image, "Zebra holiday");

        async Task<int> CountAsync(string route)
        {
            var index = await _client.GetFromJsonAsync<ViewTimelineIndexDto>(route);
            return index!.Buckets.Sum(bucket => bucket.AssetCount);
        }

        // A person with Documents finds the Shared document by its words; this is what the profile below must not get.
        _actor.Current = allowed.Authority;
        Assert.Equal(1, await CountAsync("/view/assets/timeline-index?scope=shared&q=Zebra"));

        _actor.Current = blocked.Authority;
        Assert.Equal(0, await CountAsync("/view/assets/timeline-index?scope=shared&q=Zebra"));
        Assert.Equal(1, await CountAsync("/view/assets/timeline-index?scope=mine&q=Zebra"));
        Assert.Equal(1, await CountAsync("/view/assets/timeline-index?scope=mine"));
        Assert.Equal(1, await CountAsync("/view/assets/timeline-index?scope=mine&kind=image&kind=document"));
        foreach (var route in new[]
                 {
                     "/view/assets/timeline-index?scope=mine&kind=document",
                     "/view/assets/timeline-index?scope=shared&q=Zebra&kind=document",
                 })
        {
            using var response = await _client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheItemActionsStillWorkForPhotosAndForPeopleWithDocuments()
    {
        var blocked = await CreatePerson("RestrictedProfile");
        var alice = await CreatePerson("StandardUser");
        var photo = await AddItem(blocked, '1', LocalAssetMediaKinds.Image, "Holiday");
        var document = await AddItem(alice, 'd', LocalAssetMediaKinds.Document, "Passport");

        // A profile without Documents still edits its photos, and the answer carries no document details.
        _actor.Current = blocked.Authority;
        using var described = await _client.PutAsJsonAsync($"/view/items/{photo:D}/description", new UpdateLocalAssetDescriptionRequest("Beach"));
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        var asset = await described.Content.ReadFromJsonAsync<LocalAssetDto>();
        Assert.Equal("Beach", asset!.Description);
        Assert.Null(asset.DocumentInfo);
        using var located = await _client.PutAsJsonAsync($"/view/items/{photo:D}/location", new UpdateLocalAssetLocationRequest(10, 20));
        Assert.Equal(HttpStatusCode.OK, located.StatusCode);
        using var dated = await _client.PutAsJsonAsync($"/view/items/{photo:D}/captured-at", new UpdateLocalAssetCapturedAtRequest(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, dated.StatusCode);
        using var people = await _client.PutAsJsonAsync($"/view/items/{photo:D}/people", new UpdateLocalAssetPeopleRequest([]));
        Assert.Equal(HttpStatusCode.OK, people.StatusCode);

        // A person with Documents still edits a document and gets its details back.
        _actor.Current = alice.Authority;
        using var documentDescribed = await _client.PutAsJsonAsync($"/view/items/{document:D}/description", new UpdateLocalAssetDescriptionRequest("Scanned"));
        Assert.Equal(HttpStatusCode.OK, documentDescribed.StatusCode);
        Assert.NotNull((await documentDescribed.Content.ReadFromJsonAsync<LocalAssetDto>())!.DocumentInfo);
    }

    [Fact]
    public async Task APersonReadsAndSavesTheirOwnPreferencesAndTheUnscopedListsOpen()
    {
        var alice = await CreatePerson("StandardUser");
        var blocked = await CreatePerson("RestrictedProfile");
        _actor.Current = alice.Authority;

        var saved = await _client.GetFromJsonAsync<ViewPreferencesDto>("/view/preferences");
        Assert.Equal(alice.ProfileId, saved!.ProfileId);
        using var update = await _client.PutAsJsonAsync("/view/preferences",
            new ViewPreferencesRequest("mine", null, ViewTimelineDensity.Compact, ViewerInfoOpen: false));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var reread = await _client.GetFromJsonAsync<ViewPreferencesDto>("/view/preferences");
        Assert.Equal((ViewScopeKind.Mine, ViewTimelineDensity.Compact, false), (reread!.Scope, reread.TimelineDensity, reread.ViewerInfoOpen));

        // With no scope named, the lists follow the saved scope instead of answering 404.
        foreach (var route in new[] { "/view/assets", "/view/assets/timeline-index", "/view/folders" })
        {
            using var response = await _client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {route} returned {(int)response.StatusCode}.");
        }

        // The Documents sort stays on its own route, and another person's preferences are never reachable.
        using var sort = await _client.PutAsJsonAsync("/view/preferences/documents-sort", new ViewDocumentsSortDto("title"));
        Assert.Equal(HttpStatusCode.OK, sort.StatusCode);
        _actor.Current = blocked.Authority;
        Assert.Equal(blocked.ProfileId, (await _client.GetFromJsonAsync<ViewPreferencesDto>("/view/preferences"))!.ProfileId);
        Assert.Null((await _client.GetFromJsonAsync<ViewPreferencesDto>("/view/preferences"))!.DocumentsSort);
        using var blockedSort = await _client.GetAsync("/view/preferences/documents-sort");
        Assert.Equal(HttpStatusCode.Forbidden, blockedSort.StatusCode);
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

    private async Task<Person> CreatePersonWithoutDocuments(Guid? household = null, bool householdAdmin = false)
    {
        var person = await CreatePerson("StandardUser", household, householdAdmin);
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(person.ProfileId) with { DocumentsEnabled = false });
        return person;
    }

    private static string FavouriteRule() =>
        ViewSmartGalleryRules.Serialize(CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "favorite", Op = "eq", Value = "true" }]));

    private async Task<ViewGallery> CreateManualGallery(Person owner, string name, Guid? cover = null) =>
        await new ViewGalleryRepository(_database).CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, (await _spaces.GetByOwnerAsync(owner.ProfileId))!.Id, name, ViewGalleryKind.Manual, CoverItemId: cover));

    [Fact]
    public async Task AGalleryNeverShowsOrCountsDocumentsToAProfileWithoutThem_SharedOrSmart()
    {
        var household = Guid.NewGuid();
        var owner = await CreatePerson("StandardUser", household);
        var child = await CreatePerson("RestrictedProfile", household);
        var photo = await AddItem(owner, '1', LocalAssetMediaKinds.Image, "Beach");
        var document = await AddDocument(owner, 'd');
        var galleries = new ViewGalleryRepository(_database);
        var manual = await CreateManualGallery(owner, "Trip", cover: document);
        await galleries.AddItemsAsync(manual.Id, [photo, document]);
        var smart = await galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, manual.PersonalSpaceId, "Starred", ViewGalleryKind.Smart, SmartRuleJson: FavouriteRule()));
        await _assets.SetFlagsAsync(photo, true, null);
        await _assets.SetFlagsAsync(document, true, null);
        foreach (var gallery in new[] { manual, smart })
        {
            await galleries.ReplaceSharesAsync(gallery.Id, [(child.ProfileId, ViewGallerySharePermission.View)]);
        }

        // The person with Documents sees everything, including the document as the cover.
        _actor.Current = owner.Authority;
        var ownerList = await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries");
        Assert.Equal(2, ownerList!.Owned.Single(gallery => gallery.Id == manual.Id).ItemCount);
        Assert.Equal(2, ownerList.Owned.Single(gallery => gallery.Id == smart.Id).ItemCount);
        Assert.Equal(document, ownerList.Owned.Single(gallery => gallery.Id == manual.Id).CoverItemId);
        var ownerItems = await _client.GetFromJsonAsync<ViewGalleryItemPageDto>($"/view/galleries/{manual.Id:D}/items");
        Assert.Equal(2, ownerItems!.Items.Count);

        // The profile without Documents is handed neither the document's id nor a count that includes it.
        _actor.Current = child.Authority;
        var childList = await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries");
        var sharedManual = childList!.SharedWithYou.Single(gallery => gallery.Id == manual.Id);
        Assert.Equal(1, sharedManual.ItemCount);
        Assert.Null(sharedManual.CoverItemId);
        Assert.Equal(1, childList.SharedWithYou.Single(gallery => gallery.Id == smart.Id).ItemCount);

        var childItems = await _client.GetFromJsonAsync<ViewGalleryItemPageDto>($"/view/galleries/{manual.Id:D}/items");
        Assert.Equal(photo, Assert.Single(childItems!.Items).ItemId);
        var single = await _client.GetFromJsonAsync<ViewGalleryDto>($"/view/galleries/{manual.Id:D}");
        Assert.Equal(1, single!.ItemCount);
        Assert.Null(single.CoverItemId);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewGalleryDto>($"/view/galleries/{smart.Id:D}"))!.ItemCount);
    }

    [Fact]
    public async Task AProfileWithoutDocumentsCannotAddMoveOrCountDocumentsInAGallery()
    {
        var owner = await CreatePerson("StandardUser");
        var photo = await AddItem(owner, '1', LocalAssetMediaKinds.Image, "Beach");
        var document = await AddDocument(owner, 'd');
        var galleries = new ViewGalleryRepository(_database);
        var gallery = await CreateManualGallery(owner, "Trip");
        _actor.Current = owner.Authority;

        // With Documents the document can be added and reordered like anything else.
        var added = await PostAsync<AddViewGalleryItemsResponseDto>($"/view/galleries/{gallery.Id:D}/items", new ViewGalleryItemsRequest([document]));
        Assert.Equal(1, added.Added);
        await galleries.AddItemsAsync(gallery.Id, [photo]);

        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(owner.ProfileId) with { DocumentsEnabled = false });

        // Without Documents: the document is refused as a whole request (nothing is half-added), with the answer for an
        // id that is not in the owner's space, even for the owner ...
        var other = await AddDocument(owner, 'e');
        using var refused = await _client.PostAsJsonAsync($"/view/galleries/{gallery.Id:D}/items", new ViewGalleryItemsRequest([photo, other]));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain((await galleries.GetItemsAsync(gallery.Id)).Items, item => item.ItemId == other);

        // ... it is never counted as removed or moved, so the answer cannot reveal it is in the Gallery ...
        using var remove = new HttpRequestMessage(HttpMethod.Delete, $"/view/galleries/{gallery.Id:D}/items")
        {
            Content = JsonContent.Create(new ViewGalleryItemsRequest([document])),
        };
        using var removed = await _client.SendAsync(remove);
        Assert.Equal(0, (await removed.Content.ReadFromJsonAsync<ViewItemsRemovedResponse>())!.Removed);
        using var move = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}/items/{document:D}/position", new ViewGalleryPositionRequest(7));
        Assert.Equal(HttpStatusCode.NotFound, move.StatusCode);
        Assert.Contains((await galleries.GetItemsAsync(gallery.Id)).Items, item => item.ItemId == document && item.Position != 7);

        // ... and the Gallery answers with the photo only.
        var items = await _client.GetFromJsonAsync<ViewGalleryItemPageDto>($"/view/galleries/{gallery.Id:D}/items");
        Assert.Equal(photo, Assert.Single(items!.Items).ItemId);
        var list = await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries");
        Assert.Equal(1, list!.Owned.Single().ItemCount);

        // A photo still works, and so does a cover that is a photo; a document cover is refused.
        using var photoOnly = await _client.PostAsJsonAsync($"/view/galleries/{gallery.Id:D}/items", new ViewGalleryItemsRequest([photo]));
        Assert.Equal(HttpStatusCode.OK, photoOnly.StatusCode);
        using var documentCover = await _client.PostAsJsonAsync("/view/galleries",
            new ViewGalleryRequest("Cover", ViewGalleryKind.Manual, CoverItemId: document));
        Assert.Equal(HttpStatusCode.BadRequest, documentCover.StatusCode);
        using var updateCover = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}",
            new ViewGalleryRequest("Trip", ViewGalleryKind.Manual, CoverItemId: document));
        Assert.Equal(HttpStatusCode.BadRequest, updateCover.StatusCode);
        var counted = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}", new ViewGalleryRequest("Trip", ViewGalleryKind.Manual));
        Assert.Equal(1, (await counted.Content.ReadFromJsonAsync<ViewGalleryDto>())!.ItemCount);
    }

    [Fact]
    public async Task FavouriteHideArchiveTrashAndRestoreAreRefusedOnADocumentWithoutDocuments()
    {
        var owner = await CreatePersonWithoutDocuments();
        var other = await CreatePersonWithoutDocuments();
        var document = await AddDocument(owner, 'd');
        var photo = await AddItem(owner, '1', LocalAssetMediaKinds.Image, "Beach");
        _actor.Current = owner.Authority;

        var refused = new (HttpMethod Method, string Route, HttpContent? Body)[]
        {
            (HttpMethod.Put, $"/view/items/{document:D}/favorite", JsonContent.Create(new SetLocalAssetFlagRequest(true))),
            (HttpMethod.Put, $"/view/items/{document:D}/hidden", JsonContent.Create(new SetLocalAssetFlagRequest(true))),
            (HttpMethod.Post, $"/view/items/{document:D}/archive", null),
            (HttpMethod.Post, $"/view/items/{document:D}/trash", null),
            (HttpMethod.Post, $"/view/items/{document:D}/restore", null),
        };
        foreach (var (method, route, content) in refused)
        {
            using var request = new HttpRequestMessage(method, route) { Content = content };
            using var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {route} returned {(int)response.StatusCode}.");
        }

        var unchanged = _assets.Find(document)!;
        Assert.False(unchanged.Favorite);
        Assert.False(unchanged.Hidden);
        Assert.Null(unchanged.ArchivedAt);
        Assert.Null(unchanged.TrashedAt);

        // Someone else's document answers 404 first, whether or not they may use Documents.
        _actor.Current = other.Authority;
        using var notYours = await _client.PostAsync($"/view/items/{document:D}/trash", null);
        Assert.Equal(HttpStatusCode.NotFound, notYours.StatusCode);

        // The same actions on a photo are untouched.
        _actor.Current = owner.Authority;
        using var favourite = await _client.PutAsJsonAsync($"/view/items/{photo:D}/favorite", new SetLocalAssetFlagRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, favourite.StatusCode);
        using var trash = await _client.PostAsync($"/view/items/{photo:D}/trash", null);
        Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        Assert.NotNull(_assets.Find(photo)!.TrashedAt);

        // A person with Documents still organises their documents.
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(owner.ProfileId));
        using var allowed = await _client.PostAsync($"/view/items/{document:D}/trash", null);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task APairedAppWithThePhotoScopesCannotFavouriteHideArchiveTrashOrRestoreTheOwnersPhoto()
    {
        var owner = await CreatePerson("StandardUser");
        var photo = await AddItem(owner, '7', LocalAssetMediaKinds.Image, "Beach");
        // The owner's own phone. The evaluator allows every permission, as if it holds the photo scopes.
        _actor.Current = PairedApp(owner);

        var refused = new (HttpMethod Method, string Route, HttpContent? Body)[]
        {
            (HttpMethod.Put, $"/view/items/{photo:D}/favorite", JsonContent.Create(new SetLocalAssetFlagRequest(true))),
            (HttpMethod.Put, $"/view/items/{photo:D}/hidden", JsonContent.Create(new SetLocalAssetFlagRequest(true))),
            (HttpMethod.Post, $"/view/items/{photo:D}/archive", null),
            (HttpMethod.Post, $"/view/items/{photo:D}/trash", null),
            (HttpMethod.Post, $"/view/items/{photo:D}/restore", null),
        };
        foreach (var (method, route, content) in refused)
        {
            using var request = new HttpRequestMessage(method, route) { Content = content };
            using var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {route} returned {(int)response.StatusCode}.");
        }

        var unchanged = _assets.Find(photo)!;
        Assert.False(unchanged.Favorite);
        Assert.False(unchanged.Hidden);
        Assert.Null(unchanged.ArchivedAt);
        Assert.Null(unchanged.TrashedAt);

        // The owner signed in to the Dashboard still does all of it.
        _actor.Current = owner.Authority;
        using var favourite = await _client.PutAsJsonAsync($"/view/items/{photo:D}/favorite", new SetLocalAssetFlagRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, favourite.StatusCode);
        using var hidden = await _client.PutAsJsonAsync($"/view/items/{photo:D}/hidden", new SetLocalAssetFlagRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        using var archive = await _client.PostAsync($"/view/items/{photo:D}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        using var trash = await _client.PostAsync($"/view/items/{photo:D}/trash", null);
        Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        Assert.NotNull(_assets.Find(photo)!.TrashedAt);
        using var restore = await _client.PostAsync($"/view/items/{photo:D}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        var changed = _assets.Find(photo)!;
        Assert.True(changed.Favorite);
        Assert.True(changed.Hidden);
        Assert.Null(changed.TrashedAt);
    }

    [Fact]
    public async Task APairedAppWithThePhotoScopesCannotChangeTheOwnersCatalogueRulesOrDocuments()
    {
        var owner = await CreatePerson("StandardUser");
        var document = await AddDocument(owner, 'e');
        _actor.Current = owner.Authority;
        var tag = await PostAsync<ViewTagDto>("/view/tags", new ViewCatalogueEntryRequest("Invoices"));
        var correspondent = await PostAsync<ViewCorrespondentDto>("/view/correspondents", new ViewCatalogueEntryRequest("Water Co"));
        var type = await PostAsync<ViewDocumentTypeDto>("/view/document-types", new ViewCatalogueEntryRequest("Bill"));
        var field = await PostAsync<ViewDocumentFieldDto>("/view/document-fields", new ViewDocumentFieldRequest("Status", "choice", ["Open", "Closed"]));
        var rule = new SaveViewDocumentRuleRequest("Invoices", true, "all", true, ViewDocumentRuleScopes.Mine,
            [new ViewDocumentRuleConditionDto(ViewDocumentRuleConditionKinds.FilenameContains, "e")],
            [new ViewDocumentRuleActionDto(ViewDocumentRuleActionKinds.AddTag, tag.Id)]);
        var created = await PostAsync<ViewDocumentRuleDto>("/view/document-rules", rule);
        var itemRoute = $"/view/items/{document:D}";
        // The owner's own phone, as if it holds view.personal.read and the other photo scopes.
        _actor.Current = PairedApp(owner);

        var refused = new (HttpMethod Method, string Route, HttpContent? Body)[]
        {
            (HttpMethod.Post, "/view/tags", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Put, $"/view/tags/{tag.Id:D}", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Delete, $"/view/tags/{tag.Id:D}", null),
            (HttpMethod.Post, "/view/correspondents", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Put, $"/view/correspondents/{correspondent.Id:D}", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Delete, $"/view/correspondents/{correspondent.Id:D}", null),
            (HttpMethod.Post, "/view/document-types", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Put, $"/view/document-types/{type.Id:D}", JsonContent.Create(new ViewCatalogueEntryRequest("Sneaky"))),
            (HttpMethod.Delete, $"/view/document-types/{type.Id:D}", null),
            (HttpMethod.Post, "/view/document-fields", JsonContent.Create(new ViewDocumentFieldRequest("Sneaky", "text"))),
            (HttpMethod.Put, $"/view/document-fields/{field.Id:D}", JsonContent.Create(new ViewDocumentFieldRequest("Sneaky", "choice", ["Open"]))),
            (HttpMethod.Delete, $"/view/document-fields/{field.Id:D}", null),
            (HttpMethod.Post, "/view/document-rules", JsonContent.Create(rule with { Name = "Sneaky" })),
            (HttpMethod.Put, $"/view/document-rules/{created.Id:D}", JsonContent.Create(rule with { Name = "Sneaky" })),
            (HttpMethod.Put, "/view/document-rules/order", JsonContent.Create(new ReorderViewDocumentRulesRequest([created.Id]))),
            (HttpMethod.Post, "/view/document-rules/apply", JsonContent.Create(new { })),
            (HttpMethod.Delete, $"/view/document-rules/{created.Id:D}", null),
            (HttpMethod.Post, $"{itemRoute}/inbox/file", JsonContent.Create(new FileViewDocumentFromInboxRequest(AcceptSuggestions: true))),
            (HttpMethod.Put, $"{itemRoute}/document-details", JsonContent.Create(new UpdateViewDocumentDetailsRequest(Title: "Sneaky"))),
            (HttpMethod.Put, $"{itemRoute}/tags", JsonContent.Create(new UpdateLocalAssetTagsRequest(["Sneaky"]))),
            (HttpMethod.Post, "/view/documents/bulk", JsonContent.Create(new ViewDocumentBulkRequest([document], ViewDocumentBulkOperations.AddTags, Tags: ["Sneaky"]))),
            (HttpMethod.Post, "/view/documents/bulk", JsonContent.Create(new ViewDocumentBulkRequest([document], ViewDocumentBulkOperations.MoveToTrash))),
        };
        foreach (var (method, route, content) in refused)
        {
            using var request = new HttpRequestMessage(method, route) { Content = content };
            using var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {route} returned {(int)response.StatusCode}.");
        }

        // Reading stays open to the app: the catalogue, the rules and a rule's try-out, which changes nothing.
        using var catalogueRead = await _client.GetAsync("/view/catalogue");
        Assert.Equal(HttpStatusCode.OK, catalogueRead.StatusCode);
        using var rulesRead = await _client.GetAsync("/view/document-rules");
        Assert.Equal(HttpStatusCode.OK, rulesRead.StatusCode);
        using var tryOut = await _client.PostAsJsonAsync("/view/document-rules/test", rule);
        Assert.Equal(HttpStatusCode.OK, tryOut.StatusCode);

        // Nothing changed, and the owner on the Dashboard still manages all of it.
        _actor.Current = owner.Authority;
        var catalogue = await _client.GetFromJsonAsync<ViewDocumentCatalogueDto>("/view/catalogue");
        Assert.Equal(["Invoices"], catalogue!.Tags.Select(entry => entry.Name).ToArray());
        Assert.Equal(["Water Co"], catalogue.Correspondents.Select(entry => entry.Name).ToArray());
        Assert.Equal(["Bill"], catalogue.DocumentTypes.Select(entry => entry.Name).ToArray());
        Assert.Equal(["Status"], catalogue.Fields.Select(entry => entry.Name).ToArray());
        var rules = await _client.GetFromJsonAsync<ViewDocumentRulesDto>("/view/document-rules");
        Assert.Equal(["Invoices"], rules!.Rules.Select(item => item.Name).ToArray());
        Assert.Null(_assets.Find(document)!.TrashedAt);
        using var rename = await _client.PutAsJsonAsync($"/view/tags/{tag.Id:D}", new ViewCatalogueEntryRequest("Bills"));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using var deleteRule = await _client.DeleteAsync($"/view/document-rules/{created.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRule.StatusCode);
    }

    /// <summary>The person's own paired phone: same account and profile, signed in as an app.</summary>
    private static RequestAuthority PairedApp(Person owner) => new(
        PrincipalKind.DelegatedUserClient, true, AccountId: owner.Authority.AccountId,
        ActiveProfileId: owner.ProfileId, ApplicationId: Guid.NewGuid(),
        AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true);

    [Fact]
    public async Task TagTypeAheadNeverOffersATagThatOnlyDocumentsCarryToAProfileWithoutDocuments()
    {
        var household = Guid.NewGuid();
        var blocked = await CreatePerson("RestrictedProfile", household);
        var allowed = await CreatePerson("StandardUser", household);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var sharedDocument = await AddSharedDocument(shared.LibraryId, "Shared lease", '5');
        var sharedPhoto = await AddSharedPhoto(shared.LibraryId, '6');
        var secret = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO tags (id, name, created_at) VALUES (@secret, 'Divorce papers', @now), (@holiday, 'Holiday', @now);
                INSERT INTO local_item_tag_links (item_id, tag_id, added_at)
                VALUES (@sharedDocument, @secret, @now), (@sharedPhoto, @holiday, @now);
                """, new { secret, holiday, sharedDocument, sharedPhoto, now = DateTimeOffset.UtcNow });
        }

        _actor.Current = allowed.Authority;
        var theirs = await _client.GetFromJsonAsync<List<string>>("/view/tag-suggestions");
        Assert.Contains("Divorce papers", theirs!);

        _actor.Current = blocked.Authority;
        var suggestions = await _client.GetFromJsonAsync<List<string>>("/view/tag-suggestions");
        Assert.Contains("Holiday", suggestions!);
        Assert.DoesNotContain("Divorce papers", suggestions!);
        var searched = await _client.GetFromJsonAsync<List<string>>("/view/tag-suggestions?q=divorce");
        Assert.Empty(searched!);
    }

    [Fact]
    public async Task SavingAGalleryWithoutDocumentsKeepsTheOwnersDocumentCover()
    {
        var owner = await CreatePerson("StandardUser");
        var photo = await AddItem(owner, '1', LocalAssetMediaKinds.Image, "Beach");
        var document = await AddDocument(owner, 'd');
        var galleries = new ViewGalleryRepository(_database);
        var gallery = await CreateManualGallery(owner, "Trip", cover: document);
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(owner.ProfileId) with { DocumentsEnabled = false });
        _actor.Current = owner.Authority;

        // The profile reads "no cover" (the Dashboard sends that back unchanged), and a rename must not wipe the real one.
        var read = await _client.GetFromJsonAsync<ViewGalleryDto>($"/view/galleries/{gallery.Id:D}");
        Assert.Null(read!.CoverItemId);
        using var renamed = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}",
            new ViewGalleryRequest("Holiday", ViewGalleryKind.Manual, CoverItemId: read.CoverItemId));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Null((await renamed.Content.ReadFromJsonAsync<ViewGalleryDto>())!.CoverItemId);
        var stored = (await galleries.GetAsync(gallery.Id))!;
        Assert.Equal("Holiday", stored.Name);
        Assert.Equal(document, stored.CoverItemId);

        // An explicit photo cover from them does replace it; a later save with none then clears that photo cover.
        using var chosen = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}",
            new ViewGalleryRequest("Holiday", ViewGalleryKind.Manual, CoverItemId: photo));
        Assert.Equal(photo, (await chosen.Content.ReadFromJsonAsync<ViewGalleryDto>())!.CoverItemId);
        using var cleared = await _client.PutAsJsonAsync($"/view/galleries/{gallery.Id:D}",
            new ViewGalleryRequest("Holiday", ViewGalleryKind.Manual));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await galleries.GetAsync(gallery.Id))!.CoverItemId);
    }

    [Fact]
    public async Task AGalleryContributorOrOwnerWithoutDocumentsCannotTellADocumentFromAnUnknownId()
    {
        var household = Guid.NewGuid();
        var owner = await CreatePersonWithoutDocuments(household);
        var contributor = await CreatePerson("RestrictedProfile", household);
        var photo = await AddItem(owner, '1', LocalAssetMediaKinds.Image, "Beach");
        var document = await AddDocument(owner, 'd');
        var galleries = new ViewGalleryRepository(_database);
        var gallery = await CreateManualGallery(owner, "Trip");
        await galleries.ReplaceSharesAsync(gallery.Id, [(contributor.ProfileId, ViewGallerySharePermission.Contribute)]);

        async Task<(HttpStatusCode Status, string Body)> AddAsync(Person person, Guid itemId)
        {
            _actor.Current = person.Authority;
            using var response = await _client.PostAsJsonAsync($"/view/galleries/{gallery.Id:D}/items", new ViewGalleryItemsRequest([itemId]));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // The contributor gets the same answer for the owner's document as for an id that is not there ...
        var unknown = await AddAsync(contributor, Guid.NewGuid());
        var forDocument = await AddAsync(contributor, document);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal(unknown.Status, forDocument.Status);
        Assert.DoesNotContain("Documents", forDocument.Body, StringComparison.Ordinal);

        // ... and so does the owner, whose document it is: nobody is told Documents is the reason.
        var ownerForDocument = await AddAsync(owner, document);
        Assert.Equal(HttpStatusCode.BadRequest, ownerForDocument.Status);
        Assert.Equal(unknown.Body, ownerForDocument.Body);
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(owner, photo)).Status);
    }

    [Fact]
    public async Task ADocumentUploadIsRefusedBeforeAnythingIsStoredForAProfileWithoutDocuments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima-view-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(root, "config"));
        configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "view", Label = "View", Path = Path.Combine(root, "storage"), AllowWrite = true }],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "View" },
        });
        var storage = new ViewStorageService(configuration, _spaces, new ViewSharedLibraryRepository(_database));
        var library = new ViewLibraryService(_assets, configuration, _spaces, storage, NullLogger<ViewLibraryService>.Instance);
        var blocked = await CreatePersonWithoutDocuments();
        var allowed = await CreatePerson("StandardUser");

        var (app, client) = await StartAsync(
            StandIns.Where(stub => stub != typeof(IConfigurationLoader) && stub != typeof(ViewLibraryService)).ToArray(),
            services =>
            {
                services.AddSingleton<IConfigurationLoader>(configuration);
                services.AddSingleton(storage);
                services.AddSingleton(library);
                services.AddSingleton((PhoneBackupProfileService)RuntimeHelpers.GetUninitializedObject(typeof(PhoneBackupProfileService)));
            });
        try
        {
            async Task<HttpStatusCode> UploadAsync(Person person, string name, params byte[] bytes)
            {
                _actor.Current = person.Authority;
                using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
                using var response = await client.PostAsync("/view/uploads", form);
                return response.StatusCode;
            }

            int ItemsOf(Person person)
            {
                using var connection = _database.CreateConnection();
                return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM local_items WHERE owner_profile_id = @profileId;", new { profileId = person.ProfileId });
            }

            // A person with Documents uploads a document; the same file from someone without Documents is refused
            // outright, so there is no "already added" answer and nothing is stored in their space.
            Assert.Equal(HttpStatusCode.OK, await UploadAsync(allowed, "Lease.pdf", 1, 2, 3, 4));
            Assert.Equal(HttpStatusCode.Forbidden, await UploadAsync(blocked, "Lease.pdf", 1, 2, 3, 4));
            Assert.Equal(HttpStatusCode.Forbidden, await UploadAsync(blocked, "notes.txt", 5, 6, 7));
            Assert.Equal(0, ItemsOf(blocked));

            // Photos still upload for both.
            Assert.Equal(HttpStatusCode.OK, await UploadAsync(blocked, "Beach.jpg", 9, 8, 7, 6));
            Assert.Equal(1, ItemsOf(blocked));
            Assert.Equal(HttpStatusCode.OK, await UploadAsync(allowed, "Hill.jpg", 4, 3, 2, 1));
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            configuration.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private async Task<Guid> AddSharedPhoto(Guid libraryId, char seed)
    {
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared photos', @sourceKey, 'managed',
                        'Shared/Photos/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, libraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, null, null, LocalAssetMediaKinds.Image, $"Shared photo {seed}", null,
            [new LocalAssetFileRegistration($@"C:\shared\{seed}.jpg", new string(seed, 64), $"{seed}.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;
    }

    private Guid InsertContribution(Person contributor, params Guid[] itemIds)
    {
        var contributionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_shared_contributions
                (id, submitted_by_profile_id, submitted_by_name, status, destination_kind, revision, idempotency_key, submitted_at, updated_at)
            VALUES (@contributionId, @profileId, 'Contributor', 'pending', 'timeline', 1, @key, @now, @now);
            INSERT INTO view_shared_contribution_events (id, contribution_id, actor_profile_id, actor_name, event_type, detail, occurred_at)
            VALUES (@eventId, @contributionId, @profileId, 'Contributor', 'submitted', @detail, @now);
            """, new
        {
            contributionId,
            profileId = contributor.ProfileId,
            key = Guid.NewGuid().ToString("N"),
            now,
            eventId = Guid.NewGuid(),
            detail = $"{itemIds.Length} item(s) submitted.",
        });
        for (var position = 0; position < itemIds.Length; position++)
        {
            connection.Execute("""
                INSERT INTO view_shared_contribution_items
                    (id, contribution_id, item_id, original_profile_id, original_profile_name, position, operation,
                     source_manifest_json, execution_state, updated_at)
                VALUES (@id, @contributionId, @itemId, @profileId, 'Contributor', @position, 'copy', '{}', 'waiting', @now);
                """, new { id = Guid.NewGuid(), contributionId, itemId = itemIds[position], profileId = contributor.ProfileId, position, now });
        }

        return contributionId;
    }

    [Fact]
    public async Task ContributionRoutesAnswerFilteredOrNotFoundOrRefusedForAReviewerWithoutDocuments()
    {
        var household = Guid.NewGuid();
        var contributor = await CreatePerson("StandardUser", household);
        var reviewer = await CreatePersonWithoutDocuments(household, householdAdmin: true);
        var photo = await AddItem(contributor, '1', LocalAssetMediaKinds.Image, "Beach photo");
        var document = await AddItem(contributor, 'd', LocalAssetMediaKinds.Document, "Tax return 2025");
        var documentsOnly = InsertContribution(contributor, document);
        var photosOnly = InsertContribution(contributor, photo);
        var mixed = InsertContribution(contributor, photo, document);

        var (app, client) = await StartAsync(
            StandIns.Where(stub => stub != typeof(ViewSharedContributionService) && stub != typeof(IAccountAccessDecisionService)).ToArray(),
            services =>
            {
                services.AddSingleton<IAccountAccessDecisionService, AdministratorDecisions>();
                services.AddSingleton<IViewSharedContributionQueue, ViewSharedContributionQueue>();
                services.AddSingleton<IAuthorizationAuditWriter, RecordingAuditWriter>();
                services.AddSingleton((ViewSharedTransferService)RuntimeHelpers.GetUninitializedObject(typeof(ViewSharedTransferService)));
                services.AddSingleton<ViewSharedContributionService>();
            });
        try
        {
            _actor.Current = reviewer.Authority;

            // The review list leaves the documents-only contribution out and shows the mixed one without its document.
            var list = await client.GetFromJsonAsync<ViewSharedContributionPageDto>("/view/shared/contributions?mode=review");
            Assert.Equal(new[] { photosOnly, mixed }.Order(), list!.Items.Select(value => value.Id).Order());
            var shown = list.Items.Single(value => value.Id == mixed);
            Assert.Equal(photo, Assert.Single(shown.Items).ItemId);
            Assert.True(shown.IncludesHiddenItems);
            var body = await client.GetStringAsync("/view/shared/contributions?mode=review");
            Assert.DoesNotContain("Tax return", body, StringComparison.Ordinal);
            Assert.DoesNotContain(document.ToString("D"), body, StringComparison.OrdinalIgnoreCase);

            // Detail by id: not found for the documents-only one, filtered for the mixed one.
            using var hidden = await client.GetAsync($"/view/shared/contributions/{documentsOnly:D}");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            var detail = await client.GetFromJsonAsync<ViewSharedContributionDto>($"/view/shared/contributions/{mixed:D}");
            Assert.True(detail!.IncludesHiddenItems);
            Assert.Single(detail.Items);

            // Approving or declining one that holds a document is refused; a missing one is not found; others work.
            var decision = new ViewSharedContributionDecisionRequest("declined", 1);
            using var refused = await client.PostAsJsonAsync($"/view/shared/contributions/{mixed:D}/decision", decision);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var retry = await client.PostAsJsonAsync($"/view/shared/contributions/{mixed:D}/retry", new ViewSharedContributionRevisionRequest(1));
            Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
            using var missing = await client.PostAsJsonAsync($"/view/shared/contributions/{documentsOnly:D}/decision", decision);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var allowed = await client.PostAsJsonAsync($"/view/shared/contributions/{photosOnly:D}/decision", decision);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    // Tests (named below) that prove a route leaks nothing to a profile without Documents.
    private const string Refused = "AProfileWithoutDocumentsIsRefusedOnEveryDocumentsRoute";
    private const string Lifecycle = "FavouriteHideArchiveTrashAndRestoreAreRefusedOnADocumentWithoutDocuments";
    private const string GalleryRead = "AGalleryNeverShowsOrCountsDocumentsToAProfileWithoutThem_SharedOrSmart";
    private const string GalleryWrite = "AProfileWithoutDocumentsCannotAddMoveOrCountDocumentsInAGallery";
    private const string TagSuggestions = "TagTypeAheadNeverOffersATagThatOnlyDocumentsCarryToAProfileWithoutDocuments";
    private const string TimelineIndex = "TheTimelineIndexNeverCountsOrSearchesDocumentsForAProfileWithoutThem";
    private const string ContributionRoutes = "ContributionRoutesAnswerFilteredOrNotFoundOrRefusedForAReviewerWithoutDocuments";
    private const string ContributionService = "AReviewerWithoutDocumentsNeverSeesADocumentInAContribution";
    private const string ContributionSend = "AProfileWithoutDocumentsCannotPreviewSubmitOrDirectAddADocument";
    private const string Discovery = "Places_Atlas_PlaceMediaAndPeople_NeverIncludeADocumentEvenWithLocationOrPersonMetadata";
    private const string UploadRefusal = "ADocumentUploadIsRefusedBeforeAnythingIsStoredForAProfileWithoutDocuments";
    private const string ClientDocuments = "AProfileWithoutDocumentsLearnsOfNoDocumentThroughTimelineGalleriesCountsPeoplePlacesItemsOrFiles";
    private const string TrashWithoutDocuments = "AProfileWithoutDocuments_NeverSeesDocumentsInItsTrash";
    private const string EmptyTrashWithoutDocuments = "AProfileWithoutDocuments_EmptyTrashLeavesItsDocumentsOnDisk";
    private const string NoItems = "none:";

    /// <summary>A covering test plus a phrase its source must contain (the call or path that makes the claim true).</summary>
    private static string Covers(string test, string evidence) => $"{test}|{evidence}";

    /// <summary>
    /// Every View route, and why it cannot tell a profile without Documents that a document exists: the test that
    /// proves it, or (after "none:") why it carries no item data. A new route must be added here on purpose; a route
    /// that returns or changes items and is not covered by a test is a leak waiting to happen.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ReviewedRoutes = new Dictionary<string, string>
    {
        ["DELETE /view/admin/profiles/{profileId:guid}/sources/{sourceId:guid}"] = NoItems + "administrator source settings, no items",
        ["DELETE /view/admin/shared/sources/{sourceId:guid}"] = NoItems + "administrator source settings, no items",
        ["DELETE /view/correspondents/{id:guid}"] = Refused,
        ["DELETE /view/document-fields/{id:guid}"] = Refused,
        ["DELETE /view/document-rules/{id:guid}"] = Refused,
        ["DELETE /view/document-types/{id:guid}"] = Refused,
        ["DELETE /view/galleries/{id:guid}"] = NoItems + "deletes the Gallery and returns nothing",
        ["DELETE /view/galleries/{id:guid}/items"] = Covers(GalleryWrite, "HttpMethod.Delete"),
        ["DELETE /view/tags/{id:guid}"] = Refused,
        ["GET /view/admin/profiles/{profileId:guid}/sources"] = NoItems + "administrator source settings, no items",
        ["GET /view/admin/shared/sources"] = NoItems + "administrator source settings, no items",
        ["GET /view/assets"] = Refused,
        ["GET /view/assets/added-by"] = Covers(AddedByCounts, "/view/assets/added-by"),
        ["GET /view/assets/timeline-index"] = Covers(TimelineIndex, "timeline-index"),
        ["GET /view/catalogue"] = Refused,
        ["GET /view/document-rules"] = Refused,
        ["GET /view/documents/export"] = Refused,
        ["GET /view/documents/inbox-summary"] = Refused,
        ["GET /view/folders"] = Refused,
        ["GET /view/galleries"] = Covers(GalleryRead, "\"/view/galleries\""),
        ["GET /view/galleries/{id:guid}"] = Covers(GalleryRead, "ViewGalleryDto>($\"/view/galleries/"),
        ["GET /view/galleries/{id:guid}/items"] = Covers(GalleryRead, "ViewGalleryItemPageDto>("),
        ["GET /view/galleries/{id:guid}/shares"] = NoItems + "profile ids and permissions only",
        ["GET /view/items/{id:guid}"] = Refused,
        ["GET /view/items/{id:guid}/content"] = Refused,
        ["GET /view/items/{id:guid}/document-details"] = Refused,
        ["GET /view/items/{id:guid}/preview"] = Refused,
        ["GET /view/items/{id:guid}/text"] = Refused,
        ["GET /view/items/{id:guid}/thumbnail"] = Refused,
        ["GET /view/library-capacity"] = NoItems + "disk space only",
        ["GET /view/other-people"] = NoItems + "household and profile names only",
        ["GET /view/people"] = Covers(Discovery, "QueryPeople"),
        ["GET /view/photo-views"] = NoItems + "audit of who opened photos, no items",
        ["GET /view/places"] = Covers(Discovery, "QueryPlaces"),
        ["GET /view/places/atlas"] = Covers(Discovery, "QueryAtlas"),
        ["GET /view/places/media"] = Covers(Discovery, "QueryPlaceAssets"),
        ["GET /view/preferences"] = NoItems + "the person's own settings",
        ["GET /view/preferences/documents-sort"] = Refused,
        ["GET /view/scopes"] = NoItems + "scope names only",
        ["GET /view/share-targets"] = NoItems + "profile names only",
        ["GET /view/shared/contributions"] = Covers(ContributionRoutes, "mode=review"),
        ["GET /view/shared/contributions/{id:guid}"] = Covers(ContributionRoutes, "contributions/{documentsOnly"),
        ["GET /view/tag-suggestions"] = Covers(TagSuggestions, "/view/tag-suggestions"),
        ["GET /view/trash/summary"] = Covers(TrashWithoutDocuments, "/view/trash/summary"),
        ["POST /view/admin/profiles/{profileId:guid}/reconcile"] = NoItems + "administrator scan, file totals only, no item data",
        ["POST /view/admin/profiles/{profileId:guid}/sources"] = NoItems + "administrator source settings, no items",
        ["POST /view/admin/shared/reconcile"] = NoItems + "administrator scan, file totals only, no item data",
        ["POST /view/admin/shared/sources"] = NoItems + "administrator source settings, no items",
        ["POST /view/correspondents"] = Refused,
        ["POST /view/document-fields"] = Refused,
        ["POST /view/document-rules"] = Refused,
        ["POST /view/document-rules/apply"] = Refused,
        ["POST /view/document-rules/test"] = Refused,
        ["POST /view/document-types"] = Refused,
        ["POST /view/documents/bulk"] = Refused,
        ["POST /view/galleries"] = Covers(GalleryWrite, "PostAsJsonAsync(\"/view/galleries\""),
        ["POST /view/galleries/{id:guid}/items"] = Covers(GalleryWrite, "PostAsJsonAsync($\"/view/galleries/{gallery.Id:D}/items\""),
        ["POST /view/items/{id:guid}/archive"] = Covers(Lifecycle, "/archive"),
        ["POST /view/items/{id:guid}/inbox/file"] = Refused,
        ["POST /view/items/{id:guid}/restore"] = Covers(Lifecycle, "/restore"),
        ["POST /view/items/{id:guid}/trash"] = Covers(Lifecycle, "/trash"),
        ["POST /view/shared/contributions"] = Covers(ContributionSend, "SubmitAsync"),
        ["POST /view/shared/contributions/{id:guid}/cancel"] = Covers(ContributionService, "CancelAsync"),
        ["POST /view/shared/contributions/{id:guid}/decision"] = Covers(ContributionRoutes, "/decision"),
        ["POST /view/shared/contributions/{id:guid}/retry"] = Covers(ContributionRoutes, "/retry"),
        ["POST /view/shared/contributions/preview"] = Covers(ContributionSend, "PreviewAsync"),
        ["POST /view/shared/items/direct"] = Covers(ContributionSend, "AddDirectAsync"),
        ["POST /view/tags"] = Refused,
        ["POST /view/trash/empty"] = Covers(EmptyTrashWithoutDocuments, "/view/trash/empty"),
        ["POST /view/trash/empty-preview"] = Covers(TrashWithoutDocuments, "/view/trash/empty-preview"),
        ["POST /view/uploads"] = Covers(UploadRefusal, "\"Lease.pdf\""),
        ["PUT /view/admin/profiles/{profileId:guid}/sources/{sourceId:guid}"] = NoItems + "administrator source settings, no items",
        ["PUT /view/admin/shared/sources/{sourceId:guid}"] = NoItems + "administrator source settings, no items",
        ["PUT /view/correspondents/{id:guid}"] = Refused,
        ["PUT /view/document-fields/{id:guid}"] = Refused,
        ["PUT /view/document-rules/{id:guid}"] = Refused,
        ["PUT /view/document-rules/order"] = Refused,
        ["PUT /view/document-types/{id:guid}"] = Refused,
        ["PUT /view/folders/pin"] = NoItems + "stores a path the person typed; never checks that anything is there",
        ["PUT /view/folders/timeline-policy"] = NoItems + "stores a path the person typed; never checks that anything is there",
        ["PUT /view/galleries/{id:guid}"] = Refused,
        ["PUT /view/galleries/{id:guid}/items/{itemId:guid}/position"] = Covers(GalleryWrite, "/position"),
        ["PUT /view/galleries/{id:guid}/shares"] = Refused,
        ["PUT /view/items/{id:guid}/captured-at"] = Refused,
        ["PUT /view/items/{id:guid}/description"] = Refused,
        ["PUT /view/items/{id:guid}/document-details"] = Refused,
        ["PUT /view/items/{id:guid}/favorite"] = Covers(Lifecycle, "/favorite"),
        ["PUT /view/items/{id:guid}/hidden"] = Covers(Lifecycle, "/hidden"),
        ["PUT /view/items/{id:guid}/location"] = Refused,
        ["PUT /view/items/{id:guid}/people"] = Refused,
        ["PUT /view/items/{id:guid}/tags"] = Refused,
        ["PUT /view/preferences"] = NoItems + "the person's own settings",
        ["PUT /view/preferences/documents-sort"] = Refused,
        ["PUT /view/tags/{id:guid}"] = Refused,

        // The paired-app Photos routes are photos and videos only: a document is an id nobody has, for every profile.
        ["GET /api/v1/view/galleries"] = Covers(ClientDocuments, "/api/v1/view/galleries\""),
        ["GET /api/v1/view/galleries/{id:guid}/items"] = Covers(ClientDocuments, "/api/v1/view/galleries/{mixed.Id:D}/items"),
        ["GET /api/v1/view/items/{id:guid}"] = Covers(ClientDocuments, "/api/v1/view/items/{document}{route}"),
        ["GET /api/v1/view/items/{id:guid}/original"] = Covers(ClientDocuments, "\"/original\""),
        ["GET /api/v1/view/items/{id:guid}/playback"] = Covers(ClientDocuments, "\"/playback\""),
        ["GET /api/v1/view/items/{id:guid}/preview"] = Covers(ClientDocuments, "\"/preview\""),
        ["GET /api/v1/view/items/{id:guid}/thumbnail"] = Covers(ClientDocuments, "\"/thumbnail\""),
        ["GET /api/v1/view/people"] = Covers(ClientDocuments, "/api/v1/view/people\""),
        ["GET /api/v1/view/people/{key}/items"] = Covers(ClientDocuments, "/api/v1/view/people/passport%20person/items"),
        ["GET /api/v1/view/places"] = Covers(ClientDocuments, "/api/v1/view/places\""),
        ["GET /api/v1/view/timeline"] = Covers(ClientDocuments, "/api/v1/view/timeline\""),
    };

    [Fact]
    public void EveryViewRouteIsReviewedForDocumentLeaksAndEachClaimNamesARealTest()
    {
        var registered = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/view", StringComparison.Ordinal) == true
                || endpoint.RoutePattern.RawText?.StartsWith("/api/v1/view", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);

        var unreviewed = registered.Except(ReviewedRoutes.Keys).Order().ToList();
        Assert.True(unreviewed.Count == 0,
            "These View routes are new. Decide whether they can show or change a document, make a profile without Documents "
            + "get a refusal or a filtered answer, add a test, and list them in ReviewedRoutes: " + string.Join("; ", unreviewed));
        var stale = ReviewedRoutes.Keys.Except(registered).Order().ToList();
        Assert.True(stale.Count == 0, "ReviewedRoutes lists routes that no longer exist: " + string.Join("; ", stale));

        var testSources = string.Join('\n', Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        foreach (var (route, review) in ReviewedRoutes.Where(entry => !entry.Value.StartsWith(NoItems, StringComparison.Ordinal)))
        {
            var parts = review.Split('|', 2);
            var test = parts[0];
            var start = testSources.IndexOf($"Task {test}(", StringComparison.Ordinal);
            if (start < 0)
            {
                start = testSources.IndexOf($"void {test}(", StringComparison.Ordinal);
            }

            Assert.True(start >= 0, $"{route} names the test '{test}', which does not exist.");
            if (parts.Length == 2)
            {
                var next = testSources.IndexOf("\n    [Fact]", start, StringComparison.Ordinal);
                var body = next < 0 ? testSources[start..] : testSources[start..next];
                Assert.True(body.Contains(parts[1], StringComparison.Ordinal),
                    $"{route} says '{test}' covers it, but that test never mentions '{parts[1]}'.");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
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
            AccountEnabled: true, ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: serverAdmin,
            ActiveProfileIsRestricted: role == "RestrictedProfile",
            ActiveProfileHouseholdId: household, ActiveProfileIsHouseholdAdministrator: householdAdmin);
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

    private async Task<Guid> AddItem(Person person, char seed, string kind, string title)
    {
        var space = await _spaces.GetByOwnerAsync(person.ProfileId);
        var isDocument = kind == LocalAssetMediaKinds.Document;
        var extension = isDocument ? "pdf" : "jpg";
        // A photo shows on the timeline only when it came from a source that is included in it.
        Guid? sourceId = null;
        if (!isDocument)
        {
            sourceId = Guid.NewGuid();
            using var connection = _database.CreateConnection();
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'personal', @spaceId, @libraryId, 'browser_upload', 'Uploads', @sourceKey, 'managed',
                        'Personal/Uploads/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, spaceId = space!.Id, libraryId = person.LibraryId, sourceKey = $"upload:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space!.Id, person.ProfileId, kind, title, null,
            [new LocalAssetFileRegistration($@"C:\items\{seed}.{extension}", new string(seed, 64), $"{seed}.{extension}",
                isDocument ? "application/pdf" : "image/jpeg", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }]))).ItemId;
    }

    /// <summary>An item whose only file comes from a linked (read-only) folder.</summary>
    private async Task<Guid> AddLinkedItem(Person person, char seed, string title, string kind = LocalAssetMediaKinds.Image)
    {
        var space = await _spaces.GetByOwnerAsync(person.ProfileId);
        var isDocument = kind == LocalAssetMediaKinds.Document;
        var extension = isDocument ? "pdf" : "jpg";
        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space!.Id, person.ProfileId, kind, title, null,
            [new LocalAssetFileRegistration($@"D:\Linked\{seed}.{extension}", new string(seed, 64), $"{seed}.{extension}",
                isDocument ? "application/pdf" : "image/jpeg", 1024, DateTimeOffset.UtcNow)
                with { SourceId = InsertPersonalSource(person, space.Id, "linked") }]))).ItemId;
    }

    /// <summary>A Live Photo whose still is managed but whose video comes from a linked folder.</summary>
    private async Task<Guid> AddLivePhotoWithOneLinkedFile(Person person, char seed)
    {
        var space = await _spaces.GetByOwnerAsync(person.ProfileId);
        var other = (char)(seed + 1);
        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space!.Id, person.ProfileId, LocalAssetMediaKinds.Image, "Live", null,
            [
                new LocalAssetFileRegistration($@"C:\items\{seed}.jpg", new string(seed, 64), $"{seed}.jpg", "image/jpeg",
                    1024, DateTimeOffset.UtcNow) with { SourceId = InsertPersonalSource(person, space.Id, "managed") },
                new LocalAssetFileRegistration($@"D:\Linked\{other}.mov", new string(other, 64), $"{other}.mov", "video/quicktime",
                    1024, DateTimeOffset.UtcNow) with
                {
                    Role = LocalAssetFileRoles.LivePhotoVideo,
                    SourceId = InsertPersonalSource(person, space.Id, "linked"),
                },
            ]))).ItemId;
    }

    private Guid InsertPersonalSource(Person person, Guid spaceId, string storageMode)
    {
        var sourceId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_sources
                (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                 relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
            VALUES (@sourceId, 'personal', @spaceId, @libraryId, 'folder', @storageMode, @sourceKey, @storageMode,
                    @relative, @external, 1, 1, @now, @now);
            """, new
        {
            sourceId,
            spaceId,
            libraryId = person.LibraryId,
            storageMode,
            sourceKey = $"test:{sourceId:N}",
            relative = storageMode == "managed" ? "Personal/Uploads/test" : null,
            external = storageMode == "linked" ? @"D:\Linked" : null,
            now = DateTimeOffset.UtcNow,
        });
        // Show the folder's photos on the timeline, so the page tests can see them.
        connection.Execute("""
            INSERT INTO view_source_policies (source_id, include_in_timeline, updated_at) VALUES (@sourceId, 1, @now);
            """, new { sourceId, now = DateTimeOffset.UtcNow });
        return sourceId;
    }

    private async Task<Guid> AddSharedDocument(Guid libraryId, string title, char seed)
    {
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared folder', @sourceKey, 'managed',
                        'Shared/Folders/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, libraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, null, null, LocalAssetMediaKinds.Document, title, null,
            [new LocalAssetFileRegistration($@"C:\shared\{seed}.pdf", new string(seed, 64), $"{seed}.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;
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

    /// <summary>When set, the evaluator refuses every View request, as for a caller whose account may not use View.</summary>
    private bool DenyViewAccess { get; set; }

    private sealed class AllowEvaluator(Func<bool> denyView) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) => ValueTask.FromResult(
                denyView() && requirement.AccountFeature == AccountFeatureId.View
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.ResourceDenied)
                    : AuthorizationDecision.Allow());
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
