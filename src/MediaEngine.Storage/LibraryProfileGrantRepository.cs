using System.Collections.Frozen;
using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="ILibraryProfileGrantRepository"/>, backed by <c>library_profile_grants</c>.</summary>
public sealed class LibraryProfileGrantRepository(IDatabaseConnection db) : ILibraryProfileGrantRepository
{
    public Task<IReadOnlySet<Guid>> GetGrantedLibrariesAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var libraries = conn.Query<Guid>("SELECT library_id FROM library_profile_grants WHERE profile_id=@profileId;", new { profileId })
            .ToFrozenSet();
        return Task.FromResult<IReadOnlySet<Guid>>(libraries);
    }

    public Task<IReadOnlySet<Guid>> GetGrantedProfilesAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var profiles = conn.Query<Guid>("SELECT profile_id FROM library_profile_grants WHERE library_id=@libraryId;", new { libraryId })
            .ToFrozenSet();
        return Task.FromResult<IReadOnlySet<Guid>>(profiles);
    }

    public Task ReplaceGrantedProfilesAsync(
        Guid libraryId, IReadOnlySet<Guid> profileIds, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var libraryText = libraryId.ToString("D");
            var isVault = conn.QueryFirstOrDefault<int?>(
                "SELECT 1 FROM vault_libraries WHERE lower(library_id)=@libraryText;", new { libraryText }, tx) is not null;
            if (isVault)
            {
                throw new ArgumentException("A Vault library is not given to profiles here. It opens only through Vault mode.");
            }

            foreach (var profileId in profileIds)
            {
                var exists = conn.QueryFirstOrDefault<int?>("SELECT 1 FROM profiles WHERE id=@profileId;", new { profileId }, tx) is not null;
                if (!exists)
                {
                    throw new ArgumentException($"Unknown profile '{profileId:D}'.");
                }
            }

            var current = conn.Query<Guid>("SELECT profile_id FROM library_profile_grants WHERE library_id=@libraryId;", new { libraryId }, tx).ToHashSet();
            var changed = current.Except(profileIds).Concat(profileIds.Except(current)).ToList();
            if (changed.Count == 0)
            {
                return;
            }

            var at = changedAt.ToString("O", CultureInfo.InvariantCulture);
            conn.Execute("DELETE FROM library_profile_grants WHERE library_id=@libraryId;", new { libraryId }, tx);
            foreach (var profileId in profileIds)
            {
                conn.Execute(
                    "INSERT INTO library_profile_grants(library_id,profile_id,created_at,created_by_profile_id) VALUES(@libraryId,@profileId,@at,@changedByProfileId);",
                    new { libraryId, profileId, at, changedByProfileId }, tx);
            }

            foreach (var profileId in changed)
            {
                conn.Execute("UPDATE profiles SET authorization_version=authorization_version+1 WHERE id=@profileId;", new { profileId }, tx);
            }
        }, ct);
    }

    public Task ClearLibraryAsync(Guid libraryId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var had = conn.Query<Guid>("SELECT profile_id FROM library_profile_grants WHERE library_id=@libraryId;", new { libraryId }, tx).ToList();
            if (had.Count == 0)
            {
                return;
            }

            conn.Execute("DELETE FROM library_profile_grants WHERE library_id=@libraryId;", new { libraryId }, tx);
            foreach (var profileId in had)
            {
                conn.Execute("UPDATE profiles SET authorization_version=authorization_version+1 WHERE id=@profileId;", new { profileId }, tx);
            }
        }, ct);
}
