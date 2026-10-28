namespace MediaEngine.Contracts.Editing;

/// <summary>
/// The special values an artwork change can carry in <see cref="EditSessionChange.TargetId"/> besides the id of a stored picture.
/// </summary>
public static class EditSessionArtworkTargets
{
    /// <summary>Remove the chosen picture for the slot. The pictures stay saved in the library, so choosing one again brings it back.</summary>
    public const string None = "none";
}
