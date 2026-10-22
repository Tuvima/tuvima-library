using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>
/// Puts the metadata sources of switched-on JSON plugins into the providers folder, and takes them out again
/// (Developer tools spec WP20). A plugin only suggests: the Library sets the weight, the order and the identity of every
/// source it takes in, so a plugin can supply data into the Priority Cascade but never change how the cascade ranks it or
/// pose as another source. The sources are read when the Engine starts, the same as the ones that ship with the Library.
/// </summary>
public sealed partial class PluginMetadataSourceFiles(IConfigurationLoader loader, ILogger<PluginMetadataSourceFiles> logger)
{
    /// <summary>The weight every plugin-supplied source gets; below the sources that ship with the Library (1.0).</summary>
    public const double LibraryWeight = 0.5;

    /// <summary>Plugin sources register after every source that ships with the Library, so they answer last.</summary>
    public const int FirstRegistrationOrder = 1000;

    /// <summary>The most confidence a plugin's mapping may claim, so a plugin's answers never outrank a source that ships with the Library.</summary>
    public const double MaxClaimConfidence = 0.6;

    private static readonly JsonSerializerOptions Json = new() { AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Compare = new();

    /// <summary>
    /// Makes the providers folder hold exactly the sources of the plugins that are switched on. Returns true when a file was
    /// added, changed or removed (the Engine must restart before the change takes effect).
    /// </summary>
    public bool Reconcile(IEnumerable<PluginRegistration> registrations)
    {
        var changed = false;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = FirstRegistrationOrder;
        try
        {
            foreach (var registration in registrations.Where(IsSourcePlugin))
            {
                var parsed = DeclarativePluginParser.Parse(registration.Manifest);
                foreach (var source in parsed.Definition?.MetadataSources ?? [])
                {
                    try
                    {
                        var config = Normalise(registration.Manifest, source, order++, out var problem);
                        if (config is null)
                        {
                            logger.LogWarning("The metadata source {Source} of plugin {PluginId} was not taken in: {Problem}", source.Name, registration.Manifest.Id, problem);
                            continue;
                        }

                        if (!wanted.Add(config.Name))
                        {
                            logger.LogWarning("The metadata source {Source} of plugin {PluginId} was not taken in: another source already has the name {Name}", source.Name, registration.Manifest.Id, config.Name);
                            continue;
                        }

                        var existing = loader.LoadProvider(config.Name);
                        if (existing is not null)
                        {
                            // The person's own on/off choice stays; secrets are never part of the comparison.
                            config.Enabled = existing.Enabled;
                            existing.HttpClient?.ApiKey = null;
                            existing.HttpClient?.ApiKeyOverride = null;
                            existing.HttpClient?.ClientKey = null;
                        }

                        if (existing is null || JsonSerializer.Serialize(existing, Compare) != JsonSerializer.Serialize(config, Compare))
                        {
                            loader.SaveProvider(config);
                            changed = true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ConfigValidationException or ArgumentException)
                    {
                        logger.LogWarning(ex, "The metadata source {Source} of plugin {PluginId} could not be written to the providers folder", source.Name, registration.Manifest.Id);
                    }
                }
            }

            foreach (var stale in loader.LoadAllProviders().Where(provider => provider.PluginId is not null && provider.Name.StartsWith(NamePrefix, StringComparison.Ordinal) && !wanted.Contains(provider.Name)).ToList())
            {
                try
                {
                    loader.DeleteProvider(stale.Name);
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    logger.LogWarning(ex, "The metadata source file {Name} could not be removed from the providers folder", stale.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ConfigValidationException)
        {
            logger.LogWarning(ex, "The metadata sources of plugins could not be brought up to date in the providers folder");
        }

        return changed;
    }

    private const string NamePrefix = "plugin_";

    /// <summary>The name a plugin's source goes by once the Library has taken it in. The plugin's identity in the name is a hash of its id, so two different ids can never produce the same file name.</summary>
    public static string ProviderNameFor(string pluginId, string sourceName)
    {
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pluginId.ToLowerInvariant())))[..8].ToLowerInvariant();
        return $"{NamePrefix}{idHash}_{sourceName}";
    }

    private static bool IsSourcePlugin(PluginRegistration registration) =>
        registration is { Enabled: true, IsBuiltIn: false, LoadError: null }
        && registration.Manifest.Kind == PluginKind.Declarative
        && registration.Compatibility.IsCompatible;

    // What the plugin wrote is only a suggestion; everything that decides ranking, order, identity and secrets is set here.
    private static ProviderConfiguration? Normalise(PluginManifest manifest, DeclarativeMetadataSource source, int order, out string? problem)
    {
        problem = null;
        if (!manifest.Permissions.Contains(PluginPermissionIds.NetworkHttp, StringComparer.OrdinalIgnoreCase))
        {
            problem = "the plugin does not ask for the network.http permission";
            return null;
        }

        // Nothing the plugin wrote may name an address it did not declare, however it is spelled (a template, a follow-up, an
        // allowed-hosts list), and no mapping may claim more confidence than the Library allows.
        var node = JsonNode.Parse(source.Configuration.GetRawText());
        if (node is null)
        {
            problem = "it could not be read";
            return null;
        }

        if (FindUndeclaredAddress(node, manifest) is { } undeclared)
        {
            problem = $"it contacts {undeclared}, which the plugin did not declare under network";
            return null;
        }

        ClampConfidence(node);
        var config = node.Deserialize<ProviderConfiguration>(Json);
        if (config is null)
        {
            problem = "it could not be read";
            return null;
        }

        if (!config.AdapterType.Equals("config_driven", StringComparison.OrdinalIgnoreCase)
            && !config.AdapterType.Equals("text_track", StringComparison.OrdinalIgnoreCase))
        {
            problem = "only config_driven and text_track sources can be supplied by a plugin";
            return null;
        }

        foreach (var address in config.Endpoints.Values)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                || PluginNetworkRules.FindDestination(manifest.Network, uri.Host) is null)
            {
                problem = $"it contacts {address}, which the plugin did not declare under network";
                return null;
            }
        }

        config.Name = ProviderNameFor(manifest.Id, source.Name);
        config.PluginId = manifest.Id;
        config.ProviderId = StableGuid($"tuvima-plugin-source:{manifest.Id.ToLowerInvariant()}:{source.Name}").ToString();
        config.Enabled = true;
        config.Weight = LibraryWeight;
        config.FieldWeights = [];
        config.RegistrationOrder = order;
        config.RequiresApiKey = false;
        config.Onboarding = null;
        config.PreferredBridgeIds = null;
        config.HydrationStages = [2, 3];
        config.MaxConcurrency = Math.Clamp(config.MaxConcurrency, 1, 4);
        config.ThrottleMs = Math.Max(config.ThrottleMs, 250);
        if (config.UiMetadata is not null)
        {
            config.UiMetadata.SystemRole = null;
            config.UiMetadata.RequiredSystemProvider = false;
        }

        config.Icon = null;
        config.CustomIconName = null;
        if (config.HttpClient is not null)
        {
            config.HttpClient.ApiKey = null;
            config.HttpClient.ApiKeyOverride = null;
            config.HttpClient.ClientKey = null;
        }

        config.DisplayName ??= source.Name;
        return config;
    }

