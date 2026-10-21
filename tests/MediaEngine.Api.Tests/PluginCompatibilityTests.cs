using System.Reflection;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginCompatibilityTests
{
    private const string BuiltInId = "tuvima.test.compat";

    [Fact]
    public void LibraryVersionBelowMinimum_CannotBeEnabled_AndSaysWhy()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(minimum: "2.0.0"));

        var registration = fixture.Catalog.Get(BuiltInId)!;
        Assert.False(registration.Compatibility.IsCompatible);
        Assert.Equal("Needs Library 2.0.0 or later; this is 1.0.0.", registration.Compatibility.Problem);

        var refused = Assert.Throws<PluginCannotBeEnabledException>(() => fixture.Catalog.SetEnabled(BuiltInId, true));
        Assert.Equal("Needs Library 2.0.0 or later; this is 1.0.0.", refused.Message);
        Assert.False(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void LibraryVersionAboveInclusiveMaximum_CannotBeEnabled_ButTheMaximumItselfCan()
    {
        using (var tooNew = new Fixture("1.5.1", "win-x64", Manifest(maximum: "1.5.0")))
        {
            var refused = Assert.Throws<PluginCannotBeEnabledException>(() => tooNew.Catalog.SetEnabled(BuiltInId, true));
            Assert.Equal("Works only up to Library 1.5.0; this is 1.5.1.", refused.Message);
        }

        using var atMaximum = new Fixture("1.5.0", "win-x64", Manifest(maximum: "1.5.0"));
        atMaximum.Catalog.SetEnabled(BuiltInId, true);
        Assert.True(atMaximum.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void LibraryVersionPrereleaseAndBuildSuffix_AreIgnoredWhenComparing()
    {
        using var fixture = new Fixture("1.2.0-beta.3", "win-x64", Manifest(minimum: "1.2.0", maximum: "1.2.0"));

        fixture.Catalog.SetEnabled(BuiltInId, true);

        Assert.True(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void UnsupportedPlatform_CannotBeEnabled_AndSaysWhy()
    {
        using var fixture = new Fixture("1.0.0", "linux-arm64", Manifest(platforms: ["win-x64", "linux-x64"]));

        var refused = Assert.Throws<PluginCannotBeEnabledException>(() => fixture.Catalog.SetEnabled(BuiltInId, true));

        Assert.Equal("Not available on linux-arm64.", refused.Message);
    }

    [Theory]
    [InlineData("linux", "linux-arm64")]
    [InlineData("linux", "linux-x64")]
    [InlineData("windows", "win-x64")]
    [InlineData("macos", "osx-arm64")]
    [InlineData("linux-arm64", "linux-arm64")]
    public void OperatingSystemFamilyOrExactRuntimeIdentifier_MatchesTheMachine(string declared, string machine)
    {
        using var fixture = new Fixture("1.0.0", machine, Manifest(platforms: [declared]));

        fixture.Catalog.SetEnabled(BuiltInId, true);

        Assert.True(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void OperatingSystemFamily_DoesNotMatchADifferentOperatingSystem()
    {
        using var fixture = new Fixture("1.0.0", "osx-arm64", Manifest(platforms: ["linux", "windows"]));

        Assert.Throws<PluginCannotBeEnabledException>(() => fixture.Catalog.SetEnabled(BuiltInId, true));
    }

    [Fact]
    public void EmptyPlatformList_MeansEveryPlatform()
    {
        using var fixture = new Fixture("1.0.0", "linux-arm64", Manifest(platforms: []));

        fixture.Catalog.SetEnabled(BuiltInId, true);

        Assert.True(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void UnknownPlatformValue_IsALoadErrorNamingTheValue()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(platforms: ["amiga-m68k"]));

        var registration = fixture.Catalog.Get(BuiltInId)!;

        Assert.Contains("amiga-m68k", registration.LoadError, StringComparison.Ordinal);
        Assert.False(registration.Compatibility.IsCompatible);
        Assert.Throws<PluginCannotBeEnabledException>(() => fixture.Catalog.SetEnabled(BuiltInId, true));
    }

    [Theory]
    [InlineData("one")]
    [InlineData("1.0")]
    [InlineData("1.0.x")]
    [InlineData("")]
    public void MalformedMinimumVersion_IsALoadErrorNamingTheValue(string minimum)
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(minimum: minimum));

        var registration = fixture.Catalog.Get(BuiltInId)!;

        Assert.NotNull(registration.LoadError);
        Assert.Contains("minimum_library_version", registration.LoadError, StringComparison.Ordinal);
        Assert.False(registration.Compatibility.IsCompatible);
    }

    [Fact]
    public void UndefinedKindNumber_IsALoadError_InsteadOfBreakingThePluginList()
    {
        var manifest = new PluginManifest { Id = BuiltInId, Name = "Compatibility Test", Kind = (PluginKind)7 };

        var problem = PluginCompatibility.ValidateManifest(manifest);

        Assert.NotNull(problem);
        Assert.Contains("unknown kind", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedMaximumVersion_IsALoadError()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(maximum: "soon"));

        var registration = fixture.Catalog.Get(BuiltInId)!;

        Assert.Contains("maximum_library_version", registration.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalPluginWithMalformedVersion_LoadsWithALoadErrorAndRunsNoCode()
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        fixture.AddExternalPlugin("tuvima.test.badversion", """ "minimum_library_version": "latest" """);

        var registration = Assert.Single(fixture.Catalog.List());

        Assert.Contains("minimum_library_version", registration.LoadError, StringComparison.Ordinal);
        Assert.Empty(registration.Capabilities);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("sandboxed")]
    public void NonOfficialKind_LoadsWithoutAnError_ButCannotBeEnabled(string kind)
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        fixture.AddExternalPlugin("tuvima.test.kind", $$""" "kind": "{{kind}}" """, withEntryPoint: false);

        var registration = Assert.Single(fixture.Catalog.List());
        Assert.Null(registration.LoadError);
        Assert.False(registration.Compatibility.IsCompatible);
        Assert.Equal($"This kind of plugin ({kind}) is not supported by this version of the Library yet.", registration.Compatibility.Problem);
        Assert.Empty(registration.Capabilities);

        var refused = Assert.Throws<PluginCannotBeEnabledException>(() => fixture.Catalog.SetEnabled("tuvima.test.kind", true));
        Assert.Equal(registration.Compatibility.Problem, refused.Message);
    }

    [Fact]
    public void ManifestWithoutAKind_IsOfficial()
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        fixture.AddExternalPlugin("tuvima.test.nokind", "");

        var registration = Assert.Single(fixture.Catalog.List());

        Assert.Equal(PluginKind.Official, registration.Manifest.Kind);
        Assert.Null(registration.LoadError);
        Assert.True(registration.Compatibility.IsCompatible);
    }

    [Fact]
    public void ExternalPluginDeclaringOfficialKind_IsStillPrivateTrust()
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        fixture.AddExternalPlugin("tuvima.test.sideloaded", """ "kind": "official" """);

        var registration = Assert.Single(fixture.Catalog.List());

        Assert.Equal(PluginKind.Official, registration.Manifest.Kind);
        Assert.Equal(PluginTrustLevel.Private, registration.Trust);
    }

    [Fact]
    public void ManifestDeclaringATrustLevel_CannotRaiseItsOwnTrust()
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        fixture.AddExternalPlugin("tuvima.test.trust", """ "trust_level": "official" """);

        var registration = Assert.Single(fixture.Catalog.List());

        Assert.Equal(PluginTrustLevel.Private, registration.Trust);
    }

    [Fact]
    public void IncompatiblePluginSavedAsEnabled_IsNotRun_AndItsSavedFlagIsKept()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(minimum: "1.0.0"));
        fixture.Catalog.SetEnabled(BuiltInId, true);
        Assert.Single(fixture.Catalog.GetEnabledSegmentDetectors());

        // The same saved settings, read by a newer Library that no longer fits the plugin.
        var newer = fixture.CreateCatalog("3.0.0", "win-x64", Manifest(maximum: "2.0.0"));

        var registration = newer.Get(BuiltInId)!;
        Assert.False(registration.Enabled);
        Assert.False(registration.Compatibility.IsCompatible);
        Assert.Equal("Works only up to Library 2.0.0; this is 3.0.0.", registration.Compatibility.Problem);
        Assert.Empty(newer.GetEnabledSegmentDetectors());
        Assert.True(fixture.SavedEnabledFlag(BuiltInId));
    }

    [Fact]
    public void IncompatiblePluginSavedAsEnabled_IsRefusedByThePermissionGate()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(permissions: [PluginPermissionIds.NetworkHttp]));
        fixture.Catalog.SetEnabled(BuiltInId, true);
        var newer = fixture.CreateCatalog("3.0.0", "win-x64", Manifest(maximum: "2.0.0", permissions: [PluginPermissionIds.NetworkHttp]));

        Assert.Throws<UnauthorizedAccessException>(() =>
            new PluginPermissionGate(newer).Demand(BuiltInId, PluginPermissionIds.NetworkHttp));
        new PluginPermissionGate(fixture.Catalog).Demand(BuiltInId, PluginPermissionIds.NetworkHttp);
    }

    [Fact]
    public void DisablingAnIncompatiblePlugin_AlwaysWorks()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(minimum: "9.0.0"));

        fixture.Catalog.SetEnabled(BuiltInId, false);

        Assert.False(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public async Task EnableEndpoint_ReportsConflictWithThePlainReason_ForAnIncompatiblePlugin()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest(minimum: "2.0.0"));
        using var approvals = new ApprovalTestKit();

        var result = await PluginEndpoints.EnablePlugin(BuiltInId, null, approvals.Http(), fixture.Catalog, approvals.Service, approvals.Resolver, default);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("Needs Library 2.0.0 or later; this is 1.0.0.", problem.ProblemDetails.Detail);
    }

    [Fact]
    public async Task EnableEndpoint_SwitchesACompatiblePluginOn_OnceItsStatementIsConfirmed()
    {
        using var fixture = new Fixture("1.0.0", "win-x64", Manifest());
        using var approvals = new ApprovalTestKit();
        var hash = approvals.Service.PreviewPlugin(fixture.Catalog.Get(BuiltInId)!).StatementHash;

        var result = await PluginEndpoints.EnablePlugin(
            BuiltInId, new MediaEngine.Contracts.Approvals.PluginEnableRequest { ApprovalHash = hash }, approvals.Http(), fixture.Catalog, approvals.Service, approvals.Resolver, default);

        Assert.IsType<Ok<MediaEngine.Contracts.Plugins.PluginEnabledResponse>>(result);
        Assert.True(fixture.Catalog.Get(BuiltInId)!.Enabled);
    }

    [Fact]
    public void ListedPlugin_ReportsKindTrustVersionsPlatformsAndCompatibility()
    {
        using var fixture = new Fixture("1.0.0", "linux-arm64", Manifest(minimum: "1.0.0", maximum: "1.9.0", platforms: ["win-x64"]));

        var dto = PluginEndpoints.ToDto(fixture.Catalog.Get(BuiltInId)!);

        Assert.Equal("official", dto.Kind);
        Assert.Equal("official", dto.TrustLevel);
        Assert.Equal("1.0.0", dto.MinimumLibraryVersion);
        Assert.Equal("1.9.0", dto.MaximumLibraryVersion);
        Assert.Equal(["win-x64"], dto.SupportedPlatforms);
        Assert.False(dto.IsCompatible);
        Assert.Equal("Not available on linux-arm64.", dto.CompatibilityProblem);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public void ShippedPlugins_LoadWithoutErrorAreOfficialAndCompatible(string machine)
    {
        using var fixture = new Fixture(LibraryVersionForTests(), machine);
        var catalog = fixture.CreateCatalog(LibraryVersionForTests(), machine, builtIns: ShippedPlugins());

        foreach (var registration in catalog.List())
        {
            Assert.Null(registration.LoadError);
            Assert.Equal(PluginTrustLevel.Official, registration.Trust);
            Assert.Equal(PluginKind.Official, registration.Manifest.Kind);
            Assert.True(registration.Compatibility.IsCompatible, $"{registration.Manifest.Id}: {registration.Compatibility.Problem}");
        }

        Assert.Equal(ShippedPlugins().Count, catalog.List().Count);
    }

    [Fact]
    public void ShippedPluginsThatUseOnlyManagedCodeAndFfmpeg_RunOnLinuxArm64()
    {
        using var fixture = new Fixture(LibraryVersionForTests(), "linux-arm64");
        var catalog = fixture.CreateCatalog(LibraryVersionForTests(), "linux-arm64", builtIns: ShippedPlugins());

        var incompatible = catalog.List().Where(r => !r.Compatibility.IsCompatible).Select(r => r.Manifest.Id).ToArray();

        Assert.Equal(["tuvima.commercial-skip"], incompatible);
    }

    [Fact]
    public void CurrentMachineAndLibraryVersion_AreUsableByTheShippedPlugins()
    {
        using var fixture = new Fixture(LibraryVersionForTests(), PluginCompatibility.CurrentRid);
        var catalog = new PluginCatalog(ShippedPlugins(), fixture.Settings, fixture.Loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);

        Assert.All(catalog.List(), registration =>
            Assert.True(registration.Compatibility.IsCompatible, $"{registration.Manifest.Id}: {registration.Compatibility.Problem}"));
    }

    [Fact]
    public void PluginSettingsFile_ThatCannotBeRead_IsLoggedAndFallsBackToSafeDefaults()
    {
        using var fixture = new Fixture("1.0.0", "win-x64");
        var manifest = Manifest(settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = JsonSerializer.SerializeToElement("safe"),
        });
        var settingsRoot = Path.Combine(fixture.LibraryRoot, ".data", "plugin-config");
        Directory.CreateDirectory(settingsRoot);
        File.WriteAllText(Path.Combine(settingsRoot, $"{BuiltInId}.json"), "{ this is not json");
        var logger = new SpyLogger<PluginSettingsService>();
        var service = new PluginSettingsService(fixture.Loader, logger);

        var config = service.Load(manifest);

        Assert.False(config.Enabled);
        Assert.Equal("safe", config.Settings["mode"].GetString());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(BuiltInId, entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2.3+abc123", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("a.b.c", false)]
    [InlineData("-1.0.0", false)]
    [InlineData(null, false)]
    public void VersionParsing_AcceptsOnlyMajorMinorPatch(string? text, bool expected)
    {
        Assert.Equal(expected, PluginCompatibility.TryParseVersion(text, out _));
    }

    private static string LibraryVersionForTests() => "1.0.0";

    private static List<ITuvimaPlugin> ShippedPlugins() =>
    [
        new CommercialSkipPlugin(),
        new FandomLorePlugin(),
        new IntroSkipPlugin(),
        new CreditsDetectionPlugin(),
        new RecapDetectionPlugin(),
    ];

    private static PluginManifest Manifest(
        string minimum = "1.0.0",
        string? maximum = null,
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<string>? permissions = null,
        Dictionary<string, JsonElement>? settings = null) => new()
        {
            Id = BuiltInId,
            Name = "Compatibility Test",
            MinimumLibraryVersion = minimum,
            MaximumLibraryVersion = maximum,
            SupportedPlatforms = platforms ?? [],
            Permissions = permissions ?? [],
            Network = permissions is not null && permissions.Contains(PluginPermissionIds.NetworkHttp)
                ? [new PluginNetworkDestination { Host = "api.example.com", Sends = "Looks up titles by name" }]
                : [],
            DefaultSettings = settings ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
        };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(string libraryVersion, string platform, PluginManifest? builtIn = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-compat-tests", Guid.NewGuid().ToString("N"));
            LibraryRoot = Path.Combine(_root, "library");
            Directory.CreateDirectory(LibraryRoot);
            Loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            Loader.SaveCore(new CoreConfiguration { LibraryRoot = LibraryRoot });
            Settings = new PluginSettingsService(Loader);
            Catalog = CreateCatalog(libraryVersion, platform, builtIn);
            LibraryVersion = libraryVersion;
            Platform = platform;
        }

        public string LibraryRoot { get; }
        public ConfigurationDirectoryLoader Loader { get; }
        public PluginSettingsService Settings { get; }
        public PluginCatalog Catalog { get; private set; }
        public string LibraryVersion { get; }
        public string Platform { get; }

        public PluginCatalog CreateCatalog(string libraryVersion, string platform, PluginManifest? builtIn = null, IEnumerable<ITuvimaPlugin>? builtIns = null)
        {
            var plugins = builtIns ?? (builtIn is null ? [] : [new SegmentTestPlugin(builtIn)]);
            return new PluginCatalog(plugins, Settings, Loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance, libraryVersion, platform);
        }

        public bool SavedEnabledFlag(string pluginId) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(LibraryRoot, ".data", "plugin-config", $"{pluginId}.json")))
                .RootElement.GetProperty("Enabled").GetBoolean();

        public void AddExternalPlugin(string id, string extraJsonFields, bool withEntryPoint = true)
        {
            var directory = Path.Combine(LibraryRoot, ".data", "plugins", id);
            Directory.CreateDirectory(directory);
            var entry = "";
            if (withEntryPoint)
            {
                var testAssemblyPath = Assembly.GetExecutingAssembly().Location;
                File.Copy(testAssemblyPath, Path.Combine(directory, Path.GetFileName(testAssemblyPath)), overwrite: true);
                entry = $$"""
                      "entry_assembly": "MediaEngine.Api.Tests.dll",
                      "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
                    """;
            }

            var extra = string.IsNullOrWhiteSpace(extraJsonFields) ? "" : extraJsonFields.Trim() + ",";
            File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
                {
                  {{extra}}
                  {{entry}}
                  "id": "{{id}}",
                  "name": "External Test",
                  "version": "1.0.0"
                }
                """);
            Catalog.Reload();
        }

        public void Dispose()
        {
            Loader.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class SegmentTestPlugin(PluginManifest manifest) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [new NoopDetector()];
    }

    private sealed class NoopDetector : IPlaybackSegmentDetector
    {
        public string Kind => "playback-segment-detector";

        public bool CanAnalyze(PluginMediaAssetContext asset, IPluginExecutionContext context) => false;

        public Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(
            PluginMediaAssetContext asset,
            IPluginExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginPlaybackSegment>>([]);
    }

    private sealed class SpyLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
