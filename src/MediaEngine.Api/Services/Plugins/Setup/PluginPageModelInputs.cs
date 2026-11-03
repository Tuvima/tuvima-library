using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>
/// Reads either plugin file format into the one <see cref="PluginPageModelInput"/> (Plugins and Integrations screens v5 D51): a
/// plugin manifest (and the manifests the compiled-in plugins build) on one side, a provider source file on the other. After this
/// step the page model builder cannot tell them apart.
/// </summary>
internal static class PluginPageModelInputs
{
    private static readonly JsonSerializerOptions Json = new();

    /// <summary>The standard words on the language choice every metadata source has.</summary>
    private static readonly (string Value, string Label)[] LanguageChoices =
    [
        ("source", "Source defaults"),
        ("localized", "Prefer localized metadata"),
        ("both", "Source + localized"),
    ];

    public static PluginPageModelInput FromManifest(
        PluginRegistration registration,
        PluginAccessDto access,
        IReadOnlySet<string> configuredCredentials,
        IReadOnlyDictionary<string, JsonElement> externalValues)
    {
        var manifest = registration.Manifest;
        var settings = PluginSettingsDescriptionReader.Read(registration.SettingsSchema);
        var declared = settings.Settings.Select(setting => setting.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = manifest.DefaultSettings.Keys
            .Where(key => !declared.Contains(key))
            .Select(key => new PluginSettingsNote(key, "settings_schema", $"The setting \"{key}\" has a default but is not described in settings_schema, so it is not shown."))
            .ToList();
        return new PluginPageModelInput
        {
            PluginId = manifest.Id,
            Name = manifest.Name,
            Definition = registration.ManifestPath is null ? "compiled" : "manifest",
            Version = manifest.Version,
            Description = manifest.Description,
            Kind = KindWord(manifest.Kind),
            TrustLevel = TrustWord(registration.Trust),
            FileName = registration.ManifestPath is null ? null : Path.GetFileName(registration.ManifestPath),
            Provides = manifest.Provides,
            MediaLabels = PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(manifest.MediaTypes)),
            Supplies = [.. manifest.Capabilities.Select(capability => capability.Name).Where(name => !string.IsNullOrWhiteSpace(name))],
            Access = access,
            Credentials = PluginCredentialMapper.FromManifest(manifest),
            Destinations = manifest.Network,
            Settings = settings,
            Values = registration.Settings,
            ConfiguredCredentials = configuredCredentials,
            ExternalValues = externalValues,
            ExtraNotes = notes,
        };
    }

