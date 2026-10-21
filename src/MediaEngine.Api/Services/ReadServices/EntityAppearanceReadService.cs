using System.Globalization;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads where else one thing from a Universe appears across the Library: a character, place or organisation (the same
/// Wikidata item, never the same name) or a person (their library credits). Every Work comes from the authorised display
/// projection, so a Work the requesting profile may not see is never listed or counted. Rows are grouped by format and a
/// TV show is one entry however many episodes are owned. For an entity the answer also says how many Works in the
/// Universe have no character details yet, so the list is honest about what it may be missing. A character the profile
/// has not reached is not described at all (the one spoiler rule, <see cref="CharacterSpoilerService"/>).
/// </summary>
public sealed class EntityAppearanceReadService(
    IFictionalEntityRepository entities,
    IPersonRepository people,
    IPersonCreditReadService personCredits,
    IDisplayProjectionReadService display,
    DisplayCardBuilder cards,
    UniverseClientReadService universes,
    CharacterSpoilerService spoilers)
{
    /// <summary>Format order of the groups, with the user-facing plural heading of each.</summary>
    private static readonly (string Kind, string Label)[] GroupOrder =
    [
        ("TV", "TV"),
        ("Movie", "Movies"),
        ("Book", "Books"),
        ("Audiobook", "Audiobooks"),
        ("Comic", "Comics"),
        ("Music", "Music"),
    ];

    /// <summary>
    /// The Works that share one character, place or organisation. Null when no such entity exists or none of its Works is
    /// visible to the profile, so an entity the profile cannot see about is never confirmed. A character held back by the
    /// spoiler rule comes back flagged <c>hidden_for_spoilers</c> and names nothing; an appearance marked as a spoiler for
    /// a work is left out unless the profile may see it (the same rule as the Universe page's Characters list).
    /// </summary>
    public async Task<EntityAppearancesDto?> GetForEntityAsync(
        string universeQid,
        string entityQid,
        Profile? profile,
        CancellationToken ct = default,
        Guid? reachedWorkId = null)
    {
        var entity = await entities.FindByQidAsync(entityQid.Trim(), ct).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        var rows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var view = await spoilers.LoadAsync(profile, ct).ConfigureAwait(false);
        // The phone companion asks as someone standing at a Work: that place counts as reached, as on the companion screen.
        if (reachedWorkId is { } reached && rows.FirstOrDefault(candidate => candidate.WorkId == reached) is { } reachedRow)
        {
            view = view.WithReached(OwnedStory.PositionOf(reachedRow));
        }

        var story = view.Story;

        // The same visibility rule as every Universe read: only links to a work the profile may see.
        var seenLinks = (await entities.GetWorkLinksAsync(entity.Id, ct).ConfigureAwait(false))
            .Where(link => story.VisibleQids.Contains(link.WorkQid))
            .ToList();
        if (seenLinks.Count == 0)
        {
            return null;
        }

        // ...and the one spoiler rule: a character the profile has not reached is not described, and an appearance marked as
        // a spoiler is left out (other kinds of entity have no episode to place them by, so only the markers apply).
        var isCharacter = string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase);
        if (isCharacter && view.Evaluate(seenLinks) == CharacterVisibility.Hidden)
        {
            return new EntityAppearancesDto
            {
                UniverseQid = universeQid,
                SubjectKind = EntityAppearanceSubjectKinds.Character,
                HiddenForSpoilers = true,
            };
        }

        var visibleLinks = view.VisibleAppearances(seenLinks, isCharacter);
        if (visibleLinks.Count == 0)
        {
            return null;
        }

        var entries = new Dictionary<(string Kind, Guid Id), Entry>();
        foreach (var workGroup in visibleLinks.GroupBy(link => link.WorkQid, StringComparer.OrdinalIgnoreCase))
        {
            var roles = workGroup.Select(link => link.AppearanceRole);
            foreach (var card in BuildCards(story.RowsFor(workGroup.Key)))
            {
                AddEntry(entries, card, roles);
            }
        }

        var gaps = await CountGapsAsync(universeQid, profile, rows, ct).ConfigureAwait(false);
        return new EntityAppearancesDto
        {
            UniverseQid = universeQid,
            SubjectKind = SubjectKindFor(entity.EntitySubType),
            SubjectLabel = entity.Label,
            SubjectQid = entity.WikidataQid,
            Groups = BuildGroups(entries.Values),
            TotalWorks = entries.Count,
            WorksWithoutCharacterData = gaps.WithoutData,
            WorksNotYetChecked = gaps.NotYetChecked,
        };
    }

    /// <summary>
    /// The Works a person is credited on, the same credits and visibility rule as the person page. Null when no such
    /// person exists or none of their credits is visible to the profile, so a person the profile cannot see credits for
    /// is never named.
    /// </summary>
    public async Task<EntityAppearancesDto?> GetForPersonAsync(
        string universeQid,
        Guid personId,
        CancellationToken ct = default)
    {
        var person = await people.FindByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return null;
        }

        var credits = PersonLibraryCreditAuthorizationPolicy.Filter(
            await personCredits.GetLibraryCreditsAsync(personId, ct).ConfigureAwait(false),
            await display.LoadWorksAsync(ct).ConfigureAwait(false));
        if (credits.Count == 0)
        {
            return null;
        }

        // One entry per Work (one per show for TV); a person with two roles on one Work lists both.
        var entries = new Dictionary<(string Kind, Guid Id), Entry>();
        foreach (var creditGroup in credits.GroupBy(credit => DetailCompositionOrchestrator.CreditDisplayId(credit), StringComparer.OrdinalIgnoreCase))
        {
            var credit = creditGroup.First();
            var kind = DisplayMediaRules.NormalizeDisplayKind(credit.MediaType);
            var id = Guid.Parse(DetailCompositionOrchestrator.CreditDisplayId(credit));
            entries[(kind, id)] = new Entry(
                kind,
                id,
                credit.Title,
                ParseYear(credit.Year),
                credit.CoverUrl,
                DetailCompositionOrchestrator.BuildCreditRoute(credit),
                JoinRoles(creditGroup.Select(row => row.Role)));
        }

        return new EntityAppearancesDto
        {
            UniverseQid = universeQid,
            SubjectKind = EntityAppearanceSubjectKinds.Person,
            SubjectLabel = person.Name,
            PersonId = personId,
            Groups = BuildGroups(entries.Values),
            TotalWorks = entries.Count,
        };
    }

    /// <summary>One display card per owned Work, with every episode of a TV show collapsed into its show.</summary>
    private IEnumerable<DisplayCardDto> BuildCards(IReadOnlyList<DisplayWorkRow> rows)
    {
        var tvRows = rows.Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV").ToList();
        foreach (var show in cards.BuildTvShowCards(tvRows))
        {
            yield return show;
        }

        foreach (var row in rows
                     .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV")
                     .GroupBy(row => row.WorkId)
                     .Select(group => group.First()))
        {
            yield return cards.FromWork(row, "collection", null);
        }
    }

    private static void AddEntry(
        Dictionary<(string Kind, Guid Id), Entry> entries,
        DisplayCardDto card,
        IEnumerable<string?> roles)
    {
        var kind = DisplayMediaRules.NormalizeDisplayKind(card.MediaType);
        var id = card.WorkId ?? card.Id;
        var role = JoinRoles(roles);
        if (entries.TryGetValue((kind, id), out var existing))
        {
            entries[(kind, id)] = existing with { Role = JoinRoles([existing.Role, role]) };
            return;
        }

        // The Details action is the item page; a show's only action opens the show, a song's plays it.
        var action = card.Actions.FirstOrDefault(candidate => candidate.Type == "openWork") ?? card.Actions.FirstOrDefault();
        entries[(kind, id)] = new Entry(
            kind,
            id,
            card.Title,
            card.SortYear > 0 ? card.SortYear : null,
            card.Artwork.CoverUrl ?? card.Artwork.SquareUrl,
            action?.WebUrl ?? $"/details/work/{id:D}",
            role);
    }

    private static IReadOnlyList<EntityAppearanceGroupDto> BuildGroups(IEnumerable<Entry> entries)
    {
        var byKind = entries.ToLookup(entry => entry.Kind, StringComparer.Ordinal);
        return GroupOrder
            .Where(group => byKind[group.Kind].Any())
            .Select(group => new EntityAppearanceGroupDto
            {
                MediaKind = group.Kind,
                Label = group.Label,
                Works = byKind[group.Kind]
                    .OrderBy(entry => entry.Year ?? int.MaxValue)
                    .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Id)
                    .Select(entry => new EntityAppearanceWorkDto
                    {
                        WorkId = entry.Id,
                        Title = entry.Title,
                        Year = entry.Year,
                        CoverUrl = entry.CoverUrl,
                        Route = entry.Route,
                        Role = entry.Role,
                    })
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// How many Works in the Universe have been looked up for character details and came back with none, and how many
    /// have not been looked up yet. A TV show counts once.
    /// </summary>
    private async Task<(int WithoutData, int NotYetChecked)> CountGapsAsync(
        string universeQid,
        Profile? profile,
        IReadOnlyList<DisplayWorkRow> visibleRows,
        CancellationToken ct)
    {
        var members = await universes.LoadMemberWorkRowsAsync(universeQid, profile, visibleRows, ct).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return (0, 0);
        }

        var units = members
            .GroupBy(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV"
                ? (row.RootWorkId != Guid.Empty ? row.RootWorkId : row.CollectionId ?? row.WorkId)
                : row.WorkId)
            .Select(group => group.ToList())
            .ToList();
        var linkedQids = await entities.GetLinkedWorkQidsAsync(
            members.Select(row => row.IdentityQid).Where(qid => !string.IsNullOrWhiteSpace(qid)).Select(qid => qid!),
            ct).ConfigureAwait(false);
        var enrichedIds = await entities.GetUniverseEnrichedEntityIdsAsync(
            members.SelectMany(row => new[] { row.WorkId, row.RootWorkId, row.AssetId }),
            ct).ConfigureAwait(false);

        var withoutData = 0;
        var notYetChecked = 0;
        foreach (var unit in units)
        {
            var checkedAlready = unit.Any(row =>
                enrichedIds.Contains(row.WorkId) || enrichedIds.Contains(row.RootWorkId) || enrichedIds.Contains(row.AssetId));
            if (!checkedAlready)
            {
                notYetChecked++;
            }
            else if (!unit.Any(row => !string.IsNullOrWhiteSpace(row.IdentityQid) && linkedQids.Contains(row.IdentityQid!)))
            {
                withoutData++;
            }
        }

        return (withoutData, notYetChecked);
    }

    private static string SubjectKindFor(string? entitySubType) => entitySubType?.Trim().ToLowerInvariant() switch
    {
        "character" => EntityAppearanceSubjectKinds.Character,
        "location" => EntityAppearanceSubjectKinds.Location,
        "organization" => EntityAppearanceSubjectKinds.Organization,
        _ => EntityAppearanceSubjectKinds.Entity,
    };

    private static string? JoinRoles(IEnumerable<string?> roles)
    {
        var distinct = roles
            .SelectMany(role => (role ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? null : string.Join(", ", distinct);
    }

    private static int? ParseYear(string? year)
    {
        var text = year?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var leading = text.Length >= 4 ? text[..4] : text;
        return int.TryParse(leading, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private sealed record Entry(
        string Kind,
        Guid Id,
        string Title,
        int? Year,
        string? CoverUrl,
        string Route,
        string? Role);
}
