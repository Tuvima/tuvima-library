using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IHouseholdRepository"/>.</summary>
public sealed class HouseholdRepository(IDatabaseConnection db) : IHouseholdRepository
{
    public Task<Household?> GetByIdAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HouseholdRow>(
            "SELECT id AS Id, name AS Name, created_at AS CreatedAt, primary_account_id AS PrimaryAccountId FROM households WHERE id = @householdId LIMIT 1;",
            new { householdId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<Household?> GetForAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HouseholdRow>("""
            SELECT h.id AS Id, h.name AS Name, h.created_at AS CreatedAt, h.primary_account_id AS PrimaryAccountId
            FROM accounts a JOIN households h ON h.id = a.household_id
            WHERE a.id = @accountId LIMIT 1;
            """, new { accountId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<Profile>> ListProfilesAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<ProfileRow>("""
            SELECT id AS Id, display_name AS DisplayName, avatar_color AS AvatarColor,
                   avatar_image_path AS AvatarImagePath, avatar_icon AS AvatarIcon,
                   content_limit AS ContentLimit, content_limit_allow_unrated AS ContentLimitAllowUnrated, role AS Role, created_at AS CreatedAt,
                   navigation_config AS NavigationConfig, household_id AS HouseholdId
            FROM profiles WHERE household_id = @householdId
            ORDER BY created_at, id;
            """, new { householdId }).Select(row => new Profile
        {
            Id = row.Id,
            DisplayName = row.DisplayName,
            AvatarColor = row.AvatarColor,
            AvatarImagePath = row.AvatarImagePath,
            AvatarIcon = row.AvatarIcon,
            ContentLimit = row.ContentLimit,
            ContentLimitAllowUnrated = row.ContentLimitAllowUnrated,
            Role = Enum.Parse<MediaEngine.Domain.Enums.ProfileRole>(row.Role),
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            NavigationConfig = row.NavigationConfig,
            HouseholdId = row.HouseholdId,
        }).ToList();
        return Task.FromResult<IReadOnlyList<Profile>>(rows);
    }

    public Task<IReadOnlyList<Account>> ListAccountsAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<AccountRow>("""
            SELECT id AS Id, email AS Email, normalized_email AS NormalizedEmail, is_enabled AS IsEnabled,
                   is_administrator AS IsAdministrator, authorization_version AS AuthorizationVersion,
                   created_at AS CreatedAt, updated_at AS UpdatedAt, household_id AS HouseholdId,
                   household_admin AS HouseholdAdmin, grants_inherit_from_account_id AS GrantsInheritFromAccountId
            FROM accounts WHERE household_id = @householdId
            ORDER BY created_at, id;
            """, new { householdId }).ToList();
        var accounts = new List<Account>(rows.Count);
        foreach (var row in rows)
        {
            var account = new Account
            {
                Id = row.Id,
                Email = row.Email,
                NormalizedEmail = row.NormalizedEmail,
                IsEnabled = row.IsEnabled,
                IsAdministrator = row.IsAdministrator,
                AuthorizationVersion = row.AuthorizationVersion,
                CreatedAt = DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture),
                HouseholdId = row.HouseholdId,
                GrantsInheritFromAccountId = row.GrantsInheritFromAccountId,
            };
            if (row.HouseholdAdmin && row.GrantsInheritFromAccountId is null)
            {
                account.MakeHouseholdAdmin();
            }

            accounts.Add(account);
        }

        return Task.FromResult<IReadOnlyList<Account>>(accounts);
    }

    public Task<IReadOnlyList<HouseholdSummary>> ListSummariesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<HouseholdSummaryRow>(SummarySelect + " ORDER BY h.name COLLATE NOCASE, h.created_at, h.id;")
            .Select(MapSummary).ToList();
        return Task.FromResult<IReadOnlyList<HouseholdSummary>>(rows);
    }

