using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's batch edit calls send the field operations exactly as the batch section built them, and tell refusals apart.</summary>
public sealed class EngineApiClientBatchEditTests
{
    private static EngineApiClient ClientFor(CapturingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") }, NullLogger<EngineApiClient>.Instance);

    [Fact]
    public async Task ApplyBatchEdit_SendsEachOperationAsGivenAndReadsTheCounts()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, new LibraryBatchEditResult { UpdatedCount = 2, SentToReviewCount = 1 });
        var client = ClientFor(handler);
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var outcome = await client.ApplyLibraryBatchEditAsync(new LibraryBatchEditRequest
        {
            EntityIds = ids,
            FieldChanges =
            [
                new LibraryFieldChange { Key = "genre", Op = LibraryBatchEditOps.Add, Values = ["Drama", "Comedy"] },
                new LibraryFieldChange { Key = "custom_tags", Op = LibraryBatchEditOps.Remove, Values = ["Rewatch"] },
                new LibraryFieldChange { Key = "rating", Op = LibraryBatchEditOps.Set, Value = "8.5" },
                new LibraryFieldChange { Key = "language", Op = LibraryBatchEditOps.Clear },
            ],
        });

        Assert.True(outcome.IsOk);
        Assert.Equal(2, outcome.Value!.UpdatedCount);
        Assert.Equal(1, outcome.Value.SentToReviewCount);
        Assert.Equal("/library/batch-edit", handler.Path);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, json.RootElement.GetProperty("entity_ids").GetArrayLength());
        var changes = json.RootElement.GetProperty("field_changes").EnumerateArray().ToDictionary(change => change.GetProperty("key").GetString()!);
        Assert.Equal("add", changes["genre"].GetProperty("op").GetString());
        Assert.Equal(["Drama", "Comedy"], changes["genre"].GetProperty("values").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("remove", changes["custom_tags"].GetProperty("op").GetString());
        Assert.Equal("set", changes["rating"].GetProperty("op").GetString());
        Assert.Equal("8.5", changes["rating"].GetProperty("value").GetString());
        Assert.Equal("clear", changes["language"].GetProperty("op").GetString());
    }

    [Fact]
    public async Task PreviewBatchEdit_PostsTheIdsAndReadsTheFieldStates()
    {
        var preview = new LibraryBatchEditPreview
        {
            AffectedCount = 2,
            Fields =
            [
                new LibraryBatchFieldState
                {
                    Key = "genre",
                    Label = "Genres",
                    Kind = LibraryBatchFieldKinds.List,
                    Operations = [LibraryBatchEditOps.Add],
                    Editable = true,
                    IsMixed = true,
                    DistinctValues = [new LibraryBatchDistinctValue { Value = "Drama", Count = 2 }],
                    DistinctCount = 1,
                },
            ],
        };
        var handler = new CapturingHandler(HttpStatusCode.OK, preview);
        var client = ClientFor(handler);
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var outcome = await client.PreviewLibraryBatchEditAsync(new LibraryBatchEditPreviewRequest { EntityIds = ids });

        Assert.True(outcome.IsOk);
        Assert.Equal("/library/batch-edit/preview", handler.Path);
        var field = Assert.Single(outcome.Value!.Fields);
        Assert.True(field.IsMixed);
        Assert.Equal("Drama", Assert.Single(field.DistinctValues).Value);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, json.RootElement.GetProperty("entity_ids").GetArrayLength());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, LibraryBatchEditOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest, LibraryBatchEditOutcomeStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, LibraryBatchEditOutcomeStatus.Unavailable)]
    public async Task ApplyBatchEdit_TellsARefusalFromAnInvalidRequestFromAnUnreachableEngine(HttpStatusCode status, LibraryBatchEditOutcomeStatus expected)
    {
        var client = ClientFor(new CapturingHandler(status, new { detail = "No." }));

        var outcome = await client.ApplyLibraryBatchEditAsync(new LibraryBatchEditRequest
        {
            EntityIds = [Guid.NewGuid()],
            FieldChanges = [new LibraryFieldChange { Key = "year", Op = LibraryBatchEditOps.Set, Value = "2020" }],
        });

        Assert.False(outcome.IsOk);
        Assert.Equal(expected, outcome.Status);
    }

    private sealed class CapturingHandler(HttpStatusCode status, object body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
        }
    }
}
