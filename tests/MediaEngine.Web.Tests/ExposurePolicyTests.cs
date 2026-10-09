using System.Net;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ExposurePolicyTests : IDisposable
{
    private static readonly string[] Routes =
    [
        "/",
        "/_blazor/negotiate",
        "/auth/login",
        "/engine-stream/6f1c2d3e-0000-0000-0000-000000000001",
        "/engine-image/x",
        "/view-media/x",
        "/api/v1/x",
        "/application-events/x",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tuvima-exposure-" + Guid.NewGuid().ToString("N"));

    public ExposurePolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string, string, bool, int> Matrix()
    {
        // setting, ingress address, https, expected status for every ordinary route
        var data = new TheoryData<string, string, bool, int>();
        foreach (var setting in new[] { "this_computer", "home_network", "anywhere" })
        {
            foreach (var (address, rank) in new[] { ("127.0.0.1", 0), ("192.168.1.20", 1), ("8.8.8.8", 2) })
            {
                var settingRank = setting switch { "this_computer" => 0, "home_network" => 1, _ => 2 };
                foreach (var https in new[] { false, true })
                {
                    var expected = rank > settingRank
                        ? StatusCodes.Status403Forbidden
                        : rank == 2 && !https ? StatusCodes.Status426UpgradeRequired : StatusCodes.Status200OK;
                    data.Add(setting, address, https, expected);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryRoute_FollowsTheOneDoorRule(string setting, string address, bool https, int expected)
    {
        WriteSetting(setting);
        foreach (var route in Routes)
        {
            var (status, _, _) = await SendAsync(route, address, https);
            Assert.True(status == expected, $"{setting} {address} https={https} {route}: expected {expected} got {status}");
        }
    }

    [Theory]
    [InlineData("this_computer", "192.168.1.20")]
    [InlineData("this_computer", "8.8.8.8")]
    [InlineData("home_network", "8.8.8.8")]
    public async Task LiveHealthCheck_IsAlwaysExempt(string setting, string address)
    {
        WriteSetting(setting);

        var (status, _, _) = await SendAsync("/health/live", address, https: false);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Theory]
    [InlineData("this_computer", "8.8.8.8")]
    [InlineData("home_network", "8.8.8.8")]
    public async Task RemoteProbe_IsAlwaysExempt(string setting, string address)
    {
        WriteSetting(setting);

        var (status, _, _) = await SendAsync("/_tuvima/remote-probe", address, https: false);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Refusal_IsNoStoreAndJsonForApiRoutes()
    {
        WriteSetting("home_network");

        var (status, body, cacheControl) = await SendAsync("/api/v1/display/home", "8.8.8.8", https: true);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("{\"error\":\"not_available_here\"}", body);
        Assert.Equal("no-store", cacheControl);

        (status, body, _) = await SendAsync("/application-events/stream", "8.8.8.8", https: true);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("{\"error\":\"not_available_here\"}", body);
    }

    [Fact]
    public async Task Refusal_IsAMinimalPageForBrowsers()
    {
        WriteSetting("this_computer");

        var (status, body, cacheControl) = await SendAsync("/", "192.168.1.20", https: false, accept: "text/html");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("Tuvima Library isn&#39;t available from here", body, StringComparison.Ordinal);
        Assert.Equal("no-store", cacheControl);
    }

    [Fact]
    public async Task ChangingTheSettingsFile_AppliesOnTheNextRequest()
    {
        WriteSetting("home_network");
        var (before, _, _) = await SendAsync("/", "192.168.1.20", https: false);
        Assert.Equal(StatusCodes.Status200OK, before);

        WriteSetting("this_computer");
        var (after, _, _) = await SendAsync("/", "192.168.1.20", https: false, newReader: false);
        Assert.Equal(StatusCodes.Status403Forbidden, after);
    }

    [Fact]
    public async Task MissingFile_MeansHomeNetwork_AndInvalidFile_FailsClosed()
    {
        var (home, _, _) = await SendAsync("/", "192.168.1.20", https: false);
        Assert.Equal(StatusCodes.Status200OK, home);
        var (remote, _, _) = await SendAsync("/", "8.8.8.8", https: true, newReader: false);
        Assert.Equal(StatusCodes.Status403Forbidden, remote);

        File.WriteAllText(Path.Combine(_dir, "network.json"), "{ not json");
        var (lan, _, _) = await SendAsync("/", "192.168.1.20", https: false, newReader: false);
        Assert.Equal(StatusCodes.Status403Forbidden, lan);
        var (self, _, _) = await SendAsync("/", "127.0.0.1", https: false, newReader: false);
        Assert.Equal(StatusCodes.Status200OK, self);
    }

    [Theory]
    [InlineData("this_computer", IngressKind.ThisComputer, false, ExposureDecision.Allow)]
    [InlineData("this_computer", IngressKind.HomeNetwork, true, ExposureDecision.NotAvailableHere)]
    [InlineData("home_network", IngressKind.Remote, true, ExposureDecision.NotAvailableHere)]
    [InlineData("anywhere", IngressKind.Remote, false, ExposureDecision.UpgradeRequired)]
    [InlineData("anywhere", IngressKind.Remote, true, ExposureDecision.Allow)]
    [InlineData("garbage", IngressKind.HomeNetwork, true, ExposureDecision.NotAvailableHere)]
    [InlineData(null, IngressKind.ThisComputer, false, ExposureDecision.Allow)]
    public void Evaluate_ComparesIngressToSetting(string? setting, IngressKind ingress, bool https, ExposureDecision expected) =>
        Assert.Equal(expected, ExposurePolicy.Evaluate(setting, ingress, https));

    private void WriteSetting(string value)
    {
        var path = Path.Combine(_dir, "network.json");
        File.WriteAllText(path, $"{{\"who_can_connect\":\"{value}\"}}");
        // Make the change visible even when two writes land inside the file system's timestamp granularity.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(_writes++));
    }

    private int _writes;
    private ExposureSettingsReader? _reader;

    private async Task<(int Status, string Body, string? CacheControl)> SendAsync(
        string path, string address, bool https, string? accept = null, bool newReader = true)
    {
        if (newReader || _reader is null)
        {
            _reader = new ExposureSettingsReader(new DashboardConfigurationReader(_dir), _dir);
        }

        var services = new ServiceCollection();
        services.AddSingleton(new IngressClassifier(null, null));
        services.AddSingleton(_reader);
        using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseExposurePolicy();
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Connection.LocalPort = 5016;
        context.Request.Scheme = https ? "https" : "http";
        context.Request.Host = new HostString("tuvima.local");
        context.Request.Path = path;
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        context.Response.Body = new MemoryStream();
        await pipeline(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body, context.Response.Headers.CacheControl.ToString());
    }
}
