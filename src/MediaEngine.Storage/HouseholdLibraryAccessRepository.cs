using System.Collections.Frozen;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IHouseholdLibraryAccessRepository"/>, backed by <c>household_library_hidden</c>.</summary>
public sealed class HouseholdLibraryAccessRepository(IDatabaseConnection db) : IHouseholdLibraryAccessRepository
{
    public Task<IReadOnlySet<Guid>> GetHiddenLibrariesAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var libraries = conn.Query<Guid>(
            "SELECT library_id FROM household_library_hidden WHERE household_id=@householdId;", new { householdId }).ToFrozenSet();
        return Task.FromResult<IReadOnlySet<Guid>>(libraries);
    }

    public Task<IReadOnlySet<Guid>> GetHiddenLibrariesForProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var libraries = conn.Query<Guid>("""
            SELECT h.library_id
            FROM household_library_hidden h
            JOIN profiles p ON p.household_id = h.household_id
            WHERE p.id=@profileId;
            """, new { profileId }).ToFrozenSet();
        return Task.FromResult<IReadOnlySet<Guid>>(libraries);
    }

    public Task<bool> ReplaceHiddenLibrariesAsync(
        Guid householdId, IReadOnlySet<Guid> hiddenLibraries, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hiddenLibraries);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@householdId;", new { householdId }, tx) == 0)
            {
                return false;
            }

            var vaultIds = conn.Query<string>("SELECT library_id FROM vault_libraries;", transaction: tx)
                .Select(text => Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty)
                .ToHashSet();
            if (hiddenLibraries.Any(vaultIds.Contains))
            {
                throw new ArgumentException("The Vault can't be hidden from or shown to a household as a library. Its access is set per person.");
            }

            var at = changedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var before = conn.Query<Guid>(
                "SELECT library_id FROM household_library_hidden WHERE household_id=@householdId;", new { householdId }, tx).ToHashSet();
            conn.Execute("DELETE FROM household_library_hidden WHERE household_id=@householdId;", new { householdId }, tx);
            foreach (var libraryId in hiddenLibraries)
            {
                conn.Execute(
                    "INSERT INTO household_library_hidden(household_id,library_id,created_at,created_by_profile_id) VALUES(@householdId,@libraryId,@at,@changedByProfileId);",
                    new { householdId, libraryId, at, changedByProfileId }, tx);
            }

            // Only a real change moves anyone's version: saving the same list again leaves open screens alone.
            if (!before.SetEquals(hiddenLibraries))
            {
                conn.Execute(
                    "UPDATE profiles SET authorization_version=authorization_version+1 WHERE household_id=@householdId;", new { householdId }, tx);
            }

            return true;
        }, ct);
    }
}
