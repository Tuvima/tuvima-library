using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>PUT /settings/transcoding</c> saves the encode settings as the Dashboard loaded them; the scrub-preview switch travels in the same
/// object but belongs to <c>PUT /settings/playback/scrub-previews</c> (which also removes the cache). Run through the real route table with
/// the real config folder; only the signed-in administrator is faked.
/// </summary>
public sealed class TranscodingSettingsEndpointTests : IDisposable
{
    private readonly ScrubPreviewWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task SavingEncodeSettingsWithAStaleCopyOfTheSwitch_DoesNotTurnScrubPreviewsBackOn()
    {
        await using var app = await StartAsync();
        var stale = (await app.Client.GetFromJsonAsync<TranscodingSettings>("/settings/transcoding"))!;
        Assert.True(stale.ScrubPreviews.Enabled);

        using var off = await app.Client.PutAsJsonAsync("/settings/playback/scrub-previews", new MediaEngine.Contracts.Playback.UpdateScrubPreviewSettingRequestDto { Enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(_world.Configuration.LoadTranscoding().ScrubPreviews.Enabled);

        stale.ScrubPreviews.Enabled = true;
        stale.ScheduledEncodesEnabled = false;
        using var saved = await app.Client.PutAsJsonAsync("/settings/transcoding", stale);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stored = _world.Configuration.LoadTranscoding();
        Assert.False(stored.ScrubPreviews.Enabled);
        Assert.False(stored.ScheduledEncodesEnabled);
        var answered = (await saved.Content.ReadFromJsonAsync<TranscodingSettings>())!;
        Assert.False(answered.ScrubPreviews.Enabled);
    }

    [Fact]
    public async Task SavingEncodeSettingsWithTheSwitchOff_DoesNotTurnItOffEither()
    {
        await using var app = await StartAsync();
        var current = (await app.Client.GetFromJsonAsync<TranscodingSettings>("/settings/transcoding"))!;

        current.ScrubPreviews.Enabled = false;
        using var saved = await app.Client.PutAsJsonAsync("/settings/transcoding", current);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(_world.Configuration.LoadTranscoding().ScrubPreviews.Enabled);
    }

    private async Task<TestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthPolicies.AdministratorEligibility, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<IConfigurationLoader>(_world.Configuration);
        builder.Services.AddSingleton(_world.Store);
        builder.Services.AddSingleton(new ScrubPreviewSettingsService(
            _world.Configuration, _world.Store, _world.Ffmpeg, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScrubPreviewSettingsService>.Instance));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/settings").MapTranscodingSettings();
        app.MapScrubPreviewEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
