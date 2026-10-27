using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// The four kinds of change an edit session can hold, kept apart on purpose: a provider match never silently moves
/// anything, and a file move never rides along with a details change (universal editor spec D5).
/// </summary>
public static class EditSessionChangeKinds
{
    /// <summary>Library overrides of details fields (title, year, genres, ...). Provider facts are never touched.</summary>
    public const string Details = "details";

    /// <summary>Which retail or recording record this item is.</summary>
    public const string Identity = "identity";

    /// <summary>Which album, season or series the item belongs to.</summary>
    public const string Relationship = "relationship";

    /// <summary>The chosen artwork for a slot.</summary>
    public const string Artwork = "artwork";

    /// <summary>Physical file moves. Run last and one at a time, because they cannot be undone like a data change.</summary>
    public const string FileMove = "file_move";

    public static readonly IReadOnlyList<string> All = [Details, Identity, Relationship, Artwork, FileMove];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>True for the kinds saved together as one unit (everything except file moves).</summary>
    public static bool IsDataChange(string? kind) => IsKnown(kind) && kind != FileMove;
}

/// <summary>How one pending change came out of a check or a save.</summary>
public static class EditSessionChangeStatuses
{
    /// <summary>The check passed (preview) or the change was saved (apply).</summary>
    public const string Ok = "ok";

    /// <summary>The check refused it; the reason says why. Nothing was saved for the session.</summary>
    public const string Invalid = "invalid";

    /// <summary>The change was valid but writing it failed.</summary>
    public const string Failed = "failed";

    /// <summary>The change was valid but another change in the unit failed, so it was undone or never run.</summary>
    public const string NotApplied = "not_applied";
}

/// <summary>
/// One pending change held by the Dashboard while editing. The Dashboard keeps these in memory until Save changes;
/// nothing is stored on the Engine until the whole list is applied.
/// </summary>
public sealed class EditSessionChange
{
    /// <summary>The Dashboard's own id for the change, echoed back so Review can mark the right row.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>One of <see cref="EditSessionChangeKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The item the change is about.</summary>
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>Field operations for a <see cref="EditSessionChangeKinds.Details"/> change.</summary>
    [JsonPropertyName("fields")]
    public List<EditSessionFieldChange> Fields { get; init; } = [];

    /// <summary>The record, album, season or series the change points at (identity and relationship changes).</summary>
    [JsonPropertyName("target_id")]
    public string? TargetId { get; init; }

    /// <summary>Artwork slot, such as <c>CoverArt</c>, <c>Background</c> or <c>Logo</c> (artwork changes).</summary>
    [JsonPropertyName("slot")]
    public string? Slot { get; init; }

    /// <summary>Confirmation token from a move preview (file moves).</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

/// <summary>One staged value for a details field. An empty value reverts the field to the provider's value.</summary>
public sealed class EditSessionFieldChange
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>The whole pending list, sent for a check (<see cref="PreviewOnly"/>) or a save.</summary>
public sealed class EditSessionRequest
{
    /// <summary>The most changes one session may hold.</summary>
    public const int MaxChanges = 500;

    /// <summary>True checks every change and reports what would happen without saving anything.</summary>
    [JsonPropertyName("preview_only")]
    public bool PreviewOnly { get; init; }

    [JsonPropertyName("changes")]
    public List<EditSessionChange> Changes { get; init; } = [];
}

/// <summary>What happened to one pending change.</summary>
public sealed class EditSessionChangeResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>One of <see cref="EditSessionChangeStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Plain-English reason; empty when the change is fine.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// The answer to a check or a save. After a save: <see cref="DataSaved"/> says whether the details, identity, artwork and
/// relationship changes were saved (all of them or none), and each file move reports on its own.
/// </summary>
public sealed class EditSessionResult
{
    [JsonPropertyName("preview_only")]
    public bool PreviewOnly { get; init; }

    /// <summary>True when every change passed the check.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>True when the data changes were written (never true for a preview, or when any check failed).</summary>
    [JsonPropertyName("data_saved")]
    public bool DataSaved { get; init; }

    [JsonPropertyName("changes")]
    public List<EditSessionChangeResult> Changes { get; init; } = [];
}
