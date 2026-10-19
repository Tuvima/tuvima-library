using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Services.Configuration;

public sealed class DashboardConfigurationReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _configDirectory;

    public DashboardConfigurationReader(string configDirectory)
    {
        _configDirectory = string.IsNullOrWhiteSpace(configDirectory) ? "config" : configDirectory;
    }

    public DashboardCoreConfiguration LoadCore()
    {
        var core = LoadJson<DashboardCoreConfiguration>("core.json") ?? new();
        var secrets = LoadJson<DashboardAuthProviderSecrets>(Path.Combine(".secrets", "auth-providers.json"));
        if (secrets is not null)
        {
            foreach (var provider in core.Auth.ExternalProviders)
            {
                if (secrets.Providers.TryGetValue(provider.Id, out var secret))
                {
                    provider.ClientSecret = secret.ClientSecret;
                }
            }
        }
        var emailSecrets = LoadJson<DashboardEmailSecrets>(Path.Combine(".secrets", "email.json"));
        if (emailSecrets is not null)
        {
            core.Auth.PasswordReset.Password = emailSecrets.SmtpPassword;
        }

        return core;
    }

    public PaletteConfiguration LoadPalette() =>
        LoadJson<PaletteConfiguration>(Path.Combine("ui", "palette.json")) ?? new();

    public ListenPlaybackClientSettings LoadPlaybackClientSettings() =>
        (LoadJson<ListenPlaybackClientSettings>(Path.Combine("ui", "playback-client.json")) ?? new()).Normalize();

    /// <summary>Where each Tuvima app can be installed (<c>app_store_links</c> in <c>ui/global.json</c>); every link is blank until an app is published.</summary>
    public AppStoreLinks LoadAppStoreLinks()
    {
        var links = LoadJson<UIGlobalSettings>(Path.Combine("ui", "global.json"))?.AppStoreLinks ?? new();
        return new AppStoreLinks(links.AppStore ?? string.Empty, links.GooglePlay ?? string.Empty, links.Roku ?? string.Empty);
    }

    public NetworkSettings LoadNetwork() =>
        LoadJson<NetworkSettings>("network.json") ?? new();

    private T? LoadJson<T>(string relativePath)
    {
        var path = Path.Combine(_configDirectory, relativePath);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Dashboard configuration file '{path}' is invalid at {ex.Path ?? "$"}. Fix the JSON shape or restore the .bak file.", ex);
        }
    }
}

public sealed class DashboardEmailSecrets
{
    [JsonPropertyName("smtp_password")]
    public string SmtpPassword { get; set; } = string.Empty;
}

public sealed class DashboardCoreConfiguration
{
    [JsonPropertyName("auth")]
    public AuthSettings Auth { get; set; } = new();
}

public sealed class DashboardAuthProviderSecrets
{
    [JsonPropertyName("providers")]
    public Dictionary<string, DashboardAuthProviderSecret> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DashboardAuthProviderSecret
{
    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;
}
