using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using MediaEngine.Api.Services;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class ProviderCredentialServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tuvima-provider-credentials",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TestAsync_RejectsInvalidFormatWithoutCallingProvider()
    {
        using var loader = CreateLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(loader, handler);

        var result = await service.TestAsync(
            "contract_provider",
            new Dictionary<string, string> { ["api_key"] = "not-a-key" });

        Assert.False(result.Success);
        Assert.Equal("invalid_format", result.Status);
        Assert.Contains("api_key", result.FieldErrors);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_credential")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "provider_outage")]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons, "region_restricted")]
    public async Task TestAsync_ClassifiesProviderFailures(HttpStatusCode statusCode, string expected)
    {
        using var loader = CreateLoader();
        var service = CreateService(loader, new StubHandler(statusCode));

        var result = await service.TestAsync("contract_provider", ValidCredentials('a'));

        Assert.False(result.Success);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(new string('a', 32), JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAsync_AppendsReadOnlyProbeBeneathVersionedApiPath()
    {
        using var loader = CreateLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(loader, handler);

        var result = await service.TestAsync("contract_provider", ValidCredentials('a'));

        Assert.True(result.Success);
        Assert.Equal(
            "https://provider.example/api/configuration?api_key=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            handler.LastRequestUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task SaveRotateAndRemove_AreAtomicAndRefreshRuntimeConsumer()
    {
        using var loader = CreateLoader();
        var consumer = new CredentialConsumer("contract_provider");
        var service = CreateService(loader, new StubHandler(HttpStatusCode.OK), consumer);
        var first = new string('a', 32);
        var second = new string('b', 32);

        var saved = await service.SaveAsync("contract_provider", ValidCredentials('a'));
        var secretPath = Path.Combine(_root, "secrets", "contract_provider.json");

        Assert.True(saved.Success);
        Assert.True(File.Exists(secretPath));
        Assert.False(File.Exists(secretPath + ".bak"));
        Assert.Equal(first, consumer.LastCredentials["api_key"]);
        Assert.DoesNotContain(first, JsonSerializer.Serialize(saved), StringComparison.Ordinal);

        var rotated = await service.SaveAsync("contract_provider", ValidCredentials('b'));

        Assert.True(rotated.Success);
        Assert.Contains(second, File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.DoesNotContain(first, File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.False(File.Exists(secretPath + ".bak"));
        Assert.Equal(second, consumer.LastCredentials["api_key"]);

        var removed = service.Remove("contract_provider");

        Assert.True(removed.Success);
        Assert.Equal("removed", removed.Status);
        Assert.False(File.Exists(secretPath));
        Assert.Null(consumer.LastCredentials.GetValueOrDefault("api_key"));
    }

    [Fact]
    public async Task FailedRotation_PreservesExistingCredential()
    {
        using var loader = CreateLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(loader, handler);
        await service.SaveAsync("contract_provider", ValidCredentials('a'));
        handler.StatusCode = HttpStatusCode.Unauthorized;

        var result = await service.SaveAsync("contract_provider", ValidCredentials('b'));
        var secretPath = Path.Combine(_root, "secrets", "contract_provider.json");

        Assert.False(result.Success);
        Assert.Contains(new string('a', 32), File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 32), File.ReadAllText(secretPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAsync_RejectsApplicationManagedCredentialSubmission()
    {
        using var loader = CreateApplicationManagedLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(loader, handler);

        var result = await service.TestAsync(
            "contract_provider",
            new Dictionary<string, string> { ["api_key"] = new string('x', 32) });

        Assert.False(result.Success);
        Assert.Equal("invalid_format", result.Status);
        Assert.Contains("managed by the application", result.FieldErrors["api_key"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SaveAndRemove_UserCredential_PreserveApplicationManagedCredential()
    {
        using var loader = CreateApplicationManagedLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var consumer = new CredentialConsumer("contract_provider");
        var service = CreateService(loader, handler, consumer);
        var clientKey = new string('c', 32);

        var saved = await service.SaveAsync(
            "contract_provider",
            new Dictionary<string, string> { ["client_key"] = clientKey });

        Assert.True(saved.Success);
        Assert.Contains("api_key=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", handler.LastRequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains($"client_key={clientKey}", handler.LastRequestUri?.Query, StringComparison.Ordinal);
        var secretPath = Path.Combine(_root, "secrets", "contract_provider.json");
        Assert.Contains(new string('a', 32), File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.Contains(clientKey, File.ReadAllText(secretPath), StringComparison.Ordinal);

        var removed = service.Remove("contract_provider");

        Assert.True(removed.Success);
        Assert.Contains(new string('a', 32), File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.DoesNotContain(clientKey, File.ReadAllText(secretPath), StringComparison.Ordinal);
        Assert.Equal(new string('a', 32), consumer.LastCredentials["api_key"]);
        Assert.Null(consumer.LastCredentials.GetValueOrDefault("client_key"));
    }

    [Fact]
    public async Task AdministratorApiKeyOverride_IsUsedWhileProvisionedKeyRemainsFallback()
    {
        using var loader = CreateApplicationManagedLoader();
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(loader, handler);
        var overrideKey = new string('d', 32);

        var result = await service.TestAsync(
            "contract_provider",
            new Dictionary<string, string> { ["api_key_override"] = overrideKey });

        Assert.True(result.Success);
        Assert.Contains($"api_key={overrideKey}", handler.LastRequestUri?.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=aaaaaaaa", handler.LastRequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubdlProbe_UsesV2AccountEndpointAndBearerHeader_WithoutSavingDuringTest()
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(HttpStatusCode.OK)
        {
            ResponseBody = """{"plan":{"is_pro":false,"name":"Free"},"usage":{"search":{"remaining":1988},"downloads":{"remaining":47}}}""",
        };
        var service = CreateService(loader, handler);
        var key = new string('x', 32);

        var result = await service.TestAsync("subdl", new Dictionary<string, string> { ["api_key"] = key });

        Assert.True(result.Success);
        Assert.Equal("https://api.subdl.com/api/v2/me", handler.LastRequestUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", key), handler.LastAuthorization);
        Assert.Contains("1,988 searches remaining", result.Message);
        Assert.Contains("47 downloads remaining", result.Message);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "secrets", "subdl.json")));
    }

    [Fact]
    public async Task SubdlSave_FailedProbeDoesNotReplaceExistingKey()
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(HttpStatusCode.OK)
        {
            ResponseBody = """{"plan":{"is_pro":false},"usage":{"search":{"remaining":10}}}""",
        };
        var service = CreateService(loader, handler);
        var firstKey = "first-personal-subdl-key";
        var replacement = "replacement-subdl-key";
        var saved = await service.SaveAsync("subdl", new Dictionary<string, string> { ["api_key"] = firstKey });
        Assert.True(saved.Success);
        Assert.Contains("10 searches remaining", saved.Message);

        handler.StatusCode = HttpStatusCode.Unauthorized;
        var rejected = await service.SaveAsync("subdl", new Dictionary<string, string> { ["api_key"] = replacement });

        Assert.Equal("invalid_credential", rejected.Status);
        var secretPath = Path.Combine(_root, "secrets", "subdl.json");
        Assert.Contains(firstKey, File.ReadAllText(secretPath));
        Assert.DoesNotContain(replacement, File.ReadAllText(secretPath));
        Assert.DoesNotContain(replacement, JsonSerializer.Serialize(rejected));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"quota_exceeded\"}}", "quota_exhausted")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limited\"}}", "rate_limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", "provider_outage")]
    public async Task SubdlProbe_ClassifiesQuotaAndAvailability(
        HttpStatusCode statusCode, string responseBody, string expectedStatus)
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(statusCode) { ResponseBody = responseBody };
        var service = CreateService(loader, handler);

        var result = await service.TestAsync("subdl", new Dictionary<string, string>
        {
            ["api_key"] = new string('x', 32),
        });

        Assert.False(result.Success);
        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public async Task SubdlProbe_DoesNotAcceptAnEmptyAccountResponse()
    {
        using var loader = CreateSubdlLoader();
        var service = CreateService(loader, new StubHandler(HttpStatusCode.OK) { ResponseBody = "{}" });

        var result = await service.SaveAsync("subdl", ValidCredentials('x'));

        Assert.Equal("provider_outage", result.Status);
        Assert.False(File.Exists(Path.Combine(_root, "secrets", "subdl.json")));
    }

    [Fact]
    public async Task ConfiguredSubdlCheck_UsesStoredKeyAndCachesOnlySafeResult()
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(HttpStatusCode.OK)
        {
            ResponseBody = """{"plan":{"name":"Free"},"usage":{"search":{"remaining":5}}}""",
        };
        var cache = new StubConnectionChecks();
        var service = new ProviderCredentialService(loader, new StubHttpClientFactory(handler), [], [], cache);
        var key = new string('x', 32);
        Assert.True((await service.SaveAsync("subdl", new Dictionary<string, string> { ["api_key"] = key })).Success);

        var checkedResult = await service.TestConfiguredAsync("subdl");

        Assert.True(checkedResult.Success);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", key), handler.LastAuthorization);
        Assert.Equal("valid", cache.Last?.Status);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(cache.Last), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInOnlineProviderCheck_UsesConfiguredProbeAndCachesResult()
    {
        using var loader = CreateManifestLoader("apple_api");
        var handler = new StubHandler(HttpStatusCode.OK);
        var cache = new StubConnectionChecks();
        var service = new ProviderCredentialService(loader, new StubHttpClientFactory(handler), [], [], cache);

        var result = await service.TestConfiguredAsync("apple_api");

        Assert.True(result.Success);
        Assert.Equal("https://itunes.apple.com/search?term=tuvima&limit=1",
            handler.LastRequestUri?.AbsoluteUri);
        Assert.Contains("read-only connection check", result.Message);
        Assert.Equal("valid", cache.Last?.Status);
    }

    [Fact]
    public async Task SubdlProbe_ExplainsBlockedEngineNetworkAccessWithoutExposingKey()
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(HttpStatusCode.OK)
        {
            Failure = new HttpRequestException("connection failed",
                new SocketException((int)SocketError.AccessDenied)),
        };
        var service = CreateService(loader, handler);
        var key = new string('x', 32);

        var result = await service.TestAsync("subdl", new Dictionary<string, string> { ["api_key"] = key });

        Assert.False(result.Success);
        Assert.Equal("connectivity_failure", result.Status);
        Assert.Contains("network access was blocked", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubdlQuotaError_UsesResetHeaderForRetryTime()
    {
        using var loader = CreateSubdlLoader();
        var handler = new StubHandler(HttpStatusCode.TooManyRequests)
        {
            ResponseBody = """{"error":{"code":"quota_exceeded"}}""",
            ResetSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120,
        };
        var service = CreateService(loader, handler);

        var result = await service.TestAsync("subdl", new Dictionary<string, string>
        {
            ["api_key"] = new string('x', 32),
        });

        Assert.Equal("quota_exhausted", result.Status);
        Assert.InRange(result.RetryAfterSeconds!.Value, 1, 120);
    }

    [Fact]
    public void Loader_CreatesSecretsDirectoryWhenExistingConfigRootIsEmpty()
    {
        Directory.CreateDirectory(_root);

        using var loader = new ConfigurationDirectoryLoader(_root);

        Assert.True(Directory.Exists(Path.Combine(_root, "secrets")));
    }

    private ConfigurationDirectoryLoader CreateLoader()
    {
        Directory.CreateDirectory(_root);
        var loader = new ConfigurationDirectoryLoader(_root);
        loader.SaveProvider(new ProviderConfiguration
        {
            Name = "contract_provider",
            DisplayName = "Contract Provider",
            ProviderId = "11111111-2222-3333-4444-555555555555",
            Enabled = true,
            RequiresApiKey = true,
            Endpoints = new Dictionary<string, string> { ["api"] = "https://provider.example/api" },
            HttpClient = new HttpClientConfig
            {
                ApiKeyDelivery = "query",
                ApiKeyParamName = "api_key",
            },
            Onboarding = new ProviderOnboardingConfiguration
            {
                Classification = "recommended",
                SupportedLanes = ["watch"],
                Credentials =
                [
                    new ProviderCredentialFieldConfiguration
                    {
                        Key = "api_key",
                        Label = "Provider API key",
                        Required = true,
                        MinimumLength = 32,
                        MaximumLength = 32,
                        ValidationPattern = "^[a-f0-9]{32}$",
                    },
                ],
                AuthenticationProbe = new ProviderAuthenticationProbeConfiguration
                {
                    Path = "/configuration",
                    Method = "GET",
                    SuccessStatusCodes = [200],
                },
            },
        });
        return loader;
    }

    private ConfigurationDirectoryLoader CreateApplicationManagedLoader()
    {
        var loader = CreateLoader();
        var provider = loader.LoadProvider("contract_provider")!;
        provider.HttpClient!.ApiKey = new string('a', 32);
        provider.Onboarding!.Credentials[0].Ownership = "application_managed";
        provider.Onboarding.Credentials[0].Purpose = "api_key";
        provider.Onboarding.Credentials.Add(new ProviderCredentialFieldConfiguration
        {
            Key = "client_key",
            Label = "Personal client key",
            Ownership = "user_supplied",
            Purpose = "client_key",
            Required = false,
            MinimumLength = 32,
            MaximumLength = 32,
        });
        provider.Onboarding.Credentials.Add(new ProviderCredentialFieldConfiguration
        {
            Key = "api_key_override",
            Label = "Optional application key override",
            Ownership = "user_supplied",
            Purpose = "api_key",
            Required = false,
            MinimumLength = 32,
            MaximumLength = 32,
        });
        loader.SaveProvider(provider);
        return loader;
    }

    private ConfigurationDirectoryLoader CreateSubdlLoader()
        => CreateManifestLoader("subdl");

    private ConfigurationDirectoryLoader CreateManifestLoader(string providerName)
    {
        Directory.CreateDirectory(_root);
        var loader = new ConfigurationDirectoryLoader(_root);
        var path = Path.Combine(FindRepoRoot(), "config", "providers", $"{providerName}.json");
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        File.Copy(path, Path.Combine(_root, "providers", $"{providerName}.json"));
        return loader;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static ProviderCredentialService CreateService(
        ConfigurationDirectoryLoader loader,
        HttpMessageHandler handler,
        params IExternalMetadataProvider[] providers) => new(
            loader,
            new StubHttpClientFactory(handler),
            providers,
            []);

    private static Dictionary<string, string> ValidCredentials(char value) =>
        new(StringComparer.OrdinalIgnoreCase) { ["api_key"] = new string(value, 32) };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubConnectionChecks : IProviderConnectionCheckRepository
    {
        public ProviderConnectionCheck? Last { get; private set; }
        public Task<IReadOnlyList<ProviderConnectionCheck>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderConnectionCheck>>(Last is null ? [] : [Last]);
        public Task UpsertAsync(ProviderConnectionCheck check, CancellationToken ct = default)
        {
            Last = check;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string providerName, CancellationToken ct = default)
        {
            Last = null;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = statusCode;
        public string? ResponseBody { get; set; }
        public long? ResetSeconds { get; set; }
        public Exception? Failure { get; set; }
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            if (Failure is not null)
                return Task.FromException<HttpResponseMessage>(Failure);
            var response = new HttpResponseMessage(StatusCode);
            if (ResponseBody is not null)
                response.Content = new StringContent(ResponseBody);
            if (ResetSeconds is not null)
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", ResetSeconds.Value.ToString());
            return Task.FromResult(response);
        }
    }

    private sealed class CredentialConsumer(string name) : IExternalMetadataProvider, IProviderCredentialConsumer
    {
        public string Name { get; } = name;
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Dictionary<string, string?> LastCredentials { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;
        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderClaim>>([]);
        public void ApplyCredentials(IReadOnlyDictionary<string, string?> credentials) =>
            LastCredentials = new Dictionary<string, string?>(credentials, StringComparer.OrdinalIgnoreCase);
    }
}
