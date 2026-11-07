using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Which Details fields start locked (universal editor v5 D5). Two different things share the word "lock", so they are named apart here:
/// <list type="bullet">
/// <item>The <b>form lock</b> (new default) only stops accidental typing. A provider field nobody changed stays the provider's, so a later
/// provider refresh or re-match still updates it. Unlocking and locking again stages nothing.</item>
/// <item>A field is <b>yours</b> once it was unlocked, changed and saved: it is stored as the person's own display override, which no
/// provider refresh or re-match replaces, and it shows Revert (Revert goes back to the provider's value and the field locks again).</item>
/// </list>
/// A person's own fields (library tags, sort title, the Explicit mark) hold nothing a provider fills, so they never lock.
/// </summary>
public static class EditorFieldLocks
{
    private static readonly HashSet<string> OwnKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "custom_tags", "sort_title", "sort_name", "explicit",
    };

    /// <summary>True for a field only the person fills (tags, sort title, the Explicit mark): no provider value to protect, so no form lock.</summary>
    public static bool IsOwnField(string? key) => !string.IsNullOrWhiteSpace(key) && OwnKeys.Contains(key.Trim());

    /// <summary>
    /// True when a field of a person, Series, curated collection or Universe is the person's own. A curated collection and a Universe's
    /// character, place or organisation are kept only locally, and a person's tags and sort name are the library's own; the rest are
    /// provider values the person overrides (a person's name and biography, a Series' title and description, a Universe's name).
    /// </summary>
    public static bool IsOwnSubjectField(string subjectKind, string? key) =>
        string.Equals(subjectKind, EditorSubjectKinds.Collection, StringComparison.Ordinal)
        || string.Equals(subjectKind, EditorSubjectKinds.FictionalEntity, StringComparison.Ordinal)
        || IsOwnField(key);
}
