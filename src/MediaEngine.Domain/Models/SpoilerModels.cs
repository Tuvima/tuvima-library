namespace MediaEngine.Domain.Models;

/// <summary>How much of a story a profile wants to be told about: only what it has reached, or everything.</summary>
public enum SpoilerLevel
{
    /// <summary>"Up to where I am": characters appear only once the profile has reached them. The default.</summary>
    UpToProgress = 0,

    /// <summary>"Everything": no spoiler protection.</summary>
    Everything = 1,
}

/// <summary>The stored and wire spellings of <see cref="SpoilerLevel"/>.</summary>
public static class SpoilerLevels
{
    public const string UpToProgress = "up_to_progress";
    public const string Everything = "everything";

    /// <summary>The level a profile has before it chooses one.</summary>
    public const string Default = UpToProgress;

    /// <summary>Whether <paramref name="value"/> is one of the two stored spellings (any letter case).</summary>
    public static bool IsValid(string? value) =>
        string.Equals(value, UpToProgress, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Everything, StringComparison.OrdinalIgnoreCase);

    /// <summary>The level a stored spelling names; anything unrecognised is the safe default, "up to where I am".</summary>
    public static SpoilerLevel Parse(string? value) =>
        string.Equals(value?.Trim(), Everything, StringComparison.OrdinalIgnoreCase)
            ? SpoilerLevel.Everything
            : SpoilerLevel.UpToProgress;

    /// <summary>The stored spelling of a level.</summary>
    public static string ToWire(SpoilerLevel level) =>
        level == SpoilerLevel.Everything ? Everything : UpToProgress;
}

/// <summary>What the spoiler rule lets a surface show of one character.</summary>
public enum CharacterVisibility
{
    /// <summary>The character, with everything the surface normally shows.</summary>
    Shown = 0,

    /// <summary>
    /// The character's name and source only, labelled "No plot details": no descriptions and no work count. Its "Also appears in"
    /// still opens, because that lists Works, not plot.
    /// </summary>
    NoPlotDetails = 1,

    /// <summary>Left out entirely; surfaces say only how many characters were held back.</summary>
    Hidden = 2,
}

/// <summary>An episode's place in a show: season, then episode. Orders by season, then episode.</summary>
public readonly record struct EpisodePosition(int Season, int Episode) : IComparable<EpisodePosition>
{
    public int CompareTo(EpisodePosition other) =>
        Season != other.Season ? Season.CompareTo(other.Season) : Episode.CompareTo(other.Episode);

    public static bool operator <(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) < 0;

    public static bool operator >(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(EpisodePosition left, EpisodePosition right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A place in a story the Library owns: a title (a show, a film, a book) and, for a show whose episodes are owned and
/// identified, the episode. No episode means "somewhere within the title"; books and audiobooks never have one because no
/// source gives chapters.
/// </summary>
/// <param name="TitleId">The title's work id (the show for an episode); <see cref="Guid.Empty"/> for a place the profile cannot have reached.</param>
/// <param name="Episode">The owned episode, or null when the position within the title is unknown.</param>
public sealed record WorkPosition(Guid TitleId, EpisodePosition? Episode = null)
{
    /// <summary>A work the Library does not own: it can never count as reached or finished.</summary>
    public static readonly WorkPosition Unowned = new(Guid.Empty);
}

/// <summary>How far one profile has got in one title.</summary>
/// <param name="Started">Whether any part has been started (a started episode of a show counts).</param>
/// <param name="Finished">Whether the whole title is finished (every owned episode, for a show).</param>
/// <param name="FurthestStartedEpisode">The latest episode started; null for a film or book, or a show with none started.</param>
/// <param name="FurthestFinishedEpisode">The latest episode finished; null when none is.</param>
public sealed record TitleProgress(
    bool Started,
    bool Finished,
    EpisodePosition? FurthestStartedEpisode = null,
    EpisodePosition? FurthestFinishedEpisode = null)
{
    /// <summary>Nothing started.</summary>
    public static readonly TitleProgress None = new(false, false);
}

/// <summary>How far one profile has got in every title it has started; any other title is <see cref="TitleProgress.None"/>.</summary>
public sealed class ViewerProgress(IReadOnlyDictionary<Guid, TitleProgress> byTitle)
{
    /// <summary>A viewer who has started nothing.</summary>
    public static ViewerProgress Empty { get; } = new(new Dictionary<Guid, TitleProgress>());

    public TitleProgress For(Guid titleId) =>
        byTitle.TryGetValue(titleId, out var progress) ? progress : TitleProgress.None;
}
