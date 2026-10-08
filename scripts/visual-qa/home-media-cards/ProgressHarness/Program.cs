using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

// This helper only changes playback state through normal authenticated endpoints.
// It never creates accounts, signs in, changes grants, or writes fixture/database files.
try
{
    await RunAsync(args);
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
    or IOException or JsonException or HttpRequestException or TaskCanceledException
    or FormatException or OverflowException or KeyNotFoundException
    or System.Security.Cryptography.CryptographicException)
{
    // Exception messages and HTTP response bodies may contain private fixture state.
    Console.Error.WriteLine("Progress update refused or failed. Check the marked fixture, existing QA session, loopback Engine and arguments; private state omitted.");
    Environment.ExitCode = 1;
}

static async Task RunAsync(string[] arguments)
{
    if (arguments.Length != 6)
    {
        throw new ArgumentException("Usage: ProgressHarness <fixture root> <loopback Engine URL> <manifest scenario key> <percent> <position seconds> <duration seconds>");
    }
    var repository = Path.GetFullPath(Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(repository, "AGENTS.md"))
        || !File.Exists(Path.Combine(repository, "scripts", "visual-qa", "home-media-cards", "fixture.py")))
    {
        throw new InvalidOperationException("Run from the repository root.");
    }
    var output = Path.Combine(repository, "tools", "reports", "home-media-cards-visual");
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[0]));
    if (!string.Equals(Path.GetDirectoryName(root), output, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(root).StartsWith("fixture-", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Unknown fixture root.");
    }
    RequireLocalPath(root, repository);
    var engine = new Uri(arguments[1], UriKind.Absolute);
    if (!engine.IsLoopback || engine.Scheme is not ("http" or "https")
        || engine.AbsolutePath != "/" || engine.Query.Length != 0 || engine.Fragment.Length != 0
        || engine.UserInfo.Length != 0)
    {
        throw new ArgumentException("Use a loopback Engine origin.");
    }
    var percent = ParseNumber(arguments[3]);
    var position = ParseNumber(arguments[4]);
    var duration = ParseNumber(arguments[5]);
    if (percent is < 0 or > 100 || position < 0 || duration <= 0 || position > duration)
    {
        throw new ArgumentException("Invalid progress or timing.");
    }
    using var marker = await ReadJsonAsync(".home-media-cards-disposable.json");
    RequireMarker(marker.RootElement);
    using var manifest = await ReadJsonAsync("manifest.json");
    RequireMarker(manifest.RootElement);
    if (manifest.RootElement.GetProperty("storageEpoch").GetString() != "guid-blob-v10-graph-timeline-lore")
    {
        throw new InvalidOperationException("Unknown fixture schema epoch.");
    }
    var profile = Guid.Parse(manifest.RootElement.GetProperty("profileId").GetString()!);
    var matches = manifest.RootElement.GetProperty("items").EnumerateArray()
        .Where(item => item.GetProperty("key").GetString() == arguments[2]).ToArray();
    if (matches.Length != 1 || !matches[0].TryGetProperty("assetId", out var assetElement)
        || !Guid.TryParse(assetElement.GetString(), out var asset) || asset == Guid.Empty)
    {
        throw new InvalidOperationException("Scenario must identify one seeded playback asset.");
    }
    using var auth = await ReadJsonAsync(".qa-auth.json");
    if (Guid.Parse(auth.RootElement.GetProperty("profileId").GetString()!) != profile)
    {
        throw new InvalidOperationException("Fixture profile mismatch.");
    }
    var account = Guid.Parse(auth.RootElement.GetProperty("accountId").GetString()!);
    var session = auth.RootElement.GetProperty("sessionToken").GetString();
    if (string.IsNullOrWhiteSpace(session))
    {
        throw new InvalidOperationException("Existing QA session required.");
    }
    using var core = await ReadJsonAsync(Path.Combine("config", "core.json"));
    foreach (var (field, suffix) in new[] { ("data_root", "data"), ("library_root", "media"), ("database_path", Path.Combine("data", "library.db")) })
    {
        var configured = Path.GetFullPath(core.RootElement.GetProperty(field).GetString()!);
        if (!string.Equals(configured, Path.Combine(root, suffix), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture configuration is not isolated.");
        }
        RequireLocalPath(configured, repository);
    }
    foreach (var suffix in new[] { "keys", Path.Combine("config", ".secrets", "dashboard-engine.credential.json") })
    {
        RequireLocalPath(Path.Combine(root, suffix), repository);
    }
    if (!Directory.Exists(Path.Combine(root, "keys"))
        || !File.Exists(Path.Combine(root, "config", ".secrets", "dashboard-engine.credential.json"))
        || !File.Exists(Path.Combine(root, "data", "library.db")))
    {
        throw new InvalidOperationException("Existing fixture protection keys required.");
    }
    var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")),
        builder => builder.SetApplicationName("Tuvima.Library"));
    var credential = new DashboardServiceCredentialProvider(protection,
        new DashboardServiceCredentialProviderOptions(Path.Combine(root, "config")),
        NullLogger<DashboardServiceCredentialProvider>.Instance);
    using var handler = new DashboardServiceCredentialHandler(credential)
    {
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false },
    };
    using var http = new HttpClient(handler) { BaseAddress = engine, Timeout = TimeSpan.FromSeconds(30) };
    var validated = await SendAsync<SessionValidationResponse>(HttpMethod.Post, "/auth/session/validate");
    if (validated.ActiveProfileId != profile || validated.AccountId != account)
    {
        throw new InvalidOperationException("Active session does not match the exact fixture profile/account.");
    }
    long revision = 0;
    var properties = new Dictionary<string, string>();
    using (var request = CreateRequest(HttpMethod.Get, $"/api/v1/progress/{asset:D}"))
    {
        using (var response = await http.SendAsync(request))
        {
            if (response.IsSuccessStatusCode)
            {
                var prior = await response.Content.ReadFromJsonAsync<UserStateResponse>()
                    ?? throw new InvalidOperationException("Empty progress response.");
                if (prior.AssetId != asset || prior.UserId != profile)
                {
                    throw new InvalidOperationException("Existing progress identity mismatch.");
                }
                revision = prior.Revision;
                properties = new Dictionary<string, string>(prior.ExtendedProperties);
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("Progress read failed.");
            }
        }
    }
    properties["position_seconds"] = position.ToString("R", CultureInfo.InvariantCulture);
    properties["duration_seconds"] = duration.ToString("R", CultureInfo.InvariantCulture);
    var updated = await SendAsync<UserStateResponse>(HttpMethod.Put, $"/api/v1/progress/{asset:D}",
        new ProgressUpdateRequest(null, percent, properties) { ExpectedRevision = revision });
    if (updated.AssetId != asset || updated.UserId != profile || updated.ProgressPct != percent
        || updated.Revision <= revision || updated.ExtendedProperties.GetValueOrDefault("position_seconds") != properties["position_seconds"]
        || updated.ExtendedProperties.GetValueOrDefault("duration_seconds") != properties["duration_seconds"])
    {
        throw new InvalidOperationException("Acknowledged progress does not match the requested fixture state.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario = arguments[2], profileId = updated.UserId, percent = updated.ProgressPct,
        acknowledged = new { updated.AssetId, updated.Revision, updated.LastAccessed,
            positionSeconds = updated.ExtendedProperties["position_seconds"], durationSeconds = updated.ExtendedProperties["duration_seconds"] },
    }));

    async Task<JsonDocument> ReadJsonAsync(string suffix)
    {
        var path = Path.Combine(root, suffix);
        RequireLocalPath(path, repository);
        return JsonDocument.Parse(await File.ReadAllTextAsync(path));
    }
    void RequireMarker(JsonElement element)
    {
        if (element.GetProperty("task").GetString() != "home-media-cards-2026-10-03"
            || !string.Equals(element.GetProperty("root").GetString(), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Missing or mismatched fixture marker.");
        }
    }
    HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Tuvima-Session", session);
        return request;
    }
    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Authenticated fixture endpoint failed.");
        }
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("Empty endpoint response.");
    }
}

static double ParseNumber(string value)
{
    var result = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    return double.IsFinite(result) ? result : throw new ArgumentException("Finite numbers required.");
}

static void RequireLocalPath(string path, string repository)
{
    // Reject junctions/symlinks before reading fixture state or protection material.
    for (var current = Path.GetFullPath(path); !string.Equals(current, repository, StringComparison.OrdinalIgnoreCase);)
    {
        if ((File.Exists(current) || Directory.Exists(current))
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Fixture paths must not contain links.");
        }
        current = Path.GetDirectoryName(current) ?? throw new InvalidOperationException("Unknown fixture path.");
    }
}
