namespace MediaEngine.Domain.Models;

/// <summary>The kinds of Extra: what opens it (the reader, the photo viewer or the video player).</summary>
public static class ItemExtraKinds
{
    public const string Document = "document";
    public const string Picture = "picture";
    public const string Clip = "clip";

    public static bool IsKnown(string? kind) =>
        kind is Document or Picture or Clip;
}

/// <summary>
/// What an Extra is, as a person would say it (universal editor v4 D20): the kind shown on its card and chosen in "What is this file?".
/// Separate from <see cref="ItemExtraKinds"/>, which says what opens the file. A rule-found Extra has none stored and reads as the
/// default for its file type (<see cref="DefaultFor"/>).
/// </summary>
public static class ExtraKinds
{
    public const string Trailer = "trailer";
    public const string Featurette = "featurette";
    public const string BehindTheScenes = "behind_the_scenes";
    public const string DeletedScene = "deleted_scene";
    public const string Interview = "interview";
    public const string Document = "document";
    public const string Pictures = "pictures";
    public const string OtherClip = "other_clip";

    /// <summary>Every kind, in the order the picker shows them.</summary>
    public static readonly IReadOnlyList<string> All =
        [Trailer, Featurette, BehindTheScenes, DeletedScene, Interview, Document, Pictures, OtherClip];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>The kinds that make sense for a file that opens as <paramref name="fileKind"/> (an <see cref="ItemExtraKinds"/> value).</summary>
    public static IReadOnlyList<string> ForFile(string fileKind) => fileKind switch
    {
        ItemExtraKinds.Document => [Document],
        ItemExtraKinds.Picture => [Pictures],
        _ => [Trailer, Featurette, BehindTheScenes, DeletedScene, Interview, OtherClip],
    };

    /// <summary>True when <paramref name="kind"/> is a kind a file of <paramref name="fileKind"/> can be.</summary>
    public static bool Fits(string fileKind, string? kind) => kind is not null && ForFile(fileKind).Contains(kind, StringComparer.Ordinal);

    /// <summary>What an Extra with no stored kind reads as: a document, pictures, or another clip.</summary>
    public static string DefaultFor(string fileKind) => fileKind switch
    {
        ItemExtraKinds.Document => Document,
        ItemExtraKinds.Picture => Pictures,
        _ => OtherClip,
    };

    /// <summary>The words shown for a kind ("Behind the scenes").</summary>
    public static string Label(string? kind) => kind switch
    {
        Trailer => "Trailer",
        Featurette => "Featurette",
        BehindTheScenes => "Behind the scenes",
        DeletedScene => "Deleted scene",
        Interview => "Interview",
        Document => "Document",
        Pictures => "Pictures",
        _ => "Other clip",
    };
}

/// <summary>Where an Extra belongs inside its title (D20): a season or an episode of a show, or a track of an album. Season 0 is Specials.</summary>
public static class ExtraPlaces
{
    /// <summary>"Season 1", "Specials", "Season 1, episode 3", "Track 4"; empty when the Extra belongs to the whole title.</summary>
    public static string Label(int? season, int? episode, int? track)
    {
        if (track is > 0)
        {
            return $"Track {track.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        if (season is null)
        {
            return string.Empty;
        }

        var seasonText = season == 0 ? "Specials" : $"Season {season.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return episode is > 0
            ? $"{seasonText}, episode {episode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : seasonText;
    }
}

/// <summary>Who decided a file is (or is not) an Extra.</summary>
public static class ItemExtraOrigins
{
    /// <summary>The one rule (Extras spec section 4) decided; a re-scan may change it.</summary>
    public const string Rule = "rule";

    /// <summary>A person moved a file to Extras in the editor; a re-scan never undoes it.</summary>
    public const string UserExtra = "user_extra";

