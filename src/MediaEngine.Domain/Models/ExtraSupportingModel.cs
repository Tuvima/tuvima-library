using System.Globalization;

namespace MediaEngine.Domain.Models;

/// <summary>
/// What a supporting file is, as a person says it (universal editor v5 D11, D14): one fixed list per kind of file. The ids are stable
/// words; the lists a person may pick from (and the labels shown) live in <c>config/extras.json</c>. The v4 "What is this file?" kinds
/// (<see cref="ExtraKinds"/>) map onto these.
/// </summary>
public static class ExtraRoles
{
    // Documents.
    public const string Guide = "guide";
    public const string Worksheet = "worksheet";
    public const string Transcript = "transcript";
    public const string Notes = "notes";
    public const string Booklet = "booklet";
    public const string Other = "other";

    // Videos and other clips.
    public const string Trailer = "trailer";
    public const string Featurette = "featurette";
    public const string BehindTheScenes = "behind_the_scenes";
    public const string DeletedScene = "deleted_scene";
    public const string Interview = "interview";
    public const string Clip = "clip";

    // Pictures.
    public const string Pictures = "pictures";

    /// <summary>The role a file of this kind has when nothing in its names says otherwise.</summary>
    public static string DefaultFor(string fileKind) => fileKind switch
    {
        ItemExtraKinds.Document => Other,
        ItemExtraKinds.Picture => Pictures,
        _ => Clip,
    };

    /// <summary>
    /// The role a v4 kind amounts to, or null when it says nothing about the role (a v4 "Document" never chose between Guide, Worksheet and
    /// the rest, so it leaves the guess alone).
    /// </summary>
    public static string? FromExtraKind(string? extraKind, string fileKind) => extraKind switch
    {
        ExtraKinds.Trailer => Trailer,
        ExtraKinds.Featurette => Featurette,
        ExtraKinds.BehindTheScenes => BehindTheScenes,
        ExtraKinds.DeletedScene => DeletedScene,
        ExtraKinds.Interview => Interview,
        ExtraKinds.OtherClip => Clip,
        ExtraKinds.Pictures => Pictures,
        _ => null,
    };

    /// <summary>The v4 kind a role projects to, so the places that still read <see cref="ExtraKinds"/> keep working.</summary>
    public static string ToExtraKind(string role, string fileKind) => role switch
    {
        Trailer => ExtraKinds.Trailer,
        Featurette => ExtraKinds.Featurette,
        BehindTheScenes => ExtraKinds.BehindTheScenes,
        DeletedScene => ExtraKinds.DeletedScene,
        Interview => ExtraKinds.Interview,
        Pictures => ExtraKinds.Pictures,
        Clip => ExtraKinds.OtherClip,
        _ => ExtraKinds.DefaultFor(fileKind),
    };

    /// <summary>The words shown for a role when the configuration does not give any ("Behind the scenes").</summary>
    public static string Label(string? role) => role switch
    {
        Guide => "Guide",
        Worksheet => "Worksheet",
        Transcript => "Transcript",
        Notes => "Notes",
        Booklet => "Booklet",
        Trailer => "Trailer",
        Featurette => "Featurette",
        BehindTheScenes => "Behind the scenes",
        DeletedScene => "Deleted scene",
        Interview => "Interview",
        Clip => "Clip",
        Pictures => "Pictures",
        _ => "Other",
    };
}

/// <summary>How far down a title a supporting file belongs.</summary>
public enum ExtraScopeLevel
{
    /// <summary>The whole show, course, movie, album or book.</summary>
    Root = 0,

    /// <summary>A season of a show (0 is Specials).</summary>
    Season,

    /// <summary>One episode of a show, named by season and episode number.</summary>
    Episode,

    /// <summary>A section of a course (or any sub-folder of the title), named by its folder path below the title folder.</summary>
    Folder,

    /// <summary>One lesson, episode or track, named by its file path below the title folder.</summary>
    File,

    /// <summary>One track of an album.</summary>
    Track,

    /// <summary>Only for "shown alongside": a person said it goes with no episode or lesson, overriding a guess.</summary>
    None,
}

