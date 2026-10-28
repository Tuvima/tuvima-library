using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// Makes a Vault-protected library beside the ordinary library of a content type (<c>POST /settings/vault-libraries</c>; Library pages D10, D11,
/// administrators only, no PIN). <c>kind_key</c> names the content type, <c>name</c> the new library, <c>folder_path</c> its first folder on the
/// server. The answer is a <see cref="VaultLibraryActionResultDto"/>. Anyone who is not a signed-in administrator gets "not found", and no answer
/// names a folder or a title.
/// </summary>
public sealed record CreateVaultLibraryFromKindRequest(
    [property: JsonPropertyName("kind_key")] string KindKey,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("folder_path")] string FolderPath);

/// <summary>
/// Adds a folder as a Vault folder (<c>POST /settings/vault-libraries/folders</c>; Library pages D10, administrators only, no PIN). The folder joins
/// the Vault library of the content type <c>kind_key</c>; when there is none yet the Engine makes it first, named for the content type.
/// </summary>
public sealed record AddVaultFolderRequest(
    [property: JsonPropertyName("kind_key")] string KindKey,
    [property: JsonPropertyName("folder_path")] string FolderPath);

/// <summary>
/// The answer to making a Vault library or adding a Vault folder. It carries no library, name or folder: <c>library_id</c> is set only when the
/// caller may see the Vault right now (an unlocked administrator), so the page can open the library; otherwise it is null and the answer says
/// nothing but that it worked.
/// </summary>
public sealed record VaultLibraryActionResultDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("library_id")] string? LibraryId);

/// <summary>
/// The per-library "Look up details online" switch of a Vault library of an ordinary content type
/// (<c>PUT /settings/vault-libraries/{libraryId}/online-lookup</c>; Vault sections D34). Off by default; on, titles from this library are sent to
/// online services.
/// </summary>
public sealed record SetVaultOnlineLookupRequest(
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// Where "Move to Vault" would put an item of a content type (<c>GET /settings/vault-libraries/destination?kind_key=</c>, administrators only; Library
/// pages D11). <c>state</c> is <c>none</c> (no Vault library of that type yet: the dialog offers Add a Vault library), <c>needs_managed_folder</c> (the
/// type has a Vault library but none of its folders is one Library manages) or <c>ready</c>. The move itself is not part of this answer.
/// </summary>
public sealed record VaultMoveDestinationDto(
    [property: JsonPropertyName("kind_key")] string KindKey,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("library_id")] Guid? LibraryId,
    [property: JsonPropertyName("library_name")] string? LibraryName,
    [property: JsonPropertyName("managed_folders")] int ManagedFolders);

/// <summary>The values <see cref="VaultMoveDestinationDto.State"/> takes.</summary>
public static class VaultMoveDestinationStates
{
    public const string None = "none";
    public const string NeedsManagedFolder = "needs_managed_folder";
    public const string Ready = "ready";
}