    /// <summary>A person moved a file from Extras to an episode; it is not shown as an Extra and a re-scan never makes it one again.</summary>
    public const string UserEpisode = "user_episode";
}

/// <summary>
/// A file beside a title that is not one of its main files (universal editor v5: a "supporting file"). It belongs to the folder the
/// title lives in (<see cref="ItemFolder"/>), not to a Work, so a match or re-match never loses it. A picture row is one picture;
/// pictures in the same folder share a <see cref="GroupKey"/> and show as one card. <see cref="TargetSeason"/> and
/// <see cref="TargetEpisode"/> are set only on a <see cref="ItemExtraOrigins.UserEpisode"/> row: the episode the person put the file in.
/// <see cref="ExtraKind"/> (an <see cref="ExtraKinds"/> value) and the optional <see cref="PlaceSeason"/>, <see cref="PlaceEpisode"/> or
/// <see cref="PlaceTrack"/> are what a person chose in "What is this file?" (D20); all are null on a rule-found Extra.
/// <para>
/// <b>Inferred and person-owned values live side by side.</b> A rescan recomputes <see cref="Title"/>, <see cref="InferredRole"/>,
/// <see cref="InferredGroup"/>, <see cref="InferredScope"/> and <see cref="InferredAlongside"/> from the folder and file names (section 3b
/// of the editor v5 spec) and never touches <see cref="OwnTitle"/>, <see cref="OwnDescription"/>, <see cref="Role"/>,
/// <see cref="Scope"/>, <see cref="Alongside"/>, the group a person chose (<see cref="GroupSource"/> = <see cref="ExtraGroupSources.You"/>)
/// or the tags. The effective value is the person's when set, else the inferred one (see the <c>Effective*</c> and <c>Display*</c> members).
/// </para>
/// </summary>
public sealed record ItemExtra(
    Guid Id,
    string LibraryId,
    string ItemFolder,
    string FilePath,
    string Kind,
    string Origin,
    string Title,
    string? GroupKey,
    int SortOrder,
    long SizeBytes,
    double? DurationSeconds,
    int? PageCount,
    DateTimeOffset FoundAt,
    int? TargetSeason = null,
    int? TargetEpisode = null,
    string? ExtraKind = null,
    int? PlaceSeason = null,
    int? PlaceEpisode = null,
    int? PlaceTrack = null,
    string? OwnTitle = null,
    string? OwnDescription = null,
    string? Role = null,
    string? InferredRole = null,
    Guid? GroupId = null,
    string? GroupName = null,
    string? GroupSource = null,
    string? InferredGroup = null,
    ExtraScope? Scope = null,
    ExtraScope InferredScope = default,
    ExtraScope? Alongside = null,
    ExtraScope? InferredAlongside = null,
    DateTimeOffset? MissingSince = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>The title to show: the person's when they set one, else the one read from the file name.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(OwnTitle) ? Title : OwnTitle;

    /// <summary>The description a person wrote; null when there is none (a rescan never invents one).</summary>
    public string? Description => string.IsNullOrWhiteSpace(OwnDescription) ? null : OwnDescription;

    /// <summary>
    /// What the file is: the role a person set, else the kind a person chose in "What is this file?" (v4, mapped onto the role list),
    /// else the role guessed from the names, else the default for the file type.
    /// </summary>
    public string EffectiveRole =>
        !string.IsNullOrWhiteSpace(Role) ? Role
        : ExtraRoles.FromExtraKind(ExtraKind, Kind) ?? (!string.IsNullOrWhiteSpace(InferredRole) ? InferredRole : ExtraRoles.DefaultFor(Kind));

    /// <summary>True when a person set the role (here or through the v4 picker); false when it is only guessed.</summary>
    public bool RoleIsSetByYou => !string.IsNullOrWhiteSpace(Role) || ExtraRoles.FromExtraKind(ExtraKind, Kind) is not null;

    /// <summary>What the file belongs to: the scope a person set, else the one read from its folder.</summary>
    public ExtraScope EffectiveScope => Scope ?? InferredScope;

    /// <summary>"folder" when the scope comes from the file's place on disk, "you" when a person set it (<see cref="ExtraScopeSources"/>).</summary>
    public string ScopeSource => Scope is null ? ExtraScopeSources.Folder : ExtraScopeSources.You;

    /// <summary>The episode or lesson the file is shown with: the person's choice, else the guess; null when there is none.</summary>
    public ExtraScope? EffectiveAlongside =>
        Alongside is { } set ? (set.Level == ExtraScopeLevel.None ? null : set) : InferredAlongside;

    /// <summary>The tags a person added (empty when there are none).</summary>
    public IReadOnlyList<string> TagList => Tags ?? [];

    /// <summary>True when the file is no longer on disk and the record is only being kept for a while ("File missing").</summary>
    public bool IsMissingFromDisk => MissingSince is not null;
}
