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
        // The current checkout already contains 24 raw GET calls. The new text-track
        // reader shares one existing raw-text envelope; its failure behavior is tested below.
        AssertAtOrBelow(source, "_http.GetAsync", 24);
        AssertAtOrBelow(source, "_http.PostAsJsonAsync", 62);
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
        })) { BaseAddress = new Uri("http://localhost:61495/") };
        var client = new MediaEngine.Web.Services.Integration.EngineApiClient(
            http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Web.Services.Integration.EngineApiClient>.Instance);

        Assert.Null(await client.GetTextTrackContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(404, client.LastStatusCode);
        Assert.StartsWith("WEBVTT", await client.GetTextTrackContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Null(client.LastStatusCode);
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
