using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Items;

public sealed class ItemPreferencesRequest
{
    [JsonPropertyName("fields")]
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemPreferencesResponse
{
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("fields_updated")] public int FieldsUpdated { get; init; }
    [JsonPropertyName("updated_keys")] public IReadOnlyList<string> UpdatedKeys { get; init; } = [];
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public sealed class ItemDisplayOverridesRequest
{
    [JsonPropertyName("fields")]
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemDisplayOverridesResponse
{
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("fields_updated")] public int FieldsUpdated { get; init; }
    [JsonPropertyName("updated_keys")] public IReadOnlyList<string> UpdatedKeys { get; init; } = [];
    [JsonPropertyName("display_overrides")]
    public Dictionary<string, string> DisplayOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public sealed class ItemEditorPreferencesRequest
{
    [JsonPropertyName("expected_revision")] public long ExpectedRevision { get; init; }
    [JsonPropertyName("display_overrides")]
    public Dictionary<string, string> DisplayOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("local_tags")] public List<string> LocalTags { get; init; } = [];
}

public sealed class ItemEditorPreferencesResponse
{
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("local_tags")] public IReadOnlyList<string> LocalTags { get; init; } = [];
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("display_overrides")]
    public IReadOnlyDictionary<string, string> DisplayOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemExplicitRequest
{
    [JsonPropertyName("explicit")] public bool IsExplicit { get; init; }
}

public sealed class ItemExplicitResponse
{
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("explicit")] public bool IsExplicit { get; init; }

    /// <summary>True for a video or a series of videos, the only kind that offers the Explicit mark.</summary>
    [JsonPropertyName("supported")] public bool Supported { get; init; }
}
