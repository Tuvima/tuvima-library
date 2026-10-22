namespace MediaEngine.Web.Components.Settings;

/// <summary>The words of the Delete user confirmation, so every screen that deletes a user says the same thing.</summary>
internal static class UserDeletionText
{
    /// <summary>
    /// "Delete Sam? This removes Sam's sign-in and their profiles Sam and Sam (Kids), with their progress, bookmarks and lists.
    /// Media files are kept." The names are the profiles the Engine says will go with the user.
    /// </summary>
    public static string Confirmation(string label, IReadOnlyList<string> profileNames) =>
        profileNames.Count == 0
            ? $"Delete {label}? This removes {label}'s sign-in. Media files are kept."
            : $"Delete {label}? This removes {label}'s sign-in and {(profileNames.Count == 1 ? "their profile" : "their profiles")} "
              + $"{Joined(profileNames)}, with their progress, bookmarks and lists. Media files are kept.";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string Joined(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    public static string Done(string label) => $"{label} was deleted. Media files were kept.";
}
