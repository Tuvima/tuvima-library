using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class HostAllowListTests
{
    [Theory]
    [InlineData("localhost:5016")]
    [InlineData("LOCALHOST")]
    [InlineData("192.168.1.20:5016")]
    [InlineData("[::1]:5016")]
    [InlineData("[fd12::1]")]
    [InlineData("8.8.8.8")]
    [InlineData("library.example.test")]
    [InlineData("tuvima.example.ts.net")]
    [InlineData("my-server")]
    [InlineData("MY-SERVER.local")]
    [InlineData("tuvima")]
    [InlineData("tuvima.local:5016")]
    [InlineData("media.home.arpa")]
    [InlineData("my-nas")]
    [InlineData("nas.lan:5016")]
    [InlineData("anything.local")]
    [InlineData("router.internal")]
    [InlineData("box.localdomain")]
    [InlineData("fritz.box")]
    [InlineData("my.fritz.box")]
    [InlineData("tv.home")]
    public async Task AllowedHosts_PassThrough(string host)
    {
        var (status, _) = await SendAsync(host);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:5016")]
    [InlineData("localhost.evil.example")]
    [InlineData("evil.local.example")]
    [InlineData("nas.example.com")]
    [InlineData("evilfritz.box.example")]
    [InlineData("")]
    public async Task UnknownHosts_AreRefusedWithAHelpfulMessage(string host)
    {
        var (status, body) = await SendAsync(host);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("This address isn't allowed for Tuvima Library. Add it under Settings > Network.", body);
    }

    [Fact]
    public async Task LiveHealthCheck_IsExempt()
    {
        var (status, _) = await SendAsync("evil.example", "/health/live");

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    private static async Task<(int Status, string Body)> SendAsync(string host, string path = "/")
    {
        var network = new NetworkSettings
        {
            Local = new LocalNetworkSettings { AllowedHostnames = ["media.home.arpa"] },
            Remote = new RemoteNetworkSettings { PublicHostname = "https://library.example.test" },
        };
        var services = new ServiceCollection();
        services.AddSingleton(new HostAllowList(network, "https://tuvima.example.ts.net", "my-server"));
        using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseHostAllowList();
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Host = new HostString(host.Length == 0 ? "" : host);
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await pipeline(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
