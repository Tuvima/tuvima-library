using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>Exercises the mapped editor endpoints with their real repositories and resource admission path.</summary>
public sealed class SharedEntityEditorEndpointIntegrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_shared_editor_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public SharedEntityEditorEndpointIntegrationTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    [Fact]
    public async Task MappedRoutes_DenyRawRootWithoutOwnedProvenance_AndPageOnlyVisibleEntities()
    {
        var libraryId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var eventOne = Guid.NewGuid();
        var eventTwo = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        await using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO narrative_roots (qid, label, level, created_at)
            VALUES ('Q-owned-root', 'Owned universe', 'Universe', datetime('now')),
                   ('Q-raw-only', 'Raw only universe', 'Universe', datetime('now'));
            INSERT INTO works (id, media_type, work_kind, ownership, wikidata_qid)
            VALUES (@workId, 'Books', 'standalone', 'Owned', 'Q-owned-work');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, 'shared-editor-route', 'books/shared-editor.epub', @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@workId, 'narrative_root_qid', 'Q-owned-root', datetime('now')),
                   (@eventOne, 'point_in_time', '+0019-01-01T00:00:00Z', datetime('now')),
                   (@eventOne, 'start_time', '+0018-01-01T00:00:00Z', datetime('now')),
                   (@eventOne, 'end_time', '+0020-01-01T00:00:00Z', datetime('now')),
                   (@eventOne, 'time_index', 'battle-12', datetime('now'));
            INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
            VALUES (@eventOne, 'Q-event-one', 'First event', 'Event', 'Q-owned-root', datetime('now')),
                   (@eventTwo, 'Q-event-two', 'Second event', 'Event', 'Q-owned-root', datetime('now')),
                   (@objectId, 'Q-object-one', 'First object', 'Object', 'Q-owned-root', datetime('now'));
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid, provenance)
            VALUES (randomblob(16), 'event-one-work', @eventOne, 'Q-owned-work', 'Wikidata'),
                   (randomblob(16), 'event-two-work', @eventTwo, 'Q-owned-work', 'Wikidata'),
                   (randomblob(16), 'object-one-work', @objectId, 'Q-owned-work', 'Wikidata');
            """, new { workId, editionId, assetId, libraryId = libraryId.ToString("D"), eventOne, eventTwo, objectId });

        var artwork = new EntityAssetRepository(_database);
        var existingArtworkId = Guid.NewGuid();
        await artwork.UpsertAsync(new EntityAsset
        {
            Id = existingArtworkId,
            EntityId = eventOne.ToString(),
            EntityType = "FictionalEntity",
            AssetTypeValue = "CoverArt",
            ImageUrl = "https://example.test/user-existing.jpg",
            SourceProvider = "user_upload",
            OwnerScope = "FictionalEntity",
            IsUserOverride = true,
            IsPreferred = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var lore = new PluginLoreRepository(_database);
        for (var index = 0; index < 101; index++)
        {
            var source = await lore.AddManualSourceAsync("Q-owned-root", "test-lore", $"Approved lore {index:D3}", $"https://lore{index:D3}.example.test", "https://example.test/api");
            await lore.SetSourceStatusAsync(source.Id, PluginLoreSourceStatus.Approved, "test");
            await lore.UpsertExtractionResultAsync(source,
            [new PluginLoreEntityRecord { ExternalKey = $"event-{index}", WikidataQid = "Q-event-one", Label = "First event", EntityType = "Event", SourceUrl = $"https://example.test/lore/{index}/event", Confidence = 0.8 }], []);
        }
        var pendingLore = await lore.AddManualSourceAsync("Q-owned-root", "test-lore", "Pending lore", "https://example.test/pending", "https://example.test/api");
        await lore.UpsertExtractionResultAsync(pendingLore,
        [new PluginLoreEntityRecord { ExternalKey = "pending-event", WikidataQid = "Q-event-one", Label = "First event", EntityType = "Event", SourceUrl = "https://example.test/pending/event", Confidence = 0.1 }], []);
        var unrelatedLore = await lore.AddManualSourceAsync("Q-owned-root", "test-lore", "Unrelated lore", "https://example.test/unrelated", "https://example.test/api");
        await lore.SetSourceStatusAsync(unrelatedLore.Id, PluginLoreSourceStatus.Approved, "test");
        await lore.UpsertExtractionResultAsync(unrelatedLore,
        [new PluginLoreEntityRecord { ExternalKey = "object", WikidataQid = "Q-object-one", Label = "First object", EntityType = "Object", SourceUrl = "https://example.test/unrelated/object", Confidence = 0.8 }], []);

        await using var app = await StartAsync(libraryId, workId);
        using var client = new HttpClient { BaseAddress = app.Address };

        using var raw = await client.GetAsync("/entity-editor/universes/Q-raw-only/context");
        using var context = await client.GetAsync("/entity-editor/universes/Q-owned-root/context");
        using var page = await client.GetAsync("/entity-editor/universes/Q-owned-root/entities?category=Event&offset=0&limit=1");
        using var mismatched = await client.PostAsync($"/entity-editor/universes/Q-raw-only/entities/{eventOne:D}/refresh", null);
        using var eventRefresh = await client.PostAsync($"/entity-editor/universes/Q-owned-root/entities/{eventOne:D}/refresh", null);
        using var objectRefresh = await client.PostAsync($"/entity-editor/universes/Q-owned-root/entities/{objectId:D}/refresh", null);
        using var timeline = await client.GetAsync($"/entity-editor/universes/Q-owned-root/entities/{eventOne:D}/timeline");
        using var sources = await client.GetAsync($"/entity-editor/universes/Q-owned-root/entities/{eventOne:D}/sources");
        using var artworkUpdate = await client.PutAsync(
            $"/entity-editor/universes/Q-owned-root/entities/{eventOne:D}/artwork",
            new StringContent("""{"asset_type":"CoverArt","image_url":"https://example.test/user-new.jpg","preferred":false}""", Encoding.UTF8, "application/json"));

        Assert.True(raw.StatusCode == HttpStatusCode.NotFound, await raw.Content.ReadAsStringAsync());
        Assert.True(context.StatusCode == HttpStatusCode.OK, await context.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(mismatched.StatusCode == HttpStatusCode.NotFound, await mismatched.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, eventRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, objectRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sources.StatusCode);
        Assert.Equal(HttpStatusCode.OK, artworkUpdate.StatusCode);
        using var json = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        Assert.True(json.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("items").GetArrayLength());
        var requests = app.Harvesting.Requests;
        Assert.Contains(requests, request => request.EntityId == eventOne && request.EntityType == EntityType.Event && request.Hints[BridgeIdKeys.WikidataQid] == "Q-event-one");
        Assert.Contains(requests, request => request.EntityId == objectId && request.EntityType == EntityType.Object && request.Hints[BridgeIdKeys.WikidataQid] == "Q-object-one");
        var timelineJson = await timeline.Content.ReadAsStringAsync();
        Assert.Contains("+0019-01-01T00:00:00Z", timelineJson, StringComparison.Ordinal);
        Assert.Contains("+0018-01-01T00:00:00Z", timelineJson, StringComparison.Ordinal);
        Assert.Contains("+0020-01-01T00:00:00Z", timelineJson, StringComparison.Ordinal);
        Assert.Contains("battle-12", timelineJson, StringComparison.Ordinal);
        using var sourceJson = JsonDocument.Parse(await sources.Content.ReadAsStringAsync());
        var pluginSources = sourceJson.RootElement.EnumerateArray()
            .Where(source => source.GetProperty("kind").GetString() == "plugin_lore")
            .ToList();
        Assert.Equal(100, pluginSources.Count);
        Assert.All(pluginSources, source =>
        {
            Assert.True(source.GetProperty("supplemental").GetBoolean());
            Assert.True(source.TryGetProperty("source_url", out var url) && url.GetString()!.StartsWith("https://lore", StringComparison.Ordinal));
            Assert.DoesNotContain("Pending lore", source.GetProperty("value").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unrelated lore", source.GetProperty("value").GetString(), StringComparison.Ordinal);
        });
        var persistedArtwork = await artwork.GetByEntityAsync(eventOne.ToString(), null);
        Assert.Contains(persistedArtwork, asset => asset.Id == existingArtworkId && asset.IsUserOverride && asset.IsPreferred && asset.ImageUrl == "https://example.test/user-existing.jpg");
        Assert.Contains(persistedArtwork, asset => asset.Id != existingArtworkId && asset.IsUserOverride && !asset.IsPreferred && asset.ImageUrl == "https://example.test/user-new.jpg");

        await using var deniedApp = await StartAsync(Guid.NewGuid(), workId);
        using var deniedClient = new HttpClient { BaseAddress = deniedApp.Address };
        using var deniedContext = await deniedClient.GetAsync("/entity-editor/universes/Q-owned-root/context");
        using var deniedSelector = await deniedClient.GetAsync("/entity-editor/universes/Q-owned-root/entities?category=Event");
        Assert.Equal(HttpStatusCode.NotFound, deniedContext.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedSelector.StatusCode);
    }

    private async Task<TestApplication> StartAsync(Guid libraryId, Guid workId)
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: Guid.NewGuid(),
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<INarrativeRootRepository, NarrativeRootRepository>();
        builder.Services.AddSingleton<IFictionalEntityRepository, FictionalEntityRepository>();
        builder.Services.AddSingleton<IEntityRelationshipRepository, EntityRelationshipRepository>();
        builder.Services.AddSingleton<ICanonicalValueRepository, CanonicalValueRepository>();
        builder.Services.AddSingleton<IEntityAssetRepository, EntityAssetRepository>();
        builder.Services.AddSingleton<IEntityTimelineRepository, EntityTimelineRepository>();
        builder.Services.AddSingleton<IPluginLoreRepository, PluginLoreRepository>();
        var harvesting = new RecordingHarvestingService();
        builder.Services.AddSingleton<IMetadataHarvestingService>(harvesting);
        builder.Services.AddSingleton<ArtworkScopeService>(_ => null!);
        builder.Services.AddSingleton<IDisplayProjectionReadService>(new DisplayStub([new DisplayWorkRow { WorkId = workId, IdentityQid = "Q-owned-work" }]));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(libraryId));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedEntityEditorEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()), harvesting);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed class DisplayStub(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(requestedLibraryId == libraryId ? AuthorizationDecision.Allow() : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "shared-editor-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class AllowAdministratorOrApplicationHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHarvestingService : IMetadataHarvestingService
    {
        public List<HarvestRequest> Requests { get; } = [];
        public int PendingCount => 0;
        public ValueTask EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }
        public Task ProcessSynchronousAsync(HarvestRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestApplication(WebApplication app, Uri address, RecordingHarvestingService harvesting) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public RecordingHarvestingService Harvesting { get; } = harvesting;
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
