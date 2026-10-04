using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: SetupHarness <marked fixture root> <loopback Engine URL> [loopback Dashboard URL]");
var root = Path.GetFullPath(args[0]);
var engine = RequireLoopback(args[1]);
var config = Path.Combine(root, "config");
using (var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".home-media-cards-disposable.json"))))
{
    if (!Path.GetFileName(root).StartsWith("fixture-", StringComparison.Ordinal)
        || marker.RootElement.GetProperty("task").GetString() != "home-media-cards-2026-10-03"
        || !string.Equals(marker.RootElement.GetProperty("root").GetString(), root, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Missing or mismatched disposable fixture marker.");
}
using (var core = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(config, "core.json"))))
{
    foreach (var (field, expected) in new[] { ("data_root", "data"), ("library_root", "media") })
        if (!string.Equals(Path.GetFullPath(core.RootElement.GetProperty(field).GetString()!), Path.Combine(root, expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture configuration points outside the disposable root.");
    if (!string.Equals(Path.GetFullPath(core.RootElement.GetProperty("database_path").GetString()!), Path.Combine(root, "data", "library.db"), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fixture database is not isolated.");
}
var secretsPath = Path.Combine(root, ".qa-auth.json");
var auth = File.Exists(secretsPath)
    ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(secretsPath))!
    : new Dictionary<string, string>
    {
        ["email"] = "home-qa@example.test",
        ["password"] = "QA!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
    };
await SavePrivateAsync(secretsPath, auth);
var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")), builder => builder.SetApplicationName("Tuvima.Library"));
var credential = new DashboardServiceCredentialProvider(protection,
    new DashboardServiceCredentialProviderOptions(config), NullLogger<DashboardServiceCredentialProvider>.Instance);
using var serviceHandler = new DashboardServiceCredentialHandler(credential) { InnerHandler = new HttpClientHandler() };
using var http = new HttpClient(serviceHandler) { BaseAddress = engine };
var status = await SendAsync<SetupStatusDto>(HttpMethod.Get, "/setup/v1/status");
if (!status.AdministratorConfigured)
{
    var begin = await SendAsync<SetupStartResponse>(HttpMethod.Post, "/setup/v1/begin", new { });
    auth["setupSession"] = begin.SetupSessionToken;
    await SavePrivateAsync(secretsPath, auth);
    var preflight = await SendAsync<SetupPreflightDto>(HttpMethod.Post, "/setup/v1/preflight", new { });
    if (!preflight.Passed) throw new InvalidOperationException("Required fixture preflight failed; inspect marked fixture paths.");
    _ = await SendAsync<SetupAdministratorResponse>(HttpMethod.Post, "/setup/v1/administrator", new SetupAdministratorRequest
    {
        Email = auth["email"], Password = auth["password"], DisplayName = "Home QA Administrator",
        DeviceId = "home-media-qa", DeviceName = "Disposable acceptance fixture",
    });
}
var issued = await SendAsync<AuthSessionResponse>(HttpMethod.Post, "/auth/login", new LocalLoginRequest
{
    Email = auth["email"], Password = auth["password"], DeviceId = "home-media-qa", DeviceName = "Disposable acceptance fixture",
    OriginalClientIsLocal = true, OriginalClientIsHttps = engine.Scheme == "https", Client = "Home QA harness",
});
auth["sessionToken"] = issued.SessionToken;
auth["accountId"] = issued.AccountId.ToString("D");
auth["profileId"] = issued.ActiveProfileId.ToString("D");
await SavePrivateAsync(secretsPath, auth);
if (status.State != "complete")
{
    foreach (var step in new[] { "media-locations", "providers" })
        _ = await SendAsync<SetupStatusDto>(HttpMethod.Post, $"/setup/v1/steps/{step}", new SetupStepDecisionRequest { Status = "deferred", Detail = "Disposable visual fixture has no ingestion sources or provider calls." });
    var readiness = await SendAsync<SetupReadinessDto>(HttpMethod.Get, "/setup/v1/readiness");
    if (!readiness.CanComplete) throw new InvalidOperationException("Normal setup readiness still has required blockers.");
    _ = await SendAsync<SetupStatusDto>(HttpMethod.Post, "/setup/v1/complete", new { });
}
if (args.Length == 3)
{
    var dashboard = RequireLoopback(args[2]);
    var cookies = new CookieContainer();
    using var dashboardHandler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
    using var browserSignIn = new HttpClient(dashboardHandler) { BaseAddress = dashboard };
    using var loginPage = await browserSignIn.GetAsync("/auth/login?returnUrl=%2F");
    loginPage.EnsureSuccessStatusCode();
    var html = await loginPage.Content.ReadAsStringAsync();
    var anti = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
    if (string.IsNullOrEmpty(anti)) throw new InvalidOperationException("Normal Dashboard login antiforgery token unavailable.");
    using var login = await browserSignIn.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = WebUtility.HtmlDecode(anti), ["action"] = "login", ["email"] = auth["email"],
        ["password"] = auth["password"], ["returnUrl"] = "/",
    }));
    if (login.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException($"Normal Dashboard sign-in failed ({(int)login.StatusCode}).");
    await SavePrivateAsync(Path.Combine(root, ".qa-browser-cookies.json"), cookies.GetAllCookies().Select(cookie => new
    {
        cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Expires, cookie.HttpOnly, cookie.Secure,
    }));
}
Console.WriteLine($"Normal disposable setup complete. Active profile: {issued.ActiveProfileId:D}. Private local QA state saved; no credentials printed.");

async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body);
    if (path.StartsWith("/setup", StringComparison.Ordinal) && auth.TryGetValue("setupSession", out var setup))
        request.Headers.TryAddWithoutValidation("X-Tuvima-Setup-Session", setup);
    if (auth.TryGetValue("sessionToken", out var session)) request.Headers.TryAddWithoutValidation("X-Tuvima-Session", session);
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Normal fixture endpoint {path} failed ({(int)response.StatusCode}); response secrets omitted.");
    return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("Empty fixture endpoint result.");
}

static Uri RequireLoopback(string raw)
{
    var uri = new Uri(raw);
    if (!uri.IsLoopback || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("QA setup accepts only loopback endpoints.");
    return uri;
}

static async Task SavePrivateAsync<T>(string path, T value)
{
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
