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
            "SELECT id AS Id, name AS Name, created_at AS CreatedAt, is_default AS IsDefault FROM households WHERE id = @householdId LIMIT 1;",
            new { householdId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<Household?> GetForAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HouseholdRow>("""
            SELECT h.id AS Id, h.name AS Name, h.created_at AS CreatedAt, h.is_default AS IsDefault
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN households h ON h.id = own.household_id
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
                   navigation_config AS NavigationConfig, household_id AS HouseholdId,
                   is_server_admin AS IsServerAdministrator, is_household_admin AS IsHouseholdAdministrator,
                   authorization_version AS AuthorizationVersion
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
            IsServerAdministrator = row.IsServerAdministrator,
            IsHouseholdAdministrator = row.IsHouseholdAdministrator,
            AuthorizationVersion = row.AuthorizationVersion,
        }).ToList();
        return Task.FromResult<IReadOnlyList<Profile>>(rows);
    }

    public Task<IReadOnlyList<Account>> ListAccountsAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<AccountRow>("""
            SELECT id AS Id, email AS Email, normalized_email AS NormalizedEmail, is_enabled AS IsEnabled,
                   authorization_version AS AuthorizationVersion,
                   created_at AS CreatedAt, updated_at AS UpdatedAt,
                   profile_id AS ProfileId
            FROM accounts
            WHERE (SELECT p.household_id FROM profiles p WHERE p.id = accounts.profile_id) = @householdId
            ORDER BY created_at, id;
            """, new { householdId }).ToList();
        var accounts = rows.Select(row => new Account
        {
            Id = row.Id,
            Email = row.Email,
            NormalizedEmail = row.NormalizedEmail,
            IsEnabled = row.IsEnabled,
            AuthorizationVersion = row.AuthorizationVersion,
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture),
            ProfileId = row.ProfileId,
        }).ToList();

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
            return DeleteEmptyHousehold(conn, tx, householdId);
        }, ct);

    /// <summary>True when the household's Shared Library in View holds shared photos (which keep the household from being removed).</summary>
    internal static bool HoldsSharedItems(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid householdId)
    {
        var sharedLibraryId = conn.ExecuteScalar<Guid?>(
            "SELECT library_id FROM view_shared_library WHERE household_id = @householdId;", new { householdId }, tx);
        return sharedLibraryId is { } libraryId && conn.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM local_items WHERE scope_kind = 'shared' AND library_id = @libraryId);",
            new { libraryId }, tx) == 1;
    }

    /// <summary>
    /// Deletes every shared photo and video of the household's Shared Library, on the caller's connection and transaction (users,
    /// profiles and households D18: they go with the household and are never moved into another one). Releases the share records and
    /// file locations that would otherwise hold them. Returns the files behind them, so the caller deletes the ones Tuvima owns from
    /// disk once the removal has committed. Does nothing for a household with no Shared Library.
    /// </summary>
    internal static IReadOnlyList<ProfilePersonalFile> DeleteSharedItems(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid householdId)
    {
        var libraryId = conn.ExecuteScalar<Guid?>(
            "SELECT library_id FROM view_shared_library WHERE household_id = @householdId;", new { householdId }, tx);
        if (libraryId is null)
        {
            return [];
        }

        var files = conn.Query<SharedFileRow>("""
            SELECT DISTINCT lf.id AS FileId, lfs.file_path AS FilePath,
                   CASE WHEN COALESCE(vs.storage_mode, 'linked') = 'managed' THEN 1 ELSE 0 END AS IsManaged
              FROM local_items li
              JOIN local_item_files lif ON lif.item_id = li.id
              JOIN local_files lf ON lf.id = lif.file_id
              JOIN local_file_sources lfs ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
              LEFT JOIN view_sources vs ON vs.id = lfs.source_id
             WHERE li.scope_kind = 'shared' AND li.library_id = @libraryId;
            """, new { libraryId }, tx)
            .Select(row => new ProfilePersonalFile(row.FileId, row.FilePath, row.IsManaged != 0))
            .ToList();
        conn.Execute("""
            DELETE FROM view_shared_assets
             WHERE item_id IN (SELECT id FROM local_items WHERE scope_kind = 'shared' AND library_id = @libraryId);
            DELETE FROM local_items WHERE scope_kind = 'shared' AND library_id = @libraryId;
            DELETE FROM local_file_sources WHERE library_id = @libraryId;
            """, new { libraryId }, tx);
        foreach (var fileId in files.Select(file => file.FileId).Distinct())
        {
            conn.Execute("""
                DELETE FROM local_files
                 WHERE id = @fileId AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId);
                """, new { fileId }, tx);
        }

        return files;
    }

    private sealed class SharedFileRow
    {
        public Guid FileId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public long IsManaged { get; init; }
    }

    /// <summary>
    /// The one removal rule for a household, on the caller's connection and transaction: never the default household, nothing may
    /// belong to it, and its Shared Library must hold no shared photos (an empty one goes with it). Used by
    /// <see cref="DeleteWhenEmptyAsync"/> and by the removal of a household with its last administrator's user.
    /// </summary>
    internal static HouseholdDeleteOutcome DeleteEmptyHousehold(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid householdId)
    {
        if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id = @householdId;", new { householdId }, tx) == 0)
        {
            return HouseholdDeleteOutcome.NotFound;
        }

        // The Library's own household (the server administrator's original one) is never removed.
        if (conn.ExecuteScalar<int>("SELECT is_default FROM households WHERE id = @householdId;", new { householdId }, tx) == 1)
        {
            return HouseholdDeleteOutcome.IsDefaultHousehold;
        }

        // A safety check. A user belongs to its profile's household.
        if (conn.ExecuteScalar<int>("""
            SELECT (SELECT COUNT(*) FROM accounts
                    WHERE profile_id IN (SELECT id FROM profiles WHERE household_id = @householdId))
                 + (SELECT COUNT(*) FROM profiles WHERE household_id = @householdId);
            """, new { householdId }, tx) > 0)
        {
            return HouseholdDeleteOutcome.NotEmpty;
        }

        // The household's Shared Library in View keeps the household while it holds shared photos. An empty one is
        // only a shell, so it goes with the household in this same step.
        var sharedLibraryId = conn.ExecuteScalar<Guid?>(
            "SELECT library_id FROM view_shared_library WHERE household_id = @householdId;", new { householdId }, tx);
        if (sharedLibraryId is { } libraryId)
        {
            if (conn.ExecuteScalar<int>(
                    "SELECT EXISTS (SELECT 1 FROM local_items WHERE scope_kind = 'shared' AND library_id = @libraryId);",
                    new { libraryId }, tx) == 1)
            {
                return HouseholdDeleteOutcome.HoldsSharedItems;
            }

            conn.Execute("DELETE FROM view_sources WHERE scope_kind = 'shared' AND library_id = @libraryId;", new { libraryId }, tx);
            conn.Execute("DELETE FROM view_shared_library WHERE household_id = @householdId;", new { householdId }, tx);
        }

        return conn.Execute("DELETE FROM households WHERE id = @householdId;", new { householdId }, tx) > 0
            ? HouseholdDeleteOutcome.Deleted
            : HouseholdDeleteOutcome.NotFound;
    }

    private const string SummarySelect = """
        SELECT h.id AS Id, h.name AS Name, h.created_at AS CreatedAt, h.is_default AS IsDefault,
               (SELECT COUNT(*) FROM profiles p WHERE p.household_id = h.id) AS ProfileCount,
               (SELECT COUNT(*) FROM accounts a JOIN profiles own ON own.id = a.profile_id WHERE own.household_id = h.id) AS AccountCount
        FROM households h
        """;

    private static string Iso(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static HouseholdSummary MapSummary(HouseholdSummaryRow row) =>
        new(new Household(row.Id, row.Name,
                DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture), row.IsDefault),
            row.ProfileCount, row.AccountCount);

    private static Household Map(HouseholdRow row) =>
        new(row.Id, row.Name, DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            row.IsDefault);

    private sealed class HouseholdRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
    }

    private sealed class HouseholdSummaryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
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
        public bool IsServerAdministrator { get; set; }
        public bool IsHouseholdAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
    }

    private sealed class AccountRow
    {
        public Guid Id { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public bool IsEnabled { get; set; }
        public long AuthorizationVersion { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public Guid ProfileId { get; set; }
    }
}
