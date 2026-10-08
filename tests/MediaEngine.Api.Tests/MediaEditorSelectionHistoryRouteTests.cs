using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorSelectionHistoryRouteTests
{
    [Fact]
    public async Task RouteRequiresEveryFileAndSeparatesEmptyFromLoadFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_history_route_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var show = Guid.NewGuid();
            var episode = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var firstLibrary = Guid.NewGuid();
            var secondLibrary = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                        INSERT INTO works(id, media_type, work_kind, ownership)
                        VALUES(@show, 'TV', 'parent', 'Owned');
                        INSERT INTO works(id, media_type, work_kind, parent_work_id, ownership)
                        VALUES(@episode, 'TV', 'child', @show, 'Owned');
                        INSERT INTO editions(id, work_id) VALUES(@edition, @episode);
                        INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                        VALUES(@first, @edition, @firstHash, 'C:/fixture/first.mkv', @firstLibrary),
                              (@second, @edition, @secondHash, 'C:/fixture/second.mkv', @secondLibrary);
                        """, new { show, episode, edition, first, second,
                            firstHash = Guid.NewGuid().ToString("N"),
                            secondHash = Guid.NewGuid().ToString("N"),
                            firstLibrary = firstLibrary.ToString("D"),
                            secondLibrary = secondLibrary.ToString("D") });
            }

            var access = new LibraryAccess(firstLibrary);
            var actor = new RequestAuthority(PrincipalKind.Human, true,
                AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(), SessionId: Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddSingleton<IAuthorizationHandler, AllowHandler>();
            builder.Services.AddSingleton<IDatabaseConnection>(database);
            builder.Services.AddSingleton<MediaEditorSelectionHistoryReadService>();
            builder.Services.AddSingleton<IRequestAuthorityResolver>(new StaticAuthorityResolver(actor));
            builder.Services.AddSingleton<IAccountAccessDecisionService>(access);
            builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
            builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            typeof(MetadataEndpoints).GetMethod("MapMediaEditorSelectionHistoryEndpoints",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app.MapGroup("/metadata")]);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new HttpClient { BaseAddress = address };

            using var denied = await client.PostAsJsonAsync(
                $"/metadata/{show:D}/owned-children/history",
                new MediaEditorSelectionHistoryRequestDto([first, second]));
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

            using var empty = await client.PostAsJsonAsync(
                $"/metadata/{show:D}/owned-children/history",
                new MediaEditorSelectionHistoryRequestDto([first]));
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            var emptyBody = await empty.Content.ReadFromJsonAsync<MediaEditorSelectionHistoryDto>();
            Assert.False(emptyBody!.HasEvents);
            Assert.Empty(emptyBody.Items);

            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                        INSERT INTO system_activity(entity_id, entity_type, action_type, detail)
                        VALUES(@asset, 'MediaAsset', 'MetadataEdited', 'Reviewed file');
                        """, new { asset = first });
            }
            using var loaded = await client.PostAsJsonAsync(
                $"/metadata/{show:D}/owned-children/history",
                new MediaEditorSelectionHistoryRequestDto([first]));
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
            var loadedBody = await loaded.Content.ReadFromJsonAsync<MediaEditorSelectionHistoryDto>();
            Assert.True(loadedBody!.HasEvents);
            Assert.Equal(first, Assert.Single(loadedBody.Items).EntityId);

            using (var connection = database.CreateConnection())
            {
                connection.Execute("DROP TABLE system_activity;");
            }
            using var unavailable = await client.PostAsJsonAsync(
                $"/metadata/{show:D}/owned-children/history",
                new MediaEditorSelectionHistoryRequestDto([first]));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private sealed class StaticAuthorityResolver(RequestAuthority actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context,
            CancellationToken ct = default) => ValueTask.FromResult(actor);
    }

    private sealed class LibraryAccess(Guid allowedLibrary) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority,
            AccountFeatureId feature, CancellationToken ct = default)
            => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority,
            Guid libraryId, CancellationToken ct = default)
            => ValueTask.FromResult(libraryId == allowedLibrary
                ? AuthorizationDecision.Allow()
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
                new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, "history-test"),
                ], Scheme.Name)), Scheme.Name)));
    }
}
