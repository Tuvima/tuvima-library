using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

/// <summary>
/// The caller's own Vault mode state. Only a profile that can use Vault mode ever receives one; any other profile gets "not found"
/// from the action, so nothing about it is drawn.
/// </summary>
public sealed class NsfwStatusDto
{
    /// <summary>"pin" (a PIN is asked for) or "allowed" (no PIN).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>True when this device session has Vault mode on right now.</summary>
    [JsonPropertyName("unlocked")]
    public bool Unlocked { get; init; }

    /// <summary>True when the profile has a PIN (one PIN per profile; Vault mode uses it when "Ask for my PIN to open Vault mode" is on). The PIN itself is never returned.</summary>
    [JsonPropertyName("has_pin")]
    public bool HasPin { get; init; }

    /// <summary>Minutes of inactivity before Vault mode switches itself off; 0 means never.</summary>
    [JsonPropertyName("idle_lock_minutes")]
    public int IdleLockMinutes { get; init; }

    /// <summary>
    /// True when this profile chose "Show Vault in the navigation bar" (Library pages D20). Off by default; stored with the profile
    /// so it follows the person across devices. Only the status action fills it; the other actions leave it false.
    /// </summary>
    [JsonPropertyName("show_in_navigation")]
    public bool ShowInNavigation { get; init; }
}

/// <summary>The person's own "Show Vault in the navigation bar" choice.</summary>
public sealed class NsfwNavigationRequest
{
    [JsonPropertyName("show_in_navigation")]
    public bool ShowInNavigation { get; init; }
}

public sealed class NsfwUnlockRequest
{
    /// <summary>The profile's own PIN. Not needed when Vault mode does not ask for it ("allowed").</summary>
    [JsonPropertyName("pin")]
    public string? Pin { get; init; }
}

/// <summary>The person's own idle choice for Vault mode.</summary>
public sealed class NsfwIdleRequest
{
    /// <summary>5, 15, 30 or 60 minutes, or 0 for Never (only when Vault mode does not ask for the PIN).</summary>
    [JsonPropertyName("idle_lock_minutes")]
    public int IdleLockMinutes { get; init; }
}

/// <summary>An administrator opens, changes or closes one profile's Vault access.</summary>
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

/// <summary>
/// One Vault section (a Vault library) and whether the profile whose page this is may open it (<c>GET /api/v1/profiles/{id}/vault-sections</c>;
/// Vault sections D28, D39). Only an administrator whose Vault mode is on ever receives this.
/// </summary>
public sealed class VaultSectionDto
{
    [JsonPropertyName("library_id")]
    public string LibraryId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind_key")]
    public string KindKey { get; init; } = string.Empty;

    [JsonPropertyName("granted")]
    public bool Granted { get; init; }
}

/// <summary>The Vault sections and which of them one profile may open. An administrator opens every section, so <c>opens_all</c> is true and nothing needs ticking.</summary>
public sealed class ProfileVaultSectionsDto
{
    [JsonPropertyName("opens_all")]
    public bool OpensAll { get; init; }

    [JsonPropertyName("sections")]
    public IReadOnlyList<VaultSectionDto> Sections { get; init; } = [];
}

/// <summary>Replaces the Vault sections one profile may open (<c>PUT /api/v1/profiles/{id}/vault-sections</c>). Nothing is ticked by default.</summary>
public sealed class SetProfileVaultSectionsRequest
{
    [JsonPropertyName("library_ids")]
    public IReadOnlyList<string> LibraryIds { get; init; } = [];
}