/// <summary>
/// What a supporting file belongs to, or is shown alongside (D13, D14, 3b): the title itself, a season or section, or one episode or
/// lesson. It is a plain value that stays true when the title is matched or re-matched: seasons and episodes by number, sections and
/// lessons by their path below the title folder (always written with forward slashes). <see cref="Key"/> is the one text form used in
/// storage and on the wire: <c>root</c>, <c>season:2</c>, <c>episode:2:3</c>, <c>folder:Section 1</c>, <c>file:Section 1/02 Intro.mkv</c>,
/// <c>track:4</c>, <c>none</c>.
/// </summary>
public readonly record struct ExtraScope(ExtraScopeLevel Level, int? Season = null, int? Episode = null, string? Path = null)
{
    /// <summary>The whole title.</summary>
    public static ExtraScope Root => default;

    /// <summary>"Shown alongside nothing", for an override of a guess.</summary>
    public static ExtraScope None => new(ExtraScopeLevel.None);

    public static ExtraScope ForSeason(int season) => new(ExtraScopeLevel.Season, season);

    public static ExtraScope ForEpisode(int season, int episode) => new(ExtraScopeLevel.Episode, season, episode);

    public static ExtraScope ForTrack(int track) => new(ExtraScopeLevel.Track, null, track);

    public static ExtraScope ForFolder(string relativePath) => new(ExtraScopeLevel.Folder, null, null, Normalize(relativePath));

    public static ExtraScope ForFile(string relativePath) => new(ExtraScopeLevel.File, null, null, Normalize(relativePath));

    /// <summary>The text form kept in storage and sent over the wire.</summary>
    public string Key => Level switch
    {
        ExtraScopeLevel.Season => $"season:{Number(Season)}",
        ExtraScopeLevel.Episode => $"episode:{Number(Season)}:{Number(Episode)}",
        ExtraScopeLevel.Folder => $"folder:{Path}",
        ExtraScopeLevel.File => $"file:{Path}",
        ExtraScopeLevel.Track => $"track:{Number(Episode)}",
        ExtraScopeLevel.None => "none",
        _ => "root",
    };

    /// <summary>Reads a <see cref="Key"/>. Returns false for text that is not one.</summary>
    public static bool TryParse(string? key, out ExtraScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var text = key.Trim();
        if (text.Equals("root", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Equals("none", StringComparison.Ordinal))
        {
            scope = None;
            return true;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var head = text[..colon];
        var tail = text[(colon + 1)..];
        switch (head)
        {
            case "season" when TryNumber(tail, out var season):
                scope = ForSeason(season);
                return true;
            case "episode":
                var parts = tail.Split(':');
                if (parts.Length == 2 && TryNumber(parts[0], out var s) && TryNumber(parts[1], out var e))
                {
                    scope = ForEpisode(s, e);
                    return true;
                }

                return false;
            case "track" when TryNumber(tail, out var track):
                scope = ForTrack(track);
                return true;
            case "folder" when Normalize(tail).Length > 0:
                scope = ForFolder(tail);
                return true;
            case "file" when Normalize(tail).Length > 0:
                scope = ForFile(tail);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads a <see cref="Key"/>, or null for empty or unreadable text.</summary>
    public static ExtraScope? ParseOrNull(string? key) => TryParse(key, out var scope) ? scope : null;

    /// <summary>"Season 2", "Specials", "Season 2, episode 3", "Track 4", "Section 1", "02 Intro", "Whole title".</summary>
    public string Label() => Level switch
    {
        ExtraScopeLevel.Season => ExtraPlaces.Label(Season, null, null),
        ExtraScopeLevel.Episode => ExtraPlaces.Label(Season, Episode, null),
        ExtraScopeLevel.Track => ExtraPlaces.Label(null, null, Episode),
        ExtraScopeLevel.Folder => LastName(Path),
        ExtraScopeLevel.File => System.IO.Path.GetFileNameWithoutExtension(LastName(Path)),
        ExtraScopeLevel.None => string.Empty,
        _ => string.Empty,
    };

    public override string ToString() => Key;

    private static string Normalize(string? path) =>
        string.Join('/', (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string LastName(string? path) =>
        (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    private static string Number(int? value) => (value ?? 0).ToString(CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>Where the value of "Belongs to" came from (D13): the file's place on disk, or a person.</summary>
public static class ExtraScopeSources
{
    /// <summary>"From its folder location".</summary>
    public const string Folder = "folder";

    /// <summary>"Set by you".</summary>
    public const string You = "you";
}

/// <summary>Where the group of a supporting file came from.</summary>
public static class ExtraGroupSources
{
    /// <summary>A named sub-folder under Extras, Documents or Materials gave the group (3b step 4); a rescan may change it.</summary>
    public const string Folder = "folder";

    /// <summary>A person chose the group; a rescan never changes it.</summary>
    public const string You = "you";
}

/// <summary>
/// A group a person (or the folder layout) named for a title's supporting files ("Character guides", "Homework"). Groups are virtual:
/// they never depend on, or change, the folder layout. One set per root title (<see cref="ItemFolder"/>), stored with it and deleted with it.
/// </summary>
public sealed record ExtraGroup(Guid Id, string ItemFolder, string Name, int SortOrder, int FileCount = 0);

/// <summary>What a change does to one field of <see cref="ExtraOwnedUpdate"/>.</summary>
public enum ExtraValueState
{
    /// <summary>The field is not part of the change.</summary>
    Unchanged = 0,

    /// <summary>The field becomes the value.</summary>
    Set,

    /// <summary>The field is cleared, so the value read from the folder and file names applies again.</summary>
    Cleared,
}

/// <summary>One field of <see cref="ExtraOwnedUpdate"/>: left alone, set, or cleared. (A blank text counts as cleared.)</summary>
public readonly record struct ExtraValue<T>(ExtraValueState State, T? Value)
{
    /// <summary>The field is not part of the change.</summary>
    public static ExtraValue<T> Unchanged => default;

    /// <summary>The field becomes <paramref name="value"/>.</summary>
    public static ExtraValue<T> Set(T value) => new(ExtraValueState.Set, value);

    /// <summary>The field is cleared, so the value read from the folder and file names applies again.</summary>
    public static ExtraValue<T> Clear => new(ExtraValueState.Cleared, default);

    /// <summary>True when the field is part of the change (set or cleared).</summary>
    public bool IsChanged => State != ExtraValueState.Unchanged;

    /// <summary>True when the field is being set to <see cref="Value"/>.</summary>
    public bool IsSet => State == ExtraValueState.Set;
}

/// <summary>
/// A change to the values a person owns on one or many supporting files (D11, D16 bulk actions). Only the fields that are set change.
/// Clearing a field puts the inferred value back (the title falls back to the file name, the role to the guess, the scope to
/// "From its folder location", the group to the one read from the folder). Nothing here moves, renames or rewrites a file.
/// </summary>
public sealed record ExtraOwnedUpdate
{
    /// <summary>The person's title; clear to show the cleaned file name again.</summary>
    public ExtraValue<string> Title { get; init; }

    /// <summary>The person's description.</summary>
    public ExtraValue<string> Description { get; init; }

    /// <summary>The group's name. A name no group has yet creates the group; clear returns to the group read from the folder, if any.</summary>
    public ExtraValue<string> GroupName { get; init; }

    /// <summary>The role (an <see cref="ExtraRoles"/> id); clear returns to the guessed role.</summary>
    public ExtraValue<string> Role { get; init; }

    /// <summary>Belongs to; clear is "Use folder location".</summary>
    public ExtraValue<ExtraScope> Scope { get; init; }

    /// <summary>Shown alongside; set <see cref="ExtraScope.None"/> to say "no episode or lesson", clear to return to the guess.</summary>
    public ExtraValue<ExtraScope> Alongside { get; init; }

    /// <summary>Tags to add (existing tags are matched by name, ignoring case; unknown names create a tag).</summary>
    public IReadOnlyList<string> AddTags { get; init; } = [];

    /// <summary>Tags to take off these files.</summary>
    public IReadOnlyList<string> RemoveTags { get; init; } = [];

    /// <summary>When not null, the complete tag list (replaces whatever the files had).</summary>
    public IReadOnlyList<string>? ReplaceTags { get; init; }

    /// <summary>True when the change names no field at all.</summary>
    public bool IsEmpty =>
        !Title.IsChanged && !Description.IsChanged && !GroupName.IsChanged && !Role.IsChanged && !Scope.IsChanged && !Alongside.IsChanged
        && AddTags.Count == 0 && RemoveTags.Count == 0 && ReplaceTags is null;
}
