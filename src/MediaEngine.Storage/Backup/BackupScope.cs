namespace MediaEngine.Storage.Backup;

/// <summary>
/// What an export covers: the whole Library (every Configuration table and every person's state) or the personal
/// state of one profile (a profile export holds no Configuration).
/// </summary>
/// <param name="ProfileId">The profile whose state is exported, or null for the whole Library.</param>
public sealed record BackupScope(Guid? ProfileId)
{
    /// <summary>Every Configuration table and every person's state.</summary>
    public static BackupScope WholeLibrary { get; } = new((Guid?)null);

    /// <summary>The personal state of one profile only.</summary>
    public static BackupScope ForProfile(Guid id) =>
        id == Guid.Empty
            ? throw new ArgumentException("A profile id is required.", nameof(id))
            : new BackupScope(id);

    /// <summary>True when the scope is one profile's state.</summary>
    public bool IsProfile => ProfileId.HasValue;
}
