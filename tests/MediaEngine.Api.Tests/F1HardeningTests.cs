using System.Net;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed class F1HardeningTests
{
    [Fact]
    public void EngineAllowedHosts_IsNotWildcard_AndNamesOnlyLoopback()
    {
        var root = FindRepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "appsettings.json")));
        var allowed = document.RootElement.GetProperty("AllowedHosts").GetString();

        Assert.False(string.IsNullOrWhiteSpace(allowed));
        Assert.NotEqual("*", allowed!.Trim());
        var hosts = allowed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "localhost", "127.0.0.1", "[::1]" }, hosts);
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-entrypoint.sh")]
    [InlineData("installer.iss")]
    public void EngineCallers_UseAnAllowedLoopbackHost(string file)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"https?://([A-Za-z0-9.\[\]:+_-]+?):61495"))
        {
            Assert.Contains(match.Groups[1].Value, new[] { "localhost", "127.0.0.1", "0.0.0.0", "+" });
        }
    }

    [Fact]
    public void PasskeyOptions_UsePublicAddressHostAndOnlyThatOrigin_AndFollowChanges()
    {
        using var scope = new TempConfig();
        var configure = new PublicAddressPasskeyOptions(scope.Loader);

        var none = new IdentityPasskeyOptions();
        configure.Configure(none);
        Assert.Null(none.ServerDomain);
        Assert.False(Accepts(none, "https://evil.example"));

        scope.SetPublicAddress("https://tuvima.example.com");
        var first = new IdentityPasskeyOptions();
        configure.Configure(first);
        Assert.Equal("tuvima.example.com", first.ServerDomain);
        Assert.True(Accepts(first, "https://tuvima.example.com"));
        Assert.False(Accepts(first, "https://evil.example"));
        Assert.False(Accepts(first, "http://tuvima.example.com"));

        scope.SetPublicAddress("https://other.example.org");
        var second = new IdentityPasskeyOptions();
        configure.Configure(second);
        Assert.Equal("other.example.org", second.ServerDomain);
        Assert.False(Accepts(second, "https://tuvima.example.com"));
    }

    [Fact]
    public void PairingOrigin_IgnoresForwardedHeaders_AndPrefersPublicAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 61495);
        context.Request.Headers["X-Forwarded-Host"] = "evil.example";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        var withoutAddress = ClientAuthorizationEndpoints.PairingOrigin(context.Request, new NetworkSettings());
        Assert.Equal("http://localhost:61495", withoutAddress);
        Assert.DoesNotContain("evil.example", withoutAddress);

        var network = new NetworkSettings();
        network.Remote.PublicHostname = "https://tuvima.example.com/";
        // A trailing slash is accepted as a public address and trimmed.
        Assert.Equal("https://tuvima.example.com", ClientAuthorizationEndpoints.PairingOrigin(context.Request, network));

        network.Remote.PublicHostname = "https://tuvima.example.com";
        Assert.Equal("https://tuvima.example.com", ClientAuthorizationEndpoints.PairingOrigin(context.Request, network));
    }

    [Fact]
    public void PasskeyOptions_RejectCrossOriginEvenForThePublicOrigin()
    {
        using var scope = new TempConfig();
        scope.SetPublicAddress("https://tuvima.example.com");
        var options = new IdentityPasskeyOptions();
        new PublicAddressPasskeyOptions(scope.Loader).Configure(options);

        Assert.False(Accepts(options, "https://tuvima.example.com", crossOrigin: true));
    }

    [Fact]
    public void ScopedPasskeyOptions_FollowAnAddressChangeWithoutRestart()
    {
        using var scope = new TempConfig();
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationLoader>(scope.Loader);
        services.AddOptions();
        services.AddSingleton<IConfigureOptions<IdentityPasskeyOptions>, PublicAddressPasskeyOptions>();
        services.AddScoped<IOptions<IdentityPasskeyOptions>>(sp => sp.GetRequiredService<IOptionsSnapshot<IdentityPasskeyOptions>>());
        using var provider = services.BuildServiceProvider(validateScopes: true);

        scope.SetPublicAddress("https://first.example.com");
        using (var first = provider.CreateScope())
        {
            Assert.Equal("first.example.com", first.ServiceProvider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value.ServerDomain);
        }

        scope.SetPublicAddress("https://second.example.com");
        using (var second = provider.CreateScope())
        {
            Assert.Equal("second.example.com", second.ServiceProvider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value.ServerDomain);
        }
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example", false)]
    public async Task Engine_HostFiltering_AcceptsOnlyLoopbackNames(string host, bool accepted)
    {
        var root = FindRepoRoot();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(root, "src", "MediaEngine.Api", "appsettings.json"), optional: false);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHostFiltering(options =>
            options.AllowedHosts = builder.Configuration["AllowedHosts"]!.Split(';', StringSplitOptions.RemoveEmptyEntries));
        await using var app = builder.Build();
        app.UseHostFiltering();
        app.MapGet("/ping", () => "ok");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, address + "/ping");
        request.Headers.Host = host == "evil.example" ? host : host + ":" + new Uri(address).Port;
        using var response = await client.SendAsync(request);

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static bool Accepts(IdentityPasskeyOptions options, string origin, bool crossOrigin = false) =>
        options.ValidateOrigin!(new PasskeyOriginValidationContext { Origin = origin, CrossOrigin = crossOrigin, HttpContext = new DefaultHttpContext() }).AsTask().GetAwaiter().GetResult();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class TempConfig : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-f1-{Guid.NewGuid():N}");

        public TempConfig() => Loader = new ConfigurationDirectoryLoader(_path);

        public ConfigurationDirectoryLoader Loader { get; }

        public void SetPublicAddress(string address)
        {
            var network = Loader.LoadNetwork();
            network.Remote.PublicHostname = address;
            Loader.SaveNetwork(network);
        }

        public void Dispose()
        {
            Loader.Dispose();
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
