using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
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
/// Playback path WP5: the graphics hardware answer behind Settings &gt; Playback &gt; Quality and the setup row (spec D6, D11): what each test
/// outcome becomes, and that Test hardware is an administrator action that tests again and returns the new answer.
/// </summary>
public sealed class GraphicsHardwareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_graphics_{Guid.NewGuid():N}");

    public GraphicsHardwareTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* Best effort: a temporary folder may linger. */ }
    }

    // ---- What each outcome becomes ----

    [Fact]
    public void ACardThatWorks_IsNamedAndSaysEncodingWorks()
    {
        var answer = GraphicsHardwareReport.Build(true,
            new HardwareCapabilities { HasNvenc = true, GpuName = "NVIDIA GeForce RTX 5080" }, "auto", testedAt: null);

        Assert.Equal(GraphicsHardwareOutcomes.Works, answer.Outcome);
        Assert.Equal("NVIDIA GeForce RTX 5080", answer.CardName);
        Assert.Equal("NVIDIA NVENC", answer.Accelerator);
        Assert.False(answer.ProcessorOnlyChosen);
        Assert.Null(answer.Reason);
    }

    [Fact]
    public void ACardThatWorks_WithNoNameFromTheSystem_IsNamedByItsEncoder()
    {
        var answer = GraphicsHardwareReport.Build(true, new HardwareCapabilities { HasQuickSync = true }, "auto", null);

        Assert.Equal(GraphicsHardwareOutcomes.Works, answer.Outcome);
        Assert.Equal("Intel Quick Sync", answer.CardName);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("none")]
    public void ProcessorOnly_IsReportedBesideAWorkingCard(string mode)
    {
        var answer = GraphicsHardwareReport.Build(true, new HardwareCapabilities { HasNvenc = true }, mode, null);

        Assert.Equal(GraphicsHardwareOutcomes.Works, answer.Outcome);
        Assert.True(answer.ProcessorOnlyChosen);
    }

    [Fact]
    public void AListedEncoderThatFailedTheTest_SaysWhyInPlainWordsAndKeepsFFmpegsText()
    {
        var hardware = new HardwareCapabilities
        {
            GpuName = "NVIDIA GeForce RTX 5080",
            ProbeFailures = new Dictionary<string, string> { ["h264_nvenc"] = "Driver does not support the required nvenc API version." },
        };

        var answer = GraphicsHardwareReport.Build(true, hardware, "auto", null);

        Assert.Equal(GraphicsHardwareOutcomes.Failed, answer.Outcome);
        Assert.Equal("NVIDIA GeForce RTX 5080", answer.CardName);
        Assert.Equal("The graphics driver is too old for this card or this FFmpeg.", answer.Reason);
        Assert.Contains("h264_nvenc: Driver does not support", answer.Detail);
    }

    [Fact]
    public void NoGraphicsEncoderAtAll_IsNoneNotAFailure()
    {
        var answer = GraphicsHardwareReport.Build(true, new HardwareCapabilities(), "auto", null);

        Assert.Equal(GraphicsHardwareOutcomes.None, answer.Outcome);
        Assert.Null(answer.Reason);
        Assert.Null(answer.Detail);
    }

    [Fact]
    public void WhenFFmpegIsNotReady_NothingIsTested()
    {
        var ffmpeg = new FakeFfmpeg(new HardwareCapabilities { HasNvenc = true }, available: false);

        var answer = GraphicsHardwareReport.Test(ffmpeg, "auto", TimeProvider.System);

        Assert.Equal(GraphicsHardwareOutcomes.Unavailable, answer.Outcome);
        Assert.Equal(0, ffmpeg.Retests);
    }

    // ---- The Engine actions ----

    [Fact]
    public async Task TestHardware_TestsAgainRightNowAndReturnsTheNewAnswer()
    {
        var ffmpeg = new FakeFfmpeg(new HardwareCapabilities(), available: true)
        {
            AfterRetest = new HardwareCapabilities { HasNvenc = true, GpuName = "NVIDIA GeForce RTX 5080" },
        };
        await using var app = await StartAsync(ffmpeg, authenticated: true);

        using var before = await app.Client.GetAsync("/settings/playback/hardware");
        var first = (await before.Content.ReadFromJsonAsync<GraphicsHardwareDto>())!;
        using var tested = await app.Client.PostAsync("/settings/playback/test-hardware", null);
        var second = (await tested.Content.ReadFromJsonAsync<GraphicsHardwareDto>())!;

        Assert.Equal(GraphicsHardwareOutcomes.None, first.Outcome);
        Assert.Null(first.TestedAt);
        Assert.Equal(1, ffmpeg.Retests);
        Assert.Equal(GraphicsHardwareOutcomes.Works, second.Outcome);
        Assert.Equal("NVIDIA GeForce RTX 5080", second.CardName);
        Assert.NotNull(second.TestedAt);
    }

    [Theory]
    [InlineData("GET", "/settings/playback/hardware")]
    [InlineData("POST", "/settings/playback/test-hardware")]
    public async Task TheHardwareActions_RefuseAnyoneWhoIsNotSignedInAsAnAdministrator(string method, string path)
    {
        var ffmpeg = new FakeFfmpeg(new HardwareCapabilities(), available: true);
        await using var app = await StartAsync(ffmpeg, authenticated: false);

        using var response = await app.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Expected a refusal, got {response.StatusCode}.");
        Assert.Equal(0, ffmpeg.Retests);
    }

    private async Task<TestApplication> StartAsync(FakeFfmpeg ffmpeg, bool authenticated)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new HandlerSwitch(authenticated));
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthPolicies.AdministratorEligibility, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<IConfigurationLoader>(new ConfigurationDirectoryLoader(Path.Combine(_root, "config")));
        builder.Services.AddSingleton<IFFmpegService>(ffmpeg);
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/settings").MapTranscodingSettings();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed record HandlerSwitch(bool Authenticated);

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        HandlerSwitch mode)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(mode.Authenticated
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin-test")], Scheme.Name)),
                    Scheme.Name))
                : AuthenticateResult.NoResult());
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

    private sealed class FakeFfmpeg(HardwareCapabilities hardware, bool available) : IFFmpegService
    {
        private HardwareCapabilities _hardware = hardware;
        public int Retests { get; private set; }
        public HardwareCapabilities? AfterRetest { get; init; }
        public string? FfmpegPath => available ? "fake" : null;
        public string? FfprobePath => available ? "fake" : null;
        public bool IsAvailable => available;
        public HardwareCapabilities HardwareCapabilities => _hardware;

        public HardwareCapabilities RetestHardware()
        {
            Retests++;
            _hardware = AfterRetest ?? _hardware;
            return _hardware;
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
