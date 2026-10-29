using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

/// <summary>The directions a move goes: into the Vault or back out of it.</summary>
public static class VaultMoveDirections
{
    public const string In = "in";
    public const string Out = "out";
}

/// <summary>What a move preview found. Only the first value lets the move start.</summary>
public static class VaultMovePreviewStates
{
    public const string Ready = "ready";
    public const string NoDestination = "no_destination";
    public const string NeedsManagedFolder = "needs_managed_folder";
    public const string NothingToMove = "nothing_to_move";
    public const string NotEnoughSpace = "not_enough_space";
    public const string Busy = "busy";
    public const string Refused = "refused";
}

/// <summary>Asks what moving one folder's items would do (<c>POST /api/v1/vault-moves/preview</c>). Nothing is changed.</summary>
public sealed class VaultMovePreviewRequest
{
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = VaultMoveDirections.In;

    /// <summary>The folder (library source) whose items move, by its id.</summary>
    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;
}

/// <summary>
/// What a move would do, worked out from the folder as it is now. It names no title and no path: the counts, the destination library's name
/// and whether the originals will stay. <c>fingerprint</c> is sent back to start the move; if the folder changed in between, the move is refused.
/// </summary>
public sealed class VaultMovePreviewDto
{
    [JsonPropertyName("state")]
    public string State { get; init; } = VaultMovePreviewStates.Refused;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = VaultMoveDirections.In;

    /// <summary>Titles that would move.</summary>
    [JsonPropertyName("items")]
    public int Items { get; init; }

    /// <summary>Files in the folder that would stay where they are because they are not set up as a single title.</summary>
    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    [JsonPropertyName("source_name")]
    public string SourceName { get; init; } = string.Empty;

    [JsonPropertyName("destination_name")]
    public string DestinationName { get; init; } = string.Empty;

    /// <summary>True when the folder is Read-only: the copies are made but the originals stay in place unless the person chooses to remove them.</summary>
    [JsonPropertyName("originals_stay")]
    public bool OriginalsStay { get; init; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>
/// Starts a move (<c>POST /api/v1/vault-moves</c>). The administrator's own profile PIN is asked for again here. <c>remove_read_only_originals</c> is
/// the separate, explicit choice to delete the originals from a Read-only folder once their copies are checked (Vault sections D30); it is never a
/// default and unlocks nothing else.
/// </summary>
public sealed class StartVaultMoveRequest
{
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = VaultMoveDirections.In;

    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = string.Empty;

    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }

    [JsonPropertyName("remove_read_only_originals")]
    public bool RemoveReadOnlyOriginals { get; init; }
}

/// <summary>A move and how far it has got. Shown only to an unlocked administrator.</summary>
public sealed class VaultMoveJobDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = VaultMoveDirections.In;

    /// <summary><c>queued</c>, <c>running</c>, <c>done</c>, <c>attention</c> or <c>failed</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("moved")]
    public int Moved { get; init; }

    /// <summary>Copied and checked, waiting for the destination folder's scan to add them.</summary>
    [JsonPropertyName("waiting")]
    public int Waiting { get; init; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    /// <summary>Moved, but the original could not be removed and is still in place.</summary>
    [JsonPropertyName("original_left")]
    public int OriginalLeft { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>The answer to starting a move: it either started (<c>job</c>), or it did not and <c>preview</c> says what is true now.</summary>
public sealed class StartVaultMoveResultDto
{
    [JsonPropertyName("started")]
    public bool Started { get; init; }

    [JsonPropertyName("job")]
    public VaultMoveJobDto? Job { get; init; }

    [JsonPropertyName("preview")]
    public VaultMovePreviewDto? Preview { get; init; }
}
