using System.Globalization;
using MediaEngine.Api.Services.Display;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads what the phone companion shows for one Work at one position. Every part comes from data the Library already holds
/// and from the authorised display projection, so a Work the requesting profile may not see is never described. Characters
/// follow the one spoiler rule (<see cref="CharacterSpoilerService"/>) with the viewer counted as standing at the companion's
/// position, so "Up to here" means exactly what it means on the Universe page. Nothing is guessed: a Work with no character
/// details says so, a book chapter never claims to know who appears in it, and the previous episode is the previous
/// episode the profile owns.
/// </summary>
public sealed class CompanionContextReadService(
    IDisplayProjectionReadService display,
    DisplayCardBuilder cards,
    CharacterSpoilerService spoilers,
    IFictionalEntityRepository entities,
    IPersonRepository people,
    ILibraryItemRepository libraryItems,
    UniverseClientReadService universes,
    IPersonCreditReadService credits,
    ICompanionChapterSource chapters)
{
    /// <summary>Most characters listed under "Who's here".</summary>
    public const int MaxCharacters = 24;

    /// <summary>Most actors listed.</summary>
    public const int MaxCast = 8;

    /// <summary>Most episode work items read to find a show's characters.</summary>
    private const int MaxEpisodeQids = 400;

    /// <summary>
    /// The companion context for a Work, or null when the Work does not exist or the profile may not see it.
    /// </summary>
    /// <param name="workId">The Work held: a film, a book, an audiobook, a comic issue or a TV episode.</param>
    /// <param name="chapterIndex">The chapter held by hand (books and audiobooks); null when none.</param>
    /// <param name="positionSeconds">Where a followed device is within an audiobook; used to find its chapter when none is given.</param>
    /// <param name="profile">The requesting profile; null is judged as a viewer with default protection.</param>
    public async Task<CompanionContextDto?> GetAsync(
        Guid workId,
        int? chapterIndex,
        double? positionSeconds,
        Profile? profile,
        CancellationToken ct = default)
    {
        var rows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(candidate => candidate.WorkId == workId);
        if (row is null)
        {
            return null;
        }

        var kind = KindOf(row);
        var title = row.Title;
        var position = OwnedStory.PositionOf(row);
        var episode = position.Episode;

        var chapterList = await chapters.GetChaptersAsync(row, profile?.Id, ct).ConfigureAwait(false);
        var chapter = chapterIndex is { } index
            ? chapterList.FirstOrDefault(candidate => candidate.Index == index)
            : ChapterAt(chapterList, positionSeconds);
        var view = (await spoilers.LoadAsync(profile, ct).ConfigureAwait(false)).WithReached(position);

        var universe = await FindUniverseAsync(row, profile, ct).ConfigureAwait(false);
        var whosHere = await BuildWhosHereAsync(row, rows, view, ct).ConfigureAwait(false);
        var open = cards.FromWork(row, "collection", null).Actions.FirstOrDefault(action => action.Type == "openWork");

        return new CompanionContextDto
        {
            WorkId = row.WorkId,
            Kind = kind,
            Title = title,
            ShowTitle = kind == CompanionKinds.Episode ? NullIfBlank(row.ShowName) : null,
            PositionLabel = PositionLabel(kind, row, chapter, chapterIndex, chapterList),
            ChapterIndex = chapter?.Index ?? chapterIndex,
            ChapterTitle = chapter is { Title.Length: > 0 } ? chapter.Title : null,
            CoverUrl = kind == CompanionKinds.Episode
                ? row.EpisodeStillUrl ?? row.RootCoverUrl ?? row.CoverUrl
                : row.CoverUrl,
            Synopsis = NullIfBlank(row.Description) ?? (kind == CompanionKinds.Episode ? null : NullIfBlank(row.RootDescription)),
            DetailsRoute = open?.WebUrl,
            SpoilerLevel = SpoilerLevels.ToWire(view.Level),
            Universe = universe,
            WhosHere = whosHere,
            Previously = kind == CompanionKinds.Episode && episode is { } current ? Previously(row, rows, current) : null,
            Cast = await LoadCastAsync(row, kind, ct).ConfigureAwait(false),
            Picker = BuildPicker(row, rows, kind, chapterList, view.Level, episode, chapter?.Index ?? chapterIndex),
        };
    }

    private async Task<CompanionWhosHereDto> BuildWhosHereAsync(
        DisplayWorkRow row,
        IReadOnlyList<DisplayWorkRow> rows,
        CharacterSpoilerView view,
        CancellationToken ct)
    {
        var story = view.Story;
        // The Work's own item, its show and, for a show, every owned episode: characters are linked to whichever Wikidata item
        // the source knew, and the spoiler rule places each by the earliest episode it names.
        var qids = new List<string>();
        void Add(string? qid)
        {
            if (!string.IsNullOrWhiteSpace(qid) && !qids.Contains(qid.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                qids.Add(qid.Trim());
            }
        }

        Add(row.IdentityQid);
        foreach (var titleRow in rows.Where(candidate => candidate.RootWorkId == row.RootWorkId).Take(MaxEpisodeQids))
        {
            Add(titleRow.IdentityQid);
        }

        foreach (var rootQid in story.RootQidsOf(row.RootWorkId))
        {
            Add(rootQid);
        }

        var found = new Dictionary<Guid, FictionalEntity>();
        foreach (var qid in qids)
        {
            foreach (var entity in await entities.GetByWorkQidAsync(qid, ct).ConfigureAwait(false))
            {
                if (string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(entity.WikidataQid))
                {
                    found[entity.Id] = entity;
                }
            }
        }

        if (found.Count == 0)
        {
            return new CompanionWhosHereDto { HasCharacterDetails = false };
        }

        var linksByEntity = (await entities.GetWorkLinksAsync(found.Keys, ct).ConfigureAwait(false))
            .Where(link => story.VisibleQids.Contains(link.WorkQid))
            .ToLookup(link => link.FictionalEntityId);
        var performers = (await people.GetCharacterPerformersAsync(found.Keys, ct).ConfigureAwait(false))
            .GroupBy(credit => credit.FictionalEntityId)
            .ToDictionary(group => group.Key, group => group.Select(credit => credit.PerformerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));

        var listed = new List<CompanionCharacterDto>();
        var heldBack = 0;
        foreach (var entity in found.Values)
        {
            var links = linksByEntity[entity.Id].ToList();
            var visibility = view.Evaluate(links);
            if (visibility == CharacterVisibility.Hidden)
            {
                heldBack++;
                continue;
            }

            var shown = visibility == CharacterVisibility.Shown;
            var counted = view.Counted(links);
            var otherTitles = counted
                .SelectMany(link => story.Resolve(link.WorkQid).Select(place => place.TitleId))
                .Where(titleId => titleId != Guid.Empty && titleId != row.RootWorkId)
                .Distinct()
                .Count();
            var sources = UniverseSourceLabels.Combine(
                links.Select(link => UniverseSourceLabels.FromProvenance(link.Provenance, link.SourceProvider)));
            listed.Add(new CompanionCharacterDto
            {
                Qid = entity.WikidataQid,
                Name = entity.Label,
                UniverseQid = entity.FictionalUniverseQid ?? string.Empty,
                Source = sources.Count == 0 ? "Wikidata" : string.Join(", ", sources.Select(source => source.Label)),
                Visibility = shown ? CompanionCharacterVisibility.Shown : CompanionCharacterVisibility.NoPlotDetails,
                PerformedBy = shown && performers.TryGetValue(entity.Id, out var performer) ? performer : null,
                OtherWorkCount = shown ? otherTitles : 0,
            });
        }

        var ordered = listed
                .OrderBy(character => character.Visibility == CompanionCharacterVisibility.NoPlotDetails)
                .ThenByDescending(character => character.OtherWorkCount)
                .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.Qid, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new CompanionWhosHereDto
        {
            HasCharacterDetails = true,
            HeldBackCount = heldBack,
            MoreCount = Math.Max(0, ordered.Count - MaxCharacters),
            Characters = ordered.Take(MaxCharacters).ToList(),
        };
    }

    private async Task<CompanionUniverseLinkDto?> FindUniverseAsync(DisplayWorkRow row, Profile? profile, CancellationToken ct)
    {
        var detail = await libraryItems.GetDetailAsync(row.WorkId, row.AssetId, ct).ConfigureAwait(false);
        var qid = detail?.UniverseSummary?.UniverseQid;
        if (string.IsNullOrWhiteSpace(qid))
        {
            return null;
        }

        // The same rule as the item page's "Part of" line: only a Universe the profile can open.
        var link = await universes.GetUniverseLinkAsync(qid, profile, ct).ConfigureAwait(false);
        return link is null ? null : new CompanionUniverseLinkDto { Qid = link.Qid, Name = link.Name };
    }

    private async Task<IReadOnlyList<string>> LoadCastAsync(DisplayWorkRow row, string kind, CancellationToken ct)
    {
        if (kind is not (CompanionKinds.Film or CompanionKinds.Episode))
        {
            return [];
        }

        var cast = await credits.BuildForWorkAsync(row.WorkId, ct).ConfigureAwait(false);
        return cast
            .Select(credit => credit.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCast)
            .ToList();
    }

    // The chapter an audiobook is in at a given moment: the last one that has started. Books have no times, so never match.
    private static CompanionChapter? ChapterAt(IReadOnlyList<CompanionChapter> chapterList, double? positionSeconds) =>
        positionSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0
            ? chapterList
                .Where(candidate => candidate.StartSeconds is { } start && start <= seconds)
                .OrderByDescending(candidate => candidate.StartSeconds)
                .FirstOrDefault()
            : null;

    private static CompanionPreviouslyDto? Previously(DisplayWorkRow row, IReadOnlyList<DisplayWorkRow> rows, EpisodePosition current)
    {
        var previous = rows
            .Where(candidate => candidate.RootWorkId == row.RootWorkId && candidate.WorkId != row.WorkId)
            .Select(candidate => (Row: candidate, Position: OwnedStory.EpisodeOf(candidate)))
            .Where(pair => pair.Position is { } position && position < current)
            .OrderByDescending(pair => pair.Position)
            .Select(pair => pair.Row)
            .FirstOrDefault();
        return previous is null
            ? null
            : new CompanionPreviouslyDto
            {
                WorkId = previous.WorkId,
                Label = EpisodeLabel(previous),
                Title = previous.Title,
                Synopsis = NullIfBlank(previous.Description),
            };
    }

    private static CompanionPickerDto? BuildPicker(
        DisplayWorkRow row,
        IReadOnlyList<DisplayWorkRow> rows,
        string kind,
        IReadOnlyList<CompanionChapter> chapterList,
        SpoilerLevel level,
        EpisodePosition? currentEpisode,
        int? currentChapter)
    {
        // "Up to here" never sends a title from ahead of the place: later episodes and chapters are only numbered.
        var everything = level == SpoilerLevel.Everything;
        if (kind == CompanionKinds.Episode)
        {
            var choices = rows
                .Where(candidate => candidate.RootWorkId == row.RootWorkId
                    && DisplayMediaRules.NormalizeDisplayKind(candidate.MediaType) == "TV")
                .GroupBy(candidate => candidate.WorkId)
                .Select(group => group.First())
                .Select(candidate => (Row: candidate, Position: OwnedStory.EpisodeOf(candidate)))
                .OrderBy(pair => pair.Position is null)
                .ThenBy(pair => pair.Position?.Season ?? int.MaxValue)
                .ThenBy(pair => pair.Position?.Episode ?? int.MaxValue)
                .ThenBy(pair => pair.Row.Title, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new CompanionPickerChoiceDto
                {
                    WorkId = pair.Row.WorkId,
                    Label = EpisodeLabel(pair.Row),
                    Title = everything || pair.Position is { } at && currentEpisode is { } here && at <= here ? NullIfBlank(pair.Row.Title) : null,
                })
                .ToList();
            return choices.Count == 0 ? null : new CompanionPickerDto { Kind = CompanionPickerKinds.Episode, Choices = choices };
        }

        if (kind is CompanionKinds.Book or CompanionKinds.Audiobook && chapterList.Count > 0)
        {
            return new CompanionPickerDto
            {
                Kind = CompanionPickerKinds.Chapter,
                Choices = chapterList
                    .Select((chapter, ordinal) => new CompanionPickerChoiceDto
                    {
                        WorkId = row.WorkId,
                        ChapterIndex = chapter.Index,
                        Label = $"Chapter {(ordinal + 1).ToString(CultureInfo.InvariantCulture)}",
                        Title = everything || currentChapter is { } here && chapter.Index <= here ? NullIfBlank(chapter.Title) : null,
                    })
                    .ToList(),
            };
        }

        return null;
    }

    private static string PositionLabel(
        string kind,
        DisplayWorkRow row,
        CompanionChapter? chapter,
        int? requestedIndex,
        IReadOnlyList<CompanionChapter> chapterList)
    {
        switch (kind)
        {
            case CompanionKinds.Episode:
                return EpisodeLabel(row);
            case CompanionKinds.Film:
                return "Film";
            case CompanionKinds.Comic:
                return "Issue";
            case CompanionKinds.Book or CompanionKinds.Audiobook:
                if (chapter is not null)
                {
                    var ordinal = chapterList.ToList().IndexOf(chapter) + 1;
                    var number = $"Chapter {ordinal.ToString(CultureInfo.InvariantCulture)}";
                    return string.IsNullOrWhiteSpace(chapter.Title) ? number : $"{number}: {chapter.Title}";
                }

                // A chapter number with no chapter list behind it (a file without a table of contents) is still the person's
                // own place; it is shown as they set it.
                return requestedIndex is { } fallback
                    ? $"Chapter {(fallback + 1).ToString(CultureInfo.InvariantCulture)}"
                    : "No chapter chosen";
            default:
                return string.Empty;
        }
    }

    private static string EpisodeLabel(DisplayWorkRow row)
    {
        var season = NullIfBlank(row.SeasonNumber);
        var number = NullIfBlank(row.EpisodeNumber);
        return (season, number) switch
        {
            (not null, not null) => $"Season {season} · Episode {number}",
            (null, not null) => $"Episode {number}",
            _ => "Episode",
        };
    }

    private static string KindOf(DisplayWorkRow row) => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) switch
    {
        "Movie" => CompanionKinds.Film,
        "TV" => CompanionKinds.Episode,
        "Book" => CompanionKinds.Book,
        "Audiobook" => CompanionKinds.Audiobook,
        "Comic" => CompanionKinds.Comic,
        _ => CompanionKinds.Other,
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
