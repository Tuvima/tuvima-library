using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Declarative;

/// <summary>
/// Reads the <c>declarative</c> part of a JSON-only plugin's manifest and checks it against what the manifest asks
/// permission for. Every problem comes back as one plain sentence that names the item and says what to change, so a
/// malformed file is refused with a message the author can act on.
/// </summary>
internal static partial class DeclarativePluginParser
{
    private const int MaxPromptLength = 4000;
    private const int MaxStepsPerKind = 20;

    private static readonly JsonSerializerOptions ProviderJson = new() { AllowTrailingCommas = true };

    public static DeclarativeParseResult Parse(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Declarative is not { ValueKind: JsonValueKind.Object } root)
        {
            return DeclarativeParseResult.Fail("A declarative plugin needs a \"declarative\" section describing what it does.");
        }

        var unknown = root.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => name is not ("ai_steps" or "channel_sources" or "metadata_sources" or "notification_channels"));
        if (unknown is not null)
        {
            return DeclarativeParseResult.Fail($"The \"declarative\" section has an unknown part \"{unknown}\". Use ai_steps, channel_sources, metadata_sources or notification_channels.");
        }

        var aiSteps = new List<DeclarativeAiStep>();
        var channelSources = new List<DeclarativeChannelSource>();
        var metadataSources = new List<DeclarativeMetadataSource>();
        var notificationChannels = new List<DeclarativeNotificationChannel>();

        foreach (var (name, list) in new[] { ("ai_steps", root), ("channel_sources", root), ("metadata_sources", root), ("notification_channels", root) }
                     .Select(pair => (pair.Item1, root.TryGetProperty(pair.Item1, out var element) ? element : default)))
        {
            if (list.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (list.ValueKind != JsonValueKind.Array)
            {
                return DeclarativeParseResult.Fail($"\"{name}\" must be a list.");
            }

            if (list.GetArrayLength() > MaxStepsPerKind)
            {
                return DeclarativeParseResult.Fail($"\"{name}\" may list at most {MaxStepsPerKind} items.");
            }

            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                var label = $"{name} item {index}";
                string? problem = name switch
                {
                    "ai_steps" => ParseAiStep(manifest, item, label, aiSteps),
                    "channel_sources" => ParseChannelSource(manifest, item, label, channelSources),
                    "notification_channels" => ParseNotificationChannel(manifest, item, label, notificationChannels),
                    _ => ParseMetadataSource(item, label, metadataSources),
                };
                if (problem is not null)
                {
                    return DeclarativeParseResult.Fail(problem);
                }
            }
        }

        if (aiSteps.Count + channelSources.Count + metadataSources.Count + notificationChannels.Count == 0)
        {
            return DeclarativeParseResult.Fail("The \"declarative\" section is empty. Add at least one of ai_steps, channel_sources, metadata_sources or notification_channels.");
        }

        var duplicate = aiSteps.Select(step => step.Key).GroupBy(key => key).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            return DeclarativeParseResult.Fail($"The AI step key \"{duplicate}\" is used more than once.");
        }

        duplicate = channelSources.Select(source => source.Key).GroupBy(key => key).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            return DeclarativeParseResult.Fail($"The channel source key \"{duplicate}\" is used more than once.");
        }

        if (channelSources.Count > 0)
        {
            var missing = new[] { PluginPermissionIds.NetworkHttp, PluginPermissionIds.ScheduledWork, PluginPermissionIds.PluginRecords }
                .FirstOrDefault(permission => !manifest.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));
            if (missing is not null)
            {
                return DeclarativeParseResult.Fail($"The plugin has channel sources but does not ask for the {missing} permission. Channel sources fetch from the internet on a schedule and keep what they find.");
            }
        }

        duplicate = notificationChannels.Select(channel => channel.Key).GroupBy(key => key).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            return DeclarativeParseResult.Fail($"The notification channel key \"{duplicate}\" is used more than once.");
        }

        return DeclarativeParseResult.Ok(new DeclarativeDefinition(aiSteps, channelSources, metadataSources, notificationChannels));
    }

    private static string? ParseAiStep(PluginManifest manifest, JsonElement item, string label, List<DeclarativeAiStep> into)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{label} must be an object.";
        }

        if (!manifest.Permissions.Contains(PluginPermissionIds.AiInfer, StringComparer.OrdinalIgnoreCase))
        {
            return "The plugin has AI steps but does not ask for the ai.infer permission. Add it to \"permissions\" so the approval can say it uses Local AI.";
        }

        if (!TryText(item, "key", out var key) || !KeyPattern().IsMatch(key))
        {
            return $"{label} needs a \"key\" of 1 to 64 lower-case letters, digits, hyphens or underscores.";
        }

        if (!TryText(item, "name", out var name) || name.Length > 80)
        {
            return $"AI step \"{key}\" needs a \"name\" of 1 to 80 characters.";
        }

        if (!TryText(item, "role", out var role))
        {
            return $"AI step \"{key}\" needs a \"role\" (for example text_quality).";
        }

        if (!AiAbilityCatalog.TryFromPluginRole(role, out _))
        {
            return $"AI step \"{key}\" uses the unknown role \"{role}\". Use text_fast, text_quality, text_scholar or text_cjk.";
        }

        if (!manifest.AiPermissions.Any(permission => string.Equals(permission.Role, role, StringComparison.OrdinalIgnoreCase)))
        {
            return $"AI step \"{key}\" uses the role \"{role}\" but the plugin does not list it under \"ai_permissions\". Add it so the approval can show it.";
        }

        if (!TryText(item, "prompt", out var prompt) || prompt.Length > MaxPromptLength)
        {
            return $"AI step \"{key}\" needs a \"prompt\" of 1 to {MaxPromptLength} characters.";
        }

        var maxTokens = 256;
        if (item.TryGetProperty("max_tokens", out var tokens))
        {
            if (tokens.ValueKind != JsonValueKind.Number || !tokens.TryGetInt32(out maxTokens) || maxTokens is < 16 or > 4096)
            {
                return $"AI step \"{key}\" has a \"max_tokens\" that is not a whole number from 16 to 4096.";
            }
        }

        string? grammar = null;
        if (item.TryGetProperty("json_grammar", out var grammarElement))
        {
            if (grammarElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(grammarElement.GetString()))
            {
                return $"AI step \"{key}\" has a \"json_grammar\" that is not text.";
            }

            grammar = grammarElement.GetString();
        }

        into.Add(new DeclarativeAiStep(key, name, role.Trim().ToLowerInvariant(), prompt, maxTokens, grammar));
        return null;
    }

    private static string? ParseChannelSource(PluginManifest manifest, JsonElement item, string label, List<DeclarativeChannelSource> into)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{label} must be an object.";
        }

        if (!TryText(item, "key", out var key) || !KeyPattern().IsMatch(key))
        {
            return $"{label} needs a \"key\" of 1 to 64 lower-case letters, digits, hyphens or underscores.";
        }

        if (!TryText(item, "name", out var name) || name.Length > 80)
        {
            return $"Channel source \"{key}\" needs a \"name\" of 1 to 80 characters.";
        }

        if (!TryText(item, "format", out var formatText))
        {
            return $"Channel source \"{key}\" needs a \"format\": m3u for a channel list or xmltv for a programme guide.";
        }

        DeclarativeChannelFormat format;
        switch (formatText.Trim().ToLowerInvariant())
        {
            case "m3u":
                format = DeclarativeChannelFormat.M3u;
                break;
            case "xmltv":
                format = DeclarativeChannelFormat.Xmltv;
                break;
            default:
                return $"Channel source \"{key}\" has the unknown format \"{formatText}\". Use m3u or xmltv.";
        }

        if (!TryText(item, "url", out var urlText)
            || !Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            return $"Channel source \"{key}\" needs a \"url\" that starts with https://.";
        }

        if (PluginNetworkRules.FindDestination(manifest.Network, url.Host) is null)
        {
            return $"Channel source \"{key}\" fetches from {url.Host}, which is not listed under \"network\". Declare the address and what is sent to it.";
        }

        var refresh = PluginSchedule.Every(TimeSpan.FromHours(6));
        if (item.TryGetProperty("refresh", out var refreshElement))
        {
            if (refreshElement.ValueKind != JsonValueKind.String
                || !PluginSchedule.TryParse(refreshElement.GetString(), out var parsed)
                || parsed is null)
            {
                return $"Channel source \"{key}\" has a \"refresh\" that cannot be used. Use a cron text such as \"0 */6 * * *\" or an interval of at least 5 minutes such as \"6h\".";
            }

            refresh = parsed;
        }

        into.Add(new DeclarativeChannelSource(key, name, format, url, refresh));
        return null;
    }

    private const int MaxChannelHeaders = 8;
    private const int MaxChannelBodyLength = 4000;
    private static readonly string[] ChannelPlaceholders = ["title", "body", "event"];

    private static string? ParseNotificationChannel(PluginManifest manifest, JsonElement item, string label, List<DeclarativeNotificationChannel> into)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{label} must be an object.";
        }

        if (!TryText(item, "key", out var key) || !KeyPattern().IsMatch(key))
        {
            return $"{label} needs a \"key\" of 1 to 64 lower-case letters, digits, hyphens or underscores.";
        }

        if (!TryText(item, "name", out var name) || name.Length > 80)
        {
            return $"Notification channel \"{key}\" needs a \"name\" of 1 to 80 characters.";
        }

        if (!TryText(item, "url", out var urlText)
            || !Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(url.UserInfo))
        {
            return $"Notification channel \"{key}\" needs a \"url\" that starts with https:// and holds no login. Put logins in \"secrets\" and refer to them as {{secret:name}} in a header.";
        }

        if (PluginNetworkRules.FindDestination(manifest.Network, url.Host) is null)
        {
            return $"Notification channel \"{key}\" sends to {url.Host}, which is not listed under \"network\". Declare the address and what is sent to it.";
        }

        var missing = new[] { PluginPermissionIds.NetworkHttp }
            .FirstOrDefault(permission => !manifest.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));
        if (missing is not null)
        {
            return $"The plugin has notification channels but does not ask for the {missing} permission. A channel sends over the internet.";
        }

        var contentType = "application/json";
        if (item.TryGetProperty("content_type", out var typeElement))
        {
            var chosen = typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            if (chosen is not ("application/json" or "text/plain" or "application/x-www-form-urlencoded"))
            {
                return $"Notification channel \"{key}\" has a \"content_type\" that cannot be used. Use application/json, text/plain or application/x-www-form-urlencoded.";
            }

            contentType = chosen;
        }

        var body = contentType == "application/json"
            ? "{\"title\":\"{title}\",\"message\":\"{body}\",\"event\":\"{event}\"}"
            : "{title}\n{body}";
        if (item.TryGetProperty("body", out var bodyElement))
        {
            if (bodyElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(bodyElement.GetString()) || bodyElement.GetString()!.Length > MaxChannelBodyLength)
            {
                return $"Notification channel \"{key}\" needs a \"body\" of 1 to {MaxChannelBodyLength} characters.";
            }

            body = bodyElement.GetString()!;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("headers", out var headersElement))
        {
            if (headersElement.ValueKind != JsonValueKind.Object || headersElement.EnumerateObject().Count() > MaxChannelHeaders)
            {
                return $"Notification channel \"{key}\" has \"headers\" that are not an object of at most {MaxChannelHeaders} entries.";
            }

            foreach (var header in headersElement.EnumerateObject())
            {
                if (!HeaderNamePattern().IsMatch(header.Name)
                    || header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || header.Value.ValueKind != JsonValueKind.String
                    || header.Value.GetString() is not { Length: > 0 and <= 500 } headerValue
                    || headerValue.Any(char.IsControl))
                {
                    return $"Notification channel \"{key}\" has a header \"{header.Name}\" that cannot be used. Names are plain tokens, values are text up to 500 characters, and Content-Type is set with \"content_type\".";
                }

                headers[header.Name] = headerValue;
            }
        }

        var declared = manifest.Secrets.Select(secret => secret.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (where, text) in headers.Select(pair => ($"header \"{pair.Key}\"", pair.Value)).Append(("body", body)))
        {
            foreach (Match match in ChannelToken().Matches(text))
            {
                var token = match.Groups["token"].Value;
                if (token.StartsWith("secret:", StringComparison.Ordinal))
                {
                    if (where == "body")
                    {
                        return $"Notification channel \"{key}\" uses {{{token}}} in the body. Secrets can only go in headers.";
                    }

                    if (!declared.Contains(token["secret:".Length..]))
                    {
                        return $"Notification channel \"{key}\" uses {{{token}}} in its {where}, but \"{token["secret:".Length..]}\" is not listed under \"secrets\".";
                    }

                    if (!manifest.Permissions.Contains(PluginPermissionIds.PluginSecrets, StringComparer.OrdinalIgnoreCase))
                    {
                        return $"Notification channel \"{key}\" uses a secret but the plugin does not ask for the {PluginPermissionIds.PluginSecrets} permission.";
                    }
                }
                else if (!ChannelPlaceholders.Contains(token, StringComparer.Ordinal))
                {
                    return $"Notification channel \"{key}\" uses {{{token}}} in its {where}. Use {{title}}, {{body}} or {{event}}.";
                }
            }
        }

        into.Add(new DeclarativeNotificationChannel(key, name, url, contentType, headers, body));
        return null;
    }

    private static string? ParseMetadataSource(JsonElement item, string label, List<DeclarativeMetadataSource> into)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{label} must be an object.";
        }

        ProviderConfiguration? configuration;
        try
        {
            configuration = item.Deserialize<ProviderConfiguration>(ProviderJson);
        }
        catch (JsonException ex)
        {
            return $"{label} is not a valid metadata source: {ex.Message}";
        }

        if (configuration is null || string.IsNullOrWhiteSpace(configuration.Name))
        {
            return $"{label} needs a \"name\".";
        }

        if (into.Any(existing => string.Equals(existing.Name, configuration.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"The metadata source name \"{configuration.Name}\" is used more than once.";
        }

        if (!KeyPattern().IsMatch(configuration.Name))
        {
            return $"The metadata source name \"{configuration.Name}\" must be 1 to 64 lower-case letters, digits, hyphens or underscores.";
        }

        if (string.Equals(configuration.AdapterType, "coded", StringComparison.OrdinalIgnoreCase)
            || !item.TryGetProperty("adapter_type", out _))
        {
            return $"Metadata source \"{configuration.Name}\" needs an \"adapter_type\" that describes it in JSON (for example config_driven or text_track), not a coded one.";
        }

        if (configuration.Endpoints.Count == 0)
        {
            return $"Metadata source \"{configuration.Name}\" needs at least one entry under \"endpoints\".";
        }

        var badEndpoint = configuration.Endpoints.FirstOrDefault(endpoint =>
            !Uri.TryCreate(endpoint.Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps);
        if (badEndpoint.Key is not null)
        {
            return $"Metadata source \"{configuration.Name}\" has an endpoint \"{badEndpoint.Key}\" that is not an https:// address.";
        }

        into.Add(new DeclarativeMetadataSource(configuration.Name, item.Clone()));
        return null;
    }

    private static bool TryText(JsonElement item, string property, out string text)
    {
        text = "";
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = element.GetString()?.Trim() ?? "";
        return text.Length > 0;
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderNamePattern();

    [GeneratedRegex(@"\{(?<token>[a-z][a-z0-9_:\-]{0,63})\}")]
    private static partial Regex ChannelToken();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();
}

internal sealed record DeclarativeParseResult(DeclarativeDefinition? Definition, string? Error)
{
    public static DeclarativeParseResult Ok(DeclarativeDefinition definition) => new(definition, null);

    public static DeclarativeParseResult Fail(string error) => new(null, error);
}
