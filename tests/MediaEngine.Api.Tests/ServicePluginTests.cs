using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Service;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugins that are separate programs (developer-tools WP18): a good description loads and a bad one is refused in plain
/// words; the Library starts the program, sees it healthy, asks it for a lookup, restarts it after it is killed, stops it for
/// good after repeated crashes or too much memory, and answers its calls only for what the plugin was approved to do. The
/// end-to-end tests run the real Python sample and are skipped on a machine without python3.
/// </summary>
public sealed class ServicePluginTests : IDisposable
{
    private static readonly PluginHostLimits Quick = PluginHostLimits.Default with
    {
        BackgroundRestartBackoff = [TimeSpan.FromMilliseconds(20)],
        BackgroundStopGrace = TimeSpan.FromSeconds(2),
        ServiceHealthInterval = TimeSpan.FromMilliseconds(100),
        ServiceRequestTimeout = TimeSpan.FromSeconds(3),
        ServiceStopGrace = TimeSpan.FromSeconds(2),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-service-plugins", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly PluginCatalog _catalog;
    private readonly PluginHostStatusRegistry _statuses;
    private readonly PluginKeyRegistry _keys = new();
    private readonly DatabaseConnection _database;
    private readonly List<IAsyncDisposable> _cleanup = [];

    public ServicePluginTests()
    {
        DapperConfiguration.Configure();
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _statuses = new PluginHostStatusRegistry(_catalog);
        _database = new DatabaseConnection(Path.Combine(_root, "tuvima.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        foreach (var item in _cleanup)
        {
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _database.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    // ── Reading the description ──────────────────────────────────────────────

    [Fact]
    public void ThePythonSample_LoadsAsAProgramPlugin_SwitchedOffAndCompatible()
    {
        CopySample();

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("tuvima.sample.python-lookup"));

        Assert.Null(plugin.LoadError);
        Assert.True(plugin.Compatibility.IsCompatible);
        Assert.False(plugin.Enabled);
        var lookup = Assert.Single(plugin.Capabilities.OfType<ServiceLookupCapability>());
        Assert.True(lookup.Definition.IsSupervised);
        Assert.Equal("python3", lookup.Definition.Command);
        Assert.Equal(256, lookup.Definition.MaxMemoryMb);
    }

    [Theory]
    [InlineData("\"permissions\": [\"process.execute\"]", "needs a \\\"service\\\" section")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": {}", "either a \\\"command\\\" to start the program or an \\\"address\\\"")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"python3\", \"address\": \"http://127.0.0.1:9\" }", "either a \\\"command\\\"")]
    [InlineData("\"permissions\": [], \"service\": { \"command\": \"python3\" }", "process.execute")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"../escape.sh\" }", "outside the plugin's own folder")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"python3 -m evil\" }", "one program name")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"python3\", \"health_path\": \"health\" }", "starting with /")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"python3\", \"max_memory_mb\": 4 }", "whole number from 32 to 8192")]
    [InlineData("\"permissions\": [\"process.execute\"], \"service\": { \"command\": \"python3\", \"shell\": true }", "unknown setting \\\"shell\\\"")]
    [InlineData("\"permissions\": [], \"service\": { \"address\": \"ftp://example.com\" }", "http:// or https://")]
    [InlineData("\"permissions\": [], \"service\": { \"address\": \"https://lookup.example.com\" }", "network.http")]
    [InlineData("\"permissions\": [\"network.http\"], \"network\": [{ \"host\": \"other.example.com\", \"sends\": \"nothing\" }], \"service\": { \"address\": \"https://lookup.example.com\" }", "not listed in the plugin's \\\"network\\\"")]
    public void AMalformedProgramDescription_IsRefusedWithASentenceThatSaysWhatToChange(string fields, string expected)
    {
        Add("svc.bad", fields);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("svc.bad"));

        Assert.NotNull(plugin.LoadError);
        Assert.Contains(expected.Replace("\\\"", "\""), plugin.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramAtAnAddress_OnThisMachine_NeedsNoNetworkPermission_AndIsWatchedNotStarted()
    {
        Add("svc.address", """ "permissions": [], "service": { "address": "http://127.0.0.1:9100" } """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("svc.address"));

        Assert.Null(plugin.LoadError);
        Assert.False(Assert.Single(plugin.Capabilities.OfType<ServiceLookupCapability>()).Definition.IsSupervised);
    }

    [Fact]
    public void TheApproval_NamesTheExactProgramItStarts_SoChangingItAsksAgain()
    {
        CopySample();
        var manifest = _catalog.Get("tuvima.sample.python-lookup")!.Manifest;

        var allowed = ApprovalStatementBuilder.PluginAllowedTo(manifest);

        Assert.Contains("Starts a program on this machine: python3 server.py", allowed);
        Assert.Contains("Runs as: separate program", allowed);
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AKey_OpensOnlyItsOwnPlugin_AndEndsWhenRevokedOrReplaced()
    {
        var first = _keys.Issue("one");
        var other = _keys.Issue("two");

        Assert.Equal("one", _keys.Resolve(first));
        Assert.Equal("two", _keys.Resolve(other));
        Assert.Null(_keys.Resolve("not-a-key"));
        Assert.Null(_keys.Resolve(null));

        var replaced = _keys.Issue("one");
        Assert.Null(_keys.Resolve(first));
        Assert.Equal("one", _keys.Resolve(replaced));

        _keys.Revoke("one");
        Assert.Null(_keys.Resolve(replaced));
        Assert.Equal("two", _keys.Resolve(other));
    }

    // ── The door ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePublishedContract_ListsEveryDoorRoute_AndNothingElse()
    {
        await using var app = BuildDoorApplication();
        var mapped = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/plugin-host/v1", StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method.ToLowerInvariant()} {endpoint.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var document = JsonDocument.Parse(File.ReadAllText(RepoPath("docs", "reference", "plugin-host-v1.openapi.json")));
        var published = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "put" or "post" or "delete")
                .Select(operation => $"{operation.Name} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(published, mapped);
    }

    [Fact]
    public async Task TheDoor_RefusesAMissingOrWrongKey_AndAKeyOfASwitchedOffPlugin()
    {
        CopySample();
        await using var door = await StartDoorAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await door.GetAsync("/plugin-host/v1/whoami", key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await door.GetAsync("/plugin-host/v1/whoami", key: "nope")).StatusCode);

        var key = _keys.Issue("tuvima.sample.python-lookup");
        Assert.Equal(HttpStatusCode.Unauthorized, (await door.GetAsync("/plugin-host/v1/whoami", key)).StatusCode); // still switched off

        _catalog.SetEnabled("tuvima.sample.python-lookup", true);
        var whoami = await door.GetAsync("/plugin-host/v1/whoami", key);
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        Assert.Contains("tuvima.sample.python-lookup", await whoami.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheDoor_AnswersOnlyForWhatThePluginWasApprovedToDo()
    {
        CopySample(); // asks for plugin-records, not ai.infer
        _catalog.SetEnabled("tuvima.sample.python-lookup", true);
        await using var door = await StartDoorAsync();
        var key = _keys.Issue("tuvima.sample.python-lookup");

        var saved = await door.PutAsync("/plugin-host/v1/records/notes/first", key, """{"hello":"world"}""");
        var read = await door.GetAsync("/plugin-host/v1/records/notes/first", key);
        var notJson = await door.PutAsync("/plugin-host/v1/records/notes/bad", key, "not json");
        var ai = await door.PostAsync("/plugin-host/v1/ai/text", key, """{"role":"text_fast","prompt":"Hi"}""");

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Contains("world", await read.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        // No ai.infer permission: refused as a plain status, never answered by Local AI.
        Assert.Equal(HttpStatusCode.OK, ai.StatusCode);
        Assert.Contains("\"status\":\"refused\"", await ai.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheDoor_RefusesRecords_ToAPluginThatNeverAskedForThem()
    {
        Add("svc.norecords", """ "permissions": ["process.execute"], "service": { "command": "python3", "arguments": ["server.py"] } """);
        _catalog.SetEnabled("svc.norecords", true);
        await using var door = await StartDoorAsync();
        var key = _keys.Issue("svc.norecords");

        var response = await door.PutAsync("/plugin-host/v1/records/notes/first", key, "{}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Running a real program ───────────────────────────────────────────────

    [PythonFact]
    public async Task ThePythonSample_StartsHealthy_ServesALookup_AndTheDoorLimitsItToItsPermissions()
    {
        CopySample();
        await using var door = await StartDoorAsync();
        var host = NewHost(door.Address);
        _catalog.SetEnabled("tuvima.sample.python-lookup", true);

        await host.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => host.GetEndpoint("tuvima.sample.python-lookup") is not null, "the program to be healthy", timeoutMs: 20000);

        Assert.Equal("running", _statuses.Get("tuvima.sample.python-lookup").Describe().BackgroundService);
        var client = new PluginServiceClient(host, _catalog, Quick);
        var lookup = await client.LookupAsync("tuvima.sample.python-lookup", new Dictionary<string, string> { ["title"] = "Dune" });
        Assert.Equal(ServiceCallStatus.Succeeded, lookup.Status);
        Assert.Equal("Dune", JsonDocument.Parse(lookup.Json!).RootElement.GetProperty("title").GetString());

        // The program calls the door as itself: it may keep its own records, it may not use Local AI.
        var endpoint = host.GetEndpoint("tuvima.sample.python-lookup")!;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseAddress, "/probe"));
        probeRequest.Headers.Add(PluginServiceClient.KeyHeader, endpoint.Key);
        var probe = JsonDocument.Parse(await (await http.SendAsync(probeRequest)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(200, probe.GetProperty("whoami").GetInt32());
        Assert.Equal(204, probe.GetProperty("save_own_record").GetInt32());
        Assert.Equal(200, probe.GetProperty("use_local_ai").GetInt32()); // answered with status "refused", not by Local AI

        // Only the Library may ask the program for a lookup.
        using var strangerRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseAddress, "/lookup?title=x"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(strangerRequest)).StatusCode);

        await host.StopAllAsync();
        Assert.Null(host.GetEndpoint("tuvima.sample.python-lookup"));
        Assert.Null(_keys.Resolve(endpoint.Key));
    }

    [PythonFact]
    public async Task AProgramThatIsKilled_IsStartedAgain_WithAFreshKey()
    {
        CopySample();
        await using var door = await StartDoorAsync();
        var host = NewHost(door.Address);
        _catalog.SetEnabled("tuvima.sample.python-lookup", true);
        await host.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => host.GetEndpoint("tuvima.sample.python-lookup") is not null, "the program to be healthy", timeoutMs: 20000);
        var client = new PluginServiceClient(host, _catalog, Quick);
        var firstKey = host.GetEndpoint("tuvima.sample.python-lookup")!.Key;
        var firstPid = ProcessId(await client.LookupAsync("tuvima.sample.python-lookup", new Dictionary<string, string> { ["title"] = "x" }));

        Process.GetProcessById(firstPid).Kill();

        await HostServicesHarness.EventuallyAsync(
            () => host.GetEndpoint("tuvima.sample.python-lookup") is { } now && now.Key != firstKey,
            "the program to come back",
            timeoutMs: 20000);
        var secondPid = ProcessId(await client.LookupAsync("tuvima.sample.python-lookup", new Dictionary<string, string> { ["title"] = "x" }));
        Assert.NotEqual(firstPid, secondPid);
        Assert.Null(_keys.Resolve(firstKey));
        await host.StopAllAsync();
    }

    [PythonFact]
    public async Task SwitchingThePluginOff_StopsTheProgram_AndEndsItsKey()
    {
        CopySample();
        await using var door = await StartDoorAsync();
        var host = NewHost(door.Address);
        _catalog.SetEnabled("tuvima.sample.python-lookup", true);
        await host.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => host.GetEndpoint("tuvima.sample.python-lookup") is not null, "the program to be healthy", timeoutMs: 20000);
        var key = host.GetEndpoint("tuvima.sample.python-lookup")!.Key;
        var pid = ProcessId(await new PluginServiceClient(host, _catalog, Quick).LookupAsync("tuvima.sample.python-lookup", new Dictionary<string, string>()));

        _catalog.SetEnabled("tuvima.sample.python-lookup", false);
        await host.ReconcileAsync();

        Assert.Null(host.GetEndpoint("tuvima.sample.python-lookup"));
        Assert.Null(_keys.Resolve(key));
        await HostServicesHarness.EventuallyAsync(() => !IsRunning(pid), "the program to be gone");
        Assert.Null(_statuses.Get("tuvima.sample.python-lookup").Describe().BackgroundService);
    }

    [PythonFact]
    public async Task AProgramThatKeepsCrashing_StopsForGood_AndTheCardSaysBroken()
    {
        Add("svc.crashy", """ "permissions": ["process.execute"], "service": { "command": "python3", "arguments": ["-c", "import sys; sys.exit(3)"], "startup_seconds": 5 } """);
        _catalog.SetEnabled("svc.crashy", true);
        var host = NewHost(libraryUrl: null);

        await host.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(
            () => _statuses.Get("svc.crashy").Describe().BackgroundService == "stopped",
            "the program to be given up on",
            timeoutMs: 30000);

        var described = _statuses.Get("svc.crashy").Describe();
        Assert.Equal("broken", described.State);
        Assert.Contains("Stopped after crashing", described.Problem);
        Assert.Null(host.GetEndpoint("svc.crashy"));
        await host.StopAllAsync();
    }

    [PythonFact]
    public async Task AProgramThatUsesTooMuchMemory_IsStoppedAndSaysSo()
    {
        Add("svc.hungry", """ "permissions": ["process.execute"], "service": { "command": "python3", "arguments": ["-c", "import time; x = bytearray(150 * 1024 * 1024); time.sleep(60)"], "max_memory_mb": 64, "startup_seconds": 30 } """);
        _catalog.SetEnabled("svc.hungry", true);
        var host = NewHost(libraryUrl: null);

        await host.ReconcileAsync();

        await HostServicesHarness.EventuallyAsync(
            () => _statuses.Get("svc.hungry").Describe().LastProblem?.Contains("more than 64 MB", StringComparison.Ordinal) == true,
            "the memory limit to be noticed",
            timeoutMs: 20000);
        await host.StopAllAsync();
    }

    [Fact]
    public async Task AProgramThatCannotBeStarted_SaysSoInPlainWords()
    {
        Add("svc.missing", """ "permissions": ["process.execute"], "service": { "command": "tuvima-no-such-program" } """);
        _catalog.SetEnabled("svc.missing", true);
        var host = NewHost(libraryUrl: null);

        await host.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(
            () => _statuses.Get("svc.missing").Describe().LastProblem?.Contains("could not be started on this machine", StringComparison.Ordinal) == true,
            "the failure to be reported");
        await host.StopAllAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private PluginServiceHost NewHost(string? libraryUrl)
    {
        var host = new PluginServiceHost(_catalog, _statuses, _keys, Quick, TimeProvider.System, () => libraryUrl, NullLogger<PluginServiceHost>.Instance);
        _cleanup.Add(new AsyncAction(host.StopAllAsync));
        return host;
    }

    private WebApplication BuildDoorApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var gate = new PluginPermissionGate(_catalog);
        builder.Services.AddSingleton(_keys);
        builder.Services.AddSingleton(_catalog);
        builder.Services.AddSingleton<IPluginPermissionGate>(gate);
        builder.Services.AddSingleton<IPluginExecutionContextFactory>(new HostServicesContextFactory(
            _catalog, gate, new PluginRecordRepository(_database), Quick, _statuses, TimeProvider.System, _root));
        builder.Services.AddSingleton(new PluginAiGateway(gate, new UnavailableRoom()));
        var app = builder.Build();
        app.MapPluginHostEndpoints();
        return app;
    }

    private async Task<Door> StartDoorAsync()
    {
        var app = BuildDoorApplication();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var door = new Door(app, address);
        _cleanup.Add(door);
        return door;
    }

    private void Add(string id, string fields)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "kind": "service",
              {{fields}}
            }
            """);
        _catalog.Reload();
    }

    private void CopySample()
    {
        var source = RepoPath("samples", "plugins", "python-lookup");
        var target = Path.Combine(_pluginRoot, "python-lookup");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        _catalog.Reload();
    }

    private static string RepoPath(params string[] parts) =>
        Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));

    internal static bool PythonAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int ProcessId(ServiceLookupResult result) =>
        JsonDocument.Parse(result.Json ?? "{}").RootElement.GetProperty("pid").GetInt32();

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class AsyncAction(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action().ConfigureAwait(false);
    }

    private sealed class Door(WebApplication app, string address) : IAsyncDisposable
    {
        private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false });

        public string Address { get; } = address;

        public Task<HttpResponseMessage> GetAsync(string path, string? key) => Send(HttpMethod.Get, path, key, null);

        public Task<HttpResponseMessage> PutAsync(string path, string? key, string body) => Send(HttpMethod.Put, path, key, body);

        public Task<HttpResponseMessage> PostAsync(string path, string? key, string body) => Send(HttpMethod.Post, path, key, body);

        private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? key, string? body)
        {
            var request = new HttpRequestMessage(method, Address + path);
            if (key is not null)
            {
                request.Headers.Add(PluginServiceClient.KeyHeader, key);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            return _http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class UnavailableRoom : IAiEngineRoom
    {
        public Task<AiTextResult> WriteTextAsync(
            AiCaller caller, AiAbility ability, string prompt, string? grammar = null, AiRequestOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new AiTextResult(null, new AiRequestOutcome(AiRequestStatus.Unavailable, "Local AI is switched off.")));

        public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
            AiCaller caller, AiAbility ability, string prompt, string grammar, AiRequestOptions? options = null, CancellationToken ct = default) where T : class =>
            throw new NotSupportedException();

        public Task<AiImageTextResult> ReadTextFromImageAsync(AiCaller caller, AiImageTextRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
            Task.FromResult(AiAvailability.Available);
    }
}

/// <summary>A test that runs the Python sample program; it reports itself skipped (not passed) on a machine without python3.</summary>
internal sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (!ServicePluginTests.PythonAvailable())
        {
            Skip = "python3 is not installed on this machine, so the Python sample program cannot run.";
        }
    }
}
