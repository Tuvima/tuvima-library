namespace MediaEngine.Domain.Services;

/// <summary>What a restored title turned out to be in the Library now.</summary>
public enum RestoredTitleOutcome
{
    /// <summary>Nothing matches, or more than one thing does: the state keeps waiting. Nothing is guessed.</summary>
    Unmatched,

    /// <summary>Exactly one file is this title and cut; times and positions can be used as they are.</summary>
    Exact,

    /// <summary>Exactly one file is this title but a different cut or edition; times need aligning.</summary>
    DifferentCut,
}

/// <summary>The decision for one restored file reference. <see cref="Index"/> is the position of the chosen candidate, when there is one.</summary>
public sealed record RestoredTitleDecision(RestoredTitleOutcome Outcome, int? Index)
{
    /// <summary>No single candidate.</summary>
    public static RestoredTitleDecision Unmatched { get; } = new(RestoredTitleOutcome.Unmatched, null);
}

/// <summary>What the ids two titles hold say about them.</summary>
public enum RestoredIdVerdict
{
    /// <summary>Not the same title: no id in common, or an id both hold disagrees.</summary>
    WrongTitle,

    /// <summary>The same title, and the ids do not say it is another edition.</summary>
    SameEdition,

    /// <summary>The same title (the title-level ids agree) but an edition-level id both hold disagrees: another edition of it.</summary>
    OtherEdition,
}

