using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

if (args.Length is not (2 or 3 or 4))
{
    throw new ArgumentException("Usage: SmokeAdmin <fixture-root> <engine-url> [--complete|--probe <path>]");
}

var root = Path.GetFullPath(args[0]);
var tempRoot = Path.GetFullPath(Path.GetTempPath());
if (!root.StartsWith(Path.Combine(tempRoot, "tuvima-smoke-"), StringComparison.OrdinalIgnoreCase)
    || Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))?.TrimEnd(Path.DirectorySeparatorChar)
       != tempRoot.TrimEnd(Path.DirectorySeparatorChar))
{
    throw new InvalidOperationException("The fixture must be an isolated tuvima-smoke directory directly under temp.");
}

var credentialPath = Path.Combine(root, "config", ".secrets", "dashboard-engine.credential.json");
using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(credentialPath));
var protectedToken = bundle.RootElement.GetProperty("protected_token").GetString()
    ?? throw new InvalidOperationException("The disposable service credential is missing.");
var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")),
    options => options.SetApplicationName("Tuvima.Library"));
var serviceToken = protection.CreateProtector("Tuvima.DashboardEngineCredential.v1").Unprotect(protectedToken);

using var client = new HttpClient { BaseAddress = new Uri(args[1]) };
client.DefaultRequestHeaders.TryAddWithoutValidation("X-Tuvima-Service-Key", serviceToken);
var loginPath = Path.Combine(root, "smoke-login.json");
if (args.Length >= 3 && args[2] is "--complete" or "--probe")
{
    using var login = JsonDocument.Parse(await File.ReadAllTextAsync(loginPath));
    var loginEmail = login.RootElement.GetProperty("email").GetString();
    var loginPassword = login.RootElement.GetProperty("password").GetString();
    using var response = await client.PostAsJsonAsync("/auth/login", new
    {
        email = loginEmail, password = loginPassword, device_id = Guid.NewGuid().ToString("D"), device_name = "Isolated smoke fixture",
        client = "Dashboard", original_client_ingress = "home_network", original_client_is_https = false,
    });
    response.EnsureSuccessStatusCode();
    using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var token = session.RootElement.GetProperty("session_token").GetString()
        ?? throw new InvalidOperationException("The disposable admin login returned no session.");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-Tuvima-Session", token);
    if (args[2] == "--probe")
    {
        if (args.Length != 4 || !args[3].StartsWith('/'))
        {
            throw new ArgumentException("Probe requires an API path.");
        }
        using var probe = await client.GetAsync(args[3]);
        var body = await probe.Content.ReadAsStringAsync();
        var limit = Environment.GetEnvironmentVariable("TUVIMA_SMOKE_FULL_PROBE") == "1" ? 8000 : 350;
        Console.WriteLine($"Probe status: {(int)probe.StatusCode}; body: {body[..Math.Min(body.Length, limit)]}");
        return;
    }
    foreach (var step in new[] { "media-locations", "providers" })
    {
        using var stepResponse = await client.PostAsJsonAsync($"/setup/v1/steps/{step}", new
        {
            status = "deferred", detail = "Deferred for isolated editor smoke verification.",
        });
        stepResponse.EnsureSuccessStatusCode();
    }
    using var completeResponse = await client.PostAsJsonAsync("/setup/v1/complete", new { });
    completeResponse.EnsureSuccessStatusCode();
    Console.WriteLine("Completed disposable setup with media folders and providers deferred.");
    return;
}
using var beginResponse = await client.PostAsJsonAsync("/setup/v1/begin", new { });
beginResponse.EnsureSuccessStatusCode();
using var begin = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync());
var setupSession = begin.RootElement.GetProperty("setup_session_token").GetString()
    ?? throw new InvalidOperationException("The disposable setup session was not returned.");

var password = "Smoke!a1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(15));
const string email = "smoke-admin@example.invalid";
using var adminRequest = new HttpRequestMessage(HttpMethod.Post, "/setup/v1/administrator")
{
    Content = JsonContent.Create(new
    {
        email,
        password,
        display_name = "Smoke Administrator",
        device_id = Guid.NewGuid().ToString("D"),
        device_name = "Isolated smoke fixture"
    })
};
adminRequest.Headers.TryAddWithoutValidation("X-Tuvima-Setup-Session", setupSession);
using var adminResponse = await client.SendAsync(adminRequest);
adminResponse.EnsureSuccessStatusCode();
using var result = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
if (!result.RootElement.GetProperty("created").GetBoolean())
{
    throw new InvalidOperationException("Disposable administrator was not created.");
}

await File.WriteAllTextAsync(loginPath, JsonSerializer.Serialize(new { email, password }));
Console.WriteLine("Created disposable administrator. Login is stored only in the fixture's smoke-login.json.");
