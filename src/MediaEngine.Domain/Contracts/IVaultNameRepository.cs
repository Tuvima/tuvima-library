namespace MediaEngine.Domain.Contracts;

/// <summary>A Vault-only performer (Videos spec D28). Never linked to, or merged with, a shared person.</summary>
public sealed record VaultPerformer(Guid Id, string LibraryId, string Name, string? SortName, string? Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A Vault-only studio (Videos spec D27).</summary>
public sealed record VaultStudio(Guid Id, string LibraryId, string Name, string? Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A Vault-only tag. Never created in, or joined to, the shared tag lists (Videos spec D27).</summary>
public sealed record VaultTag(Guid Id, string LibraryId, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// The Vault's own names: performers, studios and tags that a Vault lookup brought back. They live in tables of their own so
/// nothing from a Vault lookup can reach shared People, Tags, Series or artwork records. A name exists only while at least
/// one scene refers to it (the data structure removes it with its last link).
/// </summary>
public interface IVaultNameRepository
{
    /// <summary>Finds a performer by name (case-insensitive) in the library, or creates it. Returns its id.</summary>
    Task<Guid> EnsurePerformerAsync(string libraryId, string name, string? sortName, string? source, DateTimeOffset now, CancellationToken ct = default);

    Task<Guid> EnsureStudioAsync(string libraryId, string name, string? source, DateTimeOffset now, CancellationToken ct = default);

    Task<Guid> EnsureTagAsync(string libraryId, string name, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Links a scene to a performer, studio or tag. Linking twice is harmless.</summary>
    Task LinkPerformerAsync(Guid assetId, Guid performerId, CancellationToken ct = default);

    Task LinkStudioAsync(Guid assetId, Guid studioId, CancellationToken ct = default);

    Task LinkTagAsync(Guid assetId, Guid tagId, CancellationToken ct = default);

    /// <summary>Removes every performer, studio and tag link of a scene (a name left with no scene goes with it).</summary>
    Task UnlinkAllAsync(Guid assetId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultPerformer>> GetPerformersAsync(string? libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultStudio>> GetStudiosAsync(string? libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultTag>> GetTagsAsync(string? libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultPerformer>> GetPerformersOfAsync(Guid assetId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultStudio>> GetStudiosOfAsync(Guid assetId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultTag>> GetTagsOfAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The scene (media asset) ids that refer to a performer, studio or tag, so a tile can open to the owned scenes.</summary>
    Task<IReadOnlyList<Guid>> GetAssetIdsOfPerformerAsync(Guid performerId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAssetIdsOfStudioAsync(Guid studioId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAssetIdsOfTagAsync(Guid tagId, CancellationToken ct = default);
}

/// <summary>What a Vault artwork file belongs to.</summary>
public static class VaultArtworkOwnerKinds
{
    public const string Asset = "asset";
    public const string Performer = "performer";
    public const string Studio = "studio";

    public static bool IsValid(string? value) => value is Asset or Performer or Studio;
}

/// <summary>What a Vault artwork file is for.</summary>
public static class VaultArtworkKinds
{
    public const string Cover = "cover";
    public const string Thumbnail = "thumbnail";
    public const string Portrait = "portrait";
    public const string Logo = "logo";
    public const string Still = "still";

    public static bool IsValid(string? value) => value is Cover or Thumbnail or Portrait or Logo or Still;
}

/// <summary>One stored Vault artwork file. It is stored apart from normal artwork and served only to a profile with access.</summary>
public sealed record VaultArtworkRecord(
    Guid Id,
    string OwnerKind,
    Guid OwnerId,
    string Kind,
    string FilePath,
    string? ContentHash,
    int? Width,
    int? Height,
    string? Source,
    DateTimeOffset CreatedAt);

/// <summary>The Vault's own artwork records (Videos spec D27, D31). Empty until the Vault lookup packets fill it.</summary>
public interface IVaultArtworkRepository
{
    Task<Guid> AddAsync(string ownerKind, Guid ownerId, string kind, string filePath, string? contentHash, int? width, int? height, string? source, DateTimeOffset now, CancellationToken ct = default);

    Task<VaultArtworkRecord?> GetAsync(Guid artworkId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultArtworkRecord>> GetForOwnerAsync(string ownerKind, Guid ownerId, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid artworkId, CancellationToken ct = default);
}
