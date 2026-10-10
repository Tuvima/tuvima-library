using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IProfileRepository"/>.
///
/// Spec: Settings &amp; Management Layer — Identity &amp; Multi-User.
/// </summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly IDatabaseConnection _db;

    public ProfileRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<ProfileRow>("""
            SELECT id           AS Id,
                   display_name AS DisplayName,
                   avatar_color AS AvatarColor,
                   avatar_image_path AS AvatarImagePath,
                   avatar_icon  AS AvatarIcon,
                   role         AS Role,
                   created_at   AS CreatedAt,
                   navigation_config AS NavigationConfig,
                   household_id AS HouseholdId
            FROM   profiles
            ORDER  BY created_at ASC;
            """).AsList();

        return Task.FromResult<IReadOnlyList<Profile>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public Task<Profile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ProfileRow>("""
            SELECT id           AS Id,
                   display_name AS DisplayName,
                   avatar_color AS AvatarColor,
                   avatar_image_path AS AvatarImagePath,
                   avatar_icon  AS AvatarIcon,
                   role         AS Role,
                   created_at   AS CreatedAt,
                   navigation_config AS NavigationConfig,
                   household_id AS HouseholdId
            FROM   profiles
            WHERE  id = @id
            LIMIT  1;
            """, new { id });

        return Task.FromResult(row is null ? null : MapRow(row));
    }

    /// <inheritdoc/>
    public Task<bool> UpdateAsync(Profile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);

        using var conn = _db.CreateConnection();
        var rows = conn.Execute("""
            UPDATE profiles
            SET    display_name      = @name,
                   avatar_color      = @color,
                   avatar_image_path = @avatarImagePath,
                   avatar_icon       = @avatarIcon,
                   navigation_config = @nav
            WHERE  id = @id;
            """, new
        {
            name = profile.DisplayName,
            color = profile.AvatarColor,
            avatarImagePath = profile.AvatarImagePath,
            avatarIcon = profile.AvatarIcon,
            nav = profile.NavigationConfig,
            id = profile.Id,
        });

        return Task.FromResult(rows > 0);
    }

    // ── Private DTO + mapper ────────────────────────────────────────────────
    private sealed class ProfileRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarColor { get; set; } = string.Empty;
        public string? AvatarImagePath { get; set; }
        public string? AvatarIcon { get; set; }
        public string Role { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string? NavigationConfig { get; set; }
        public Guid? HouseholdId { get; set; }
    }

    private static Profile MapRow(ProfileRow r) => new()
    {
        Id = r.Id,
        DisplayName = r.DisplayName,
        AvatarColor = r.AvatarColor,
        AvatarImagePath = r.AvatarImagePath,
        AvatarIcon = r.AvatarIcon,
        Role = Enum.Parse<ProfileRole>(r.Role),
        CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
        NavigationConfig = r.NavigationConfig,
        HouseholdId = r.HouseholdId,
    };
}