/// <summary>
/// The rules that decide which file in the Library a restored backup entry belongs to (backup and restore spec D3 to D6). They reuse
/// <see cref="BookmarkPackMatcher"/>, so a backup and a bookmark pack agree on what the same title and the same cut are. Pure, so the
/// rules are tested without a data store.
/// </summary>
public static class RestoredTitleRules
{
    /// <summary>
    /// The id kinds that name one edition of a title, not the title: a hardcover and an ebook of the same book carry different ISBNs
    /// and edition ids, a re-release has its own MusicBrainz release, an audiobook its own Audible id. All other kinds (Wikidata id,
    /// IMDb, TMDB, TVDB, Goodreads, a MusicBrainz release group, a Stash-box record, the parent's ids) name the title itself.
    /// Edition-level ids never make a wrong title when the title-level ids agree: they make another edition, which is a different cut.
    /// </summary>
    public static readonly IReadOnlySet<string> EditionLevelIdKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "edition_wikidata_qid",
        BridgeIdKeys.Isbn,
        BridgeIdKeys.Isbn10,
        BridgeIdKeys.Isbn13,
        BridgeIdKeys.Asin,
        BridgeIdKeys.AudibleId,
        BridgeIdKeys.AppleBooksId,
        BridgeIdKeys.MusicBrainzReleaseId,
        BridgeIdKeys.AppleMusicCollectionId,
    };

    /// <summary>
    /// Compares the ids two titles hold. (a) If they share a title-level id kind, the title-level ids decide same title or wrong title,
    /// and an edition-level id both hold that disagrees makes it <see cref="RestoredIdVerdict.OtherEdition"/>, not a wrong title.
    /// (b) If they share no title-level kind, an agreeing edition-level id means the same edition and a disagreeing one a wrong title.
    /// (c) Otherwise there is nothing to compare, and the caller falls back to the exact file fingerprint.
    /// </summary>
    public static RestoredIdVerdict CompareIds(IReadOnlyDictionary<string, string> backupIds, IReadOnlyDictionary<string, string> liveIds)
    {
        ArgumentNullException.ThrowIfNull(backupIds);
        ArgumentNullException.ThrowIfNull(liveIds);
        var (backupTitle, backupEdition) = Split(backupIds);
        var (liveTitle, liveEdition) = Split(liveIds);
        if (Shares(backupTitle, liveTitle))
        {
            if (!Agrees(backupTitle, liveTitle))
            {
                return RestoredIdVerdict.WrongTitle;
            }

            return Shares(backupEdition, liveEdition) && !Agrees(backupEdition, liveEdition)
                ? RestoredIdVerdict.OtherEdition
                : RestoredIdVerdict.SameEdition;
        }

        if (Shares(backupEdition, liveEdition))
        {
            return Agrees(backupEdition, liveEdition) ? RestoredIdVerdict.SameEdition : RestoredIdVerdict.WrongTitle;
        }

        return RestoredIdVerdict.WrongTitle;
    }

    /// <summary>True when the ids say the two are the same title (whether or not they are the same edition of it).</summary>
    public static bool IsSameTitle(IReadOnlyDictionary<string, string> backupIds, IReadOnlyDictionary<string, string> liveIds) =>
        CompareIds(backupIds, liveIds) != RestoredIdVerdict.WrongTitle;

    /// <summary>
    /// Chooses one file out of the candidates that share an id with the title the backup recorded. Each candidate is a pair: the
    /// recognition data the backup holds and the recognition data of the candidate file. A candidate that is not this title
    /// (<see cref="CompareIds"/>) is ignored. Exactly one candidate of the same cut is <see cref="RestoredTitleOutcome.Exact"/>; with
    /// none of the same cut, exactly one of another cut or edition is <see cref="RestoredTitleOutcome.DifferentCut"/>; anything else is
    /// <see cref="RestoredTitleOutcome.Unmatched"/>.
    /// </summary>
    public static RestoredTitleDecision ChooseFile(IReadOnlyList<(BookmarkPackTitle Backup, BookmarkPackTitle Live)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var sameCut = new List<int>();
        var otherCut = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (backup, live) = candidates[i];
            var verdict = CompareIds(backup.Ids, live.Ids);
            if (verdict == RestoredIdVerdict.WrongTitle)
            {
                continue;
            }

            // The running time and the edition label are judged on the ids that decided the title.
            var (backupTitle, backupEdition) = Split(backup.Ids);
            var (liveTitle, liveEdition) = Split(live.Ids);
            var titleLevel = Shares(backupTitle, liveTitle);
            var deciding = titleLevel ? (backupTitle, liveTitle) : (backupEdition, liveEdition);
            var kind = BookmarkPackMatcher.Compare(
                new BookmarkPackTitle(deciding.Item1, backup.RunningTimeSeconds, backup.Edition),
                new BookmarkPackTitle(deciding.Item2, live.RunningTimeSeconds, live.Edition),
                AllKeys(deciding.Item1, deciding.Item2)).Kind;
            if (kind == BookmarkPackMatchKind.WrongTitle)
            {
                continue;
            }

            if (kind == BookmarkPackMatchKind.Match && verdict == RestoredIdVerdict.SameEdition)
            {
                sameCut.Add(i);
            }
            else
            {
                otherCut.Add(i);
            }
        }

        if (sameCut.Count > 0)
        {
            return sameCut.Count == 1 ? new RestoredTitleDecision(RestoredTitleOutcome.Exact, sameCut[0]) : RestoredTitleDecision.Unmatched;
        }

        return otherCut.Count == 1 ? new RestoredTitleDecision(RestoredTitleOutcome.DifferentCut, otherCut[0]) : RestoredTitleDecision.Unmatched;
    }

    /// <summary>
    /// True when two sets of ids share an id kind (title-level or edition-level), whether or not the values agree. A child title with
    /// ids of its own is compared on them; one without is compared on its parent's ids and its position in the parent.
    /// </summary>
    public static bool ShareAnIdKind(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return Shares(first, second);
    }

    /// <summary>
    /// True when two edition labels agree: both blank, or equal ignoring case and surrounding spaces. Used to pick one edition of a
    /// work.
    /// </summary>
    public static bool SameEditionLabel(string? backup, string? live)
    {
        var first = string.IsNullOrWhiteSpace(backup) ? string.Empty : backup.Trim();
        var second = string.IsNullOrWhiteSpace(live) ? string.Empty : live.Trim();
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static (Dictionary<string, string> Title, Dictionary<string, string> Edition) Split(IReadOnlyDictionary<string, string> ids)
    {
        var title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ids)
        {
            (EditionLevelIdKeys.Contains(key) ? edition : title)[key] = value;
        }

        return (title, edition);
    }

    private static HashSet<string> AllKeys(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Keys.Concat(second.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Shares(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        BookmarkPackMatcher.ShareAnIdKind(first, second, AllKeys(first, second));

    private static bool Agrees(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        BookmarkPackMatcher.Compare(
            new BookmarkPackTitle(first, null, null),
            new BookmarkPackTitle(second, null, null),
            AllKeys(first, second)).Kind != BookmarkPackMatchKind.WrongTitle;
}
