using System.Net;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Sandbox;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Sandboxed (WebAssembly) plugins (Developer tools spec WP21): a module runs with hard limits on time, memory and work,
/// can reach nothing but the Library's plugin services, and a bad module is refused or stopped with a plain message.
/// </summary>
public sealed class SandboxPluginTests : IDisposable
{
    private const string Allocator = """
        (memory (export "memory") 1)
        (global $top (mut i32) (i32.const 1024))
        (func (export "alloc") (param $n i32) (result i32)
          (local $p i32)
          (local.set $p (global.get $top))
          (global.set $top (i32.add (global.get $top) (local.get $n)))
          (local.get $p))
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-sandbox-plugins", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly PluginCatalog _catalog;

    public SandboxPluginTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    // ── Running a module ─────────────────────────────────────────────────────

    [Fact]
    public void AModule_ReturnsItsResult()
    {
        var path = Module($$"""
            (module {{Allocator}}
              (data (i32.const 0) "{\"ok\":true}")
              (func (export "run") (param i32 i32) (result i64) (i64.const 11)))
            """);

        var result = WasmModuleRunner.Run(path, "run", "{}", Limits(path), new Host(), default);

        Assert.Equal("{\"ok\":true}", result);
    }

    [Fact]
    public void AModule_CanLogAndFetchOnlyThroughTheLibrary()
    {
        var path = Module($$"""
            (module
              (import "library" "http_get" (func $get (param i32 i32) (result i64)))
              (import "library" "log" (func $log (param i32 i32)))
              {{Allocator}}
              (data (i32.const 0) "https://api.example.com/x")
              (func (export "run") (param i32 i32) (result i64)
                (call $log (i32.const 0) (i32.const 25))
                (call $get (i32.const 0) (i32.const 25))))
            """);
        var host = new Host { Body = "hello" };

        var result = WasmModuleRunner.Run(path, "run", "{}", Limits(path), host, default);

        Assert.Equal("hello", result);
        Assert.Equal(["https://api.example.com/x"], host.Logged);
        Assert.Equal(["https://api.example.com/x"], host.Fetched);
    }

    [Fact]
    public void AModuleThatLoopsForever_IsStoppedWhenItUsesUpItsSteps()
    {
        var path = Module($$"""
            (module {{Allocator}}
              (func (export "run") (param i32 i32) (result i64) (loop $l (br $l)) (i64.const 0)))
            """);

        var problem = Assert.Throws<SandboxRunException>(() => WasmModuleRunner.Run(path, "run", "{}", Limits(path, steps: 100_000), new Host(), default));

        Assert.Contains("more work", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatRunsTooLong_IsStoppedByTheClock()
    {
        var path = Module($$"""
            (module {{Allocator}}
              (func (export "run") (param i32 i32) (result i64) (loop $l (br $l)) (i64.const 0)))
            """);

        var problem = Assert.Throws<SandboxRunException>(() => WasmModuleRunner.Run(path, "run", "{}", Limits(path, seconds: 1, steps: long.MaxValue), new Host(), default));

        Assert.Contains("1 seconds", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatNeedsMoreMemoryThanAllowed_DoesNotStart()
    {
        var path = Module($$"""
            (module
              (memory (export "memory") 2000)
              (func (export "alloc") (param i32) (result i32) (i32.const 0))
              (func (export "run") (param i32 i32) (result i64) (i64.const 0)))
            """);

        var problem = Assert.Throws<SandboxRunException>(() => WasmModuleRunner.Run(path, "run", "{}", Limits(path, memoryMb: 16), new Host(), default));

        Assert.Contains("could not be started", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatAsksForFilesOrTheClock_DoesNotStart_BecauseTheyDoNotExist()
    {
        var path = Module("""
            (module
              (import "wasi_snapshot_preview1" "path_open" (func (param i32 i32 i32 i32 i32 i64 i64 i32 i32) (result i32)))
              (memory (export "memory") 1)
              (func (export "alloc") (param i32) (result i32) (i32.const 0))
              (func (export "run") (param i32 i32) (result i64) (i64.const 0)))
            """);

        var problem = Assert.Throws<SandboxRunException>(() => WasmModuleRunner.Run(path, "run", "{}", Limits(path), new Host(), default));

        Assert.Contains("could not be started", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatCrashes_ReportsAPlainMessage()
    {
        var path = Module($$"""
            (module {{Allocator}}
              (func (export "run") (param i32 i32) (result i64) unreachable))
            """);

        var problem = Assert.Throws<SandboxRunException>(() => WasmModuleRunner.Run(path, "run", "{}", Limits(path), new Host(), default));

        Assert.StartsWith("The plugin stopped with an error", problem.Message, StringComparison.Ordinal);
    }

    // ── The manifest ─────────────────────────────────────────────────────────

    [Fact]
    public void ASandboxedPlugin_LoadsWithItsJobsAsScheduledJobs()
    {
        Add("box.good", "good.wat", Echo(), """ "jobs": [{ "key": "main", "name": "Main job", "schedule": "12h" }] """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("box.good"));

        Assert.Null(plugin.LoadError);
        Assert.True(plugin.Compatibility.IsCompatible);
        var job = Assert.Single(plugin.Capabilities.OfType<IPluginScheduledJob>());
        Assert.Equal("sandbox-main", job.Key);
        Assert.Equal(TimeSpan.FromHours(12), job.DefaultSchedule.Interval);
    }

    [Theory]
    [InlineData("\"module\": \"../outside.wasm\"", "outside the plugin's own folder")]
    [InlineData("\"module\": \"missing.wasm\"", "was not found")]
    [InlineData("\"module\": \"notes.txt\"", ".wasm file")]
    [InlineData("\"surprise\": 1, \"module\": \"good.wat\"", "unknown setting \"surprise\"")]
    [InlineData("\"module\": \"good.wat\", \"max_seconds\": 9999", "max_seconds")]
    [InlineData("\"module\": \"good.wat\", \"jobs\": []", "jobs")]
    [InlineData("\"module\": \"good.wat\", \"jobs\": [{ \"key\": \"Bad Key\", \"name\": \"x\" }]", "\"key\"")]
    public void AMalformedSandboxSection_IsRefusedWithAPlainMessage(string sandbox, string expected)
    {
        var directory = Path.Combine(_pluginRoot, "box.bad");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "good.wat"), Echo());
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "x");
        var jobs = sandbox.Contains("\"jobs\"", StringComparison.Ordinal) ? "" : ", \"jobs\": [{ \"key\": \"main\", \"name\": \"Main\" }]";
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "box.bad", "name": "bad", "version": "1.0.0", "kind": "sandboxed", "permissions": ["scheduled-work"],
              "sandbox": { {{sandbox}}{{jobs}} } }
            """);
        _catalog.Reload();

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("box.bad"));

        Assert.NotNull(plugin.LoadError);
        Assert.Contains(expected, plugin.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void ASandboxedPluginWithoutTheScheduledWorkPermission_SaysWhichOne()
    {
        var directory = Path.Combine(_pluginRoot, "box.noperm");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "good.wat"), Echo());
        File.WriteAllText(Path.Combine(directory, "plugin.json"), """
            { "id": "box.noperm", "name": "x", "version": "1.0.0", "kind": "sandboxed", "permissions": [],
              "sandbox": { "module": "good.wat", "jobs": [{ "key": "main", "name": "Main" }] } }
            """);
        _catalog.Reload();

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("box.noperm"));

        Assert.Contains("scheduled-work", plugin.LoadError, StringComparison.Ordinal);
    }

    // ── A job ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunningJob_KeepsItsRunAndTheRecordsItListed()
    {
        Add("box.job", "job.wat", Echo(), """ "jobs": [{ "key": "main", "name": "Main job" }] """);
        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("box.job"));
        var job = Assert.Single(plugin.Capabilities.OfType<IPluginScheduledJob>());
        var context = new JobContext();

        await job.RunAsync(context, default);

        Assert.Contains(context.Saved, saved => saved is { Collection: "sandbox", Key: "main" });
        var kept = Assert.Single(context.Saved, saved => saved.Collection == "shelf");
        Assert.Equal("one", kept.Key);
        Assert.Equal("{\"a\":1}", kept.Json);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Echo() => $$$"""
        (module {{{Allocator}}}
          (data (i32.const 0) "{\"records\":[{\"collection\":\"shelf\",\"key\":\"one\",\"json\":{\"a\":1}}]}")
          (func (export "run") (param i32 i32) (result i64) (i64.const 63)))
        """;

    private string Module(string text)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.wat");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, text);
        return path;
    }

    private static SandboxDefinition Limits(string path, int memoryMb = 64, int seconds = 10, long steps = SandboxDefinition.DefaultMaxSteps) =>
        new(path, [], memoryMb, seconds, steps);

    private void Add(string id, string moduleFile, string module, string jobs)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, moduleFile), module);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "kind": "sandboxed", "permissions": ["scheduled-work", "plugin-records"],
              "sandbox": { "module": "{{moduleFile}}", {{jobs}} } }
            """);
        _catalog.Reload();
    }

    private sealed class Host : ISandboxHost
    {
        public string? Body { get; init; }

        public List<string> Logged { get; } = [];

        public List<string> Fetched { get; } = [];

        public void Log(string message) => Logged.Add(message);

        public byte[]? HttpGet(string url, CancellationToken cancellationToken)
        {
            Fetched.Add(url);
            return Body is null ? null : System.Text.Encoding.UTF8.GetBytes(Body);
        }
    }

    private sealed class JobContext : IPluginExecutionContext
    {
        public List<(string Collection, string Key, string Json)> Saved { get; } = [];

        public string PluginId => "box.job";

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginHttpClient Http => new FixedHttp();

        public IPluginRecordStore Records => new SavingRecords(Saved);

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedHttp : IPluginHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
    }

    private sealed class SavingRecords(List<(string Collection, string Key, string Json)> saved) : IPluginRecordStore
    {
        public Task<PluginRecord?> GetAsync(string collection, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
        {
            saved.Add((collection, key, json));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginRecord>> ListAsync(string collection, string? keyPrefix = null, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
