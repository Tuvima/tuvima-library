using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Identify, match and activity calls (Videos spec WP12): they use the documented routes, a refusal reads "not available"
/// (the Engine answers not found to anyone who may not see Vault items), and typed search text is never written to a log.
/// </summary>
public sealed class EngineApiClientVaultIdentifyTests
{
    private static readonly Guid Asset = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private const string Typed = "secret-title-words";

    private sealed record Seen(HttpMethod Method, string Path, string Body);

    private static (EngineApiClient Client, List<Seen> Requests, CapturingLoggerProvider Logs) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var requests = new List<Seen>();
        var logs = new CapturingLoggerProvider();
        var http = new HttpClient(new FuncHandler(request =>
        {
            requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return answer(request);
        }))
        { BaseAddress = new Uri("http://engine.test") };
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        return (new EngineApiClient(http, factory.CreateLogger<EngineApiClient>(), loggerFactory: factory), requests, logs);
    }

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "Not found.", detail = "Not found.", status = 404 }) };

    [Fact]
    public async Task Search_PostsTheTypedTextInTheBody_ToTheItemRoute_AndNeverLogsIt()
    {
        var (client, requests, logs) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new VaultCandidateListDto { Status = "ok" }),
        });

        var call = await client.SearchVaultIdentifyAsync(Asset, Typed);

        Assert.Equal(VaultOutcome.Ok, call.Outcome);
        var seen = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal($"/api/v1/vault/items/{Asset:D}/identify/search", seen.Path);
        Assert.Contains(Typed, seen.Body);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(Typed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_Preview_Apply_AndMatch_ReadNotAvailable_WhenTheEngineAnswersNotFound()
    {
        var (client, _, logs) = Client(_ => NotFound());

        Assert.Equal(VaultOutcome.NotAvailable, (await client.SearchVaultIdentifyAsync(Asset, Typed)).Outcome);
        Assert.Equal(VaultOutcome.NotAvailable, (await client.PreviewVaultIdentifyAsync(Asset, new VaultIdentifyPreviewRequest { ServerId = "s", RecordId = "r" })).Outcome);
        Assert.Equal(VaultOutcome.NotAvailable, (await client.ApplyVaultIdentifyAsync(Asset, new VaultIdentifyApplyRequest { ServerId = "s", RecordId = "r", Fields = ["title"] })).Outcome);
        Assert.Equal(VaultOutcome.NotAvailable, (await client.GetVaultMatchAsync(Asset)).Outcome);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Apply_SendsOnlyTheFieldsItWasGiven_AndAPlainRefusalKeepsItsSentence()
    {
        var (client, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { title = "Invalid request.", detail = "Choose at least one field.", status = 400 }),
        });

        var call = await client.ApplyVaultIdentifyAsync(Asset, new VaultIdentifyApplyRequest { ServerId = "s", RecordId = "r", Fields = ["title", "cover"] });

        Assert.Equal(VaultOutcome.Rejected, call.Outcome);
        var seen = Assert.Single(requests);
        Assert.Equal($"/api/v1/vault/items/{Asset:D}/identify/apply", seen.Path);
        Assert.Contains("\"title\"", seen.Body);
        Assert.Contains("\"cover\"", seen.Body);
    }

    [Fact]
    public async Task RestoredBookmarks_ReadsTheCount_AndNotFoundMeansNotAvailable()
    {
        var (ok, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new VaultRestoredBookmarksDto { Waiting = 2, Message = "2 saved bookmarks are still waiting." }),
        });

        var call = await ok.GetVaultRestoredBookmarksAsync();

        Assert.Equal(VaultOutcome.Ok, call.Outcome);
        Assert.Equal(2, call.Value!.Waiting);
        Assert.Equal("/api/v1/vault/restored-bookmarks", Assert.Single(requests).Path);

        var (hidden, _, _) = Client(_ => NotFound());
        Assert.Equal(VaultOutcome.NotAvailable, (await hidden.GetVaultRestoredBookmarksAsync()).Outcome);
    }

    [Fact]
    public async Task Activity_ReadsTheList_AndNotFoundMeansNotAvailable()
    {
        var line = new VaultActivityItemDto { Id = Guid.NewGuid(), AssetId = Asset, Kind = "added", FileName = "a.mp4", At = DateTimeOffset.UtcNow };
        var (ok, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { line }) });

        var call = await ok.GetVaultActivityAsync();

        Assert.Equal(VaultOutcome.Ok, call.Outcome);
        Assert.Equal("added", Assert.Single(call.Value!).Kind);
        Assert.Equal("/api/v1/vault/activity", Assert.Single(requests).Path);

        var (hidden, _, _) = Client(_ => NotFound());
        Assert.Equal(VaultOutcome.NotAvailable, (await hidden.GetVaultActivityAsync()).Outcome);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
