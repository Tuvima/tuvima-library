using System.Security.Claims;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Matching Series block's two Engine actions, run through the real route handlers, endpoint filters (resource scope
/// and the ingestion edit guard) and repositories against a temp data store: who may call them, what they return, what
/// they persist, and that the lock-less <c>/library/universe-assign</c> action is gone.
/// </summary>
public sealed class SeriesPlacementEndpointTests : IDisposable
{
    private const string PrincipalClaim = "test-principal";

    // An asset whose library is a real library id is one the caller may see; one with no library is refused.
    private static readonly string VisibleLibrary = Guid.NewGuid().ToString("D");

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly SeriesPlacementRepository _placements;
    private readonly ReviewQueueRepository _reviews;
    private readonly RecordingParentResolver _parents = new();
    private readonly WebApplication _app;

    public SeriesPlacementEndpointTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_placement_endpoint_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _collections = new CollectionRepository(_db);
        var manifests = new SeriesManifestRepository(_db);
        _placements = new SeriesPlacementRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        var canonicals = new CanonicalValueRepository(_db);
        var activity = new SystemActivityRepository(_db);
        var outcomes = new StageOutcomeFactory(_reviews, activity, new SilentEventPublisher(), canonicals, NullLogger<StageOutcomeFactory>.Instance);
        var reviewer = new SeriesAmbiguityReviewer(
            outcomes, _collections, NullLogger<SeriesAmbiguityReviewer>.Instance, new MediaAssetRepository(_db), _reviews, _placements);
        var assignment = new CollectionAssignmentService(
            _collections, canonicals, new WorkRepository(_db), NullLogger<CollectionAssignmentService>.Instance,
            new CanonicalValueArrayRepository(_db), manifests, reviewer, _placements);
        var finalization = new CollectionFinalizationService(assignment, _parents, activity, NullLogger<CollectionFinalizationService>.Instance);
        var choices = new SeriesPlacementChoiceService(
            _placements, manifests, finalization, reviewer, _parents, NullLogger<SeriesPlacementChoiceService>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new ServiceApplicationResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new TestAllowAuthorizationEvaluator());
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IDatabaseConnection>(_db);
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<ISeriesPlacementRepository>(_placements);
        builder.Services.AddSingleton(choices);
        _app = builder.Build();
        var group = _app.MapGroup("/metadata");
        typeof(MetadataEndpoints)
            .GetMethod("MapSeriesPlacementEndpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [group]);
    }

