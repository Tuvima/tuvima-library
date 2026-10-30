namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// A show, season, album or Series a person is deleting, as the Dashboard names it to the Engine. <see cref="Kind"/> is one of
/// <c>show</c>, <c>season</c>, <c>album</c> or <c>series</c>. <see cref="WorkIds"/> are the titles the page lists: the Engine uses them
/// only for a group it cannot find by id, and still checks each one.
/// </summary>
public sealed record LibraryGroupTarget(Guid GroupId, string Kind, string Title, IReadOnlyList<Guid> WorkIds);

/// <summary>The four kinds of group Delete reaches (spec Delete a show, season, album or Series).</summary>
public static class LibraryGroupKinds
{
    public const string Show = "show";
    public const string Season = "season";
    public const string Album = "album";
    public const string Series = "series";
}
