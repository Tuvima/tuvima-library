using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Sets up the one content limit there is (library access D9, D10): a Kid's profile follows its household's levels. The tests that need "a
/// profile with a limit" make the seeded profile a Kid's profile and put the level (and the Show unrated items switch) on its household.
/// </summary>
internal static class KidsLimitTestSupport
{
    /// <summary>
    /// <paramref name="level"/> <see langword="null"/> makes the seeded profile an ordinary administrator again. Otherwise the profile
    /// becomes a Kid's profile and its household gets <paramref name="level"/> for Watch, Read and Listen.
    /// </summary>
    public static void SetLimit(IDatabaseConnection database, string? level, bool showUnrated = false) =>
        SetLimit(database, Profile.SeedProfileId, level, level, level, showUnrated);

    /// <summary>The same with a different level for each area; a <see langword="null"/> watch level makes the profile an administrator.</summary>
    public static void SetLimit(
        IDatabaseConnection database, Guid profileId, string? watch, string? read, string? listen, bool showUnrated)
    {
        using var connection = database.CreateConnection();
        var household = connection.ExecuteScalar<Guid?>("SELECT household_id FROM profiles WHERE id=@profileId;", new { profileId });
        if (household is null)
        {
            household = Guid.NewGuid();
            connection.Execute(
                "INSERT INTO households(id,name,created_at,is_default) VALUES(@household,'Test household',@now,0);",
                new { household, now = DateTimeOffset.UtcNow.ToString("O") });
            connection.Execute("UPDATE profiles SET household_id=@household WHERE id=@profileId;", new { household, profileId });
        }

        connection.Execute(
            """
            UPDATE households SET kid_watch_level=@watch, kid_read_level=@read, kid_listen_level=@listen, kid_show_unrated=@showUnrated
            WHERE id=@household;
            """,
            new { household, watch = watch ?? "PG", read = read ?? "PG", listen = listen ?? "PG", showUnrated });
        connection.Execute(
            "UPDATE profiles SET role=@role WHERE id=@profileId;",
            new { profileId, role = watch is null ? "Administrator" : "RestrictedProfile" });
    }
}