    public static PluginPageModelInput FromSourceFile(
        ProviderConfiguration config,
        PluginSourceDto? sheet,
        bool alwaysOn,
        IReadOnlySet<string> configuredCredentials,
        IReadOnlyDictionary<string, JsonElement> externalValues)
    {
        var declared = PluginSettingsDescriptionReader.Read(config.SettingsSchema);
        var tunables = Tunables(config);
        var declaredKeys = declared.Settings.Select(setting => setting.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = declared with
        {
            Settings = [.. declared.Settings, .. tunables.Settings.Where(setting => !declaredKeys.Contains(setting.Key))],
        };

        var values = TunableValues(config);
        foreach (var (key, value) in externalValues)
        {
            values[key] = value;
        }

        var hosts = sheet?.SendsDataTo ?? config.Endpoints.Values
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sends = config.RequiresApiKey
            ? "What it is asked about (titles, names and identifiers) and your key"
            : "What it is asked about (titles, names and identifiers)";
        var summary = config.Onboarding?.Intro?.Summary ?? "";
        var defaultActions = new List<PluginDeclaredAction>();
        if (config.Onboarding?.AuthenticationProbe is not null)
        {
            defaultActions.Add(new PluginDeclaredAction(PluginDeclaredActions.TestConnection, PluginSettingsDescriptionReader.DefaultActionLabel(PluginDeclaredActions.TestConnection)));
        }

        if (!alwaysOn)
        {
            defaultActions.Add(new PluginDeclaredAction(PluginDeclaredActions.CheckNow, PluginSettingsDescriptionReader.DefaultActionLabel(PluginDeclaredActions.CheckNow)));
        }

        return new PluginPageModelInput
        {
            PluginId = config.Name,
            Name = config.DisplayName ?? config.Name,
            Definition = "source_file",
            Version = config.Version,
            Description = string.IsNullOrWhiteSpace(summary) ? config.UiMetadata?.ShortDescription ?? "" : summary,
            Kind = "official",
            TrustLevel = "official",
            FileName = $"{config.Name}.json",
            Provides = PluginProvides.Resolve(config.UiMetadata?.Provides),
            MediaLabels = sheet?.MediaTypes ?? [],
            Supplies = sheet?.Supplies ?? [],
            Access = sheet?.Access ?? new PluginAccessDto(),
            Credentials = PluginCredentialMapper.FromSourceFile(config),
            Destinations = [.. hosts.Select(host => new PluginNetworkDestination { Host = host, Sends = sends })],
            Settings = settings,
            Values = values,
            ConfiguredCredentials = configuredCredentials,
            ExternalValues = externalValues,
            DefaultActions = defaultActions,
            NoSettingsCard = alwaysOn,
        };
    }

    /// <summary>
    /// The tunables every source file has, said in the settings vocabulary as Advanced settings (D51). The ones the Dashboard let
    /// people change before (language, timeout, request interval, concurrency, address) stay editable; weight, rate limit and cache
    /// time are shown read-only because nothing changed them before.
    /// </summary>
    private static PluginSettingsDescription Tunables(ProviderConfiguration config)
    {
        var properties = new JsonObject
        {
            ["language_strategy"] = new JsonObject
            {
                ["title"] = "Language",
                ["description"] = "Which language the source's details come in.",
                ["choices"] = new JsonArray([.. LanguageChoices.Select(choice => (JsonNode)new JsonObject { ["value"] = choice.Value, ["label"] = choice.Label })]),
                ["default"] = "source",
                ["order"] = 1000,
            },
            ["timeout_seconds"] = Number("Request timeout", "How long to wait for the source before giving up.", "seconds", 1, 120, 1001, readOnly: false, integer: true),
            ["throttle_ms"] = Number("Minimum request interval", "The shortest wait between two requests.", "ms", 0, 60000, 1002, readOnly: false, integer: true),
            ["max_concurrency"] = Number("Most requests at once", "How many requests may be waiting for the source at the same time.", "", 1, 32, 1003, readOnly: false, integer: true),
        };

        if (PrimaryEndpoint(config) is not null)
        {
            properties["primary_endpoint"] = new JsonObject
            {
                ["title"] = "Primary address",
                ["description"] = "Where Library asks this source.",
                ["format"] = "url",
                ["advanced"] = true,
                ["order"] = 1004,
            };
        }

        properties["weight"] = Number("Weight", "How much Library trusts this source when two disagree. Set by Library.", "", 0, 1, 1005, readOnly: true, integer: false);
        if (config.RateLimit?.RequestsPerSecond is not null)
        {
            properties["requests_per_second"] = Number("Rate limit", "The most requests a second Library makes to this source.", "per second", 0, 1000, 1006, readOnly: true, integer: false);
        }

        if (config.CacheTtlHours is not null)
        {
            properties["cache_ttl_hours"] = Number("Cache time", "How long an answer is kept before the source is asked again.", "hours", 0, 8760, 1007, readOnly: true, integer: true);
        }

        foreach (var (key, node) in properties)
        {
            if (node is JsonObject obj && key != "language_strategy")
            {
                obj["advanced"] = true;
            }
        }

        using var document = JsonDocument.Parse(new JsonObject { ["properties"] = properties }.ToJsonString());
        return PluginSettingsDescriptionReader.Read(document.RootElement.Clone());
    }

    private static JsonObject Number(string title, string description, string unit, double minimum, double maximum, int order, bool readOnly, bool integer) => new()
    {
        ["type"] = integer ? "integer" : "number",
        ["title"] = title,
        ["description"] = description,
        ["unit"] = unit,
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["order"] = order,
        ["read_only"] = readOnly,
    };

    private static Dictionary<string, JsonElement> TunableValues(ProviderConfiguration config)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["language_strategy"] = JsonSerializer.SerializeToElement(config.LanguageStrategyRaw, Json),
            ["timeout_seconds"] = JsonSerializer.SerializeToElement(config.HttpClient?.TimeoutSeconds ?? 10, Json),
            ["throttle_ms"] = JsonSerializer.SerializeToElement(config.ThrottleMs, Json),
            ["max_concurrency"] = JsonSerializer.SerializeToElement(config.MaxConcurrency, Json),
            ["weight"] = JsonSerializer.SerializeToElement(config.Weight, Json),
        };

        if (PrimaryEndpoint(config) is { } endpoint)
        {
            values["primary_endpoint"] = JsonSerializer.SerializeToElement(endpoint, Json);
        }

        if (config.RateLimit?.RequestsPerSecond is { } rate)
        {
            values["requests_per_second"] = JsonSerializer.SerializeToElement(rate, Json);
        }

        if (config.CacheTtlHours is { } hours)
        {
            values["cache_ttl_hours"] = JsonSerializer.SerializeToElement(hours, Json);
        }

        return values;
    }

    private static string? PrimaryEndpoint(ProviderConfiguration config) =>
        config.Endpoints.GetValueOrDefault("api") ?? config.Endpoints.Values.FirstOrDefault();

    private static string KindWord(PluginKind kind) => kind switch
    {
        PluginKind.Declarative => "declarative",
        PluginKind.Service => "service",
        PluginKind.Sandboxed => "sandboxed",
        _ => "official",
    };

    private static string TrustWord(PluginTrustLevel trust) => trust switch
    {
        PluginTrustLevel.Official => "official",
        PluginTrustLevel.Verified => "verified",
        PluginTrustLevel.Community => "community",
        _ => "private",
    };
}
