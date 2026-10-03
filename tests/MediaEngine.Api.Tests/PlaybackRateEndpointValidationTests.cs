using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackRateEndpointValidationTests
{
    [Fact]
    public async Task InvalidNormalSpeedCommandReturnsBadRequestBeforeSessionMutation()
    {
        var profileId = Guid.NewGuid();
        var response = await InvokeAsync("/api/v1/player/command", new PlayerCommandRequestDto
        {
            ProfileId = profileId,
            Command = PlayerCommands.Speed,
            PlaybackRate = 3.01d,
        }, profileId);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidNormalHeartbeatRateReturnsBadRequestBeforeTelemetryMutation()
    {
        var profileId = Guid.NewGuid();
        var response = await InvokeAsync("/api/v1/player/heartbeat", new PlayerHeartbeatDto
        {
            ProfileId = profileId,
            PositionSeconds = 12,
            PlaybackRate = 3.01d,
            IsPlaying = true,
        }, profileId);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    private static async Task<DefaultHttpContext> InvokeAsync(string route, object request, Guid profileId)
    {
        var accessor = new HttpContextAccessor();
        var authority = new FixedAuthorityResolver(new RequestAuthority(
            PrincipalKind.Human, true, Guid.NewGuid(), profileId, AccountEnabled: true, GrantEnabled: true));
        var scope = new PlayerCatalogueScope(accessor);
        var player = new PlayerService(null!, null!, null!, null!, null!, null!, null!, null!, null!, scope, null!);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IHttpContextAccessor>(accessor);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(authority);
        builder.Services.AddSingleton<PlayerService>(player);
        builder.Services.AddSingleton<PlayerCatalogueScope>(scope);
        builder.Services.AddSingleton<AudiobookChapterNamingService>(_ => null!);
        await using var app = builder.Build();
        app.MapPlayerEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate => candidate.RoutePattern.RawText == route);
        var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, request.GetType()));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Request = { Method = "POST", Path = route, ContentType = "application/json", ContentLength = body.Length, Body = body },
            Response = { Body = new MemoryStream() },
        };
        accessor.HttpContext = context;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());

        await endpoint.RequestDelegate!(context);
        return context;
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
