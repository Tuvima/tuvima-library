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
}
