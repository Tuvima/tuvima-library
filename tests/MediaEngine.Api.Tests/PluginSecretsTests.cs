using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginSecretsTests : IDisposable
{
    private const string PluginA = "tuvima.test.secrets-a";
    private const string PluginB = "tuvima.test.secrets-b";
    private const string SecretValue = "super-secret-plugin-value-4242";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-secrets-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly ConfigFolderSecretsStore _store;
    private readonly PluginCatalog _catalog;

    public PluginSecretsTests()
    {
        var configRoot = Path.Combine(_root, "config");
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _store = new ConfigFolderSecretsStore(configRoot);
        _loader = new ConfigurationDirectoryLoader(configRoot, _store);
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog(
            [
                new TestPlugin(Manifest(PluginA, [PluginPermissionIds.PluginSecrets], ["api_token", "login"])),
                new TestPlugin(Manifest(PluginB, [PluginPermissionIds.PluginSecrets], ["api_token"])),
                new TestPlugin(Manifest("tuvima.test.secrets-nopermission", [], ["api_token"])),
            ],
            new PluginSettingsService(_loader),
            _loader,
            AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        foreach (var plugin in _catalog.List())
        {
            _catalog.SetEnabled(plugin.Manifest.Id, true);
        }
    }

    [Fact]
    public async Task Secrets_RefusedWithoutThePermission()
    {
        await _store.SetAsync(SecretOwner.Plugin("tuvima.test.secrets-nopermission"), Values("api_token", SecretValue));
        var secrets = new BoundPluginSecrets("tuvima.test.secrets-nopermission", new PluginPermissionGate(_catalog), _store);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secrets.GetAsync("api_token"));
    }

    [Fact]
    public async Task Secrets_RefusedWhenThePluginIsDisabled()
    {
        await _store.SetAsync(SecretOwner.Plugin(PluginA), Values("api_token", SecretValue));
        var secrets = new BoundPluginSecrets(PluginA, new PluginPermissionGate(_catalog), _store);
        _catalog.SetEnabled(PluginA, false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secrets.GetAsync("api_token"));
    }

    [Fact]
    public async Task Secrets_ReturnNullForAnUndeclaredOrUnsetName()
    {
        await _store.SetAsync(SecretOwner.Plugin(PluginA), Values("api_token", SecretValue, "undeclared", "hidden"));
        var secrets = new BoundPluginSecrets(PluginA, new PluginPermissionGate(_catalog), _store);

        Assert.Null(await secrets.GetAsync("undeclared"));
        Assert.Null(await secrets.GetAsync("login"));
        Assert.Null(await secrets.GetAsync(""));
    }

    [Fact]
    public async Task Secrets_ReadTheirOwnDeclaredValue_AndNeverAnotherPluginsValue()
    {
        await _store.SetAsync(SecretOwner.Plugin(PluginA), Values("api_token", SecretValue));
        await _store.SetAsync(SecretOwner.Plugin(PluginB), Values("api_token", "value-for-b"));
        var gate = new PluginPermissionGate(_catalog);

        Assert.Equal(SecretValue, await new BoundPluginSecrets(PluginA, gate, _store).GetAsync("api_token"));
        Assert.Equal("value-for-b", await new BoundPluginSecrets(PluginB, gate, _store).GetAsync("api_token"));
    }

    [Fact]
    public async Task ProviderSecrets_AreNotVisibleToAPluginWithTheSameName()
    {
        await _store.SetAsync(SecretOwner.Provider(PluginA), Values("api_token", "provider-value"));
        var secrets = new BoundPluginSecrets(PluginA, new PluginPermissionGate(_catalog), _store);

        Assert.Null(await secrets.GetAsync("api_token"));
    }

    [Fact]
    public void Manifest_DeclaredSecrets_ReadFromAndWrittenToJson()
    {
        var json = JsonSerializer.Serialize(Manifest(PluginA, [PluginPermissionIds.PluginSecrets], ["api_token"]));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json)!;

        var declared = Assert.Single(manifest.Secrets);
        Assert.Equal("api_token", declared.Name);
        Assert.Equal("Label api_token", declared.Label);
        Assert.Contains("\"secrets\"", json, StringComparison.Ordinal);
        Assert.Contains(PluginPermissionIds.PluginSecrets, PluginPermissionIds.All);
    }

    [Fact]
    public void Routes_AreAdministratorOnly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<PluginCatalog>(_ => null!);
        builder.Services.AddSingleton<PluginDirectoryService>(_ => null!);
        builder.Services.AddSingleton<IPluginExecutionContextFactory>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<PluginScheduledSegmentService>(_ => null!);
        using var app = builder.Build();
        app.MapPluginEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        foreach (var (route, method) in new[]
                 {
                     ("/plugins/{pluginId}/secrets", "GET"),
                     ("/plugins/{pluginId}/secrets/{name}", "PUT"),
                     ("/plugins/{pluginId}/secrets/{name}", "DELETE"),
                 })
        {
            var endpoint = Assert.Single(endpoints, candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                metadata => metadata.Policy == AuthPolicies.Administrator);
        }
    }

    [Fact]
    public async Task Administrator_CanSetListAndRemoveASecret_WithoutTheValueEverComingBack()
    {
        var logs = new SpyLoggerFactory();

        var set = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            PluginA, "api_token", new PluginSecretWriteRequest { Value = SecretValue }, _catalog, _store, logs, default));
        Assert.Equal(StatusCodes.Status204NoContent, set.Status);
        Assert.Equal(SecretValue, await _store.GetAsync(SecretOwner.Plugin(PluginA), "api_token"));

        var listed = await Execute(await PluginEndpoints.ListPluginSecretsAsync(PluginA, _catalog, _store, logs, default));
        Assert.Equal(StatusCodes.Status200OK, listed.Status);
        Assert.DoesNotContain(SecretValue, listed.Body, StringComparison.Ordinal);
        var statuses = JsonSerializer.Deserialize<List<PluginSecretStatusResponse>>(listed.Body)!;
        Assert.Equal(["api_token", "login"], statuses.Select(s => s.Name));
        Assert.True(statuses[0].Configured);
        Assert.False(statuses[1].Configured);
        Assert.Equal("Label api_token", statuses[0].Label);

        var summary = await Execute(Results.Ok(PluginEndpoints.ToDto(_catalog.Get(PluginA)!, configuredSecrets: 1)));
        Assert.DoesNotContain(SecretValue, summary.Body, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(summary.Body))
        {
            Assert.Equal(2, document.RootElement.GetProperty("declared_secrets").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("configured_secrets").GetInt32());
        }

        var removed = await Execute(await PluginEndpoints.RemovePluginSecretAsync(PluginA, "api_token", _catalog, _store, logs, default));
        Assert.Equal(StatusCodes.Status204NoContent, removed.Status);
        Assert.Null(await _store.GetAsync(SecretOwner.Plugin(PluginA), "api_token"));
    }

    [Fact]
    public async Task SetSecret_RefusesUndeclaredNamesEmptyValuesAndUnknownPlugins()
    {
        var logs = new SpyLoggerFactory();

        var undeclared = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            PluginA, "not_declared", new PluginSecretWriteRequest { Value = SecretValue }, _catalog, _store, logs, default));
        var empty = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            PluginA, "api_token", new PluginSecretWriteRequest { Value = "  " }, _catalog, _store, logs, default));
        var unknown = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            "tuvima.test.unknown", "api_token", new PluginSecretWriteRequest { Value = SecretValue }, _catalog, _store, logs, default));

        Assert.Equal(StatusCodes.Status400BadRequest, undeclared.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, empty.Status);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Status);
        Assert.DoesNotContain(SecretValue, undeclared.Body + empty.Body + unknown.Body, StringComparison.Ordinal);
        Assert.Empty(await _store.GetAllAsync(SecretOwner.Plugin(PluginA)));
    }

    [Fact]
    public async Task EndpointsAndStore_NeverLogTheSecretValue()
    {
        var logs = new SpyLoggerFactory();

        await PluginEndpoints.SetPluginSecretAsync(
            PluginA, "api_token", new PluginSecretWriteRequest { Value = SecretValue }, _catalog, _store, logs, default);
        await PluginEndpoints.SetPluginSecretAsync(
            PluginA, "not_declared", new PluginSecretWriteRequest { Value = SecretValue }, _catalog, _store, logs, default);
        await PluginEndpoints.RemovePluginSecretAsync(PluginA, "api_token", _catalog, _store, logs, default);
        await _store.SetAsync(SecretOwner.Plugin(PluginA), Values("api_token", SecretValue));
        File.WriteAllText(
            Path.Combine(_root, "config", "secrets", "plugins", $"{PluginB}.json"),
            "{\"api_token\": \"" + SecretValue + "\" broken");
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetAllAsync(SecretOwner.Plugin(PluginB)));
        // An unreadable secrets file is reported by plugin id and error type only.
        var configured = await PluginEndpoints.CountConfiguredSecretsAsync(_catalog.Get(PluginB)!, _store, logs, default);

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(SecretValue, StringComparison.Ordinal));
        Assert.Equal(0, configured);
        Assert.Contains(logs.Entries, entry => entry.Contains(PluginB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADamagedSecretsFile_IsAPlainConflict_OnListSetAndRemove_LoggedByPluginIdOnly()
    {
        var logs = new SpyLoggerFactory();
        await _store.SetAsync(SecretOwner.Plugin(PluginB), Values("api_token", SecretValue));
        File.WriteAllText(
            Path.Combine(_root, "config", "secrets", "plugins", $"{PluginB}.json"),
            "{\"api_token\": \"" + SecretValue + "\" broken");

        var listed = await Execute(await PluginEndpoints.ListPluginSecretsAsync(PluginB, _catalog, _store, logs, default));
        var set = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            PluginB, "api_token", new PluginSecretWriteRequest { Value = "another-value-77" }, _catalog, _store, logs, default));
        var removed = await Execute(await PluginEndpoints.RemovePluginSecretAsync(PluginB, "api_token", _catalog, _store, logs, default));

        Assert.All(new[] { listed, set, removed }, outcome =>
        {
            Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
            Assert.Contains("could not be read or saved", outcome.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, outcome.Body, StringComparison.Ordinal);
        });
        Assert.NotEmpty(logs.Entries);
        Assert.Contains(logs.Entries, entry => entry.Contains(PluginB, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(SecretValue, StringComparison.Ordinal) || entry.Contains("another-value-77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APluginIdTheSecretsStoreCannotUse_IsABadRequest_OnListSetAndRemove()
    {
        var logs = new SpyLoggerFactory();
        var badId = "Bad/Id";
        var catalog = new PluginCatalog(
            [new TestPlugin(Manifest(badId, [PluginPermissionIds.PluginSecrets], ["api_token"]))],
            new PluginSettingsService(_loader),
            _loader,
            AllowAllPluginApprovals.Instance,
            NullLogger<PluginCatalog>.Instance);

        var listed = await Execute(await PluginEndpoints.ListPluginSecretsAsync(badId, catalog, _store, logs, default));
        var set = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            badId, "api_token", new PluginSecretWriteRequest { Value = SecretValue }, catalog, _store, logs, default));
        var removed = await Execute(await PluginEndpoints.RemovePluginSecretAsync(badId, "api_token", catalog, _store, logs, default));

        Assert.All(new[] { listed, set, removed }, outcome =>
        {
            Assert.Equal(StatusCodes.Status400BadRequest, outcome.Status);
            Assert.DoesNotContain(SecretValue, outcome.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UninstallingAPlugin_RemovesItsSecretsFile()
    {
        var pluginRoot = Path.Combine(_root, "library", ".data", "plugins", "sample-plugin");
        Directory.CreateDirectory(pluginRoot);
        var testAssemblyPath = Assembly.GetExecutingAssembly().Location;
        File.Copy(testAssemblyPath, Path.Combine(pluginRoot, Path.GetFileName(testAssemblyPath)));
        File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"), """
            {
              "id": "tuvima.test.dynamic",
              "name": "Dynamic Test",
              "version": "1.0.0",
              "description": "Loaded from plugin.json.",
              "entry_assembly": "MediaEngine.Api.Tests.dll",
              "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
              "permissions": ["secrets.plugin"],
              "secrets": [{ "name": "api_token", "label": "Token", "description": "Key from the service." }]
            }
            """);
        var catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        var owner = SecretOwner.Plugin("tuvima.test.dynamic");
        await _store.SetAsync(owner, Values("api_token", SecretValue));
        var secretsFile = Path.Combine(_root, "config", "secrets", "plugins", "tuvima.test.dynamic.json");
        Assert.True(File.Exists(secretsFile));

        var deleted = await Execute(await PluginEndpoints.DeletePluginAsync("tuvima.test.dynamic", catalog, _store, default));

        Assert.Equal(StatusCodes.Status200OK, deleted.Status);
        Assert.False(File.Exists(secretsFile));
        Assert.Empty(await _store.GetAllAsync(owner));
    }

    [Fact]
    public void Manifest_WithBadSecretDeclarations_IsReportedAsAProblem()
    {
        var duplicate = Manifest(PluginA, [PluginPermissionIds.PluginSecrets], ["api_token", "API_TOKEN"]);
        var blank = Manifest(PluginA, [PluginPermissionIds.PluginSecrets], [" "]);
        var badId = Manifest("Bad/Id", [PluginPermissionIds.PluginSecrets], ["api_token"]);

        Assert.Contains("more than once", PluginCompatibility.ValidateManifest(duplicate), StringComparison.Ordinal);
        Assert.Contains("invalid name", PluginCompatibility.ValidateManifest(blank), StringComparison.Ordinal);
        Assert.Contains("lowercase", PluginCompatibility.ValidateManifest(badId), StringComparison.Ordinal);
        Assert.Null(PluginCompatibility.ValidateManifest(Manifest(PluginA, [PluginPermissionIds.PluginSecrets], ["api_token"])));
    }

    public void Dispose()
    {
        _loader.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Dictionary<string, string> Values(params string[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            values[pairs[i]] = pairs[i + 1];
        }

        return values;
    }

    private static PluginManifest Manifest(string id, IReadOnlyList<string> permissions, IReadOnlyList<string> secretNames) => new()
    {
        Id = id,
        Name = $"Secrets test {id}",
        Permissions = permissions,
        Secrets = secretNames.Select(name => new PluginSecretDeclaration
        {
            Name = name,
            Label = $"Label {name}",
            Description = $"Description {name}",
        }).ToList(),
    };

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class TestPlugin(PluginManifest manifest) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = manifest;
        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }

    /// <summary>Hand-written spy: records every message, structured value and exception text it is given.</summary>
    private sealed class SpyLoggerFactory : ILoggerFactory
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new SpyLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class SpyLogger(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var pair in values)
                    {
                        text.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
                    }
                }

                if (exception is not null)
                {
                    text.Append(' ').Append(exception);
                }

                lock (entries)
                {
                    entries.Add(text.ToString());
                }
            }
        }
    }
}
