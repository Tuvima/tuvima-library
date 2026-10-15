using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
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

/// <summary>
/// <c>GET /api/v1/devices/current/playback-preferences</c>: the real route, wired to a real temp data store and the real
/// playback settings service. The caller's profile comes from the token claim alone, the playback.read scope is enforced,
/// and nothing about the route can read or change another person's settings.
/// </summary>
public sealed class NativePlaybackPreferencesEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/v1/devices/current/playback-preferences";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-native-prefs-{Guid.NewGuid():N}.db");
    private readonly Guid _mary = Guid.NewGuid();
    private readonly Guid _tom = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Actor _actor = new();
    private DatabaseConnection _database = null!;
    private UserPlaybackSettingsService _settings = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        foreach (var (id, name) in new[] { (_mary, "Mary"), (_tom, "Tom") })
        {
            await ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = id,
                DisplayName = name,
                AvatarColor = "#7C4DFF",
                Role = ProfileRole.StandardUser,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        _settings = new UserPlaybackSettingsService(_database, new ProfileRepository(_database));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(_actor);
        builder.Services.AddSingleton<IUserPlaybackSettingsService>(_settings);
        builder.Services.AddSingleton<IRequestAuthorityResolver, ActorAuthorityResolver>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, ScopeEvaluator>();
        DeclareUnusedEngineServices(builder.Services);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapClientAuthorizationEndpoints();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };

        _actor.ProfileId = _mary;
        _actor.DeviceId = _deviceId;
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
    }

    [Fact]
    public async Task ReturnsTheActiveProfilesSavedValues_WithTheExactWireNames()
    {
        var saved = await _settings.GetAsync(_mary);
        saved.Subtitles.AudioLanguage = "Japanese";
        saved.Subtitles.DefaultSubtitleLanguage = "French";
        saved.Subtitles.ForcedSubtitlesMode = PlaybackPreferenceValues.Always;
        saved.Subtitles.SubtitleSize = PlaybackPreferenceValues.Large;
        saved.Subtitles.SubtitleBackground = false;
        saved.Watching.SkipBackSeconds = 15;
        saved.Watching.SkipForwardSeconds = 60;
        saved.Watching.AutoplayNextEpisode = false;
        saved.Watching.DefaultPlaybackSpeed = 1.25m;
        saved.General.ResumePlayback = false;
        await _settings.UpdateAsync(_mary, saved);

        using var response = await _client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "audioLanguage", "autoplayNextEpisode", "defaultPlaybackSpeed", "resumePlayback", "skipBackSeconds",
                "skipForwardSeconds", "subtitleBackground", "subtitleLanguage", "subtitleMode", "subtitleSize",
            }.Order(StringComparer.Ordinal).ToArray(),
            names);

        var dto = json.RootElement.Deserialize<NativePlaybackPreferencesDto>()!;
        Assert.Equal("Japanese", dto.AudioLanguage);
        Assert.Equal("French", dto.SubtitleLanguage);
        Assert.Equal(NativeSubtitleModes.Always, dto.SubtitleMode);
        Assert.Equal("Large", dto.SubtitleSize);
        Assert.False(dto.SubtitleBackground);
        Assert.Equal(15, dto.SkipBackSeconds);
        Assert.Equal(60, dto.SkipForwardSeconds);
        Assert.False(dto.AutoplayNextEpisode);
        Assert.False(dto.ResumePlayback);
        Assert.Equal(1.25m, dto.DefaultPlaybackSpeed);
    }

    [Fact]
    public async Task ReturnsTheDefaults_WhenNothingIsSaved_AndDoesNotWrite()
    {
        var defaults = UserPlaybackSettingsDto.CreateDefaults(_mary);

        var dto = await _client.GetFromJsonAsync<NativePlaybackPreferencesDto>(Route);

        Assert.NotNull(dto);
        Assert.Equal(defaults.Subtitles.AudioLanguage, dto.AudioLanguage);
        Assert.Equal(defaults.Subtitles.DefaultSubtitleLanguage, dto.SubtitleLanguage);
        Assert.Equal(NativeSubtitleModes.Auto, dto.SubtitleMode);
        Assert.Equal(defaults.Subtitles.SubtitleSize, dto.SubtitleSize);
        Assert.Equal(defaults.Subtitles.SubtitleBackground, dto.SubtitleBackground);
        Assert.Equal(defaults.Watching.SkipBackSeconds, dto.SkipBackSeconds);
        Assert.Equal(defaults.Watching.SkipForwardSeconds, dto.SkipForwardSeconds);
        Assert.Equal(defaults.Watching.AutoplayNextEpisode, dto.AutoplayNextEpisode);
        Assert.Equal(defaults.General.ResumePlayback, dto.ResumePlayback);
        Assert.Equal(defaults.Watching.DefaultPlaybackSpeed, dto.DefaultPlaybackSpeed);

        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM user_playback_settings;"));
    }

    [Fact]
    public async Task ATokenWithoutPlaybackRead_IsRefusedWithInsufficientScope()
    {
        _actor.PlaybackRead = false;

        using var response = await _client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient_scope", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallerCannotReadAnotherProfilesValues_NoUrlOrQueryCanSelectOne()
    {
        var tom = await _settings.GetAsync(_tom);
        tom.Subtitles.AudioLanguage = "German";
        tom.Watching.SkipForwardSeconds = 90;
        await _settings.UpdateAsync(_tom, tom);
        var mary = await _settings.GetAsync(_mary);
        mary.Subtitles.AudioLanguage = "Spanish";
        await _settings.UpdateAsync(_mary, mary);

        // A profile id in the query or a header is ignored: Mary's token answers with Mary's values.
        var viaQuery = await _client.GetFromJsonAsync<NativePlaybackPreferencesDto>($"{Route}?profileId={_tom:D}&profile_id={_tom:D}");
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("X-Profile-Id", _tom.ToString("D"));
        using var viaHeader = await _client.SendAsync(request);
        Assert.Equal("Spanish", viaQuery!.AudioLanguage);
        Assert.Equal(30, viaQuery.SkipForwardSeconds);
        Assert.Equal("Spanish", (await viaHeader.Content.ReadFromJsonAsync<NativePlaybackPreferencesDto>())!.AudioLanguage);

        // There is no route that takes a profile id.
        using var byId = await _client.GetAsync($"{Route}/{_tom:D}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // Once the device is switched to Tom, his token (and only his) answers with his values.
        _actor.ProfileId = _tom;
        var asTom = await _client.GetFromJsonAsync<NativePlaybackPreferencesDto>(Route);
        Assert.Equal("German", asTom!.AudioLanguage);
        Assert.Equal(90, asTom.SkipForwardSeconds);
    }

    [Fact]
    public async Task ARequestWithoutAPairedDeviceOrProfile_IsRefused_AndNothingCanWrite()
    {
        _actor.DeviceId = null;
        using (var noDevice = await _client.GetAsync(Route))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noDevice.StatusCode);
        }

        _actor.DeviceId = _deviceId;
        _actor.ProfileId = null;
        using (var noProfile = await _client.GetAsync(Route))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noProfile.StatusCode);
        }

        _actor.ProfileId = _mary;
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, Route) { Content = JsonContent.Create(new { }) };
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Auto", "Auto")]
    [InlineData("Always", "Always")]
    [InlineData("Never", "Off")]
    [InlineData("never", "Off")]
    [InlineData("ALWAYS", "Always")]
    [InlineData("", "Auto")]
    [InlineData(null, "Auto")]
    [InlineData("Something else", "Auto")]
    public void ShowSubtitlesIsMappedToAutoAlwaysOrOff(string? stored, string expected) =>
        Assert.Equal(expected, NativePlaybackPreferencesMapper.ToSubtitleMode(stored));

    /// <summary>
    /// The whole devices group is built at start-up, and its other routes take Engine services this test does not
    /// construct. Only the route under test is called, so those services are declared (so the routes can be built)
    /// but never created.
    /// </summary>
    private static void DeclareUnusedEngineServices(IServiceCollection services)
    {
        var assemblies = new[]
        {
            typeof(ClientAuthorizationEndpoints).Assembly,
            typeof(MediaEngine.Domain.Contracts.IAuthorizationEvaluator).Assembly,
            typeof(MediaEngine.Identity.Contracts.IAccountExternalLoginService).Assembly,
            typeof(DatabaseConnection).Assembly,
        };
        string[] suffixes = ["Service", "Repository", "Loader", "Resolver", "Store", "Provider", "Reader", "Evaluator", "Access", "Gate"];
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (type.IsGenericTypeDefinition || (type.IsAbstract && type.IsSealed) || !suffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal))
                || (!type.IsInterface && (type.IsAbstract || !type.IsClass))
                || services.Any(descriptor => descriptor.ServiceType == type))
            {
                continue;
            }

            services.AddSingleton(type, _ => throw new NotSupportedException($"{type.Name} is not used by this test."));
        }
    }

    private sealed class Actor
    {
        public Guid? ProfileId { get; set; }
        public Guid? DeviceId { get; set; }
        public bool PlaybackRead { get; set; } = true;
    }

    private sealed class ActorAuthorityResolver(Actor actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(
                PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), actor.ProfileId,
                ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: actor.DeviceId));
    }

    /// <summary>Allows an Application permission only when the token holds it; here only playback.read can be taken away.</summary>
    private sealed class ScopeEvaluator(Actor actor) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) =>
            ValueTask.FromResult(requirement.ApplicationPermission is { } permission
                && permission.Value == ClientApiScopes.PlaybackRead
                && !actor.PlaybackRead
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission)
                    : AuthorizationDecision.Allow());
    }

    /// <summary>Signs a request in with the claims of the swapped-in actor, as the bearer handler would for a paired app's token.</summary>
    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Actor actor)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "native-prefs-test") };
            if (actor.ProfileId is { } profileId)
            {
                claims.Add(new Claim(TuvimaClaimTypes.ActiveProfileId, profileId.ToString("D")));
            }

            if (actor.DeviceId is { } deviceId)
            {
                claims.Add(new Claim(TuvimaClaimTypes.DeviceId, deviceId.ToString("D")));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