    public Task<HouseholdSummary?> GetSummaryAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HouseholdSummaryRow>(SummarySelect + " WHERE h.id = @householdId LIMIT 1;",
            new { householdId });
        return Task.FromResult(row is null ? null : MapSummary(row));
    }

    public Task<Household> CreateAsync(string name, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        var household = new Household(Guid.NewGuid(), name, createdAt);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("INSERT INTO households(id,name,created_at) VALUES(@Id,@Name,@createdAt);",
                new { household.Id, household.Name, createdAt = Iso(household.CreatedAt) }, tx);
            return household;
        }, ct);
    }

    public Task<bool> RenameAsync(Guid householdId, string name, CancellationToken ct = default)
    {
        var trimmed = new Household(householdId, name, DateTimeOffset.UnixEpoch).Name;
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return conn.Execute("UPDATE households SET name=@trimmed WHERE id=@householdId;",
                new { trimmed, householdId }, tx) > 0;
        }, ct);
    }

    public Task<HouseholdDeleteOutcome> DeleteWhenEmptyAsync(Guid householdId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id = @householdId;", new { householdId }, tx) == 0)
            {
                return HouseholdDeleteOutcome.NotFound;
            }

            if (conn.ExecuteScalar<int>("""
                SELECT (SELECT COUNT(*) FROM accounts WHERE household_id = @householdId)
                     + (SELECT COUNT(*) FROM profiles WHERE household_id = @householdId);
                """, new { householdId }, tx) > 0)
            {
                return HouseholdDeleteOutcome.NotEmpty;
            }

            // The household's Shared Library in View keeps the household while it holds shared photos. The data store
            // does not yet allow removing even an empty Shared Library row (trg_view_shared_library_delete), so an empty
            // one still keeps the household until a data-store step relaxes that trigger.
            var sharedLibraryId = conn.ExecuteScalar<Guid?>(
                "SELECT library_id FROM view_shared_library WHERE household_id = @householdId;", new { householdId }, tx);
            if (sharedLibraryId is { } libraryId)
            {
                return conn.ExecuteScalar<int>(
                        "SELECT EXISTS (SELECT 1 FROM local_items WHERE scope_kind = 'shared' AND library_id = @libraryId);",
                        new { libraryId }, tx) == 1
                    ? HouseholdDeleteOutcome.HoldsSharedItems
                    : HouseholdDeleteOutcome.HoldsSharedLibrary;
            }

            return conn.Execute("DELETE FROM households WHERE id = @householdId;", new { householdId }, tx) > 0
                ? HouseholdDeleteOutcome.Deleted
                : HouseholdDeleteOutcome.NotFound;
        }, ct);

    private const string SummarySelect = """
        SELECT h.id AS Id, h.name AS Name, h.created_at AS CreatedAt, h.primary_account_id AS PrimaryAccountId,
               (SELECT COUNT(*) FROM profiles p WHERE p.household_id = h.id) AS ProfileCount,
               (SELECT COUNT(*) FROM accounts a WHERE a.household_id = h.id) AS AccountCount
        FROM households h
        """;

    private static string Iso(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static HouseholdSummary MapSummary(HouseholdSummaryRow row) =>
        new(new Household(row.Id, row.Name,
                DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture), row.PrimaryAccountId),
            row.ProfileCount, row.AccountCount);

    private static Household Map(HouseholdRow row) =>
        new(row.Id, row.Name, DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            row.PrimaryAccountId);

    private sealed class HouseholdRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public Guid? PrimaryAccountId { get; set; }
    }

    private sealed class HouseholdSummaryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public Guid? PrimaryAccountId { get; set; }
        public int ProfileCount { get; set; }
        public int AccountCount { get; set; }
    }

    private sealed class ProfileRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarColor { get; set; } = string.Empty;
        public string? AvatarImagePath { get; set; }
        public string? AvatarIcon { get; set; }
        public string? ContentLimit { get; set; }
        public bool ContentLimitAllowUnrated { get; set; }
        public string Role { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string? NavigationConfig { get; set; }
        public Guid? HouseholdId { get; set; }
    }

    private sealed class AccountRow
    {
        public Guid Id { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public Guid? HouseholdId { get; set; }
        public bool HouseholdAdmin { get; set; }
        public Guid? GrantsInheritFromAccountId { get; set; }
    }
}
