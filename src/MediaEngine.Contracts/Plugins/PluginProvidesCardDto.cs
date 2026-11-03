using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>One "what it provides" card: the kind (a word from <see cref="PluginProvides"/>) and its one plain sentence.</summary>
public sealed class PluginProvidesCardDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("sentence")]
    public string Sentence { get; set; } = "";
}
