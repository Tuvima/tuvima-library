using System.Collections.Frozen;
using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IVaultSectionGrantRepository"/>, backed by <c>vault_section_grants</c>.</summary>
public sealed class VaultSectionGrantRepository(IDatabaseConnection db) : IVaultSectionGrantRepository
{
    public Task<IReadOnlySet<string>> GetGrantedAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var libraries = conn.Query<string>("SELECT library_id FROM vault_section_grants WHERE profile_id=@profileId;", new { profileId })
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlySet<string>>(libraries);
    }

    public Task ReplaceGrantedAsync(
        Guid profileId, IReadOnlyCollection<string> libraryIds, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var profile = conn.QueryFirstOrDefault<ProfileRow>(
                "SELECT role AS Role, content_limit AS ContentLimit FROM profiles WHERE id=@profileId;", new { profileId }, tx);
            if (profile is null)
            {
                throw new KeyNotFoundException("Profile not found.");
            }

            if (libraryIds.Count > 0 && (profile.Role == "RestrictedProfile" || profile.ContentLimit is not null))
            {
                throw new InvalidOperationException("A Kids or content-limited profile cannot open a Vault section.");
            }

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var libraryId in libraryIds)
            {
                var known = conn.QueryFirstOrDefault<string>(
                    "SELECT library_id FROM vault_libraries WHERE library_id=@libraryId COLLATE NOCASE;", new { libraryId }, tx);
                if (known is null)
                {
                    throw new ArgumentException("That is not a Vault section.");
                }

                wanted.Add(known);
            }

            var current = conn.Query<string>("SELECT library_id FROM vault_section_grants WHERE profile_id=@profileId;", new { profileId }, tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.SetEquals(wanted))
            {
                return;
            }

            var at = changedAt.ToString("O", CultureInfo.InvariantCulture);
            conn.Execute("DELETE FROM vault_section_grants WHERE profile_id=@profileId;", new { profileId }, tx);
            foreach (var libraryId in wanted)
            {
                conn.Execute(
                    "INSERT INTO vault_section_grants(library_id,profile_id,created_at,created_by_profile_id) VALUES(@libraryId,@profileId,@at,@changedByProfileId);",
                    new { libraryId, profileId, at, changedByProfileId }, tx);
            }
        }, ct);
    }

    private sealed class ProfileRow
    {
        public string Role { get; init; } = string.Empty;

        public string? ContentLimit { get; init; }
    }
}
