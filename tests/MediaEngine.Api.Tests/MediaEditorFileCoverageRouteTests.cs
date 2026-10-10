using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>The "This file covers" routes: a file in a library the account cannot reach is 403, an unknown file is 404.</summary>
public sealed class MediaEditorFileCoverageRouteTests
{
    [Fact]
    public async Task Routes_AnswerOwnFile_ForbidAnotherLibrarysFile_AndHideUnknownFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_file_coverage_route_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var ownLibrary = Guid.NewGuid();
            var otherLibrary = Guid.NewGuid();
            var own = SeedEpisodeFile(database, ownLibrary);
            var foreign = SeedEpisodeFile(database, otherLibrary);

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
            builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
            builder.Services.AddSingleton<IDatabaseConnection>(database);
            builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(actor));
            builder.Services.AddSingleton<IAccountAccessDecisionService>(new AllowLibraryAccess(ownLibrary));
            builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
            builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
            builder.Services.AddSingleton<MediaEditorFileCoverageRepository>();
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            var map = typeof(MetadataEndpoints).GetMethod("MapFileCoverageEndpoints",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            map.Invoke(null, [app.MapGroup("/metadata")]);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new HttpClient { BaseAddress = address };

            using var okRead = await client.GetAsync($"/metadata/{own.AssetId:D}/file-coverage?assetId={own.AssetId:D}");
            Assert.Equal(HttpStatusCode.OK, okRead.StatusCode);

            using var foreignRead = await client.GetAsync($"/metadata/{own.AssetId:D}/file-coverage?assetId={foreign.AssetId:D}");
            Assert.Equal(HttpStatusCode.Forbidden, foreignRead.StatusCode);

            using var foreignSave = await client.PutAsJsonAsync($"/metadata/{own.AssetId:D}/file-coverage",
                new MediaEditorFileCoverageSaveRequestDto(Guid.NewGuid(), foreign.AssetId, [foreign.WorkId]));
            Assert.Equal(HttpStatusCode.Forbidden, foreignSave.StatusCode);

            using var unknownRead = await client.GetAsync($"/metadata/{own.AssetId:D}/file-coverage?assetId={Guid.NewGuid():D}");
            Assert.Equal(HttpStatusCode.NotFound, unknownRead.StatusCode);

            using var ownSave = await client.PutAsJsonAsync($"/metadata/{own.AssetId:D}/file-coverage",
                new MediaEditorFileCoverageSaveRequestDto(Guid.NewGuid(), own.AssetId, [own.WorkId, own.SecondWorkId]));
            Assert.Equal(HttpStatusCode.OK, ownSave.StatusCode);
            var saved = await ownSave.Content.ReadFromJsonAsync<MediaEditorFileCoverageDto>();
            Assert.Equal(2, saved!.Episodes.Count(episode => episode.IsCovered));
        }
        finally
        {
            try { File.Delete(path); } catch { /* Test cleanup is best effort. */ }
        }
    }

    private static (Guid AssetId, Guid WorkId, Guid SecondWorkId) SeedEpisodeFile(DatabaseConnection database, Guid library)
    {
        var season = Guid.NewGuid();
        var work = Guid.NewGuid();
        var second = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@season, 'TV', 'parent', 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, ownership)
            VALUES (@work, 'TV', 'child', @season, 1, 1, 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, ownership)
            VALUES (@second, 'TV', 'child', @season, 2, 2, 'Unowned');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@asset, @edition, @hash, '/tv/Show.S01E01.mkv', @library);
            """, new { season, work, second, edition, asset, hash = $"coverage-route-{asset:N}", library = library.ToString("D") });
        return (asset, work, second);
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken ct = default)
            => ValueTask.FromResult(authority);
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
                    new Claim(ClaimTypes.NameIdentifier, "file-coverage-test"),
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
