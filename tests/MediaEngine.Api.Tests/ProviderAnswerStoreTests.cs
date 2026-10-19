using System.Net;
using System.Text;
using MediaEngine.Api.DevSupport;

namespace MediaEngine.Api.Tests;

public sealed class ProviderAnswerStoreTests : IDisposable
{
    private const string ApiKey = "sk-live-1234567890abcdef";
    private const string SessionToken = "eyJhbGciOiJIUzI1NiJ9.payload.signature";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_provider_answers_{Guid.NewGuid():N}");

    public ProviderAnswerStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left behind only if a scanner holds a temp file; the unique folder name keeps runs apart.
        }
    }

    private string Answers => Path.Combine(_root, "answers");

    private ProviderAnswerStore Store(ProviderAnswerMode mode) =>
        new(mode, Answers, [ApiKey], Path.Combine(_root, "misses.txt"));

    private static HttpClient Client(ProviderAnswerStore store, HttpMessageHandler network) =>
        new(new ProviderAnswerHandler(store) { InnerHandler = network });

    [Fact]
    public async Task ReplayedRun_AnswersRecordedRequests_WithoutReachingTheNetwork()
    {
        var network = new FakeNetwork(_ => Json(HttpStatusCode.OK, """{"title":"Dune"}"""));
        using (var recording = Client(Store(ProviderAnswerMode.Record), network))
        {
            var recorded = await recording.GetStringAsync($"https://api.example.org/search?q=dune&api_key={ApiKey}");
            Assert.Equal("""{"title":"Dune"}""", recorded);
        }

        var offline = new FakeNetwork(_ => throw new InvalidOperationException("A replay must never reach the network."));
        using var replaying = Client(Store(ProviderAnswerMode.Replay), offline);

        var replayed = await replaying.GetAsync($"https://api.example.org/search?q=dune&api_key={ApiKey}");

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal("""{"title":"Dune"}""", await replayed.Content.ReadAsStringAsync());
        Assert.Equal("application/json", replayed.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, network.Calls);
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public async Task ReplayMiss_ReturnsServiceUnavailable_AndListsTheRequestWithoutItsKey()
    {
        using (var recording = Client(Store(ProviderAnswerMode.Record), new FakeNetwork(_ => Json(HttpStatusCode.OK, "{}"))))
        {
            await recording.GetStringAsync("https://api.example.org/a");
        }

        using var replaying = Client(Store(ProviderAnswerMode.Replay), new FakeNetwork(_ => throw new InvalidOperationException()));

        var missed = await replaying.GetAsync($"https://api.example.org/b?api_key={ApiKey}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, missed.StatusCode);
        var misses = await File.ReadAllTextAsync(Path.Combine(_root, "misses.txt"));
        Assert.Contains("https://api.example.org/b?api_key=REDACTED", misses, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, misses, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_RemovesKeysAndTokens_FromEverySavedFile()
    {
        var network = new FakeNetwork(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, $$$"""{"status":"success","data":{"token":"{{{SessionToken}}}"}}""")
            : Json(HttpStatusCode.OK, $$"""{"echo":"{{ApiKey}}","next":"https://api.example.org/p2?apikey={{ApiKey}}"}"""));
        using (var recording = Client(Store(ProviderAnswerMode.Record), network))
        {
            using var login = await recording.PostAsync(
                "https://api.example.org/login",
                new StringContent($$"""{"apikey":"{{ApiKey}}"}""", Encoding.UTF8, "application/json"));
            Assert.Contains(SessionToken, await login.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await recording.GetStringAsync($"https://api.example.org/{ApiKey}/series?token=abc123456");
        }

        Assert.Empty(ProviderAnswerStore.FindLeaks(Answers, [ApiKey, SessionToken, "abc123456"]));

        using var replaying = Client(Store(ProviderAnswerMode.Replay), new FakeNetwork(_ => throw new InvalidOperationException()));
        using var replayedLogin = await replaying.PostAsync(
            "https://api.example.org/login",
            new StringContent($$"""{"apikey":"{{ApiKey}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal("""{"status":"success","data":{"token":"REDACTED"}}""", await replayedLogin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recording_KeepsTheFirstAnswer_ButReplacesAPassingOutageWithALaterRealAnswer()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.TooManyRequests, "{}"),
            Json(HttpStatusCode.OK, """{"n":1}"""),
            Json(HttpStatusCode.OK, """{"n":2}"""),
        ]);
        using (var recording = Client(Store(ProviderAnswerMode.Record), new FakeNetwork(_ => answers.Dequeue())))
        {
            for (var i = 0; i < 3; i++)
            {
                using var response = await recording.GetAsync("https://api.example.org/x");
            }
        }

        using var replaying = Client(Store(ProviderAnswerMode.Replay), new FakeNetwork(_ => throw new InvalidOperationException()));
        Assert.Equal("""{"n":1}""", await replaying.GetStringAsync("https://api.example.org/x"));
    }

    [Fact]
    public async Task ReplayedEntityBatch_IsAssembledFromRecordedEntities_WhenTheGroupingDiffers()
    {
        const string Path = "https://www.wikidata.org/w/api.php?action=wbgetentities&ids=";
        const string Rest = "&format=json&props=labels";
        var network = new FakeNetwork(request =>
        {
            var ids = Uri.UnescapeDataString(request.RequestUri!.Query).Split("ids=")[1].Split('&')[0].Split('|');
            var entities = new System.Text.Json.Nodes.JsonObject();
            foreach (var id in ids)
            {
                entities[id] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["labels"] = new System.Text.Json.Nodes.JsonObject { ["en"] = new System.Text.Json.Nodes.JsonObject { ["value"] = "label " + id } },
                };
            }

            var body = new System.Text.Json.Nodes.JsonObject { ["entities"] = entities, ["success"] = 1 };
            return Json(HttpStatusCode.OK, body.ToJsonString());
        });
        using (var recording = Client(Store(ProviderAnswerMode.Record), network))
        {
            await recording.GetStringAsync(Path + "Q1%7CQ2" + Rest);
            await recording.GetStringAsync(Path + "Q3" + Rest);
        }

        using var replaying = Client(Store(ProviderAnswerMode.Replay), new FakeNetwork(_ => throw new InvalidOperationException()));

        var assembled = System.Text.Json.Nodes.JsonNode.Parse(await replaying.GetStringAsync(Path + "Q3%7CQ1" + Rest))!;
        Assert.Equal(["Q3", "Q1"], assembled["entities"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal("label Q1", (string?)assembled["entities"]!["Q1"]!["labels"]!["en"]!["value"]);

        using var unknown = await replaying.GetAsync(Path + "Q1%7CQ9" + Rest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
        using var otherProps = await replaying.GetAsync(Path + "Q1%7CQ3&format=json&props=claims");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, otherProps.StatusCode);
    }

    [Fact]
    public async Task LoopbackRequests_AlwaysPassThrough()
    {
        var network = new FakeNetwork(_ => Json(HttpStatusCode.OK, "{}"));
        using var replaying = Client(new ProviderAnswerStore(ProviderAnswerMode.Record, Answers, []), network);

        await replaying.GetStringAsync("http://127.0.0.1:5000/local");

        Assert.Equal(1, network.Calls);
        Assert.Equal(0, ProviderAnswerStore.CountEntries(Answers));
    }

    [Fact]
    public void LoadSecrets_ReadsEveryStringInBothKeyFolders()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "secrets"));
        Directory.CreateDirectory(Path.Combine(config, ".secrets"));
        File.WriteAllText(Path.Combine(config, "secrets", "tmdb.json"), $$$"""{"api_key":"{{{ApiKey}}}","nested":{"pin":"pin-value-1"}}""");
        File.WriteAllText(Path.Combine(config, ".secrets", "service.json"), """{"secret":"service-secret-1"}""");

        var secrets = ProviderAnswerStore.LoadSecrets(config);

        Assert.Contains(ApiKey, secrets);
        Assert.Contains("pin-value-1", secrets);
        Assert.Contains("service-secret-1", secrets);
    }

    [Fact]
    public void ResolveAnswers_FindsTheNewestRunWithALabel()
    {
        foreach (var run in new[] { "20261019-090000-rec", "20261019-100000-rec" })
        {
            Directory.CreateDirectory(Path.Combine(_root, run, BaselineRun.AnswersFolder, ProviderAnswerStore.EntriesFolder));
        }

        var resolved = BaselineRun.ResolveAnswers("rec", _root);

        Assert.Equal(Path.Combine(_root, "20261019-100000-rec", BaselineRun.AnswersFolder), resolved);
        Assert.Throws<DirectoryNotFoundException>(() => BaselineRun.ResolveAnswers("missing", _root));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
