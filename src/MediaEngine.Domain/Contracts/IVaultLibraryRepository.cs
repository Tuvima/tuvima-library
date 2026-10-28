namespace MediaEngine.Domain.Contracts;

/// <summary>Where a Vault library looks up details (Videos spec D26). A Vault library can never use a normal source.</summary>
public static class VaultLookupSources
{
    public const string None = "none";
    public const string StashBox = "stash_box";

    public static bool IsValid(string? value) => value is None or StashBox;
}

/// <summary>One Vault library and its own settings (Videos spec D45, D26, D34).</summary>
/// <param name="LibraryId">The library's id, the same id media assets carry in <c>library_id</c>.</param>
/// <param name="LookupSource">One of <see cref="VaultLookupSources"/>.</param>
/// <param name="LookupServerId">The Stash-box server the administrator picked, or <see langword="null"/> (the library then uses None).</param>
/// <param name="AdultsConfirmedAt">When the administrator confirmed everyone who can unlock it is an adult and the content is lawful.</param>
/// <param name="CreatedByProfileId">The administrator profile that created it.</param>
public sealed record VaultLibrary(
    string LibraryId,
    string LookupSource,
    string? LookupServerId,
    DateTimeOffset AdultsConfirmedAt,
    Guid? CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The source that really applies: a Stash-box library with no server yet uses None.</summary>
    public string EffectiveLookupSource =>
        LookupSource == VaultLookupSources.StashBox && !string.IsNullOrWhiteSpace(LookupServerId)
            ? VaultLookupSources.StashBox
            : VaultLookupSources.None;
}

/// <summary>
/// Which libraries are the Vault. This list is the single source of truth for "is this item Vault": an item is Vault
/// because its file sits in one of these libraries, never because of a flag a person can edit on the item.
/// </summary>
public interface IVaultLibraryRepository
{
    Task<IReadOnlyList<VaultLibrary>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The ids of every Vault library (lower-case, hyphenated GUID text, as stored on media assets).</summary>
    Task<IReadOnlySet<string>> GetIdsAsync(CancellationToken ct = default);

    Task<VaultLibrary?> GetAsync(string libraryId, CancellationToken ct = default);

    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="id"/> is a media asset in a Vault library, or a work with any file in one. For events and other
    /// places that only have an id and must not trust what a sender says about it.
    /// </summary>
    Task<bool> IsInVaultAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="personId"/> may be known only from Vault items, so an event naming them must not reach a session that
    /// cannot see the Vault (security review leak 3). It fails closed: it is true when every credited file is in a Vault library, and also
    /// when no credited file is known yet (credits are written after the person is created) or a credited file has no library yet. It is
    /// false only when at least one credited file sits in a known library outside the Vault, which puts the person on the shared people list.
    /// </summary>
    Task<bool> MayPersonBeVaultOnlyAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// True when the Library already holds an ordinary (non-Vault) file inside <paramref name="folderPath"/>, so adding the folder to
    /// a Vault would turn those files into Vault files. Vault and ordinary files are kept in separate folders on disk.
    /// </summary>
    Task<bool> HoldsOrdinaryFilesAsync(string folderPath, CancellationToken ct = default);

    /// <summary>Marks a library as a Vault library. Throws <see cref="InvalidOperationException"/> when it already is one.</summary>
    Task AddAsync(VaultLibrary library, CancellationToken ct = default);

    /// <summary>Changes the lookup source and server. Returns false when there is no such Vault library.</summary>
    Task<bool> UpdateLookupAsync(string libraryId, string lookupSource, string? lookupServerId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Removes the Vault mark. Refused with <see cref="InvalidOperationException"/> while any item is still in the library,
    /// so its items can never turn into normal ones.
    /// </summary>
    Task<bool> RemoveAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    /// Removes the Vault mark and, in the same write, the records that were only recognised as Vault because of it: the library's
    /// excluded-file rows and the operations (with their events) about files under <paramref name="sourcePaths"/>. Left behind they
    /// would turn into ordinary rows that name Vault files. Refused like <see cref="RemoveAsync(string, CancellationToken)"/> while
    /// any item is still in the library.
    /// </summary>
    Task<bool> RemoveAsync(string libraryId, IReadOnlyCollection<string> sourcePaths, CancellationToken ct = default);
}
