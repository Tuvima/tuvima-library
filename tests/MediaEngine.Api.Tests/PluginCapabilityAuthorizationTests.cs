using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginCapabilityAuthorizationTests
{
    [Fact]
    public void Gate_DeniesUnknownDisabledAndUndeclaredPermissions()
    {
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.MediaRead]));
        var gate = new PluginPermissionGate(fixture.Catalog);

        Assert.Throws<UnauthorizedAccessException>(() => gate.Demand(TestPlugin.Id, PluginPermissionIds.MediaRead));
        fixture.Enable();
        Assert.Throws<UnauthorizedAccessException>(() => gate.Demand(TestPlugin.Id, PluginPermissionIds.NetworkHttp));
        Assert.Throws<UnauthorizedAccessException>(() => gate.Demand(TestPlugin.Id, "unknown.permission"));
        Assert.Throws<UnauthorizedAccessException>(() => gate.Demand("another.plugin", PluginPermissionIds.MediaRead));
    }

    [Fact]
    public async Task HttpPermission_IsCheckedBeforeSending_AndRevokedLive()
    {
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.NetworkHttp]));
        fixture.Enable();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new BoundPluginHttpClient(TestPlugin.Id, new PluginPermissionGate(fixture.Catalog), new SingleClientFactory(handler));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/data"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);

        fixture.Catalog.SetEnabled(TestPlugin.Id, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/again")));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task MissingHttpPermission_HasNoNetworkSideEffect()
    {
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.MediaRead]));
        fixture.Enable();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new BoundPluginHttpClient(TestPlugin.Id, new PluginPermissionGate(fixture.Catalog), new SingleClientFactory(handler));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/data")));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void MediaAccess_IsBoundToTheExactHostSelectedAsset()
    {
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.MediaRead]));
        fixture.Enable();
        var path = Path.Combine(fixture.Root, "video.mkv");
        File.WriteAllText(path, "test");
        var allowed = new PluginMediaAssetContext { AssetId = Guid.NewGuid(), FilePath = path, MediaType = "Movies" };
        var media = new BoundPluginMediaAccess(TestPlugin.Id, allowed, new PluginPermissionGate(fixture.Catalog));

        Assert.True(media.Exists(allowed));
        Assert.Throws<UnauthorizedAccessException>(() => media.Exists(allowed with { AssetId = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() => media.Exists(allowed with { FilePath = Path.Combine(fixture.Root, "other.mkv") }));

        using var deniedFixture = new CatalogFixture(Manifest([]));
        deniedFixture.Enable();
        var denied = new BoundPluginMediaAccess(TestPlugin.Id, allowed, new PluginPermissionGate(deniedFixture.Catalog));
        Assert.Throws<UnauthorizedAccessException>(() => denied.Exists(allowed));
    }

    [Fact]
    public void Storage_DeniesMissingPermissionAndPathTraversalBeforeWrites()
    {
        using var deniedFixture = new CatalogFixture(Manifest([]));
        deniedFixture.Enable();
        var deniedRoot = Path.Combine(deniedFixture.Root, "denied-storage");
        var denied = new BoundPluginStorage(TestPlugin.Id, deniedRoot, new PluginPermissionGate(deniedFixture.Catalog));
        Assert.Throws<UnauthorizedAccessException>(() => denied.CreateDirectory("work"));
        Assert.False(Directory.Exists(deniedRoot));

        using var allowedFixture = new CatalogFixture(Manifest([PluginPermissionIds.PluginStorage]));
        allowedFixture.Enable();
        var allowedRoot = Path.Combine(allowedFixture.Root, "plugin-storage");
        var allowed = new BoundPluginStorage(TestPlugin.Id, allowedRoot, new PluginPermissionGate(allowedFixture.Catalog));
        allowed.CreateDirectory("work");
        Assert.True(Directory.Exists(Path.Combine(allowedRoot, "work")));
        Assert.Throws<UnauthorizedAccessException>(() => allowed.CreateDirectory(Path.Combine("..", "escape")));
        Assert.False(Directory.Exists(Path.Combine(allowedFixture.Root, "escape")));
    }

    [Fact]
    public async Task AiPermission_RequiresCapabilityAndExactRole_AndRevokesLive()
    {
        var aiPermission = new PluginAiPermission { Role = "text_fast", MaxTokens = 128, Schedule = "scheduled", ResourceClass = "ai" };
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.AiInfer], ai: [aiPermission]));
        fixture.Enable();
        var inference = new RecordingEngineRoom();
        var client = new BoundPluginAiClient(TestPlugin.Id, new PluginPermissionGate(fixture.Catalog), new PluginAiClient(inference));

        Assert.Equal("ok", await client.InferTextAsync("text_fast", "prompt"));
        Assert.Equal(1, inference.CallCount);
        Assert.Equal("ok", await client.InferTextAsync("text_fast", "prompt", new PluginAiOptions
        {
            MaxTokens = 64,
            Schedule = "scheduled",
            ResourceClass = "ai",
        }));
        Assert.Equal(2, inference.CallCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.InferTextAsync("text_fast", "prompt", new PluginAiOptions
        {
            MaxTokens = 129,
            Schedule = "scheduled",
            ResourceClass = "ai",
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.InferTextAsync("text_fast", "prompt", new PluginAiOptions
        {
            MaxTokens = 64,
            Schedule = "on-demand",
            ResourceClass = "ai",
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.InferTextAsync("text_fast", "prompt", new PluginAiOptions
        {
            MaxTokens = 64,
            Schedule = "scheduled",
            ResourceClass = "ai-heavy",
        }));
        Assert.Equal(2, inference.CallCount);

        using var deniedFixture = new CatalogFixture(Manifest([], ai: [aiPermission]));
        deniedFixture.Enable();
        var deniedInference = new RecordingEngineRoom();
        var denied = new BoundPluginAiClient(TestPlugin.Id, new PluginPermissionGate(deniedFixture.Catalog), new PluginAiClient(deniedInference));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.InferTextAsync("text_fast", "prompt"));
        Assert.Equal(0, deniedInference.CallCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.InferTextAsync("text_quality", "prompt"));
        Assert.Equal(2, inference.CallCount);

        fixture.Catalog.SetEnabled(TestPlugin.Id, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.InferTextAsync("text_fast", "prompt"));
        Assert.Equal(2, inference.CallCount);
    }

    [Fact]
    public async Task PluginAiRequests_GoToTheEngineRoomAsThatPlugin_WithTheirTokenLimit()
    {
        var aiPermission = new PluginAiPermission { Role = "text_fast", MaxTokens = 128, Schedule = "scheduled", ResourceClass = "ai" };
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.AiInfer], ai: [aiPermission]));
        fixture.Enable();
        var room = new RecordingEngineRoom();
        var client = new BoundPluginAiClient(TestPlugin.Id, new PluginPermissionGate(fixture.Catalog), new PluginAiClient(room));

        await client.InferTextAsync("text_fast", "prompt", new PluginAiOptions { MaxTokens = 64 });
        await client.InferJsonAsync<object>("text_fast", "prompt", "root ::= \"{}\"", new PluginAiOptions { MaxTokens = 32 });
        await client.InferTextAsync("text_fast", "prompt");

        Assert.Equal(
            [
                (new AiCaller(AiCallerKind.Plugin, TestPlugin.Id), AiAbility.WriteTextQuick, (int?)64),
                (new AiCaller(AiCallerKind.Plugin, TestPlugin.Id), AiAbility.WriteTextQuick, (int?)32),
                (new AiCaller(AiCallerKind.Plugin, TestPlugin.Id), AiAbility.WriteTextQuick, (int?)null),
            ],
            room.Requests);
    }

    [Theory]
    [InlineData("text_fast", AiAbility.WriteTextQuick)]
    [InlineData("text_quality", AiAbility.WriteTextCareful)]
    [InlineData("text_scholar", AiAbility.WriteTextLong)]
    [InlineData("text_cjk", AiAbility.WriteTextCareful)]
    public async Task PluginRoleWords_AreMappedToAbilities(string role, AiAbility expected)
    {
        var room = new RecordingEngineRoom();

        await new PluginAiClient(room).InferTextAsync(TestPlugin.Id, role, "prompt");

        Assert.Equal(expected, Assert.Single(room.Requests).Ability);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("vision")]
    public async Task PluginAudioAndVisionRoles_StayReservedForLater(string role)
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new PluginAiClient(new RecordingEngineRoom()).InferTextAsync(TestPlugin.Id, role, "prompt"));
    }

    [Fact]
    public void Catalog_FailsUnknownPermissionsAndEntryAssemblyEscape()
    {
        using (var unknown = new CatalogFixture(Manifest(["NETWORK.HTTP"])))
        {
            var registration = Assert.Single(unknown.Catalog.List());
            Assert.Contains("unknown permission", registration.LoadError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(registration.Capabilities);
        }

        var root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-path-tests", Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(root, "library");
        var pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        var pluginDirectory = Path.Combine(pluginRoot, "escape-test");
        Directory.CreateDirectory(pluginDirectory);
        try
        {
            using var loader = new ConfigurationDirectoryLoader(Path.Combine(root, "config"));
            loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
            File.WriteAllText(Path.Combine(pluginRoot, "outside.dll"), "not loaded");
            File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), """
                {
                  "id": "tuvima.test.escape",
                  "name": "Escape",
                  "entry_assembly": "../outside.dll",
                  "entry_type": "Anything.Plugin"
                }
                """);
            var catalog = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);

            var registration = Assert.Single(catalog.List());
            Assert.Contains("outside the plugin directory", registration.LoadError, StringComparison.OrdinalIgnoreCase);
            Assert.False(registration.Enabled);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToolDownload_IsIndependentFromExecutionPermission_AndDeniedBeforeDownload()
    {
        var payload = Encoding.UTF8.GetBytes("fake executable");
        var tool = DownloadTool(payload);
        using var fixture = new CatalogFixture(Manifest(
            [PluginPermissionIds.ProcessExecute, PluginPermissionIds.PluginStorage], tools: [tool]));
        fixture.Enable();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var raw = new PluginToolRuntime(new SingleClientFactory(handler), fixture.Loader, NullLogger<PluginToolRuntime>.Instance);
        var storage = new BoundPluginStorage(TestPlugin.Id, Path.Combine(fixture.Root, "working"), new PluginPermissionGate(fixture.Catalog));
        var bound = new BoundPluginToolRuntime(TestPlugin.Id, fixture.Catalog.Get(TestPlugin.Id)!.Settings, storage, new PluginPermissionGate(fixture.Catalog), raw);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bound.ResolveToolAsync(tool.Id));
        Assert.Equal(0, handler.RequestCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.LibraryRoot, ".data", "plugin-tools")));

        using var noProcessFixture = new CatalogFixture(Manifest(
            [PluginPermissionIds.ToolDownload, PluginPermissionIds.PluginStorage], tools: [tool]));
        noProcessFixture.Enable();
        var noProcessHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var noProcessGate = new PluginPermissionGate(noProcessFixture.Catalog);
        var noProcessRaw = new PluginToolRuntime(new SingleClientFactory(noProcessHandler), noProcessFixture.Loader, NullLogger<PluginToolRuntime>.Instance);
        var noProcessStorage = new BoundPluginStorage(TestPlugin.Id, Path.Combine(noProcessFixture.Root, "working"), noProcessGate);
        var noProcess = new BoundPluginToolRuntime(TestPlugin.Id, noProcessFixture.Catalog.Get(TestPlugin.Id)!.Settings, noProcessStorage, noProcessGate, noProcessRaw);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noProcess.ResolveToolAsync(tool.Id));
        Assert.Equal(0, noProcessHandler.RequestCount);
    }

    [Fact]
    public async Task ToolDownload_UsesPinnedManifestAndForgedExecutionIsDenied()
    {
        var payload = Encoding.UTF8.GetBytes("fake executable");
        var tool = DownloadTool(payload);
        using var fixture = new CatalogFixture(Manifest(
            [PluginPermissionIds.ProcessExecute, PluginPermissionIds.ToolDownload, PluginPermissionIds.PluginStorage], tools: [tool]));
        fixture.Enable();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var gate = new PluginPermissionGate(fixture.Catalog);
        var raw = new PluginToolRuntime(new SingleClientFactory(handler), fixture.Loader, NullLogger<PluginToolRuntime>.Instance);
        var storage = new BoundPluginStorage(TestPlugin.Id, Path.Combine(fixture.Root, "working"), gate);
        var bound = new BoundPluginToolRuntime(TestPlugin.Id, fixture.Catalog.Get(TestPlugin.Id)!.Settings, storage, gate, raw);

        var resolved = await bound.ResolveToolAsync(tool.Id);
        Assert.True(resolved.IsAvailable);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(File.Exists(resolved.ExecutablePath));

        var forged = new PluginToolResolution { IsAvailable = true, ExecutablePath = Path.Combine(fixture.Root, "not-resolved.exe") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bound.RunToolAsync(forged, [], storage.WorkingDirectory, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ProcessExecution_AllowsOnlyAResolvedDeclaredTool()
    {
        var shellPath = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "exit 0" }
            : new[] { "-c", "exit 0" };
        var requirement = new PluginToolRequirement
        {
            Id = "shell",
            Version = "system",
            ExecutableName = Path.GetFileName(shellPath),
        };
        var settings = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["shell_tool_path"] = System.Text.Json.JsonSerializer.SerializeToElement(shellPath),
        };
        using var fixture = new CatalogFixture(Manifest(
            [PluginPermissionIds.ProcessExecute, PluginPermissionIds.PluginStorage],
            tools: [requirement],
            settings: settings));
        fixture.Enable();
        var gate = new PluginPermissionGate(fixture.Catalog);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No download expected."));
        var raw = new PluginToolRuntime(new SingleClientFactory(handler), fixture.Loader, NullLogger<PluginToolRuntime>.Instance);
        var storage = new BoundPluginStorage(TestPlugin.Id, Path.Combine(fixture.Root, "working"), gate);
        var bound = new BoundPluginToolRuntime(TestPlugin.Id, fixture.Catalog.Get(TestPlugin.Id)!.Settings, storage, gate, raw);

        var resolved = await bound.ResolveToolAsync("shell");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bound.RunToolAsync(resolved, arguments, fixture.Root, TimeSpan.FromSeconds(5)));
        var result = await bound.RunToolAsync(resolved, arguments, storage.WorkingDirectory, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ToolManifest_PathSegmentsAreValidatedBeforeDownload()
    {
        var payload = Encoding.UTF8.GetBytes("payload");
        var malformed = DownloadTool(payload) with { Id = "../escape" };
        using var fixture = new CatalogFixture(Manifest(
            [PluginPermissionIds.ProcessExecute, PluginPermissionIds.ToolDownload, PluginPermissionIds.PluginStorage],
            tools: [malformed]));
        fixture.Enable();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var gate = new PluginPermissionGate(fixture.Catalog);
        var raw = new PluginToolRuntime(new SingleClientFactory(handler), fixture.Loader, NullLogger<PluginToolRuntime>.Instance);
        var storage = new BoundPluginStorage(TestPlugin.Id, Path.Combine(fixture.Root, "working"), gate);
        var bound = new BoundPluginToolRuntime(TestPlugin.Id, fixture.Catalog.Get(TestPlugin.Id)!.Settings, storage, gate, raw);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ResolveToolAsync(malformed.Id));
        Assert.Equal(0, handler.RequestCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.LibraryRoot, ".data", "escape")));
    }

    [Fact]
    public async Task ExecutionContext_DisposalRemovesItsBoundWorkingDirectory()
    {
        using var fixture = new CatalogFixture(Manifest([PluginPermissionIds.PluginStorage]));
        fixture.Enable();
        var storageRoot = Path.Combine(fixture.Root, "working");
        var storage = new BoundPluginStorage(TestPlugin.Id, storageRoot, new PluginPermissionGate(fixture.Catalog));
        storage.CreateDirectory("nested");
        var context = new PluginExecutionContext(TestPlugin.Id, new Dictionary<string, System.Text.Json.JsonElement>(), null!, null!, storage, null!, null!, null!);

        await context.DisposeAsync();

        Assert.False(Directory.Exists(storageRoot));
        Assert.Throws<ObjectDisposedException>(() => _ = storage.WorkingDirectory);
    }

    [Fact]
    public void BundledPlugins_DeclareOnlyTheirUsedHostCapabilities()
    {
        Assert.Equal(
            [PluginPermissionIds.MediaRead, PluginPermissionIds.ProcessExecute, PluginPermissionIds.ToolDownload, PluginPermissionIds.PluginStorage],
            new CommercialSkipPlugin().Manifest.Permissions);
        Assert.Equal([PluginPermissionIds.NetworkHttp], new FandomLorePlugin().Manifest.Permissions);
        Assert.Equal(
            [PluginPermissionIds.MediaRead, PluginPermissionIds.ProcessExecute, PluginPermissionIds.PluginStorage],
            new IntroSkipPlugin().Manifest.Permissions);
        Assert.Equal([PluginPermissionIds.MediaRead], new RecapDetectionPlugin().Manifest.Permissions);
        Assert.Equal(
            [PluginPermissionIds.MediaRead, PluginPermissionIds.AiInfer],
            new AiVisualVerifierPlugin().Manifest.Permissions);
    }

    [Fact]
    public void PluginFacingServices_DoNotAcceptCallerSuppliedPluginIdentity()
    {
        Assert.All(typeof(IPluginToolRuntime).GetMethods(), method =>
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name == "pluginId"));
        Assert.All(typeof(IPluginAiClient).GetMethods(), method =>
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name == "pluginId"));
        Assert.All(typeof(IPluginHttpClient).GetMethods(), method =>
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name == "pluginId"));
        Assert.DoesNotContain(typeof(IPluginExecutionContext).GetProperties(), property =>
            property.PropertyType == typeof(IPluginExecutionContextFactory));
    }

    private static PluginManifest Manifest(
        IReadOnlyList<string> permissions,
        IReadOnlyList<PluginToolRequirement>? tools = null,
        IReadOnlyList<PluginAiPermission>? ai = null,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? settings = null) => new()
        {
            Id = TestPlugin.Id,
            Name = "Capability test plugin",
            Permissions = permissions,
            ToolRequirements = tools ?? [],
            AiPermissions = ai ?? [],
            DefaultSettings = settings is null
            ? new Dictionary<string, System.Text.Json.JsonElement>()
            : new Dictionary<string, System.Text.Json.JsonElement>(settings),
        };

    private static PluginToolRequirement DownloadTool(byte[] payload) => new()
    {
        Id = "test-tool",
        Version = "1.0.0",
        ExecutableName = "test-tool",
        Platforms =
        [
            new PluginToolPlatform
            {
                Rid = RuntimeInformation.RuntimeIdentifier,
                DownloadUrl = "https://example.test/payload",
                Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                RelativeExecutablePath = "test-tool",
            },
        ],
    };

    private sealed class CatalogFixture : IDisposable
    {
        public CatalogFixture(PluginManifest manifest)
        {
            Root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-capability-tests", Guid.NewGuid().ToString("N"));
            LibraryRoot = Path.Combine(Root, "library");
            Directory.CreateDirectory(LibraryRoot);
            Loader = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            Loader.SaveCore(new CoreConfiguration { LibraryRoot = LibraryRoot });
            Catalog = new PluginCatalog(
                [new TestPlugin(manifest)],
                new PluginSettingsService(Loader),
                Loader,
                AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        }

        public string Root { get; }
        public string LibraryRoot { get; }
        public ConfigurationDirectoryLoader Loader { get; }
        public PluginCatalog Catalog { get; }
        public void Enable() => Catalog.SetEnabled(TestPlugin.Id, true);

        public void Dispose()
        {
            Loader.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class TestPlugin(PluginManifest manifest) : ITuvimaPlugin
    {
        public const string Id = "tuvima.test.capabilities";
        public PluginManifest Manifest { get; } = manifest;
        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class RecordingEngineRoom : IAiEngineRoom
    {
        public int CallCount => Requests.Count;

        public List<(AiCaller Caller, AiAbility Ability, int? MaxTokens)> Requests { get; } = [];

        public Task<AiTextResult> WriteTextAsync(
            AiCaller caller, AiAbility ability, string prompt, string? grammar = null, AiRequestOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add((caller, ability, options?.MaxTokens));
            return Task.FromResult(new AiTextResult("ok", new AiRequestOutcome(AiRequestStatus.Succeeded, "Done")));
        }

        public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
            AiCaller caller, AiAbility ability, string prompt, string grammar, AiRequestOptions? options = null, CancellationToken ct = default) where T : class
        {
            Requests.Add((caller, ability, options?.MaxTokens));
            return Task.FromResult(new AiJsonResult<T>(null, new AiRequestOutcome(AiRequestStatus.Succeeded, "Done")));
        }

        public Task<AiImageTextResult> ReadTextFromImageAsync(AiCaller caller, AiImageTextRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
            Task.FromResult(AiAvailability.Available);
    }
}
