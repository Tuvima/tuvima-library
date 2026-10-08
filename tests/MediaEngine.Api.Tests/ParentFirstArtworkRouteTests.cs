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

public sealed class ParentFirstArtworkRouteTests
{
    [Fact]
    public async Task PreviewRejectsUnauthorizedSiblingThenSavesReviewedSharedImpact()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var denied = await fixture.PreviewAsync();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        fixture.Access.Grant(fixture.OtherLibrary);
        using var previewResponse = await fixture.PreviewAsync();
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<MediaEditorPairingArtworkPreviewDto>();
        Assert.Equal(3, preview!.AffectedFiles.Count);
        Assert.Contains(preview.AffectedFiles, row => row.AssetId == fixture.OtherSibling);

        using var saved = await fixture.SaveAsync(preview.ArtworkReviewToken);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var response = await saved.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>();
        Assert.Equal("Committed", response?.Outcome);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(fixture.Target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = fixture.SourceEdition }));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_artwork;"));
    }

    [Fact]
    public async Task ChangedSiblingLibraryOrPreferenceRejectsSaveBeforeIdentityMove()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Access.Grant(fixture.OtherLibrary);
        var artworkToken = await fixture.PreviewTokenAsync();
        var changedLibrary = Guid.NewGuid();
        fixture.Access.Grant(changedLibrary);
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library WHERE id=@asset;",
                    new { library = changedLibrary.ToString("D"), asset = fixture.OtherSibling });
        }

        using var librarySave = await fixture.SaveAsync(artworkToken);
        Assert.Equal(HttpStatusCode.Conflict, librarySave.StatusCode);
        fixture.AssertNotCommitted();

        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library WHERE id=@asset;",
                    new { library = fixture.OtherLibrary.ToString("D"), asset = fixture.OtherSibling });
        }
        artworkToken = await fixture.PreviewTokenAsync();
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO entity_artwork_links
                        (id, entity_id, entity_type, artwork_asset_id, role, context, is_preferred)
                    VALUES(@id, @target, 'Work', @art, 'Primary', 'Episode', 1);
                    """, new { id = Guid.NewGuid(), target = fixture.Target, art = fixture.Artwork });
        }

        using var revisionSave = await fixture.SaveAsync(artworkToken);
        Assert.Equal(HttpStatusCode.Conflict, revisionSave.StatusCode);
        fixture.AssertNotCommitted();
    }

    [Fact]
    public async Task ArtworkSqlFailureRollsBackPairingAndPreference()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Access.Grant(fixture.OtherLibrary);
        var artworkToken = await fixture.PreviewTokenAsync();
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("""
                    CREATE TRIGGER reject_artwork_link BEFORE INSERT ON entity_artwork_links
                    BEGIN SELECT RAISE(ABORT, 'artwork failure'); END;
                    """);
        }

        using var failed = await fixture.SaveAsync(artworkToken);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        fixture.AssertNotCommitted();
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(0, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_artwork_links
            WHERE entity_id=@target AND role='Primary' AND context='Episode';
            """, new { target = fixture.Target }));
    }

    [Fact]
    public async Task SharedShowArtworkRequiresSiblingAccessAndSavesWithPairing()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var denied = await fixture.SharedPreviewAsync("TvShow", fixture.Show, "Background");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        fixture.Access.Grant(fixture.OtherLibrary);
        using var response = await fixture.SharedPreviewAsync("TvShow", fixture.Show, "Background");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<MediaEditorPairingSharedArtworkPreviewDto>();
        Assert.Equal(3, preview!.AffectedFiles.Count);
        Assert.Contains(preview.AffectedFiles, item => item.AssetId == fixture.OtherSibling);
        var operation = Guid.NewGuid().ToString("D");
        using var saved = await fixture.SaveSharedAsync(preview.SharedArtworkReviewToken, operation);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Committed", (await saved.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>())?.Outcome);
        using var replay = await fixture.SaveSharedAsync(preview.SharedArtworkReviewToken, operation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("Replayed", (await replay.Content.ReadFromJsonAsync<MediaEditorPairingSaveResultDto>())?.Outcome);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(fixture.Target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = fixture.SourceEdition }));
        Assert.Equal(fixture.Artwork, verify.QuerySingle<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@show AND role='Background' AND is_preferred=1;
            """, new { show = fixture.Show }));
        Assert.Equal("pending", verify.QuerySingle<string>(
            "SELECT writeback_status FROM media_assets WHERE id=@id;",
            new { id = fixture.OtherSibling }));
    }

    [Fact]
    public async Task SharedArtworkPreferenceChangeAndNewSiblingRejectBeforeMove()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Access.Grant(fixture.OtherLibrary);
        var token = await fixture.SharedPreviewTokenAsync("TvShow", fixture.Show, "Logo");
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO entity_artwork_links
                        (id, entity_id, entity_type, artwork_asset_id, role, context, is_preferred)
                    VALUES(@id, @show, 'Work', @art, 'Logo', '', 1);
                    """, new { id = Guid.NewGuid(), show = fixture.Show, art = fixture.Artwork });
        }
        using var changedPreference = await fixture.SaveSharedAsync(token);
        Assert.Equal(HttpStatusCode.Conflict, changedPreference.StatusCode);
        fixture.AssertNotCommitted();

        token = await fixture.SharedPreviewTokenAsync("TvShow", fixture.Show, "Logo");
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@edition, @work);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, '/tv/new-sibling.mkv', @library);
                    """, new { edition = Guid.NewGuid(), work = fixture.Target,
                        asset = Guid.NewGuid(), hash = Guid.NewGuid().ToString("N"),
                        library = fixture.MainLibrary.ToString("D") });
        }
        using var changedImpact = await fixture.SaveSharedAsync(token);
        Assert.Equal(HttpStatusCode.Conflict, changedImpact.StatusCode);
        fixture.AssertNotCommitted();
    }

    [Fact]
    public async Task SharedArtworkInsertFailureRollsBackPairing()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Access.Grant(fixture.OtherLibrary);
        var token = await fixture.SharedPreviewTokenAsync("TvSeason", fixture.Season, "Primary");
        using (var connection = fixture.Database.CreateConnection())
        {
            connection.Execute("""
                    CREATE TRIGGER reject_shared_art BEFORE INSERT ON entity_artwork_links
                    BEGIN SELECT RAISE(ABORT, 'artwork failure'); END;
                    """);
        }
        using var failed = await fixture.SaveSharedAsync(token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        fixture.AssertNotCommitted();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_art_route_{Guid.NewGuid():N}.db");
        private readonly string _artPath = Path.Combine(Path.GetTempPath(), $"tuvima_art_route_{Guid.NewGuid():N}.jpg");
        private WebApplication? _app;
        private HttpClient? _client;
        private string _pairingToken = string.Empty;
        public DatabaseConnection Database { get; }
        public MutableLibraryAccess Access { get; }
        public Guid MainLibrary { get; } = Guid.NewGuid();
        public Guid OtherLibrary { get; } = Guid.NewGuid();
        public Guid Show { get; } = Guid.NewGuid();
        public Guid Season { get; } = Guid.NewGuid();
        public Guid Source { get; } = Guid.NewGuid();
        public Guid Target { get; } = Guid.NewGuid();
        public Guid SourceEdition { get; } = Guid.NewGuid();
        public Guid SourceAsset { get; } = Guid.NewGuid();
        public Guid OtherSibling { get; } = Guid.NewGuid();
        public Guid Artwork { get; } = Guid.NewGuid();

        private Fixture()
        {
            Database = new DatabaseConnection(_dbPath);
            Database.InitializeSchema();
            Access = new MutableLibraryAccess(MainLibrary);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await File.WriteAllBytesAsync(fixture._artPath, [1, 2, 3, 4]);
            fixture.Seed();
            await fixture.StartAsync();
            return fixture;
        }

        private void Seed()
        {
            using var connection = Database.CreateConnection();
            connection.Execute("""
                INSERT INTO works(id, media_type, work_kind, ownership)
                VALUES(@Show, 'TV', 'parent', 'Owned');
                INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES(@Season, 'TV', 'parent', @Show, 1, 'Owned'),
                      (@Source, 'TV', 'child', @Season, 1, 'Owned'),
                      (@Target, 'TV', 'child', @Season, 2, 'Owned');
                INSERT INTO editions(id, work_id) VALUES(@SourceEdition, @Source),
                    (@mainEdition, @Target), (@otherEdition, @Target);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                VALUES(@SourceAsset, @SourceEdition, 'source-art-route', '/tv/source.mkv', @mainLibrary),
                      (@mainSibling, @mainEdition, 'main-art-route', '/tv/main.mkv', @mainLibrary),
                      (@OtherSibling, @otherEdition, 'other-art-route', '/tv/other.mkv', @otherLibrary);
                INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES
                    (@showBridge, @Show, 'tvdb_id', '42'),
                    (@sourceBridge, @Source, 'tvdb_episode_id', '101'),
                    (@targetBridge, @Target, 'tvdb_episode_id', '102');
                INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES
                    (@Source, 'identity_revision', 'source-r1', @now),
                    (@Target, 'identity_revision', 'target-r1', @now),
                    (@Show, 'identity_revision', 'show-r1', @now);
                INSERT INTO artwork_assets(id, content_hash, original_path)
                VALUES(@Artwork, 'route-art', @artPath);
                INSERT INTO entity_artwork_links
                    (id, entity_id, entity_type, artwork_asset_id, role, context)
                VALUES(@artLink, @Show, 'Work', @Artwork, 'Primary', '');
                """, new
            {
                Show, Season, Source, Target, SourceEdition, SourceAsset, OtherSibling, Artwork,
                mainEdition = Guid.NewGuid(), otherEdition = Guid.NewGuid(), mainSibling = Guid.NewGuid(),
                mainLibrary = MainLibrary.ToString("D"), otherLibrary = OtherLibrary.ToString("D"),
                showBridge = Guid.NewGuid(), sourceBridge = Guid.NewGuid(), targetBridge = Guid.NewGuid(),
                artLink = Guid.NewGuid(), artPath = _artPath, now = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        private async Task StartAsync()
        {
            var actor = new RequestAuthority(PrincipalKind.Human, true,
                AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(), SessionId: Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddSingleton<IAuthorizationHandler, AllowHandler>();
            builder.Services.AddSingleton<IDatabaseConnection>(Database);
            builder.Services.AddSingleton<PairingAssetReadService>();
            builder.Services.AddSingleton<TvPairingLocalTargetReadService>();
            builder.Services.AddSingleton<EpisodeStillReviewReadService>();
            builder.Services.AddSingleton<IRequestAuthorityResolver>(new StaticAuthorityResolver(actor));
            builder.Services.AddSingleton<IAccountAccessDecisionService>(Access);
            builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
            builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
            builder.Services.AddSingleton<MediaEditorCommitRepository>();
            builder.Services.AddSingleton<MusicPairingCommitRepository>();
            builder.Services.AddSingleton<MusicTrackRelocationRepository>();
            builder.Services.AddSingleton<IMediaEditorOwnedChildReadService, MediaEditorOwnedChildReadService>();
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton(new TvdbRetailClient(null!, null!, null!));
            builder.Services.AddSingleton(new MusicBrainzReleaseClient(null!, null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzReleaseClient>.Instance));
            _app = builder.Build();
            _app.UseAuthentication();
            _app.UseAuthorization();
            var group = _app.MapGroup("/metadata");
            foreach (var name in new[] { "MapParentFirstPairingPreviewEndpoints", "MapParentFirstArtworkEndpoints" })
            {
                typeof(MetadataEndpoints).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [group]);
            }
            await _app.StartAsync();
            var address = new Uri(_app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            _client = new HttpClient { BaseAddress = address };

            var source = new PairingAssetReadService(Database).Load([SourceAsset], CancellationToken.None);
            var catalogue = new[] { new PairingCatalogueChild("102", "42", "tvdb", "Second",
                SeasonNumber: 1, EpisodeNumber: 2) };
            var targets = new TvPairingLocalTargetReadService(Database)
                .Resolve(Show, "42", catalogue, CancellationToken.None);
            var receipt = new TvPairingReviewTokenService(_app.Services.GetRequiredService<IMemoryCache>())
                .Store(Show, actor, null, "42", Show, source, targets, catalogue);
            _pairingToken = receipt.Token;
        }

        public Task<HttpResponseMessage> PreviewAsync() => _client!.PostAsJsonAsync(
            $"/metadata/{Show:D}/pairing-artwork-preview",
            new MediaEditorPairingArtworkPreviewRequestDto(_pairingToken,
                [new MediaEditorPairingAcceptedDto(SourceAsset, "102")], [], "102", Artwork));

        public Task<HttpResponseMessage> SharedPreviewAsync(string scope, Guid owner, string role)
            => _client!.PostAsJsonAsync(
                $"/metadata/{Show:D}/pairing-shared-artwork-preview",
                new MediaEditorPairingSharedArtworkPreviewRequestDto(_pairingToken,
                    [new MediaEditorPairingAcceptedDto(SourceAsset, "102")], [],
                    owner, scope, role, Artwork));

        public async Task<string> SharedPreviewTokenAsync(string scope, Guid owner, string role)
        {
            using var response = await SharedPreviewAsync(scope, owner, role);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadFromJsonAsync<MediaEditorPairingSharedArtworkPreviewDto>();
            return preview!.SharedArtworkReviewToken;
        }

        public Task<HttpResponseMessage> SaveSharedAsync(string token, string? operationToken = null)
            => _client!.PostAsJsonAsync($"/metadata/{Show:D}/pairing-save",
                new MediaEditorPairingSaveRequestDto(_pairingToken,
                    operationToken ?? Guid.NewGuid().ToString("D"),
                    [new MediaEditorPairingAcceptedDto(SourceAsset, "102")], [],
                    null, token));

        public async Task<string> PreviewTokenAsync()
        {
            using var response = await PreviewAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadFromJsonAsync<MediaEditorPairingArtworkPreviewDto>();
            return preview!.ArtworkReviewToken;
        }

        public Task<HttpResponseMessage> SaveAsync(string artworkToken) => _client!.PostAsJsonAsync(
            $"/metadata/{Show:D}/pairing-save",
            new MediaEditorPairingSaveRequestDto(_pairingToken, Guid.NewGuid().ToString("D"),
                [new MediaEditorPairingAcceptedDto(SourceAsset, "102")], [], artworkToken));

        public void AssertNotCommitted()
        {
            using var connection = Database.CreateConnection();
            Assert.Equal(Source, connection.QuerySingle<Guid>(
                "SELECT work_id FROM editions WHERE id=@edition;", new { edition = SourceEdition }));
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
            Database.Dispose();
            try { File.Delete(_dbPath); } catch { }
            try { File.Delete(_artPath); } catch { }
        }
    }

    private sealed class StaticAuthorityResolver(RequestAuthority actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
            => ValueTask.FromResult(actor);
    }

    private sealed class MutableLibraryAccess(Guid initial) : IAccountAccessDecisionService
    {
        private readonly HashSet<Guid> _allowed = [initial];
        public void Grant(Guid library) => _allowed.Add(library);
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority,
            AccountFeatureId feature, CancellationToken ct = default)
            => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority,
            Guid libraryId, CancellationToken ct = default)
            => ValueTask.FromResult(_allowed.Contains(libraryId) ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken ct = default)
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
                    new Claim(ClaimTypes.NameIdentifier, "artwork-test"),
                ], Scheme.Name)), Scheme.Name)));
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
}
