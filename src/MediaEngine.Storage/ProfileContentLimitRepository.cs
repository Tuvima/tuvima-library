using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IProfileContentLimitRepository"/>. A Kid's profile is the restricted role; its levels are the
/// household's, read live, so a change to the household applies on the Kid's profile's very next call.
/// </summary>
public sealed class ProfileContentLimitRepository(IDatabaseConnection database) : IProfileContentLimitRepository
{
    public Task<ProfileContentLimit> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<LimitRow>(
            """
            SELECT p.role AS Role, h.kid_watch_level AS WatchLevel, h.kid_read_level AS ReadLevel,
                   h.kid_listen_level AS ListenLevel, h.kid_show_unrated AS ShowUnrated
            FROM profiles p
            LEFT JOIN households h ON h.id = p.household_id
            WHERE p.id = @profileId LIMIT 1;
            """,
            new { profileId });
        if (row is null)
        {
            return Task.FromResult(ProfileContentLimit.Strictest);
        }

        if (!string.Equals(row.Role, "RestrictedProfile", StringComparison.Ordinal))
        {
            return Task.FromResult(ProfileContentLimit.Unrestricted);
        }

        // A Kid's profile with no household (cannot happen once an administrator exists) gets the defaults.
        var settings = row.WatchLevel is null || row.ReadLevel is null || row.ListenLevel is null
            ? HouseholdKidSettings.Default
            : new HouseholdKidSettings(row.WatchLevel, row.ReadLevel, row.ListenLevel, row.ShowUnrated);
        return Task.FromResult(new ProfileContentLimit(true, settings));
    }

    private sealed class LimitRow
    {
        public string Role { get; set; } = string.Empty;
        public string? WatchLevel { get; set; }
        public string? ReadLevel { get; set; }
        public string? ListenLevel { get; set; }
        public bool ShowUnrated { get; set; }
    }
}
