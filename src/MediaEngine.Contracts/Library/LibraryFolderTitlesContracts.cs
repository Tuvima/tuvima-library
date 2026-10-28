using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>How many titles the catalogue holds for each folder of one library (the Folders tab's Titles column), keyed by the folder's source id.</summary>
public sealed class LibraryFolderTitlesDto
{
    [JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; init; } = [];
}
