using System.Globalization;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads the owned story the spoiler rule judges characters against: for each Wikidata work item the requesting profile may
/// see, which title it is (a show, a film, a book) and, for an owned and identified episode, which episode (season and
/// number). Nothing is stored: "first seen at" is computed from the character's appearance links and the authorised display
/// projection, so it can never disagree with what the profile owns. Matching is by Wikidata item only, never by a name.
/// </summary>
public sealed class CharacterFirstSeenReader(
    IDisplayProjectionReadService display,
    IViewerProgressRepository progress)
{
    // The reader is scoped to one request, which sees one set of works, so the story is built once per request.
    private Task<OwnedStory>? _story;

    public Task<OwnedStory> LoadAsync(CancellationToken ct = default) => _story ??= BuildAsync(ct);

    private async Task<OwnedStory> BuildAsync(CancellationToken ct)
    {
        var rows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var tvQids = rows
            .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV"
                && !string.IsNullOrWhiteSpace(row.IdentityQid))
            .Select(row => row.IdentityQid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var childQids = await progress.GetChildWorkQidsAsync(tvQids, ct).ConfigureAwait(false);
        // A show's own identity is visible whenever any of its episodes is, even when every episode carries one of its own.
        var rootQids = await progress.GetWorkQidsAsync(
            rows.Where(row => row.RootWorkId != Guid.Empty && row.RootWorkId != row.WorkId).Select(row => row.RootWorkId),
            ct).ConfigureAwait(false);
        return new OwnedStory(rows, childQids, rootQids);
    }
}

/// <summary>A visible Work as a list names it.</summary>
public sealed record StoryWork(string Label, string MediaType, string? CoverUrl);

/// <summary>The owned works a profile may see, indexed for the spoiler rule: where a Wikidata work item sits, and how far the profile is.</summary>
public sealed class OwnedStory
{
    /// <summary>A file at or above this percentage counts as finished (the same line as "Continue watching").</summary>
    public const double FinishedProgressPct = 99.5;

    /// <summary>The link type Wikidata-derived data uses for the first work a character appears in.</summary>
    public const string FirstAppearanceLinkType = "first_appearance";

    private readonly ILookup<string, DisplayWorkRow> _rowsByQid;
    private readonly ILookup<Guid, DisplayWorkRow> _rowsByTitle;
    private readonly IReadOnlySet<string> _childQids;
    private readonly IReadOnlyDictionary<Guid, string> _rootQids;

