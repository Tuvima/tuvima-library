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
/// A file beside a title that is not one of its main files. It belongs to the folder the title lives in
/// (<see cref="ItemFolder"/>), not to a Work, so a match or re-match never loses it. A picture row is one picture;
/// pictures in the same folder share a <see cref="GroupKey"/> and show as one card. <see cref="TargetSeason"/> and
/// <see cref="TargetEpisode"/> are set only on a <see cref="ItemExtraOrigins.UserEpisode"/> row: the episode the person put the file in.
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
    int? TargetEpisode = null);
