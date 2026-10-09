using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Owns provider credential validation, non-mutating authentication probes,
/// atomic persistence, rotation, removal, and live adapter refresh.
/// Credential material is never included in result DTOs or log messages.
/// </summary>
public sealed class ProviderCredentialService
{
    private static readonly JsonSerializerOptions SecretJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IConfigurationLoader _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReadOnlyList<IProviderCredentialConsumer> _consumers;
    private readonly IProviderConnectionCheckRepository? _connectionChecks;

    public ProviderCredentialService(
        IConfigurationLoader configuration,
        IHttpClientFactory httpClientFactory,
        IEnumerable<IExternalMetadataProvider> metadataProviders,
        IEnumerable<ITextTrackProvider> textTrackProviders,
        IProviderConnectionCheckRepository? connectionChecks = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _consumers = metadataProviders.Cast<object>()
            .Concat(textTrackProviders)
            .OfType<IProviderCredentialConsumer>()
            .ToList();
        _connectionChecks = connectionChecks;
    }

    /// <summary>Checks the provider configuration currently used by the Engine.</summary>
    public async Task<ProviderCredentialOperationResultDto> TestConfiguredAsync(
        string providerName, CancellationToken ct = default)
    {
        var provider = _configuration.LoadProvider(providerName);
        if (provider is null)
        {
            return Failure("provider_not_found", "The provider is not available.");
        }
        if (!provider.Enabled)
        {
            return Failure("disabled", "Enable the provider before testing its connection.");
        }

        ProviderCredentialOperationResultDto result;
        if (provider.Onboarding?.AuthenticationProbe is null
            && provider.Endpoints.Count == 0)
        {
            result = new ProviderCredentialOperationResultDto
            {
                Success = true,
                Status = "local_ready",
                Message = "This local provider does not require an external connection.",
            };
        }
        else
        {
            result = await TestAsync(providerName, new Dictionary<string, string>(), ct)
                .ConfigureAwait(false);
        }

        await CacheConfiguredResultAsync(providerName, result, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<ProviderCredentialOperationResultDto> TestAsync(
        string providerName,
        IReadOnlyDictionary<string, string> submittedCredentials,
        CancellationToken ct = default)
    {
        var provider = _configuration.LoadProvider(providerName);
        if (provider is null)
        {
            return Failure("provider_not_found", "The provider is not available.");
        }

        var fields = provider.Onboarding?.Credentials ?? [];
        var effectiveCredentials = BuildEffectiveCredentials(provider, fields, submittedCredentials);
        var fieldErrors = ValidateCredentials(fields, submittedCredentials, effectiveCredentials);
        if (fieldErrors.Count > 0)
        {
            return new ProviderCredentialOperationResultDto
            {
                Status = "invalid_format",
                Message = "One or more credential fields are missing or have an invalid format.",
                FieldErrors = fieldErrors,
            };
        }

        var probe = provider.Onboarding?.AuthenticationProbe;
        if (probe is null)
        {
            return Failure("probe_unavailable", "This provider does not declare an authentication check.");
        }

        if (!provider.Endpoints.TryGetValue("api", out var baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            return Failure("probe_unavailable", "This provider does not have a valid API endpoint.");
        }

        if (!probe.Path.StartsWith("/", StringComparison.Ordinal)
            || probe.Path.StartsWith("//", StringComparison.Ordinal)
            || probe.Path.Contains('#')
            || probe.Path.Contains('\\')
            || Uri.TryCreate(probe.Path, UriKind.Absolute, out _))
        {
            return Failure("probe_unavailable", "This provider has an invalid authentication check path.");
        }

        var pathParts = probe.Path.Split('?', 2);
        var requestUriBuilder = new UriBuilder(baseUri)
        {
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}{pathParts[0]}",
            Query = pathParts.Length == 2 ? pathParts[1] : string.Empty,
            Fragment = string.Empty,
        };
        var requestUri = requestUriBuilder.Uri;
        if (string.Equals(provider.Name, "subdl", StringComparison.OrdinalIgnoreCase)
            && (requestUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(requestUri.Host, "api.subdl.com", StringComparison.OrdinalIgnoreCase)
                || requestUri.Port != 443
                || !string.IsNullOrEmpty(requestUri.UserInfo)
                || requestUri.AbsolutePath != "/api/v2/me"
                || !string.Equals(probe.Method, "GET", StringComparison.OrdinalIgnoreCase)))
        {
            return Failure("probe_unavailable", "SubDL has an invalid account check endpoint.");
        }

        using var request = new HttpRequestMessage(new HttpMethod(probe.Method), requestUri);
        if (string.Equals(provider.Name, "tvdb", StringComparison.OrdinalIgnoreCase))
        {
            // TVDB v4 exchanges the project key and optional subscriber PIN for
            // a bearer token. Neither credential belongs in a URL or header.
            var login = new Dictionary<string, string>
            {
                ["apikey"] = effectiveCredentials["api_key"],
            };
            if (effectiveCredentials.TryGetValue("pin", out var pin) && !string.IsNullOrWhiteSpace(pin))
            {
                login["pin"] = pin;
            }
            request.Content = JsonContent.Create(login);
        }
        else
        {
            ApplyAuthentication(request, provider, effectiveCredentials);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClientFactory.CreateClient(provider.Name)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            if (probe.SuccessStatusCodes.Contains(statusCode))
            {
                if (string.Equals(provider.Name, "tvdb", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body?["data"]?["token"]?.GetValue<string>()))
                    {
                        return Failure("invalid_credential", "TheTVDB did not return a usable access token.");
                    }
                }
                if (string.Equals(provider.Name, "subdl", StringComparison.OrdinalIgnoreCase))
                {
                    var account = await ReadSubdlAccountAsync(response, ct).ConfigureAwait(false);
                    if (account is null)
                    {
                        return Failure("provider_outage", "SubDL returned an unreadable account response.",
                                (int)stopwatch.ElapsedMilliseconds);
                    }

                    return new ProviderCredentialOperationResultDto
                    {
                        Success = true,
                        Status = "valid",
                        Message = account,
                        ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    };
                }
                return new ProviderCredentialOperationResultDto
                {
                    Success = true,
                    Status = "valid",
                    Message = fields.Count == 0
                        ? "The provider responded to a read-only connection check."
                        : "The provider accepted the credentials.",
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                };
            }

            var classified = ClassifyStatus(response.StatusCode);
            if (string.Equals(provider.Name, "subdl", StringComparison.OrdinalIgnoreCase)
                && response.StatusCode == HttpStatusCode.TooManyRequests
                && await HasSubdlQuotaErrorAsync(response, ct).ConfigureAwait(false))
            {
                classified = Failure("quota_exhausted", "The SubDL account quota is exhausted.");
            }
            classified.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
            classified.RetryAfterSeconds = ResolveRetryAfterSeconds(response)
                ?? (string.Equals(provider.Name, "subdl", StringComparison.OrdinalIgnoreCase)
                    ? ResolveSubdlResetSeconds(response) : null);
            return classified;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(
                "connectivity_failure",
                "The authentication check timed out before the provider responded.",
                (int)stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode is { } statusCode)
            {
                var classified = ClassifyStatus(statusCode);
                classified.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                return classified;
            }

            var message = ex.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied }
                ? "The Engine's network access was blocked by Windows or its sandbox. Restart the Engine with network access and try again."
                : "The Engine could not establish a connection to the provider.";
            return Failure("connectivity_failure", message, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (System.Text.Json.JsonException)
        {
            return Failure("provider_outage",
                "The provider returned an unreadable authentication response.",
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<ProviderCredentialOperationResultDto> SaveAsync(
        string providerName,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken ct = default)
    {
        var result = await TestAsync(providerName, credentials, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        var provider = _configuration.LoadProvider(providerName)!;
        var fields = provider.Onboarding!.Credentials;
        var effectiveCredentials = BuildEffectiveCredentials(provider, fields, credentials);
        var normalized = effectiveCredentials
            .Where(pair => fields.Any(field =>
                string.Equals(field.Key, pair.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

        WriteSecretFile(provider, normalized);
        ApplyRuntimeCredentials(provider.Name, normalized.ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value,
            StringComparer.OrdinalIgnoreCase));

        await CacheConfiguredResultAsync(provider.Name, result, ct).ConfigureAwait(false);

        return result.WithMessage(string.Equals(provider.Name, "subdl", StringComparison.OrdinalIgnoreCase)
            ? $"SubDL key verified and saved. {result.Message}"
            : "The provider credentials were verified and saved.");
    }

    public ProviderCredentialOperationResultDto Remove(string providerName)
    {
        var provider = _configuration.LoadProvider(providerName);
        if (provider is null)
        {
            return Failure("provider_not_found", "The provider is not available.");
        }

        var fields = provider.Onboarding?.Credentials ?? [];
        var retained = fields
            .Where(field => !IsUserSupplied(field))
            .Select(field => (field.Key, Value: GetStoredCredential(provider, field.Key)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!,
                StringComparer.OrdinalIgnoreCase);

        var path = GetSecretPath(provider);
        if (retained.Count > 0)
        {
            WriteSecretFile(provider, retained);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        ApplyRuntimeCredentials(provider.Name, retained.ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value,
            StringComparer.OrdinalIgnoreCase));
        _connectionChecks?.DeleteAsync(provider.Name).GetAwaiter().GetResult();
        return new ProviderCredentialOperationResultDto
        {
            Success = true,
            Status = "removed",
            Message = "The provider credentials were removed.",
        };
    }

    private static Dictionary<string, string> BuildEffectiveCredentials(
        ProviderConfiguration provider,
        IReadOnlyList<ProviderCredentialFieldConfiguration> fields,
        IReadOnlyDictionary<string, string> submitted)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (submitted.TryGetValue(field.Key, out var submittedValue))
            {
                if (IsUserSupplied(field))
                {
                    result[field.Key] = submittedValue.Trim();
                    continue;
                }
            }

            var stored = GetStoredCredential(provider, field.Key);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                result[field.Key] = stored;
            }
        }

        return result;
    }

    private static Dictionary<string, string> ValidateCredentials(
        IReadOnlyList<ProviderCredentialFieldConfiguration> fields,
        IReadOnlyDictionary<string, string> submitted,
        IReadOnlyDictionary<string, string> effective)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowedKeys = fields
            .Where(IsUserSupplied)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in submitted.Keys.Where(key => !allowedKeys.Contains(key)))
        {
            errors[key] = fields.Any(field =>
                string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
                ? "This credential is managed by the application and cannot be changed here."
                : "This credential field is not supported by the provider.";
        }

        foreach (var field in fields)
        {
            effective.TryGetValue(field.Key, out var value);
            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                errors[field.Key] = "This credential is required.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (field.MinimumLength.HasValue && value.Length < field.MinimumLength.Value)
            {
                errors[field.Key] = $"Enter at least {field.MinimumLength.Value} characters.";
            }
            else if (field.MaximumLength.HasValue && value.Length > field.MaximumLength.Value)
            {
                errors[field.Key] = $"Enter no more than {field.MaximumLength.Value} characters.";
            }
            else if (!string.IsNullOrWhiteSpace(field.ValidationPattern)
                     && !Regex.IsMatch(value, field.ValidationPattern, RegexOptions.CultureInvariant, PatternTimeout))
            {
                errors[field.Key] = field.FormatHint ?? "The credential format is invalid.";
            }
        }

        return errors;
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        ProviderConfiguration provider,
        IReadOnlyDictionary<string, string> credentials)
    {
        var http = provider.HttpClient;
        if (http is null)
        {
            return;
        }

        credentials.TryGetValue("api_key", out var apiKey);
        credentials.TryGetValue("api_key_override", out var apiKeyOverride);
        var effectiveApiKey = !string.IsNullOrWhiteSpace(apiKeyOverride) ? apiKeyOverride : apiKey;
        switch (http.ApiKeyDelivery?.ToLowerInvariant())
        {
            case "bearer" when !string.IsNullOrWhiteSpace(effectiveApiKey):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveApiKey);
                break;
            case "header" when !string.IsNullOrWhiteSpace(effectiveApiKey):
                request.Headers.TryAddWithoutValidation(http.ApiKeyParamName ?? "Api-Key", effectiveApiKey);
                break;
            case "query" when !string.IsNullOrWhiteSpace(effectiveApiKey):
                var builder = new UriBuilder(request.RequestUri!);
                var separator = string.IsNullOrEmpty(builder.Query) ? string.Empty : "&";
                builder.Query = builder.Query.TrimStart('?')
                    + separator
                    + Uri.EscapeDataString(http.ApiKeyParamName ?? "api_key")
                    + "="
                    + Uri.EscapeDataString(effectiveApiKey);
                request.RequestUri = builder.Uri;
                break;
            case "basic":
                credentials.TryGetValue("username", out var username);
                credentials.TryGetValue("password", out var password);
                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
                }
                break;
        }

        if (credentials.TryGetValue("client_key", out var clientKey)
            && !string.IsNullOrWhiteSpace(clientKey))
        {
            AppendQueryCredential(request, "client_key", clientKey);
        }

        if (credentials.TryGetValue("access_token", out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken)
            && request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    private static void AppendQueryCredential(HttpRequestMessage request, string key, string value)
    {
        var builder = new UriBuilder(request.RequestUri!);
        var separator = string.IsNullOrEmpty(builder.Query) ? string.Empty : "&";
        builder.Query = builder.Query.TrimStart('?')
            + separator
            + Uri.EscapeDataString(key)
            + "="
            + Uri.EscapeDataString(value);
        request.RequestUri = builder.Uri;
    }

    private static bool IsUserSupplied(ProviderCredentialFieldConfiguration field) =>
        !string.Equals(field.Ownership, "application_managed", StringComparison.OrdinalIgnoreCase);

    private static string? GetStoredCredential(ProviderConfiguration provider, string key) =>
        key.ToLowerInvariant() switch
        {
            "api_key" when string.Equals(provider.Name, "tmdb", StringComparison.OrdinalIgnoreCase) =>
                !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride)
                    ? provider.HttpClient.ApiKeyOverride : provider.HttpClient?.ApiKey,
            "api_key" => provider.HttpClient?.ApiKey,
            "api_key_override" => provider.HttpClient?.ApiKeyOverride,
            "client_key" => provider.HttpClient?.ClientKey,
            "username" => provider.HttpClient?.Username,
            "password" => provider.HttpClient?.Password,
            "access_token" => provider.HttpClient?.AccessToken,
            "pin" => provider.HttpClient?.Pin,
            _ => null,
        };

    private void WriteSecretFile(ProviderConfiguration provider, IReadOnlyDictionary<string, string> credentials)
    {
        var path = GetSecretPath(provider);
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(credentials, SecretJsonOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetSecretPath(ProviderConfiguration provider)
    {
        if (!string.Equals(Path.GetFileName(provider.Name), provider.Name, StringComparison.Ordinal)
            || provider.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("The provider has an invalid credential storage name.");
        }

        var directory = Path.GetFullPath(Path.Combine(_configuration.ConfigDirectoryPath, "secrets"));
        var path = Path.GetFullPath(Path.Combine(directory, $"{provider.Name}.json"));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The provider credential path escaped the secrets directory.");
        }

        return path;
    }

    private void ApplyRuntimeCredentials(string providerName, IReadOnlyDictionary<string, string?> credentials)
    {
        foreach (var consumer in _consumers.Where(consumer =>
                     string.Equals(consumer.Name, providerName, StringComparison.OrdinalIgnoreCase)))
        {
            consumer.ApplyCredentials(credentials);
        }
    }

    private static ProviderCredentialOperationResultDto ClassifyStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        401 or 403 =>
            Failure("invalid_credential", "The provider rejected the credentials."),
        429 =>
            Failure("rate_limited", "The provider rate limit was reached. Try again later."),
        451 =>
            Failure("region_restricted", "The provider is not available from this region."),
        >= 500 =>
            Failure("provider_outage", "The provider is currently unavailable."),
        _ => Failure("connectivity_failure", "The provider returned an unexpected response."),
    };

    private static int? ResolveRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter?.Date is { } date)
        {
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        return null;
    }

    private Task CacheConfiguredResultAsync(
        string providerName, ProviderCredentialOperationResultDto result, CancellationToken ct) =>
        _connectionChecks?.UpsertAsync(new ProviderConnectionCheck(
            providerName, result.Status, result.Message, DateTimeOffset.UtcNow,
            result.ResponseTimeMs), ct) ?? Task.CompletedTask;

    private static int? ResolveSubdlResetSeconds(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            || values.FirstOrDefault() is not { } rawReset)
        {
            return null;
        }

        long seconds;
        if (long.TryParse(rawReset, out var numericReset))
        {
            seconds = numericReset > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    ? numericReset - DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : numericReset;
        }
        else if (DateTimeOffset.TryParse(rawReset, out var dateReset))
        {
            seconds = (long)Math.Ceiling((dateReset - DateTimeOffset.UtcNow).TotalSeconds);
        }
        else
        {
            return null;
        }

        return seconds is > 0 and <= 604800 ? (int)seconds : null;
    }

    private static async Task<string?> ReadSubdlAccountAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        using var document = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("plan", out var plan)
            || plan.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var searchRemaining = ReadRemaining(usage, "search");
        var downloadRemaining = ReadRemaining(usage, "downloads");
        var quantities = new List<string>();
        if (searchRemaining is not null)
        {
            quantities.Add($"{searchRemaining.Value:N0} searches remaining");
        }
        if (downloadRemaining is not null)
        {
            quantities.Add($"{downloadRemaining.Value:N0} downloads remaining");
        }

        return quantities.Count == 0
            ? "SubDL accepted the key."
            : $"SubDL accepted the key: {string.Join("; ", quantities)}.";
    }

    private static int? ReadRemaining(JsonElement usage, string category)
    {
        if (!usage.TryGetProperty(category, out var bucket)
            || bucket.ValueKind != JsonValueKind.Object
            || !bucket.TryGetProperty("remaining", out var remaining)
            || remaining.ValueKind != JsonValueKind.Number
            || !remaining.TryGetInt32(out var count))
        {
            return null;
        }

        return count >= 0 ? count : null;
    }

    private static async Task<bool> HasSubdlQuotaErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
            return document is not null
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "quota_exceeded", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<JsonDocument?> ReadBoundedJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        const int maxBytes = 16 * 1024;
        if (response.Content.Headers.ContentLength is > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        return total > maxBytes
            ? null
            : JsonDocument.Parse(buffer.AsMemory(0, total), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static ProviderCredentialOperationResultDto Failure(
        string status,
        string message,
        int responseTimeMs = 0) => new()
        {
            Status = status,
            Message = message,
            ResponseTimeMs = responseTimeMs,
        };
}

file static class ProviderCredentialResultExtensions
{
    public static ProviderCredentialOperationResultDto WithMessage(
        this ProviderCredentialOperationResultDto result,
        string message)
    {
        result.Message = message;
        return result;
    }
}