    public void Dispose()
    {
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task BothActions_DemandThePermissionAndTheCallersScopeOnTheItem()
    {
        var get = EndpointOf("GET");
        var put = EndpointOf("PUT");

        Assert.Equal(ApplicationPermissionIds.MetadataRead, RequirementOf(get).Permission);
        Assert.Equal(ApplicationPermissionIds.MetadataWrite, RequirementOf(put).Permission);
        Assert.Contains(get.Metadata.OfType<CatalogueAnyEntityAccessMetadata>(), m => m.Permission == ApplicationPermissionIds.MetadataRead.Value);
        Assert.Contains(put.Metadata.OfType<CatalogueAnyEntityAccessMetadata>(), m => m.Permission == ApplicationPermissionIds.MetadataWrite.Value);

        // And an authenticated service Application may pass the policy of both (the evaluator lets it through).
        Assert.True(await PassesPolicyAsync(get));
        Assert.True(await PassesPolicyAsync(put));
    }

    [Fact]
    public async Task Read_ReturnsTheCurrentSeries_TheSeriesThatListIt_AndTheSearchResultsOfTheSameMediaType()
    {
        var current = AddShelf("QCurrent", "Dune Novels");
        var listing = AddShelf("QListing", "Dune Reading Order");
        AddShelf("QSpice", "Spice Wars");
        AddShelf("QAudio", "Dune Audio", mediaType: "Audiobooks");
        var work = AddWork("Dune");
        await _placements.SetChoiceAsync(work.WorkId, current);
        StoreListing(listing, "QListing", work.WorkId, "Supplementary");

        var (status, body) = await SendAsync("GET", work.WorkId, query: "?q=dune");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(body.GetProperty("supports_series").GetBoolean());
        Assert.True(body.GetProperty("is_person_choice").GetBoolean());
        Assert.Equal("Dune Novels", body.GetProperty("current").GetProperty("label").GetString());
        var listed = Assert.Single(body.GetProperty("listed").EnumerateArray());
        Assert.Equal("Dune Reading Order", listed.GetProperty("label").GetString());
        Assert.Equal("Supplementary", listed.GetProperty("scope").GetString());
        // "dune" matches no other Series of this kind that is not already shown; the Audiobooks Series is never offered.
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());

        var (_, all) = await SendAsync("GET", work.WorkId);
        Assert.Equal(["Spice Wars"], all.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Read_OffersOnlySeriesTheCallerMaySee()
    {
        AddShelf("QOpen", "Open Series");
        AddShelf("QHidden", "Hidden Series", visible: false);
        var work = AddWork("Dune");

        var (_, body) = await SendAsync("GET", work.WorkId);

        Assert.Equal(["Open Series"], body.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Read_ForATvEpisode_SaysSeriesAreNotSupported()
    {
        var episode = AddWork("Episode", mediaType: "TV");

        var (status, body) = await SendAsync("GET", episode.WorkId);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(body.GetProperty("supports_series").GetBoolean());
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task ACallerWhoCannotSeeTheItem_GetsNotFound_OnBothActions()
    {
        var shelf = AddShelf("QOpen", "Open Series");
        var hidden = AddWork("Not yours", visible: false);

        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync("GET", hidden.WorkId)).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync("PUT", hidden.WorkId, new { collection_id = shelf, locked = true })).Status);
        Assert.Null(await _placements.GetLockAsync(hidden.WorkId));
    }

    [Fact]
    public async Task Choosing_AShelfOfAnotherMediaTypeOrNotASeries_IsA400_AndNothingChanges()
    {
        var audiobookShelf = AddShelf("QAudio", "Audio", mediaType: "Audiobooks");
        var work = AddWork("Dune");

        var (status, _) = await SendAsync("PUT", work.WorkId, new { collection_id = audiobookShelf, locked = true });
        var tv = AddWork("Episode", mediaType: "TV");
        var (tvStatus, _) = await SendAsync("PUT", tv.WorkId, new { collection_id = audiobookShelf, locked = true });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(StatusCodes.Status400BadRequest, tvStatus);
        Assert.Null(await _placements.GetLockAsync(work.WorkId));
    }

    [Fact]
    public async Task Choosing_ASeriesTheCallerCannotSee_IsNotFound()
    {
        var hiddenShelf = AddShelf("QHidden", "Hidden Series", visible: false);
        var work = AddWork("Dune");

        var (status, _) = await SendAsync("PUT", work.WorkId, new { collection_id = hiddenShelf, locked = true });

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Null(await _placements.GetLockAsync(work.WorkId));
    }

    [Fact]
    public async Task AnItemThatIsStillBeingIngested_CannotBeChanged_ThenCan()
    {
        var shelf = AddShelf("QOpen", "Open Series");
        var work = AddWork("Dune");
        var job = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state) VALUES(@id,@asset,'MediaAsset','Books','Queued');",
                new { id = job, asset = work.AssetId });
        }

