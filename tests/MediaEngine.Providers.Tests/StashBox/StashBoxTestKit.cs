using System.Net;
using System.Reflection;
using System.Text;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;
using MediaEngine.Providers.Services.Vault;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests.StashBox;

/// <summary>One request the fake stash-box server received.</summary>
internal sealed record SeenRequest(Uri Uri, string Body, string? ApiKey);

/// <summary>
/// A stash-box server that answers from recorded responses and never touches the network. It records every request, so a test
/// can prove what was (and was not) sent.
/// </summary>
internal sealed class RecordedStashBox : HttpMessageHandler, IHttpClientFactory
{
    public List<SeenRequest> Requests { get; } = [];

    public Func<SeenRequest, HttpResponseMessage>? Respond { get; set; }

    public static string Fixture(string name)
    {
        var dir = Path.GetDirectoryName(typeof(RecordedStashBox).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return File.ReadAllText(Path.Combine(dir!, "tests", "MediaEngine.Providers.Tests", "Fixtures", "StashBox", name));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A real, decodable landscape JPEG (the recorded cover).</summary>
    public static byte[] FakeJpeg() => TestImages.Jpeg(640, 360);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = request.Headers.TryGetValues("ApiKey", out var values) ? values.Single() : null;
        var seen = new SeenRequest(request.RequestUri!, body, key);
        Requests.Add(seen);
        return Respond?.Invoke(seen) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Answers the GraphQL address by operation and the cover address with an image.</summary>
    public static Func<SeenRequest, HttpResponseMessage> Script(
        string? search = null, string? fingerprint = null, string? me = null, bool cover = true, string? scene = null) =>
        seen =>
        {
            if (seen.Uri.AbsolutePath.EndsWith("/cover.jpg", StringComparison.Ordinal))
            {
                return cover
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeJpeg()) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (seen.Body.Contains("findScenesByFullFingerprints", StringComparison.Ordinal))
            {
                return Json(fingerprint ?? "{\"data\":{\"findScenesByFullFingerprints\":[]}}");
            }

            if (seen.Body.Contains("findScene(", StringComparison.Ordinal))
            {
                return Json(scene ?? "{\"data\":{\"findScene\":null}}");
            }

            if (seen.Body.Contains("searchScene", StringComparison.Ordinal))
            {
                return Json(search ?? Fixture("search-empty.json"));
            }

            return Json(me ?? Fixture("me-ok.json"));
        };
}

/// <summary>A config folder holding the shipped provider files plus test providers, with a real secrets store.</summary>
internal sealed class StashBoxConfigFolder : IDisposable
{
    public StashBoxConfigFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tuvima-stashbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "providers"));
        var root = System.IO.Path.GetDirectoryName(typeof(StashBoxConfigFolder).Assembly.Location);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root, "MediaEngine.slnx")))
        {
            root = System.IO.Path.GetDirectoryName(root);
        }

        foreach (var name in new[] { "stashbox.json", "youtube.json" })
        {
            File.Copy(System.IO.Path.Combine(root!, "config", "providers", name), System.IO.Path.Combine(Path, "providers", name));
        }

        Secrets = new ConfigFolderSecretsStore(Path);
        Loader = new ConfigurationDirectoryLoader(Path, Secrets);
        Settings = new StashBoxSettings(Loader, Secrets, NullLogger<StashBoxSettings>.Instance);
    }

    public string Path { get; }

    public ConfigFolderSecretsStore Secrets { get; }

    public ConfigurationDirectoryLoader Loader { get; }

    public StashBoxSettings Settings { get; }

    /// <summary>Adds a test provider file (for example a fake adult provider).</summary>
    public void AddProvider(string name, bool adultOnly) =>
        File.WriteAllText(
            System.IO.Path.Combine(Path, "providers", name + ".json"),
            $$"""{ "name": "{{name}}", "version": "1.0", "enabled": true, "weight": 0.5, "domain": "Video", "adapter_type": "coded", "adult_only": {{(adultOnly ? "true" : "false")}} }""");

    public StashBoxClient Client(IHttpClientFactory http) => new(http, NullLogger<StashBoxClient>.Instance);

    public StashBoxVideoLookup Lookup(IHttpClientFactory http) =>
        new(Loader, Settings, Client(http), new VaultNamingConfigurationProvider(() => Path), NullLogger<StashBoxVideoLookup>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed right now is cleaned up with the machine's temp files.
        }
    }
}

/// <summary>A stand-in for a repository interface: methods listed in <see cref="Handlers"/> run, anything else fails the test.</summary>
internal class Spy<T> : DispatchProxy
    where T : class
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

    public List<string> Calls { get; } = [];

    public static (T Proxy, Spy<T> Spy) Create()
    {
        var proxy = Create<T, Spy<T>>();
        return (proxy, (Spy<T>)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.Add(name);
        if (!Handlers.TryGetValue(name, out var handler))
        {
            throw new InvalidOperationException($"{typeof(T).Name}.{name} was not expected.");
        }

        var result = handler(args ?? []);
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
        }

        return result;
    }
}
