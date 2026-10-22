using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Contracts.Tests;

public sealed class IngestionQueueProgressContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void IngestionQueueProgress_KeepsItsSnakeCaseWireNames()
    {
        var node = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(Sample(), JsonOptions));

        Assert.Equal(
            new[]
            {
                "state", "files_found", "files_done", "files_waiting", "waiting_for", "files_more_found", "outcomes",
                "tally", "files_unsorted", "steps_total", "steps_completed", "progress", "started_at",
                "last_activity_at", "estimated_seconds_remaining", "updated_at",
            }.Order(StringComparer.Ordinal),
            node.Select(pair => pair.Key).Order(StringComparer.Ordinal));

        Assert.Equal(
            new[] { "added", "needs_review", "failed", "skipped", "unmatched" }.Order(StringComparer.Ordinal),
            Assert.IsType<JsonObject>(node["outcomes"]).Select(pair => pair.Key).Order(StringComparer.Ordinal));

        var tally = Assert.IsType<JsonArray>(node["tally"]);
        Assert.Equal(
            new[] { "media_type", "files" }.Order(StringComparer.Ordinal),
            Assert.IsType<JsonObject>(tally[0]).Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void IngestionQueueProgress_RoundTripsWithoutLosingAnything()
    {
        var original = Sample();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<IngestionQueueProgressDto>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original.State, restored.State);
        Assert.Equal(original.FilesFound, restored.FilesFound);
        Assert.Equal(original.FilesDone, restored.FilesDone);
        Assert.Equal(original.FilesWaiting, restored.FilesWaiting);
        Assert.Equal(original.WaitingFor, restored.WaitingFor);
        Assert.Equal(original.FilesMoreFound, restored.FilesMoreFound);
        Assert.Equal(original.Outcomes.Added, restored.Outcomes.Added);
        Assert.Equal(original.Outcomes.NeedsReview, restored.Outcomes.NeedsReview);
        Assert.Equal(original.Outcomes.Failed, restored.Outcomes.Failed);
        Assert.Equal(original.Outcomes.Skipped, restored.Outcomes.Skipped);
        Assert.Equal(original.Outcomes.Unmatched, restored.Outcomes.Unmatched);
        var tally = Assert.Single(restored.Tally);
        Assert.Equal("Movies", tally.MediaType);
        Assert.Equal(7, tally.Files);
        Assert.Equal(original.FilesUnsorted, restored.FilesUnsorted);
        Assert.Equal(original.StepsTotal, restored.StepsTotal);
        Assert.Equal(original.StepsCompleted, restored.StepsCompleted);
        Assert.Equal(original.Progress, restored.Progress);
        Assert.Equal(original.StartedAt, restored.StartedAt);
        Assert.Equal(original.LastActivityAt, restored.LastActivityAt);
        Assert.Equal(original.EstimatedSecondsRemaining, restored.EstimatedSecondsRemaining);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
    }

    [Fact]
    public void IdleSnapshot_WritesNullForTheStartAndTheEstimate()
    {
        var node = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(new IngestionQueueProgressDto(), JsonOptions));

        Assert.Equal("idle", node["state"]!.GetValue<string>());
        Assert.Null(node["started_at"]);
        Assert.Null(node["estimated_seconds_remaining"]);
        Assert.Empty(Assert.IsType<JsonArray>(node["waiting_for"]));
        Assert.Empty(Assert.IsType<JsonArray>(node["tally"]));
    }

    private static IngestionQueueProgressDto Sample() => new()
    {
        State = "waiting",
        FilesFound = 508,
        FilesDone = 400,
        FilesWaiting = 12,
        WaitingFor = ["ffmpeg"],
        FilesMoreFound = 120,
        Outcomes = new IngestionQueueOutcomesDto { Added = 380, NeedsReview = 10, Failed = 6, Skipped = 4, Unmatched = 7 },
        Tally = [new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 7 }],
        FilesUnsorted = 3,
        StepsTotal = 4572,
        StepsCompleted = 4000,
        Progress = 0.875,
        StartedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
        LastActivityAt = new DateTimeOffset(2026, 10, 18, 12, 5, 0, TimeSpan.Zero),
        EstimatedSecondsRemaining = 42,
        UpdatedAt = new DateTimeOffset(2026, 10, 18, 12, 5, 1, TimeSpan.Zero),
    };
}
