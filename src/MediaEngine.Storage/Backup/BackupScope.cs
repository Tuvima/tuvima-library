namespace MediaEngine.Storage.Backup;

/// <summary>
/// What an export covers: the whole Library (every Configuration table and every person's state), the personal
/// state of one profile (a profile export holds no Configuration), or one user: an account, every profile it may use and
/// those profiles' own state (see <see cref="UserExportScope"/>).
/// </summary>
/// <param name="ProfileId">The profile whose state is exported, or null.</param>
/// <param name="AccountId">The account a user export is about, or null.</param>
/// <param name="LeaveOutTwoStep">True when a user export leaves the account's two-step secret out (an administrator exporting someone else).</param>
public sealed record BackupScope(Guid? ProfileId, Guid? AccountId = null, bool LeaveOutTwoStep = false)
{
    /// <summary>Every Configuration table and every person's state.</summary>
    public static BackupScope WholeLibrary { get; } = new((Guid?)null);

    /// <summary>The personal state of one profile only.</summary>
    public static BackupScope ForProfile(Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException("A profile id is required.", nameof(id))
            : new BackupScope(id);

    /// <summary>One user: the account, its profiles and their own state, with the two-step secret unless <paramref name="leaveOutTwoStep"/>.</summary>
    public static BackupScope ForAccount(Guid id, bool leaveOutTwoStep = false) =>
        id == Guid.Empty
            ? throw new ArgumentException("An account id is required.", nameof(id))
            : new BackupScope(null, id, leaveOutTwoStep);

    /// <summary>True when the scope is one profile's state.</summary>
    public bool IsProfile => ProfileId.HasValue;

    /// <summary>True when the scope is one user.</summary>
    public bool IsAccount => AccountId.HasValue;
}
