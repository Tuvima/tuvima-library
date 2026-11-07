using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Where the editor's Match tab offers a source (universal editor v5, D8). Which kinds of item a source can match comes from its
/// <c>can_handle</c> block; this block only says which editor kinds it is the <em>default</em> for, so the provider dropdown lists it first.
/// </summary>
public sealed class ProviderEditorMatchConfiguration
{
    /// <summary>The editor kinds (<c>movie</c>, <c>book</c>, <c>album</c> and so on) this source is the default provider for.</summary>
    [JsonPropertyName("default_for")]
    public List<string> DefaultFor { get; set; } = [];

    /// <summary>
    /// The editor kinds this source may be offered for. Empty means every kind of the media types it handles. A source listed here is
    /// offered only for these kinds, which is how TV stays TheTVDB only and YouTube is offered only for a video.
    /// </summary>
    [JsonPropertyName("kinds")]
    public List<string> Kinds { get; set; } = [];

    /// <summary>
    /// The identifiers a person can type into the Provider ID or ISBN box that this source can look up directly, by the search field the
    /// source's lookup strategy needs. An identifier of a kind not listed here is evidence only and is never sent as search words.
    /// </summary>
    [JsonPropertyName("id_lookup")]
    public Dictionary<string, string> IdLookup { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
