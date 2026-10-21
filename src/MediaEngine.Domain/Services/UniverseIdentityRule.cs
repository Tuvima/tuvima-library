using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Services;

/// <summary>A Wikidata QID that a claim names, with the label the claim carried (when it had one).</summary>
public sealed record UniverseClaim(string Qid, string? Label);

/// <summary>
/// Everything the Universe rule looks at for one subject (a Series shelf or a Work): its own Series QID and the
/// QIDs its claims name for each relationship type. A relationship type can carry several values.
/// </summary>
public sealed record UniverseIdentityInput
{
    /// <summary>The subject's own Series QID (a shelf's <c>wikidata_qid</c>, or a Work's first <c>series_qid</c>).</summary>
    public string? OwnSeriesQid { get; init; }

    /// <summary>The label of the subject's own Series, used when nothing broader names a Universe.</summary>
    public string? OwnSeriesLabel { get; init; }

    /// <summary>P1434 fictional universe values.</summary>
    public IReadOnlyList<UniverseClaim> FictionalUniverses { get; init; } = [];

    /// <summary>P8345 franchise values.</summary>
    public IReadOnlyList<UniverseClaim> Franchises { get; init; } = [];

    /// <summary>P179 series values (the own Series may be listed too; it is skipped as "broader").</summary>
    public IReadOnlyList<UniverseClaim> Series { get; init; } = [];

    /// <summary>P144 based-on values.</summary>
    public IReadOnlyList<UniverseClaim> BasedOn { get; init; } = [];

    /// <summary>
    /// QIDs of the subject's own characters, locations, events and objects. A "fictional universe" or "franchise"
    /// value that is also one of these is a member of the story, not its Universe, and is skipped.
    /// </summary>
    public IReadOnlyCollection<string> FictionalMemberQids { get; init; } = [];
}

/// <summary>Which step of the Universe rule decided.</summary>
public enum UniverseDecisionStep
{
    /// <summary>A fictional universe (P1434).</summary>
    FictionalUniverse,

    /// <summary>A franchise (P8345).</summary>
    Franchise,

    /// <summary>A Series other than the subject's own (for example a broader film series).</summary>
    BroaderSeries,

    /// <summary>A work it is based on (P144).</summary>
    BasedOn,

    /// <summary>Nothing broader exists: the subject's own Series.</summary>
    OwnSeries,
}

/// <summary>Where the final QID came from after the chosen QID was looked up in the Library.</summary>
public enum UniverseCanonicalSource
{
    /// <summary>The chosen QID itself.</summary>
    Chosen,

    /// <summary>A Series shelf for the chosen QID already sits in a Universe; that Universe wins.</summary>
    SeriesShelf,

    /// <summary>The chosen QID has a recorded parent; the top of its parent chain wins.</summary>
    ParentChain,
}

/// <summary>The rule's pick before the Library is consulted.</summary>
public sealed record UniverseChoice(string Qid, string Label, string Level, UniverseDecisionStep Step);

/// <summary>The Universe a subject belongs to: the record's QID, label and level, and which step decided.</summary>
public sealed record UniverseIdentity(
    string Qid,
    string Label,
    string Level,
    UniverseDecisionStep Step,
    UniverseCanonicalSource CanonicalSource,
    string ChosenQid);

/// <summary>A Universe record as stored (the <c>narrative_roots</c> row).</summary>
public sealed record UniverseRecord(string Qid, string Label, string Level, string? ParentQid);

/// <summary>The Library lookups the rule needs to canonicalise a chosen QID. The caller supplies them; the rule stays pure.</summary>
public interface IUniverseCanonicalLookup
{
    /// <summary>
    /// The Universe a Series shelf with this Wikidata QID already belongs to, or null when there is no such shelf or it
    /// has no Universe yet. The shelf being decided must not answer for itself.
    /// </summary>
    Task<string?> FindShelfUniverseQidAsync(string seriesQid, CancellationToken ct);

    /// <summary>The stored Universe record for this QID, or null when none exists.</summary>
    Task<UniverseRecord?> FindRecordAsync(string qid, CancellationToken ct);
}

/// <summary>A Series shelf's identity, as the Universe threshold needs it.</summary>
public sealed record UniverseSeriesShelf(string? WikidataQid, string? MediaType);

