namespace MediaEngine.Storage.Backup;

/// <summary>
/// What an export covers: the whole Library (every Configuration table and every person's state), the personal
/// state of one profile (a profile export holds no Configuration), or one user: an account, every profile it may use and
/// those profiles' own state (see <see cref="UserExportScope"/>).
/// </summary>
/// <param name="ProfileId">The profile whose state is exported, or null.</param>
/// <param name="AccountId">The account a user export is about, or null.</param>
public sealed record BackupScope(Guid? ProfileId, Guid? AccountId = null)
{
    /// <summary>Every Configuration table and every person's state.</summary>
    public static BackupScope WholeLibrary { get; } = new((Guid?)null);

    /// <summary>The personal state of one profile only.</summary>
    public static BackupScope ForProfile(Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException("A profile id is required.", nameof(id))
            : new BackupScope(id);

    /// <summary>One user: the account, its profiles and their own state.</summary>
    public static BackupScope ForAccount(Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException("An account id is required.", nameof(id))
            : new BackupScope(null, id);

    /// <summary>True when the scope is one profile's state.</summary>
    public bool IsProfile => ProfileId.HasValue;

    /// <summary>True when the scope is one user.</summary>
    public bool IsAccount => AccountId.HasValue;
}
