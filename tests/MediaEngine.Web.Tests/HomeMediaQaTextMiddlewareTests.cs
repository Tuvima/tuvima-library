using System.Text.Json;
using MediaEngine.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace MediaEngine.Web.Tests;

public sealed class HomeMediaQaTextMiddlewareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fixture-text-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProductionQueryLeavesNormalResponseAndAuthorizationOutcomeUnchanged()
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new QueryString("?qaTextScale=200");
        await using var body = new MemoryStream(); context.Response.Body = body;
        var middleware = new HomeMediaQaTextMiddleware(async request =>
        {
            request.Response.StatusCode = StatusCodes.Status401Unauthorized;
            request.Response.ContentType = "text/html";
            await request.Response.WriteAsync("<html><head></head><body>Sign in required</body></html>");
        }, new ProductionEnvironment());
        await middleware.InvokeAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        Assert.Equal("<html><head></head><body>Sign in required</body></html>", await reader.ReadToEndAsync());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void TextStressRequiresDevelopmentExactOptInAndMatchingDisposableMarker()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "config");
        File.WriteAllText(Path.Combine(_root, ".home-media-cards-disposable.json"), JsonSerializer.Serialize(new { task = "home-media-cards-2026-10-03", root = _root }));
        Assert.True(HomeMediaQaTextMiddleware.ShouldEnable(true, _root, config, "200"));
        Assert.False(HomeMediaQaTextMiddleware.ShouldEnable(false, _root, config, "200"));
        Assert.False(HomeMediaQaTextMiddleware.ShouldEnable(true, null, config, "200"));
        Assert.False(HomeMediaQaTextMiddleware.ShouldEnable(true, _root, config, null));
        Assert.False(HomeMediaQaTextMiddleware.ShouldEnable(true, _root, "config", "200"));
        File.WriteAllText(Path.Combine(_root, ".home-media-cards-disposable.json"), "{}");
        Assert.False(HomeMediaQaTextMiddleware.ShouldEnable(true, _root, config, "200"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ProductionEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tuvima.Library";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