        var (blocked, _) = await SendAsync("PUT", work.WorkId, new { collection_id = shelf, locked = true });
        Assert.Equal(StatusCodes.Status409Conflict, blocked);
        Assert.Null(await _placements.GetLockAsync(work.WorkId));

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE identity_jobs SET state='Ready' WHERE id=@job;", new { job });
        }

        var (allowed, _) = await SendAsync("PUT", work.WorkId, new { collection_id = shelf, locked = true });
        Assert.Equal(StatusCodes.Status200OK, allowed);
    }

    [Fact]
    public async Task ASavedChoice_Persists_ShowsAsThePersonsChoice_AndRunsTheUniverseStepForTheNewAndTheOldShelf()
    {
        var old = AddShelf("QOld", "Old Series");
        var chosen = AddShelf("QChosen", "Chosen Series");
        var work = AddWork("Dune");
        await SetShelfAsync(work.WorkId, old);

        var (status, body) = await SendAsync("PUT", work.EditionId, new { collection_id = chosen, locked = true });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(body.GetProperty("is_person_choice").GetBoolean());
        Assert.Equal("Chosen Series", body.GetProperty("current").GetProperty("label").GetString());
        Assert.Equal(new SeriesPlacementLock(chosen, "QChosen", "Books"), await _placements.GetLockAsync(work.WorkId));
        Assert.Equal(chosen, await ShelfOfAsync(work.WorkId));
        Assert.Contains(chosen, _parents.Resolved);
        Assert.Contains(old, _parents.Resolved);
    }

    [Fact]
    public async Task ANoSeriesChoice_Persists()
    {
        var shelf = AddShelf("QOld", "Old Series");
        var work = AddWork("Dune");
        await SetShelfAsync(work.WorkId, shelf);

        var (status, body) = await SendAsync("PUT", work.AssetId, new { collection_id = (Guid?)null, locked = true });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(body.GetProperty("is_person_choice").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("current").ValueKind);
        Assert.True((await _placements.GetLockAsync(work.WorkId))!.IsNoSeries);
        Assert.Null(await ShelfOfAsync(work.WorkId));
    }

    [Fact]
    public async Task LettingTheLibraryDecide_ReleasesTheChoice_AndPlacesTheWorkFromItsEvidenceAgain()
    {
        var evidence = AddShelf("Q9", "Dune");
        var chosen = AddShelf("QChosen", "Chosen Series");
        var work = AddWork("Dune", seriesQid: "Q9");
        await SendAsync("PUT", work.WorkId, new { collection_id = chosen, locked = true });
        Assert.Equal(chosen, await ShelfOfAsync(work.WorkId));

        var (status, body) = await SendAsync("PUT", work.WorkId, new { collection_id = (Guid?)null, locked = false });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(body.GetProperty("is_person_choice").GetBoolean());
        Assert.Null(await _placements.GetLockAsync(work.WorkId));
        Assert.Equal(evidence, await ShelfOfAsync(work.WorkId));
        Assert.Contains(evidence, _parents.Resolved);
    }

    [Fact]
    public void TheLockLessUniverseAssignAction_IsGone_SoNothingCanMoveAWorkAroundAPersonsChoice()
    {
        var root = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Endpoints", "LibraryEndpoints.cs"));

        Assert.DoesNotContain("universe-assign", library, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignWorkToCollectionAsync", library, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Api", "Models", "UniverseDtos.cs")));
    }

    [Fact]
    public void TheWireNamesAreSnakeCase()
    {
        var request = JsonSerializer.Serialize(new SeriesPlacementChoiceRequestDto { CollectionId = Guid.Empty, Locked = true });
        var response = JsonSerializer.Serialize(new SeriesPlacementDto { SupportsSeries = true });

        Assert.Contains("\"collection_id\"", request, StringComparison.Ordinal);
        Assert.Contains("\"locked\":true", request, StringComparison.Ordinal);
        Assert.Contains("\"supports_series\":true", response, StringComparison.Ordinal);
        Assert.Contains("\"is_person_choice\"", response, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Host helpers: the real endpoint delegate, run the way the other endpoint tests do (no network)
    // ----------------------------------------------------------------------------------------------------------------

    private RouteEndpoint EndpointOf(string method) =>
        Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(), candidate =>
            candidate.RoutePattern.RawText == "/metadata/{entityId:guid}/series-placement"
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static AdministratorOrApplicationRequirement RequirementOf(RouteEndpoint endpoint) =>
        Assert.Single(endpoint.Metadata.OfType<AuthorizationPolicy>().SelectMany(policy => policy.Requirements).OfType<AdministratorOrApplicationRequirement>());

    private async Task<bool> PassesPolicyAsync(RouteEndpoint endpoint)
    {
        var user = Principal();
        var context = new DefaultHttpContext { RequestServices = _app.Services, User = user };
        var policy = await AuthorizationPolicy.CombineAsync(
            _app.Services.GetRequiredService<IAuthorizationPolicyProvider>(),
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.NotNull(policy);
        return (await _app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, context, policy)).Succeeded;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([new Claim(PrincipalClaim, "service")], "test"));

    private async Task<(int Status, JsonElement Body)> SendAsync(string method, Guid entityId, object? body = null, string query = "")
    {
        var endpoint = EndpointOf(method);
        var responseBody = new MemoryStream();
        await using var scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = Principal(),
            Response = { Body = responseBody },
        };
        context.Request.Method = method;
        context.Request.Path = $"/metadata/{entityId:D}/series-placement";
        context.Request.QueryString = new QueryString(query);
        context.Request.RouteValues = new RouteValueDictionary { ["entityId"] = entityId.ToString("D") };
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }

        context.SetEndpoint(endpoint);
        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (context.Response.StatusCode, json);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Data
    // ----------------------------------------------------------------------------------------------------------------

    private sealed record Seeded(Guid WorkId, Guid EditionId, Guid AssetId);

    private Seeded AddWork(string title, string mediaType = "Books", bool visible = true, string? seriesQid = null, Guid? shelf = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@workId, @shelf, @mediaType, 'standalone');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Edition');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, @hash, @path, @library);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'media_type', @mediaType, @now);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                shelf,
                mediaType,
                title,
                hash = Guid.NewGuid().ToString("N"),
                path = $"{title}-{assetId:N}.bin",
                library = visible ? VisibleLibrary : null,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        if (seriesQid is not null)
        {
            foreach (var (key, value) in new[] { ("series_qid", seriesQid), ("series", "A Series") })
            {
                conn.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @key, @value, @now);",
                    new { workId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
            }
        }

        return new Seeded(workId, editionId, assetId);
    }

    /// <summary>A Series shelf with one member work whose file the caller may (or may not) see; a shelf nobody can see is not offered.</summary>
    private Guid AddShelf(string qid, string name, string mediaType = "Books", bool visible = true)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = qid,
            MediaType = mediaType,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        _collections.UpsertAsync(collection).GetAwaiter().GetResult();
        AddWork($"{name} member", mediaType, visible, shelf: collection.Id);
        return collection.Id;
    }

    private void StoreListing(Guid shelf, string seriesQid, Guid workId, string scope)
    {
        var now = DateTimeOffset.UtcNow;
        new SeriesManifestRepository(_db).UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = seriesQid,
                CollectionId = shelf,
                SeriesLabel = seriesQid,
                WarningsJson = "[]",
                ApiMetadataJson = "{}",
                LastHydratedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            [
                new SeriesManifestItemRecord
                {
                    Id = Guid.NewGuid(),
                    CollectionId = shelf,
                    SeriesQid = seriesQid,
                    ItemQid = "Q-item",
                    ItemLabel = "Item",
                    MediaType = "Books",
                    SortOrder = 1,
                    OrderSource = "SeriesOrdinal",
                    OwnershipState = "Owned",
                    LinkedWorkId = workId,
                    MembershipScope = scope,
                    SourcePropertiesJson = "[]",
                    RelationshipsJson = "[]",
                    LastHydratedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ]).GetAwaiter().GetResult();
    }

    private async Task SetShelfAsync(Guid workId, Guid shelf)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE works SET collection_id = @shelf WHERE id = @workId;", new { shelf, workId });
    }

    private async Task<Guid?> ShelfOfAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>(
            "-- readiness: test lookup of one work by id\nSELECT collection_id FROM works WHERE id = @workId;", new { workId });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ServiceApplicationResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(PrincipalKind.ServiceApplication, true, null, null, Guid.NewGuid(), ApplicationEnabled: true));
    }

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class RecordingParentResolver : IParentCollectionResolver
    {
        public List<Guid> Resolved { get; } = [];

        public Task ResolveParentCollectionAsync(Guid collectionId, CancellationToken ct = default)
        {
            Resolved.Add(collectionId);
            return Task.CompletedTask;
        }
    }

    private sealed class SilentEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }
}
