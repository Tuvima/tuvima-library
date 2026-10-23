using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// A safety net for the move from account-held to profile-held access (users, profiles and households spec). For every sign-in that
/// opens a profile it compares what the account side says with what the profile side holds, and lists every difference in plain
/// words. Read-only. It computes the expected profile side from the raw account rows on its own, so it does not share logic with the
/// writes it checks. Used by tests; an empty list means the two sides agree.
/// </summary>
internal static class ProfileAccessConsistency
{
    /// <summary>One line per difference; empty when the profile side matches the account side for every sign-in with a profile.</summary>
    internal static IReadOnlyList<string> FindDifferences(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var differences = new List<string>();
        using var conn = db.CreateConnection();
        var accounts = conn.Query<AccountSide>("""
            SELECT a.id AS AccountId, a.profile_id AS ProfileId, a.is_administrator AS IsAdministrator, a.household_admin AS HouseholdAdmin,
                   COALESCE(a.grants_inherit_from_account_id, a.id) AS AccessAccountId,
                   EXISTS(SELECT 1 FROM account_profile_grants g
                          WHERE g.account_id = a.id AND g.profile_id = a.profile_id AND g.is_enabled = 1 AND g.admin_enabled = 1) AS OwnGrantAdmin
            FROM accounts a WHERE a.profile_id IS NOT NULL ORDER BY a.created_at, a.id;
            """).ToList();
        foreach (var account in accounts)
        {
            var who = $"account {account.AccountId:D} / profile {account.ProfileId:D}";
            var profile = conn.QueryFirstOrDefault<ProfileSide>(
                "SELECT is_server_admin AS IsServerAdministrator, is_household_admin AS IsHouseholdAdministrator FROM profiles WHERE id = @ProfileId;",
                new { account.ProfileId });
            if (profile is null)
            {
                differences.Add($"{who}: the profile does not exist.");
                continue;
            }

            var expectedServer = account.IsAdministrator && account.OwnGrantAdmin;
            if (profile.IsServerAdministrator != expectedServer)
            {
                differences.Add($"{who}: server administrator is {profile.IsServerAdministrator} on the profile, expected {expectedServer}.");
            }

            if (profile.IsHouseholdAdministrator != account.HouseholdAdmin)
            {
                differences.Add($"{who}: household administrator is {profile.IsHouseholdAdministrator} on the profile, expected {account.HouseholdAdmin}.");
            }

            var oldFeatures = conn.Query<string>("SELECT feature_id FROM account_feature_grants WHERE account_id = @AccessAccountId ORDER BY feature_id;", new { account.AccessAccountId }).ToList();
            var newFeatures = conn.Query<string>("SELECT feature_id FROM profile_feature_grants WHERE profile_id = @ProfileId ORDER BY feature_id;", new { account.ProfileId }).ToList();
            if (!oldFeatures.SequenceEqual(newFeatures, StringComparer.Ordinal))
            {
                differences.Add($"{who}: features are [{string.Join(",", newFeatures)}] on the profile, expected [{string.Join(",", oldFeatures)}].");
            }

            var oldLibraries = conn.Query<Guid>("SELECT library_id FROM account_library_grants WHERE account_id = @AccessAccountId;", new { account.AccessAccountId }).ToHashSet();
            var newLibraries = conn.Query<Guid>("SELECT library_id FROM profile_library_grants WHERE profile_id = @ProfileId;", new { account.ProfileId }).ToHashSet();
            if (!oldLibraries.SetEquals(newLibraries))
            {
                differences.Add($"{who}: libraries differ ({newLibraries.Count} on the profile, expected {oldLibraries.Count}).");
            }

            var oldProtection = conn.QueryFirstOrDefault<ProtectionSide>("""
                SELECT is_enabled AS IsEnabled, unlock_mode AS UnlockMode, unlock_minutes AS UnlockMinutes
                FROM grant_admin_protections WHERE account_id = @AccountId AND profile_id = @ProfileId;
                """, new { account.AccountId, account.ProfileId });
            var newProtection = conn.QueryFirstOrDefault<ProtectionSide>("""
                SELECT is_enabled AS IsEnabled, unlock_mode AS UnlockMode, unlock_minutes AS UnlockMinutes
                FROM profile_admin_protections WHERE profile_id = @ProfileId;
                """, new { account.ProfileId });
            if (!Equals(oldProtection, newProtection))
            {
                differences.Add($"{who}: the admin PIN question differs (profile {Describe(newProtection)}, expected {Describe(oldProtection)}).");
            }
        }

        if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE is_default = 1;") > 1)
        {
            differences.Add("More than one household is marked as the default.");
        }

        return differences;
    }

    private static string Describe(ProtectionSide? side) =>
        side is null ? "none" : $"{(side.IsEnabled ? "on" : "off")}/{side.UnlockMode}/{side.UnlockMinutes?.ToString() ?? "-"}";

    private sealed class AccountSide
    {
        public Guid AccountId { get; set; }
        public Guid ProfileId { get; set; }
        public bool IsAdministrator { get; set; }
        public bool HouseholdAdmin { get; set; }
        public Guid AccessAccountId { get; set; }
        public bool OwnGrantAdmin { get; set; }
    }

    private sealed class ProfileSide
    {
        public bool IsServerAdministrator { get; set; }
        public bool IsHouseholdAdministrator { get; set; }
    }

    private sealed record ProtectionSide
    {
        public bool IsEnabled { get; set; }
        public string UnlockMode { get; set; } = string.Empty;
        public int? UnlockMinutes { get; set; }
    }
}
