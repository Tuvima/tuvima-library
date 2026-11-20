using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>One input of a channel type's setup form (a link, a token, a topic).</summary>
internal sealed class ChannelFieldTemplate
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("help")] public string Help { get; set; } = "";

    /// <summary><c>text</c>, <c>url</c> (an http or https address) or <c>token</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";

    /// <summary>Kept in the secrets store and never shown again.</summary>
    [JsonPropertyName("secret")] public bool Secret { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("placeholder")] public string Placeholder { get; set; } = "";
    [JsonPropertyName("default")] public string Default { get; set; } = "";

    /// <summary>When the form should offer a long random value (a private topic).</summary>
    [JsonPropertyName("suggest_random")] public bool SuggestRandom { get; set; }

    /// <summary>For a <c>url</c> field: the address must be https and on one of these hosts (or their subdomains).</summary>
    [JsonPropertyName("host_suffixes")] public List<string> HostSuffixes { get; set; } = [];
}

/// <summary>The one web request a channel makes. Tokens in braces are filled from the form and the message.</summary>
internal sealed class ChannelRequestTemplate
{
    [JsonPropertyName("method")] public string Method { get; set; } = "POST";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = "application/json";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
}

/// <summary>A kind of channel a person can add: Discord, ntfy, a webhook. Types are data, one JSON file each.</summary>
internal sealed class ChannelTypeTemplate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; } = 100;

    /// <summary>Shown under Advanced rather than in the main tiles.</summary>
    [JsonPropertyName("advanced")] public bool Advanced { get; set; }
    [JsonPropertyName("cost")] public string Cost { get; set; } = "";
    [JsonPropertyName("effort")] public string Effort { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("help")] public string Help { get; set; } = "";
    [JsonPropertyName("what_leaves")] public string WhatLeaves { get; set; } = "";
    [JsonPropertyName("fields")] public List<ChannelFieldTemplate> Fields { get; set; } = [];
    [JsonPropertyName("request")] public ChannelRequestTemplate Request { get; set; } = new();
}

/// <summary>Loads the channel types that ship inside the Engine (<c>Services/Notifications/ChannelTypes/*.json</c>).</summary>
internal sealed class ChannelTypeCatalogue
{
    private const string ResourcePrefix = "MediaEngine.Api.notification-channel-types.";

    private readonly IReadOnlyList<ChannelTypeTemplate> _types = Load();

    public IReadOnlyList<ChannelTypeTemplate> All => _types;

    public ChannelTypeTemplate? Find(string id) =>
        _types.FirstOrDefault(type => string.Equals(type.Id, id, StringComparison.Ordinal));

    private static IReadOnlyList<ChannelTypeTemplate> Load()
    {
        var assembly = typeof(ChannelTypeCatalogue).Assembly;
        var types = new List<ChannelTypeTemplate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var type = JsonSerializer.Deserialize<ChannelTypeTemplate>(stream)
                ?? throw new InvalidDataException($"Notification channel type '{name}' is empty.");
            types.Add(type);
        }

        return [.. types.OrderBy(type => type.Order).ThenBy(type => type.Name, StringComparer.Ordinal)];
    }
}