    private static string? FindUndeclaredAddress(JsonNode node, PluginManifest manifest)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is null)
                    {
                        continue;
                    }

                    if (key.Equals("allowed_hosts", StringComparison.OrdinalIgnoreCase) && child is JsonArray hosts)
                    {
                        foreach (var host in hosts.Select(h => h?.GetValue<string>() ?? ""))
                        {
                            if (PluginNetworkRules.FindDestination(manifest.Network, host) is null)
                            {
                                return host;
                            }
                        }
                    }
                    else if (FindUndeclaredAddress(child, manifest) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonArray array:
                return array.Select(child => child is null ? null : FindUndeclaredAddress(child, manifest)).FirstOrDefault(found => found is not null);
            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (Match match in AddressPattern().Matches(text))
                {
                    var host = match.Groups[1].Value;
                    if (PluginNetworkRules.FindDestination(manifest.Network, host) is null)
                    {
                        return match.Value;
                    }
                }

                return text.StartsWith("//", StringComparison.Ordinal) ? text : null;
            default:
                return null;
        }
    }

    private static void ClampConfidence(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (key.Equals("confidence", StringComparison.OrdinalIgnoreCase) && child is JsonValue value && value.TryGetValue<double>(out var confidence))
                    {
                        obj[key] = Math.Clamp(confidence, 0, MaxClaimConfidence);
                    }
                    else if (child is not null)
                    {
                        ClampConfidence(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array.Where(child => child is not null))
                {
                    ClampConfidence(child!);
                }

                break;
        }
    }

    [GeneratedRegex(@"https?://([^/\s?#:]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    private static Guid StableGuid(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
