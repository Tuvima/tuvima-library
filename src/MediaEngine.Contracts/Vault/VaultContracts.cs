using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

/// <summary>
/// The caller's own NSFW state. Only a profile that can use NSFW mode ever receives one; any other profile gets "not found"
/// from the action, so nothing about it is drawn.
/// </summary>
public sealed class NsfwStatusDto
{
    /// <summary>"pin" (a PIN is asked for) or "allowed" (no PIN).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>True when this device session has NSFW mode on right now.</summary>
    [JsonPropertyName("unlocked")]
    public bool Unlocked { get; init; }

    /// <summary>True when the profile has saved an NSFW PIN. The PIN itself is never returned.</summary>
    [JsonPropertyName("has_pin")]
    public bool HasPin { get; init; }

    /// <summary>Minutes of inactivity before NSFW mode switches itself off; 0 means never.</summary>
    [JsonPropertyName("idle_lock_minutes")]
    public int IdleLockMinutes { get; init; }
}

public sealed class NsfwUnlockRequest
{
    /// <summary>The profile's NSFW PIN. Not needed for an Allowed profile.</summary>
    [JsonPropertyName("pin")]
    public string? Pin { get; init; }
}

public sealed class NsfwSetPinRequest
{
    [JsonPropertyName("new_pin")]
    public string NewPin { get; init; } = string.Empty;

    /// <summary>The current NSFW PIN, needed only when one is already saved.</summary>
    [JsonPropertyName("current_pin")]
    public string? CurrentPin { get; init; }

    /// <summary>
    /// The profile's own sign-in PIN, needed when saving a first NSFW PIN (or one after an administrator reset) and the profile
    /// has a profile PIN. A profile with none confirms with a recent sign-in instead.
    /// </summary>
    [JsonPropertyName("profile_pin")]
    public string? ProfilePin { get; init; }
}

/// <summary>An administrator removes one profile's NSFW PIN.</summary>
public sealed class ResetNsfwPinRequest
{
    /// <summary>The administrator's own profile PIN, asked for again at this moment.</summary>
    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}

/// <summary>An administrator opens, changes or closes one profile's NSFW access.</summary>
public sealed class SetNsfwAccessRequest
{
    /// <summary>"off", "pin" or "allowed". A Kids or content-limited profile can only be "off".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "off";

    /// <summary>Minutes of inactivity before it locks; 0 means never (Allowed profiles only). Omit for the default.</summary>
    [JsonPropertyName("idle_lock_minutes")]
    public int? IdleLockMinutes { get; init; }

    /// <summary>The administrator's own profile PIN, asked for again at this moment. Needed to grant access, not to close it.</summary>
    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}

public sealed class VaultLibraryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>"none" or "stash_box".</summary>
    [JsonPropertyName("lookup_source")]
    public string LookupSource { get; init; } = "none";

    [JsonPropertyName("lookup_server_id")]
    public string? LookupServerId { get; init; }

    [JsonPropertyName("adults_confirmed_at")]
    public DateTimeOffset AdultsConfirmedAt { get; init; }
}

public sealed class CreateVaultLibraryRequest
{
    [JsonPropertyName("lookup_source")]
    public string LookupSource { get; init; } = "none";

    [JsonPropertyName("lookup_server_id")]
    public string? LookupServerId { get; init; }

    /// <summary>The administrator confirms that everyone who can unlock the Vault is an adult and the content is lawful.</summary>
    [JsonPropertyName("adults_confirmed")]
    public bool AdultsConfirmed { get; init; }

    /// <summary>The administrator's own profile PIN, asked for again at this moment.</summary>
    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}

public sealed class UpdateVaultLibraryRequest
{
    [JsonPropertyName("lookup_source")]
    public string LookupSource { get; init; } = "none";

    [JsonPropertyName("lookup_server_id")]
    public string? LookupServerId { get; init; }

    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}

public sealed class RemoveVaultLibraryRequest
{
    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}

/// <summary>Puts a folder on the server into a Vault library. Asked for by an unlocked administrator, with their own profile PIN.</summary>
public sealed class AttachVaultFolderRequest
{
    /// <summary>The folder as the server sees it. Never logged.</summary>
    [JsonPropertyName("folder_path")]
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>The administrator's own profile PIN, asked for again at this moment.</summary>
    [JsonPropertyName("admin_pin")]
    public string? AdminPin { get; init; }
}
