using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>What the Dashboard's client sends and reads for the editor's Quick Facts totals (universal editor v4, D4).</summary>
public sealed class EngineApiClientEditorQuickFactsTests
{
    [Fact]
    public async Task TheRead_AsksForTheItemById_AndReadsTheTotals()
    {
        var id = Guid.NewGuid();
        var handler = new StaticHandler(HttpStatusCode.OK, """
            {"id":"11111111-2222-3333-4444-555555555555","kind":"work","file_count":3,"size_bytes":4096,"runtime_seconds":120.5,"best_quality":"1080p",
             "direct_owned":2,"direct_total":3,"leaf_owned":20,"leaf_total":24,"read_only_library":true,"roles":[],"counts_by_kind":[]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var facts = await client.GetEditorQuickFactsAsync(id);

        Assert.NotNull(facts);
        Assert.Equal("GET", handler.Method);
        Assert.Equal("/library/editor-quick-facts/" + id.ToString("D"), handler.Path);
        Assert.Equal(3, facts.FileCount);
        Assert.Equal(4096, facts.SizeBytes);
        Assert.Equal("1080p", facts.BestQuality);
        Assert.Equal(20, facts.LeafOwned);
        Assert.Equal(24, facts.LeafTotal);
        Assert.True(facts.ReadOnlyLibrary);
    }

    [Fact]
    public async Task ARefusedOrMissingItem_HasNoTotals()
    {
        var handler = new StaticHandler(HttpStatusCode.NotFound, """{"title":"Not found","status":404}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        Assert.Null(await client.GetEditorQuickFactsAsync(Guid.NewGuid()));
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Method { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
