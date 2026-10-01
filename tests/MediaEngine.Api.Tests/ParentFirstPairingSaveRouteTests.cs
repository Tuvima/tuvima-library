using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed class ParentFirstPairingSaveRouteTests
{
    [Fact]
    public async Task SaveRoute_RejectsTamperedCandidate_ThenCommitsCatalogTargetAsPending()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_pairing_route_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var libraryId = Guid.NewGuid();
            var show = Guid.NewGuid();
            var season = Guid.NewGuid();
            var sourceWork = Guid.NewGuid();
            var targetWork = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@show, 'TV', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@season, 'TV', 'parent', @show, 1, 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@sourceWork, 'TV', 'child', @season, 1, 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@targetWork, 'TV', 'catalog', @season, 2, 'Unowned');
                    INSERT INTO editions (id, work_id) VALUES (@edition, @sourceWork);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                    VALUES (@asset, @edition, 'pairing-route-source', '/tv/Show.S01E02.mkv', @libraryId);
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@showBridge, @show, 'tvdb_id', '42'),
                      (@sourceBridge, @sourceWork, 'tvdb_episode_id', '101'),
                      (@targetBridge, @targetWork, 'tvdb_episode_id', '102');
                    """, new { show, season, sourceWork, targetWork, edition, asset,
                    libraryId = libraryId.ToString("D"), showBridge = Guid.NewGuid(),
                    sourceBridge = Guid.NewGuid(), targetBridge = Guid.NewGuid() });
            }

            var actor = new RequestAuthority(PrincipalKind.Human, true,
                AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(), SessionId: Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true);
            var resolver = new MutableAuthorityResolver(actor);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
            builder.Services.AddSingleton<IDatabaseConnection>(database);
            builder.Services.AddSingleton<PairingAssetReadService>();
            builder.Services.AddSingleton<TvPairingLocalTargetReadService>();
            builder.Services.AddSingleton<EpisodeStillReviewReadService>();
            builder.Services.AddSingleton<IMediaEditorOwnedChildReadService, MediaEditorOwnedChildReadService>();
            builder.Services.AddSingleton<IRequestAuthorityResolver>(resolver);
            builder.Services.AddSingleton<IAccountAccessDecisionService>(new AllowLibraryAccess(libraryId));
            builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
            builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
            builder.Services.AddSingleton<MediaEditorCommitRepository>();
            builder.Services.AddSingleton<MusicPairingCommitRepository>();
            builder.Services.AddMemoryCache();
            // The read-only preview route requires these service registrations;
            // this test never resolves them or makes a provider request.
            builder.Services.AddSingleton(new TvdbRetailClient(null!, null!, null!));
            builder.Services.AddSingleton(new MusicBrainzReleaseClient(null!, null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzReleaseClient>.Instance));
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            var map = typeof(MetadataEndpoints).GetMethod("MapParentFirstPairingPreviewEndpoints",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            map.Invoke(null, [app.MapGroup("/metadata")]);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new HttpClient { BaseAddress = address };

            // An album Work may identify an original release while one of its
            // track Editions is unscoped. That Work hint must not silently
            // default the whole reviewed selection to the original release.
            var album = Guid.NewGuid();
            var track = Guid.NewGuid();
            var scopedEdition = Guid.NewGuid();
            var unscopedEdition = Guid.NewGuid();
            var scopedAsset = Guid.NewGuid();
            var unscopedAsset = Guid.NewGuid();
            var albumRelease = Guid.NewGuid().ToString("D");
            using (var musicSetup = database.CreateConnection())
                musicSetup.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES
                      (@album, 'Music', 'parent', 'Owned'),
                      (@track, 'Music', 'child', 'Owned');
                    UPDATE works SET parent_work_id=@album WHERE id=@track;
                    INSERT INTO editions (id, work_id) VALUES
                      (@scopedEdition, @track), (@unscopedEdition, @track);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES
                      (@scopedAsset, @scopedEdition, 'scoped-music', '/music/scoped.flac', @libraryId),
                      (@unscopedAsset, @unscopedEdition, 'unscoped-music', '/music/unscoped.flac', @libraryId);
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@albumBridge, @album, 'musicbrainz_release_id', @albumRelease),
                      (@editionBridge, @scopedEdition, 'musicbrainz_release_id', @albumRelease);
                    """, new { album, track, scopedEdition, unscopedEdition,
                    scopedAsset, unscopedAsset, albumRelease,
                    libraryId = libraryId.ToString("D"), albumBridge = Guid.NewGuid(),
                    editionBridge = Guid.NewGuid() });
            var musicRevisions = await app.Services.GetRequiredService<IMediaEditorOwnedChildReadService>()
                .GetSelectionRevisionsForAssetsAsync(album, [scopedAsset, unscopedAsset], CancellationToken.None);
            using var unscopedMusic = await client.PostAsJsonAsync($"/metadata/{album:D}/pairing-preview",
                new MediaEditorPairingPreviewRequestDto([scopedAsset, unscopedAsset], null,
                    musicRevisions));
            Assert.Equal(HttpStatusCode.Conflict, unscopedMusic.StatusCode);

            using var missingRevisions = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-preview",
                new MediaEditorPairingPreviewRequestDto([asset], "42", null));
            Assert.Equal(HttpStatusCode.BadRequest, missingRevisions.StatusCode);

            using var staleSelection = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-preview",
                new MediaEditorPairingPreviewRequestDto([asset], "42",
                    new Dictionary<Guid, string> { [asset] = "v1:stale" }));
            Assert.Equal(HttpStatusCode.Conflict, staleSelection.StatusCode);

            var source = new PairingAssetReadService(database).Load([asset], CancellationToken.None);
            var catalog = new[]
            {
                new PairingCatalogueChild("102", "42", "tvdb", "Second",
                    SeasonNumber: 1, EpisodeNumber: 2),
                new PairingCatalogueChild("201", "42", "tvdb", "Next Season",
                    SeasonNumber: 2, EpisodeNumber: 1),
            };
            var targets = new TvPairingLocalTargetReadService(database)
                .Resolve(show, "42", catalog, CancellationToken.None);
            Assert.Equal("catalog", targets["102"].WorkKind);
            Assert.Equal(0, targets["102"].ActualAssetCount);
            var receipt = new TvPairingReviewTokenService(app.Services.GetRequiredService<IMemoryCache>())
                .Store(show, actor, null, "42", show, source, targets, catalog);

            using var searched = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-children",
                new MediaEditorPairingChildSearchRequestDto(receipt.Token, asset, "S02E01"));
            Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
            var search = await searched.Content.ReadFromJsonAsync<MediaEditorPairingChildSearchDto>();
            Assert.Equal("201", Assert.Single(search!.Items).Child.ChildId);
            Assert.False(search.Items[0].CanSave);
            using var catalogSeasonSearch = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-children",
                new MediaEditorPairingChildSearchRequestDto(receipt.Token, asset, "S01E02"));
            Assert.Equal(HttpStatusCode.OK, catalogSeasonSearch.StatusCode);
            var catalogSeasonResult = await catalogSeasonSearch.Content
                .ReadFromJsonAsync<MediaEditorPairingChildSearchDto>();
            Assert.Equal(targets["102"].SeasonWorkId,
                Assert.Single(catalogSeasonResult!.Items).Child.LocalSeasonWorkId);

            using var tampered = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(receipt.Token, Guid.NewGuid().ToString("D"),
                    [new MediaEditorPairingAcceptedDto(asset, "999")], []));
            Assert.Equal(HttpStatusCode.Conflict, tampered.StatusCode);
            using (var verify = database.CreateConnection())
                Assert.Equal(sourceWork, verify.QuerySingle<Guid>("SELECT work_id FROM editions WHERE id=@edition", new { edition }));

            var reviewedRevision = await app.Services.GetRequiredService<IMediaEditorOwnedChildReadService>()
                .GetSelectionRevisionsForAssetsAsync(show, [asset], CancellationToken.None);
            var staleReviewReceipt = new TvPairingReviewTokenService(app.Services.GetRequiredService<IMemoryCache>())
                .Store(show, actor, null, "42", show, source, targets, catalog, reviewedRevision);
            using (var change = database.CreateConnection())
                change.Execute("""
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@asset, 'identity_revision', 'changed-after-preview', datetime('now'));
                    """, new { asset });
            using var changedBeforeSave = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(staleReviewReceipt.Token, Guid.NewGuid().ToString("D"),
                    [new MediaEditorPairingAcceptedDto(asset, "102")], []));
            Assert.Equal(HttpStatusCode.Conflict, changedBeforeSave.StatusCode);
            using (var verify = database.CreateConnection())
                Assert.Equal(sourceWork, verify.QuerySingle<Guid>("SELECT work_id FROM editions WHERE id=@edition", new { edition }));

            resolver.Current = actor with { SessionId = Guid.NewGuid() };
            using var switchedActor = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(receipt.Token, Guid.NewGuid().ToString("D"),
                    [new MediaEditorPairingAcceptedDto(asset, "102")], []));
            Assert.Equal(HttpStatusCode.Conflict, switchedActor.StatusCode);
            resolver.Current = actor;

            app.Services.GetRequiredService<IMemoryCache>()
                .Remove($"media-editor:tv-review:{receipt.Token}");
            using var evicted = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(receipt.Token, Guid.NewGuid().ToString("D"),
                    [new MediaEditorPairingAcceptedDto(asset, "102")], []));
            Assert.Equal(HttpStatusCode.Conflict, evicted.StatusCode);
            using (var verify = database.CreateConnection())
                Assert.Equal(sourceWork, verify.QuerySingle<Guid>("SELECT work_id FROM editions WHERE id=@edition", new { edition }));

            var freshReceipt = new TvPairingReviewTokenService(app.Services.GetRequiredService<IMemoryCache>())
                .Store(show, actor, null, "42", show, source, targets, catalog);

            var operationToken = Guid.NewGuid().ToString("D");
            using var saved = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(freshReceipt.Token, operationToken,
                    [new MediaEditorPairingAcceptedDto(asset, "102")], []));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>();
            Assert.Equal("Committed", body?.Outcome);
            Assert.Equal("pending", Assert.Single(body!.Rows).SyncState);
            using var committed = database.CreateConnection();
            Assert.Equal(targetWork, committed.QuerySingle<Guid>("SELECT work_id FROM editions WHERE id=@edition", new { edition }));

            using var replay = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(freshReceipt.Token, operationToken,
                    [new MediaEditorPairingAcceptedDto(asset, "102")], []));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal("Replayed", (await replay.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>())?.Outcome);

            var otherShow = Guid.NewGuid();
            var otherSeason = Guid.NewGuid();
            var otherTarget = Guid.NewGuid();
            var crossSource = Guid.NewGuid();
            var crossEdition = Guid.NewGuid();
            var crossAsset = Guid.NewGuid();
            var existingTargetEdition = Guid.NewGuid();
            var existingTargetAsset = Guid.NewGuid();
            using (var setupCrossShow = database.CreateConnection())
                setupCrossShow.Execute("""
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES
                      (@crossSource, 'TV', 'child', @season, 3, 'Owned'),
                      (@otherShow, 'TV', 'parent', NULL, NULL, 'Owned'),
                      (@otherSeason, 'TV', 'parent', @otherShow, 1, 'Owned'),
                      (@otherTarget, 'TV', 'child', @otherSeason, 1, 'Owned');
                    INSERT INTO editions (id, work_id) VALUES
                      (@crossEdition, @crossSource), (@existingTargetEdition, @otherTarget);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                    VALUES
                      (@crossAsset, @crossEdition, 'cross-show-source', '/tv/Wrong.Show.S01E01.mkv', @libraryId),
                      (@existingTargetAsset, @existingTargetEdition, 'existing-target', '/tv/Correct.Show.S01E01.existing.mkv', @libraryId);
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@crossSourceBridge, @crossSource, 'tvdb_episode_id', '103'),
                      (@otherShowBridge, @otherShow, 'tvdb_id', '84'),
                      (@otherTargetBridge, @otherTarget, 'tvdb_episode_id', '8401');
                    """, new { crossSource, season, otherShow, otherSeason, otherTarget,
                    crossEdition, crossAsset, existingTargetEdition, existingTargetAsset,
                    libraryId = libraryId.ToString("D"),
                    crossSourceBridge = Guid.NewGuid(), otherShowBridge = Guid.NewGuid(),
                    otherTargetBridge = Guid.NewGuid() });

            var crossCatalogue = new[] { new PairingCatalogueChild("8401", "84", "tvdb",
                "Correct Show Pilot", SeasonNumber: 1, EpisodeNumber: 1) };
            var crossSourceRows = new PairingAssetReadService(database)
                .Load([crossAsset], CancellationToken.None);
            var resolvedOtherShow = new TvPairingLocalTargetReadService(database)
                .ResolveBySeriesId("84", crossCatalogue, CancellationToken.None);
            Assert.NotNull(resolvedOtherShow);
            Assert.Equal(otherShow, resolvedOtherShow.Value.ShowWorkId);
            var crossReceipt = new TvPairingReviewTokenService(
                app.Services.GetRequiredService<IMemoryCache>()).Store(
                    show, actor, null, "84", otherShow, crossSourceRows,
                    resolvedOtherShow.Value.Targets, crossCatalogue);
            var crossOperation = Guid.NewGuid().ToString("D");
            using var crossSaved = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(crossReceipt.Token, crossOperation,
                    [new MediaEditorPairingAcceptedDto(crossAsset, "8401")], []));
            Assert.Equal(HttpStatusCode.OK, crossSaved.StatusCode);
            var crossBody = await crossSaved.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>();
            Assert.Equal("Committed", crossBody?.Outcome);
            Assert.Equal("pending", Assert.Single(crossBody!.Rows).SyncState);
            using (var verifyCross = database.CreateConnection())
                Assert.Equal(otherTarget, verifyCross.QuerySingle<Guid>(
                    "SELECT work_id FROM editions WHERE id=@crossEdition", new { crossEdition }));

            using var crossReplay = await client.PostAsJsonAsync($"/metadata/{show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(crossReceipt.Token, crossOperation,
                    [new MediaEditorPairingAcceptedDto(crossAsset, "8401")], []));
            Assert.Equal(HttpStatusCode.OK, crossReplay.StatusCode);
            Assert.Equal("Replayed", (await crossReplay.Content
                .ReadFromJsonAsync<MediaEditorPairingSaveResultDto>())?.Outcome);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private sealed class MutableAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public RequestAuthority Current { get; set; } = authority;
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
            => ValueTask.FromResult(Current);
    }

    private sealed class AllowLibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken ct = default)
            => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid requestedLibraryId, CancellationToken ct = default)
            => ValueTask.FromResult(requestedLibraryId == libraryId ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken ct = default)
            => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, "pairing-test"),
                ], Scheme.Name)), Scheme.Name)));
    }

    private sealed class AllowAdministratorOrApplicationHandler
        : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
            AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
}
