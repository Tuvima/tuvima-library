using System.Net;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class MetadataResourceAdmissionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_metadata_admission_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public MetadataResourceAdmissionTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task MappedMetadataReadHidesDeniedAndMissingResourcesIdentically()
    {
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        var allowedWork = await InsertWorkAsync(allowedLibrary);
        var deniedWork = await InsertWorkAsync(deniedLibrary);
        await using var app = await StartAsync(allowedLibrary);
        using var client = new HttpClient { BaseAddress = app.Address };

        using var allowed = await client.GetAsync($"/metadata/claims/{allowedWork.WorkId:D}");
        using var allowedEdition = await client.GetAsync($"/metadata/claims/{allowedWork.EditionId:D}");
        using var denied = await client.GetAsync($"/metadata/claims/{deniedWork.WorkId:D}");
        using var deniedEdition = await client.GetAsync($"/metadata/claims/{deniedWork.EditionId:D}");
        using var missing = await client.GetAsync($"/metadata/claims/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowedEdition.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedEdition.StatusCode);
        Assert.Equal(missing.StatusCode, denied.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RejectedMappedOverrideDoesNotInvokeMutationDelegate()
    {
        var allowedLibrary = Guid.NewGuid();
        var deniedWork = await InsertWorkAsync(Guid.NewGuid());
        var allowedWork = await InsertWorkAsync(allowedLibrary);
        await using var app = await StartAsync(allowedLibrary);
        using var client = new HttpClient { BaseAddress = app.Address };

        using var denied = await client.PutAsync($"/metadata/{deniedWork.WorkId:D}/override", null);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, app.MutationCount);

        using var allowed = await client.PutAsync($"/metadata/{allowedWork.WorkId:D}/override", null);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(1, app.MutationCount);
    }

    [Fact]
    public async Task ScopedProviderRefreshRechecksResolvedAssetWhenWorkHasMixedLibraries()
    {
        var allowedLibrary = Guid.NewGuid();
        var work = await InsertWorkAsync(allowedLibrary);
        var deniedAsset = await InsertAssetAsync(work.WorkId, Guid.NewGuid());
        await using var app = await StartAsync(allowedLibrary);
        using var client = new HttpClient { BaseAddress = app.Address };

        using var denied = await client.PostAsync(
            $"/metadata/{work.WorkId:D}/artwork/item/refresh-provider?targetAssetId={deniedAsset:D}", null);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, app.MutationCount);

        using var allowed = await client.PostAsync(
            $"/metadata/{work.WorkId:D}/artwork/item/refresh-provider?targetAssetId={work.AssetId:D}", null);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(1, app.MutationCount);
    }

    [Fact]
    public void MetadataAndSequenceEndpointsAttachResourceAdmissionBeforeTheirHandlers()
    {
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");
        var details = ReadSource("src/MediaEngine.Api/Endpoints/DetailEndpoints.cs");

        AssertEndpointGuard(metadata, "GetClaimHistory", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "HydrateEntity", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRun)");
        AssertEndpointGuard(metadata, "OverrideMetadata", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "ReclassifyMediaType", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "GetMediaEditorContext", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "GetScopedArtworkEditor", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "RefreshScopedProviderArtwork", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRun)");
        AssertEndpointGuard(metadata, "GetArtworkEditor", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "UploadCover", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "UploadScopedArtwork", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "UploadScopedArtworkFromUrl", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "UploadEntityArtwork", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "GetSearchResultsCache", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "PutSearchResultsCache", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch)");
        AssertEndpointGuard(metadata, "GetCanonicalValues", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead)");
        AssertEndpointGuard(metadata, "CoverFromUrl", "RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "SetPreferredArtwork", "RequireCatalogueArtworkAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(metadata, "DeleteArtworkVariant", "RequireCatalogueArtworkAccess(ApplicationPermissionIds.MetadataWrite)");
        AssertEndpointGuard(details, "SetDetailDefaultSequence", "RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, \"id\")");
        AssertEndpointGuard(metadata, "GetConflicts", "RequireEffectiveAdministrator()");
        Assert.Contains("resources.EvaluateAnyEntityAsync(", metadata, StringComparison.Ordinal);
    }

    private async Task<OwnedWork> InsertWorkAsync(Guid libraryId)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, 'Book', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                libraryId = libraryId.ToString("D"),
            });
        return new OwnedWork(workId, editionId, assetId);
    }

    private async Task<Guid> InsertAssetAsync(Guid workId, Guid libraryId)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                libraryId = libraryId.ToString("D"),
            });
        return assetId;
    }

    private async Task<TestApplication> StartAsync(Guid allowedLibrary)
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new ResourceDecisions(allowedLibrary));
        builder.Services.AddSingleton<IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        var app = builder.Build();
        var mutations = new MutationCounter();
        app.MapGet("/metadata/claims/{entityId:guid}", () => Results.Ok())
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
        app.MapPut("/metadata/{entityId:guid}/override", () =>
            {
                mutations.Count++;
                return Results.NoContent();
            })
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
        app.MapPost("/metadata/{entityId:guid}/artwork/{scopeId}/refresh-provider", async (
                Guid targetAssetId,
                HttpContext httpContext,
                CatalogueResourceAuthorizationService resources) =>
            {
                if (await resources.EvaluateAssetAsync(
                        httpContext,
                        targetAssetId,
                        ApplicationPermissionIds.MetadataEnrichmentRun,
                        httpContext.RequestAborted) != CatalogueResourceAccess.Allowed)
                {
                    return Results.NotFound();
                }

                mutations.Count++;
                return Results.NoContent();
            })
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRun);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()), mutations);
    }

    private static void AssertEndpointGuard(string source, string endpointName, string guard)
    {
        var start = source.IndexOf($".WithName(\"{endpointName}\")", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Endpoint '{endpointName}' was not found.");
        var end = source.IndexOf(';', start);
        Assert.True(end > start, $"Endpoint '{endpointName}' has no route terminator.");
        Assert.Contains(guard, source[start..end], StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // SQLite can release a pooled test handle just after disposal; the OS temp sweep removes the file.
        }
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class ResourceDecisions(Guid allowedLibrary) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority,
            AccountFeatureId feature,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(feature == AccountFeatureId.Read
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingFeatureGrant));

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority,
            Guid libraryId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(libraryId == allowedLibrary
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority,
            bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowAuthorizationEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class MutationCounter
    {
        public int Count { get; set; }
    }

    private sealed record OwnedWork(Guid WorkId, Guid EditionId, Guid AssetId);

    private sealed class TestApplication(
        WebApplication app,
        Uri address,
        MutationCounter mutations) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public int MutationCount => mutations.Count;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
