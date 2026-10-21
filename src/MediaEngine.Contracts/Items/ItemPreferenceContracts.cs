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

public sealed class VideoOrderResponse
{
    [JsonPropertyName("groups")] public IReadOnlyList<VideoOrderGroupDto> Groups { get; init; } = [];
}

public sealed class VideoOrderGroupDto
{
    [JsonPropertyName("parent_id")] public Guid ParentId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("entries")] public IReadOnlyList<VideoOrderEntryDto> Entries { get; init; } = [];

    /// <summary>True when every video in the group lives in a Managed folder, so "Rename files to match this order" is offered.</summary>
    [JsonPropertyName("can_rename_files")] public bool CanRenameFiles { get; init; }
}

public sealed class VideoOrderEntryDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
}

public sealed class VideoOrderRequest
{
    [JsonPropertyName("parent_id")] public Guid ParentId { get; init; }
    [JsonPropertyName("ordered_ids")] public List<Guid> OrderedIds { get; init; } = [];
}

public sealed class VideoRenameRequest
{
    [JsonPropertyName("parent_id")] public Guid ParentId { get; init; }
}

public sealed class VideoRenameResponse
{
    [JsonPropertyName("renamed")] public int Renamed { get; init; }

    /// <summary>Videos left as they were: the file was missing, already named right, or its folder is not Managed.</summary>
    [JsonPropertyName("skipped")] public int Skipped { get; init; }
}

public sealed class VideoSeriesTargetDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("video_count")] public int VideoCount { get; init; }
}

public sealed class VideoSeriesStateResponse
{
    /// <summary>True for a video, the only kind that can join or leave a series.</summary>
    [JsonPropertyName("supported")] public bool Supported { get; init; }
    [JsonPropertyName("in_series")] public bool InSeries { get; init; }
    [JsonPropertyName("series_title")] public string? SeriesTitle { get; init; }

    /// <summary>The series (or section) a video can join. Empty when none exists yet; a new one can still be started.</summary>
    [JsonPropertyName("targets")] public IReadOnlyList<VideoSeriesTargetDto> Targets { get; init; } = [];
}

/// <summary>Put a standalone video in a series: name an existing one by <see cref="ParentId"/>, or start a new one with <see cref="NewSeriesName"/>.</summary>
public sealed class VideoSeriesPromoteRequest
{
    [JsonPropertyName("parent_id")] public Guid? ParentId { get; init; }
    [JsonPropertyName("new_series_name")] public string? NewSeriesName { get; init; }
}

public sealed class VideoIdentifyStateResponse
{
    /// <summary>True for a video, the only kind with an online lookup.</summary>
    [JsonPropertyName("supported")] public bool Supported { get; init; }

    /// <summary>True once an online lookup matched the video, so the Match section has nothing to ask.</summary>
    [JsonPropertyName("matched")] public bool Matched { get; init; }

    /// <summary>False when this video's library has the online lookup switched off in Settings.</summary>
    [JsonPropertyName("lookup_enabled")] public bool LookupEnabled { get; init; }
}

public sealed class VideoIdentifyRequest
{
    /// <summary>A YouTube link or the 11-character video ID. Only the ID is sent to YouTube.</summary>
    [JsonPropertyName("link")] public string Link { get; init; } = "";
}

public sealed class VideoIdentifyResponse
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
}
