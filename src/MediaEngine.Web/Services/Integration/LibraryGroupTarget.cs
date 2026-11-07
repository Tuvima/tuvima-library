namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// A show, season, album or Series a person is deleting, as the Dashboard names it to the Engine. <see cref="Kind"/> is one of
/// <c>show</c>, <c>season</c>, <c>album</c> or <c>series</c>. <see cref="WorkIds"/> are the titles the page lists: the Engine uses them
/// only for a group it cannot find by id, and still checks each one.
/// </summary>
/// <param name="IsVideoFolder">The group is a folder of Other videos (it is called a folder in menus and dialogs, never a show or Series).</param>
/// <param name="SupportingFileCount">How many supporting files go with the group, when the editor's header knew (universal editor v5 D29); 0 says nothing.</param>
public sealed record LibraryGroupTarget(Guid GroupId, string Kind, string Title, IReadOnlyList<Guid> WorkIds, bool IsVideoFolder = false, int SupportingFileCount = 0);

/// <summary>The four kinds of group Delete reaches (spec Delete a show, season, album or Series).</summary>
public static class LibraryGroupKinds
{
    public const string Show = "show";
    public const string Season = "season";
    public const string Album = "album";
    public const string Series = "series";
}

/// <summary>
/// One file of one item the person is deleting from the editor's Files tab. The Engine decides what happens to the item: it stays with
/// its other files, or leaves the Library when this was its last file.
/// </summary>
public sealed record LibraryFileTarget(Guid WorkId, Guid AssetId, string FileName);