/// <summary>
/// The one rule that decides which Universe a Series shelf or a Work belongs to. The Series shelves and the Universe
/// graph both use it, so they always agree.
///
/// <para>
/// Order: a fictional universe (P1434) wins, then a franchise (P8345), then a Series other than the subject's own,
/// then a work it is based on (P144), then the subject's own Series. When one type names several QIDs the lowest
/// numeric QID wins, so the answer is the same on every run. The chosen QID is then canonicalised through the Library:
/// a Series shelf for that QID that already sits in a Universe wins, otherwise the top of the QID's recorded parent chain.
/// </para>
/// </summary>
public static class UniverseIdentityRule
{
    /// <summary>The deepest parent chain the canonicalisation follows.</summary>
    public const int MaxParentDepth = 8;

    /// <summary>Applies steps 1 to 5 to the subject's own claims. Returns null when the subject names nothing at all.</summary>
    public static UniverseChoice? Choose(UniverseIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var own = NormaliseQid(input.OwnSeriesQid);
        var members = new HashSet<string>(
            input.FictionalMemberQids.Select(NormaliseQid).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        var universe = LowestQid(input.FictionalUniverses, members);
        if (universe is not null)
        {
            return new UniverseChoice(universe.Qid, LabelOrQid(universe), NarrativeLevel.Universe, UniverseDecisionStep.FictionalUniverse);
        }

        var franchise = LowestQid(input.Franchises, members);
        if (franchise is not null)
        {
            return new UniverseChoice(franchise.Qid, LabelOrQid(franchise), NarrativeLevel.Franchise, UniverseDecisionStep.Franchise);
        }

        var broaderSeries = LowestQid(input.Series, excluded: own is null ? null : new HashSet<string>([own], StringComparer.OrdinalIgnoreCase));
        if (broaderSeries is not null)
        {
            return new UniverseChoice(broaderSeries.Qid, LabelOrQid(broaderSeries), NarrativeLevel.Series, UniverseDecisionStep.BroaderSeries);
        }

        var basedOn = LowestQid(input.BasedOn, excluded: null);
        if (basedOn is not null)
        {
            return new UniverseChoice(basedOn.Qid, LabelOrQid(basedOn), NarrativeLevel.Franchise, UniverseDecisionStep.BasedOn);
        }

        if (own is not null)
        {
            // The label Wikidata gave the Series wins over the local name of the subject.
            var label = input.Series
                .Where(candidate => string.Equals(NormaliseQid(candidate.Qid), own, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Label)
                .Concat([input.OwnSeriesLabel])
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim() ?? own;
            return new UniverseChoice(own, label, NarrativeLevel.Series, UniverseDecisionStep.OwnSeries);
        }

        return null;
    }

    /// <summary>
    /// Looks the chosen QID up in the Library: (a) a Series shelf for that QID that already has a Universe gives that
    /// Universe; (b) otherwise the chosen QID's parent chain is followed to its top (at most
    /// <see cref="MaxParentDepth"/> steps, stopping at a cycle); (c) otherwise the chosen QID itself.
    /// </summary>
    public static async Task<UniverseIdentity> CanonicaliseAsync(
        UniverseChoice choice,
        IUniverseCanonicalLookup lookup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(lookup);

        var viaShelf = NormaliseQid(await lookup.FindShelfUniverseQidAsync(choice.Qid, ct).ConfigureAwait(false));
        if (viaShelf is not null)
        {
            if (string.Equals(viaShelf, choice.Qid, StringComparison.OrdinalIgnoreCase))
            {
                return Identity(choice, viaShelf, choice.Label, choice.Level, UniverseCanonicalSource.SeriesShelf);
            }

            var shelfRecord = await lookup.FindRecordAsync(viaShelf, ct).ConfigureAwait(false);
            return Identity(
                choice,
                viaShelf,
                shelfRecord?.Label ?? viaShelf,
                shelfRecord?.Level ?? NarrativeLevel.Universe,
                UniverseCanonicalSource.SeriesShelf);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { choice.Qid };
        var current = choice.Qid;
        var currentRecord = await lookup.FindRecordAsync(current, ct).ConfigureAwait(false);
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            var parent = NormaliseQid(currentRecord?.ParentQid);
            if (parent is null || !visited.Add(parent))
            {
                break;
            }

            current = parent;
            currentRecord = await lookup.FindRecordAsync(current, ct).ConfigureAwait(false);
        }

        if (string.Equals(current, choice.Qid, StringComparison.OrdinalIgnoreCase))
        {
            return Identity(choice, choice.Qid, choice.Label, choice.Level, UniverseCanonicalSource.Chosen);
        }

        return Identity(
            choice,
            current,
            currentRecord?.Label ?? current,
            currentRecord?.Level ?? NarrativeLevel.Universe,
            UniverseCanonicalSource.ParentChain);
    }

    /// <summary>
    /// Whether a Universe shelf should exist for <paramref name="universeQid"/> given the Series shelves that name it:
    /// at least two, except when every one of them is a Books or Audiobooks Series whose own QID is the Universe's QID
    /// (the reading and listening formats of one book Series do not make a Universe on their own).
    /// </summary>
    public static bool ShouldPresentUniverseShelf(string universeQid, IReadOnlyCollection<UniverseSeriesShelf> seriesShelves)
    {
        ArgumentNullException.ThrowIfNull(seriesShelves);

        if (seriesShelves.Count < 2)
        {
            return false;
        }

        var onlyFormatsOfOneBookSeries = seriesShelves.All(shelf =>
            IsBookFormat(shelf.MediaType)
            && string.Equals(NormaliseQid(shelf.WikidataQid), NormaliseQid(universeQid), StringComparison.OrdinalIgnoreCase));
        return !onlyFormatsOfOneBookSeries;
    }

    /// <summary>
    /// The QID in canonical form (upper-case Q followed by letters and digits), or null when the value is blank or is
    /// not a QID. Real QIDs are Q and digits; the looser form keeps stand-in ids in tests working.
    /// </summary>
    public static string? NormaliseQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != 'Q' && trimmed[0] != 'q'))
        {
            return null;
        }

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(trimmed[i]))
            {
                return null;
            }
        }

        return "Q" + trimmed[1..];
    }

    /// <summary>
    /// Compares two normalised QIDs by their number (Q123 sorts before Q1000). A QID without a plain number sorts after
    /// the numbered ones and among themselves by text, so the order is still the same on every run.
    /// </summary>
    public static int CompareQids(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        if (!leftNumeric)
        {
            return string.CompareOrdinal(left, right);
        }

        var leftDigits = left.AsSpan(1).TrimStart('0');
        var rightDigits = right.AsSpan(1).TrimStart('0');
        var byLength = leftDigits.Length.CompareTo(rightDigits.Length);
        return byLength != 0 ? byLength : leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
    }

    private static bool IsNumeric(string qid) => qid.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsBookFormat(string? mediaType) =>
        string.Equals(mediaType, nameof(MediaType.Books), StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, nameof(MediaType.Audiobooks), StringComparison.OrdinalIgnoreCase);

    private static UniverseClaim? LowestQid(IReadOnlyList<UniverseClaim> candidates, IReadOnlySet<string>? excluded)
    {
        string? best = null;
        string? bestLabel = null;
        foreach (var candidate in candidates)
        {
            var qid = NormaliseQid(candidate.Qid);
            if (qid is null || (excluded?.Contains(qid) ?? false))
            {
                continue;
            }

            if (best is null)
            {
                best = qid;
                bestLabel = candidate.Label;
                continue;
            }

            var comparison = CompareQids(qid, best);
            if (comparison < 0)
            {
                best = qid;
                bestLabel = candidate.Label;
            }
            else if (comparison == 0 && string.IsNullOrWhiteSpace(bestLabel))
            {
                bestLabel = candidate.Label;
            }
        }

        return best is null ? null : new UniverseClaim(best, bestLabel);
    }

    private static string LabelOrQid(UniverseClaim candidate) =>
        string.IsNullOrWhiteSpace(candidate.Label) ? candidate.Qid : candidate.Label.Trim();

    private static UniverseIdentity Identity(
        UniverseChoice choice,
        string qid,
        string label,
        string level,
        UniverseCanonicalSource source) =>
        new(qid, label, level, choice.Step, source, choice.Qid);
}
