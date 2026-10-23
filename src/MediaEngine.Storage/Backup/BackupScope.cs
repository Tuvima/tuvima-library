namespace MediaEngine.Storage.Backup;

/// <summary>
/// What an export covers: the whole Library (every Configuration table and every person's state), the personal
/// state of one profile (a profile export holds no Configuration), or the people of a user file: one profile with the sign-in that opens
/// it, or a whole household (see <see cref="UserExportScope"/>).
/// </summary>
/// <param name="ProfileId">The profile whose state is exported, or null.</param>
/// <param name="AccountId">The user (sign-in) a one-profile user file is about, or null.</param>
/// <param name="LeaveOutTwoStep">True when a user file leaves two-step secrets out (someone else is exporting the person).</param>
/// <param name="HouseholdId">The household a household user file holds, or null.</param>
/// <param name="KeepTwoStepForAccountId">When two-step secrets are left out, the one user whose own secret stays (the person exporting themselves), or null.</param>
public sealed record BackupScope(
    Guid? ProfileId,
    Guid? AccountId = null,
    bool LeaveOutTwoStep = false,
    Guid? HouseholdId = null,
    Guid? KeepTwoStepForAccountId = null)
{
    /// <summary>Every Configuration table and every person's state.</summary>
    public static BackupScope WholeLibrary { get; } = new((Guid?)null);

    /// <summary>The personal state of one profile only.</summary>
    public static BackupScope ForProfile(Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException("A profile id is required.", nameof(id))
            : new BackupScope(id);

    /// <summary>One user: the sign-in, the one profile it opens and that profile's own state, with the two-step secret unless <paramref name="leaveOutTwoStep"/>.</summary>
    public static BackupScope ForAccount(Guid id, bool leaveOutTwoStep = false) =>
        id == Guid.Empty
            ? throw new ArgumentException("An account id is required.", nameof(id))
            : new BackupScope(null, id, leaveOutTwoStep);

    /// <summary>
    /// A whole household: every profile in it, their sign-ins and their own state. Two-step secrets are left out, except the secret of
    /// <paramref name="keepTwoStepForAccount"/> (the person exporting, when they are in the household).
    /// </summary>
    public static BackupScope ForHousehold(Guid id, Guid? keepTwoStepForAccount = null) =>
        id == Guid.Empty
            ? throw new ArgumentException("A household id is required.", nameof(id))
            : new BackupScope(null, null, true, id, keepTwoStepForAccount);

    /// <summary>True when the scope is one profile's state.</summary>
    public bool IsProfile => ProfileId.HasValue;

    /// <summary>True when the scope is one user and the profile it opens.</summary>
    public bool IsAccount => AccountId.HasValue;

    /// <summary>True when the scope is a whole household.</summary>
    public bool IsHousehold => HouseholdId.HasValue;

    /// <summary>True when the scope is a user file (one profile with its sign-in, or a household).</summary>
    public bool IsUserFile => IsAccount || IsHousehold;
}
