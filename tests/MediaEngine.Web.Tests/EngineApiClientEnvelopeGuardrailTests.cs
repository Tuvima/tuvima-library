namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientEnvelopeGuardrailTests
{
    // Stage 5B found that the client contains three observably different failure-state
    // families. Only thirteen methods matched the tracked helper family. These ceilings
    // preserve the behavior-compatible decision: existing legacy calls may be migrated
    // deliberately, but new raw HTTP envelopes must not be added.
    [Fact]
    public void RawHttpEnvelopeInventory_CannotGrow()
    {
        var source = ReadClientSources();

        AssertAtOrBelow(source, "_http.GetFromJsonAsync", 116);
        // SharedEntityEditor uses a manual status check so failed typed target loads retain HTTP
        // failure classification and LastStatusCode; its 404 behavior is covered explicitly.
        // The current checkout contains 25 raw GET calls: the added typed audiobook bookmark
        // list read preserves per-call HTTP status versus unknown transport outcomes. The
        // text-track reader still shares its existing raw-text envelope.
        AssertAtOrBelow(source, "_http.GetAsync", 25);
        // One additional raw POST is the typed bookmark-create outcome path. Its focused tests
        // cover success classification, definite 4xx vs unknown 5xx/malformed results, and one
        // send per attempt with no automatic retry.
        AssertAtOrBelow(source, "_http.PostAsJsonAsync", 63);
        AssertAtOrBelow(source, "_http.PutAsJsonAsync", 28);
        AssertAtOrBelow(source, "_http.DeleteAsync", 14);
    }

    [Fact]
    public void TrackedHelpersAndBehavioralExclusions_RemainExplicit()
    {
        var facade = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.cs");

        Assert.Contains("private async Task<T?> GetAsync<T>(", facade, StringComparison.Ordinal);
        Assert.Contains("private async Task<TRes?> PostAsync<TReq, TRes>(", facade, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> PutAsync<TReq>(", facade, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> DeleteAsync(", facade, StringComparison.Ordinal);
        Assert.Contains("\"Legacy LastError-only\" shape", facade, StringComparison.Ordinal);
        Assert.Contains("\"Manual GetAsync + explicit status check\" GET shape", facade, StringComparison.Ordinal);
        Assert.Contains("Methods with no failure-state bookkeeping at all", facade, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawTextEnvelope_ReportsFailureAndClearsItAfterAuthorizedTrackRead()
    {
        var calls = 0;
        using var http = new HttpClient(new SequenceHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello"),
                };
        }))
        { BaseAddress = new Uri("http://localhost:61495/") };
        var client = new MediaEngine.Web.Services.Integration.EngineApiClient(
            http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Web.Services.Integration.EngineApiClient>.Instance);

        Assert.Null(await client.GetTextTrackContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(404, client.LastStatusCode);
        Assert.StartsWith("WEBVTT", await client.GetTextTrackContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Null(client.LastStatusCode);
    }

    [Fact]
    public async Task AudiobookBookmarkListOutcome_PreservesPerCallHttpAndTransportFailures()
    {
        var calls = 0;
        using var http = new HttpClient(new SequenceHandler(_ =>
        {
            calls++;
            return calls switch
            {
                1 => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(new[]
                        {
                            new MediaEngine.Contracts.Playback.AudiobookBookmarkDto
                            {
                                Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(), WorkId = Guid.NewGuid(),
                                AssetId = Guid.NewGuid(), PositionSeconds = 12,
                            },
                        }),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                },
                2 => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
                3 => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError),
                4 => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("not-json") },
                5 => throw new IOException("Connection ended before a response."),
                _ => throw new OperationCanceledException("The request was canceled."),
            };
        }))
        { BaseAddress = new Uri("http://localhost:61495/") };
        var client = new MediaEngine.Web.Services.Integration.EngineApiClient(
            http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Web.Services.Integration.EngineApiClient>.Instance);
        var workId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var success = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.Success, success.Outcome);
        Assert.Single(success.Value!);
        Assert.Equal(1, calls);

        var missing = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.DefiniteFailure, missing.Outcome);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkFailureKind.NotFound, missing.FailureKind);
        Assert.Equal(2, calls);

        var serverFailure = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.Unknown, serverFailure.Outcome);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, serverFailure.StatusCode);
        Assert.Equal(3, calls);

        var malformed = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.Unknown, malformed.Outcome);
        Assert.Null(malformed.StatusCode);
        Assert.Equal(4, calls);

        var transport = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.Unknown, transport.Outcome);
        Assert.Null(transport.StatusCode);
        Assert.Equal(5, calls);

        var canceled = await client.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId);
        Assert.Equal(MediaEngine.Web.Services.Integration.AudiobookBookmarkOperationOutcome.Unknown, canceled.Outcome);
        Assert.Null(canceled.StatusCode);
        Assert.Equal(6, calls);
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static void AssertAtOrBelow(string source, string token, int ceiling)
    {
        var count = source.Split(token, StringSplitOptions.None).Length - 1;
        Assert.True(
            count <= ceiling,
            $"Raw Engine client call '{token}' grew from its Stage 5B ceiling of {ceiling} to {count}. "
            + "Use the matching shared helper, or add an explicitly reviewed failure-semantics test before changing the ceiling.");
    }

    private static string ReadClientSources()
    {
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "src", "MediaEngine.Web", "Services", "Integration");
        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "EngineApiClient*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
