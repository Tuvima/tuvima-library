using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's batch edit call keeps sending what the editor sends, now as field operations.</summary>
public sealed class EngineApiClientBatchEditTests
{
    [Fact]
    public async Task BatchEdit_SendsGenresAndTagsAsReplaceAndOtherFieldsAsSetOrClear_AndReadsTheCounts()
    {
        var handler = new CapturingHandler(new LibraryBatchEditResult { UpdatedCount = 2, SentToReviewCount = 1 });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var result = await client.BatchEditAsync(ids, new Dictionary<string, string>
        {
            ["genre"] = "Drama, Comedy",
            ["custom_tags"] = "Favourite; Rewatch",
            ["rating"] = "8.5",
            ["language"] = "  ",
        });

        Assert.Equal(2, result!.UpdatedCount);
        Assert.Equal(1, result.SentToReviewCount);
        Assert.Equal("/library/batch-edit", handler.Path);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, json.RootElement.GetProperty("entity_ids").GetArrayLength());
        var changes = json.RootElement.GetProperty("field_changes").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        Assert.Equal("replace", changes["genre"].GetProperty("op").GetString());
        Assert.Equal(["Drama", "Comedy"], changes["genre"].GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["Favourite", "Rewatch"], changes["custom_tags"].GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("set", changes["rating"].GetProperty("op").GetString());
        Assert.Equal("8.5", changes["rating"].GetProperty("value").GetString());
        Assert.Equal("clear", changes["language"].GetProperty("op").GetString());
    }

    private sealed class CapturingHandler(LibraryBatchEditResult result) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