    /// <param name="rows">The works the requesting profile may see.</param>
    /// <param name="childQids">The QIDs that are an episode-level work's own identity.</param>
    /// <param name="rootQids">The QID of a show (or other root work) by work id, for roots that have one.</param>
    public OwnedStory(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlySet<string> childQids,
        IReadOnlyDictionary<Guid, string>? rootQids = null)
    {
        _rowsByQid = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.IdentityQid))
            .ToLookup(row => row.IdentityQid!.Trim(), StringComparer.OrdinalIgnoreCase);
        _rowsByTitle = rows.ToLookup(row => row.RootWorkId);
        _childQids = childQids;
        _rootQids = (rootQids ?? new Dictionary<Guid, string>())
            .Where(pair => _rowsByTitle.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
        VisibleQids = new HashSet<string>(
            _rowsByQid.Select(group => group.Key).Concat(_rootQids.Values),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every Wikidata work item the profile may see: the identity of each visible work plus the identity of the show
    /// (or other root work) it belongs to. This is the one visibility set every Universe read judges a link against.
    /// </summary>
    public IReadOnlySet<string> VisibleQids { get; }

    /// <summary>The identities of the given rows' works and of the shows they belong to: how a Universe's own works are named.</summary>
    public IReadOnlySet<string> QidsOf(IEnumerable<DisplayWorkRow> rows)
    {
        var qids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.IdentityQid))
            {
                qids.Add(row.IdentityQid.Trim());
            }

            if (_rootQids.TryGetValue(row.RootWorkId, out var rootQid))
            {
                qids.Add(rootQid);
            }
        }

        return qids;
    }

    /// <summary>
    /// How a visible Work is named on a list: a show by its show name, anything else by its title, with its format and cover.
    /// Null when the profile may not see a Work with that identity.
    /// </summary>
    public StoryWork? Describe(string workQid)
    {
        var rows = RowsFor(workQid);
        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        var kind = DisplayMediaRules.NormalizeDisplayKind(first.MediaType);
        var isShow = kind == "TV" && !_childQids.Contains(workQid.Trim());
        var label = isShow && !string.IsNullOrWhiteSpace(first.ShowName) ? first.ShowName : first.Title;
        var cover = isShow ? first.RootCoverUrl ?? first.CoverUrl : first.CoverUrl;
        return new StoryWork(label, kind, cover);
    }

    /// <summary>The visible rows a Wikidata work item reaches: rows with that identity, and every row of a title whose own identity it is.</summary>
    public IReadOnlyList<DisplayWorkRow> RowsFor(string workQid)
    {
        var qid = workQid.Trim();
        var titles = _rootQids
            .Where(pair => string.Equals(pair.Value, qid, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToHashSet();
        return _rowsByQid[qid]
            .Concat(titles.SelectMany(title => _rowsByTitle[title]))
            .DistinctBy(row => (row.WorkId, row.AssetId))
            .ToList();
    }
    /// <summary>
    /// The places a Wikidata work item holds in the owned story: one per title. An item that is an owned episode's own
    /// identity is that episode (season and number); anything else, a show, film or book, is the title with the position
    /// within it unknown. Empty when the profile owns nothing with that identity.
    /// </summary>
    public IReadOnlyList<WorkPosition> Resolve(string workQid)
    {
        var qid = workQid.Trim();
        var isEpisode = _childQids.Contains(qid);
        var positions = _rowsByQid[qid]
            .GroupBy(row => row.RootWorkId)
            .Select(title => new WorkPosition(
                title.Key,
                isEpisode ? title.Select(EpisodeOf).FirstOrDefault(episode => episode is not null) : null))
            .ToList();
        // A show's own identity names the show as a whole, whatever identities its episodes carry.
        foreach (var (title, rootQid) in _rootQids)
        {
            if (string.Equals(rootQid, qid, StringComparison.OrdinalIgnoreCase) && positions.All(position => position.TitleId != title))
            {
                positions.Add(new WorkPosition(title));
            }
        }

        return positions;
    }

    /// <summary>
    /// A character's first-seen positions, one per title: the earliest owned episode its links name (a first-appearance link
    /// wins over a plain presence link), or the title itself when its links name only the title as a whole.
    /// </summary>
    public IReadOnlyList<WorkPosition> FirstSeen(IEnumerable<FictionalEntityWorkLink> links)
    {
        var byTitle = new Dictionary<Guid, (EpisodePosition? FirstAppearance, EpisodePosition? Other)>();
        foreach (var link in links)
        {
            var isFirstAppearance = string.Equals(link.LinkType, FirstAppearanceLinkType, StringComparison.OrdinalIgnoreCase);
            foreach (var position in Resolve(link.WorkQid))
            {
                byTitle.TryGetValue(position.TitleId, out var seen);
                if (position.Episode is { } episode)
                {
                    seen = isFirstAppearance
                        ? (Earlier(seen.FirstAppearance, episode), seen.Other)
                        : (seen.FirstAppearance, Earlier(seen.Other, episode));
                }

                byTitle[position.TitleId] = seen;
            }
        }

        return byTitle
            .Select(title => new WorkPosition(title.Key, title.Value.FirstAppearance ?? title.Value.Other))
            .ToList();
    }

    /// <summary>
    /// How far a profile has got in every title it has started. A show is started once any episode is, and finished once
    /// every owned episode is; a film or book is started and finished by its own file.
    /// </summary>
    public ViewerProgress ProgressOf(IReadOnlyList<ViewerWorkProgress> started)
    {
        var pctByWork = started
            .GroupBy(work => work.WorkId)
            .ToDictionary(group => group.Key, group => group.Max(work => work.ProgressPct));
        var byTitle = new Dictionary<Guid, TitleProgress>();
        foreach (var title in _rowsByTitle)
        {
            var works = title
                .GroupBy(row => row.WorkId)
                .Select(group => (Row: group.First(), Pct: pctByWork.GetValueOrDefault(group.Key)))
                .ToList();
            if (works.All(work => work.Pct <= 0))
            {
                continue;
            }

            byTitle[title.Key] = new TitleProgress(
                Started: true,
                Finished: works.All(work => work.Pct >= FinishedProgressPct),
                FurthestStartedEpisode: Furthest(works.Where(work => work.Pct > 0).Select(work => EpisodeOf(work.Row))),
                FurthestFinishedEpisode: Furthest(works.Where(work => work.Pct >= FinishedProgressPct).Select(work => EpisodeOf(work.Row))));
        }

        return new ViewerProgress(byTitle);
    }

    private static EpisodePosition? Earlier(EpisodePosition? current, EpisodePosition candidate) =>
        current is { } existing && existing <= candidate ? existing : candidate;

    private static EpisodePosition? Furthest(IEnumerable<EpisodePosition?> episodes)
    {
        EpisodePosition? best = null;
        foreach (var episode in episodes)
        {
            if (episode is { } value && (best is null || value > best.Value))
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>The episode a TV row is, from its season and episode numbers; a missing season reads as season 1.</summary>
    private static EpisodePosition? EpisodeOf(DisplayWorkRow row)
    {
        if (DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV" || LeadingNumber(row.EpisodeNumber) is not { } episode)
        {
            return null;
        }

        return new EpisodePosition(LeadingNumber(row.SeasonNumber) ?? 1, episode);
    }

    // "5", "05" and "5-6" (a double episode) all start at 5.
    private static int? LeadingNumber(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var digits = new string(trimmed.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
