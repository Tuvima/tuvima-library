using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>A hand-written list of Vault libraries for tests that do not need a data store. Pass no ids for "there is no Vault".</summary>
internal sealed class FixedVaultLibraries(params string[] ids) : IVaultLibraryRepository
{
    private readonly HashSet<string> _ids = new(ids, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<VaultLibrary>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VaultLibrary>>(_ids.Select(Build).ToList());

    public Task<IReadOnlySet<string>> GetIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(_ids, StringComparer.OrdinalIgnoreCase));

    public Task<VaultLibrary?> GetAsync(string libraryId, CancellationToken ct = default) =>
        Task.FromResult(_ids.Contains(libraryId) ? Build(libraryId) : null);

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(_ids.Count > 0);

    public Task<bool> HoldsOrdinaryFilesAsync(string folderPath, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Entities (works, assets) that count as Vault titles; empty unless a test names some.</summary>
    public HashSet<Guid> VaultEntities { get; } = [];

    public Task<bool> IsInVaultAsync(Guid id, CancellationToken ct = default) => Task.FromResult(VaultEntities.Contains(id));

    public Task<bool> MayPersonBeVaultOnlyAsync(Guid personId, CancellationToken ct = default) => Task.FromResult(false);

    public Task AddAsync(VaultLibrary library, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> UpdateLookupAsync(string libraryId, string lookupSource, string? lookupServerId, DateTimeOffset now, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<bool> RemoveAsync(string libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> RemoveAsync(string libraryId, IReadOnlyCollection<string> sourcePaths, CancellationToken ct = default) => throw new NotSupportedException();

    private static VaultLibrary Build(string id) =>
        new(id, VaultLookupSources.None, null, DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
