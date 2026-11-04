using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// The page of one thing of a Universe (Universe contents spec section 4.2), built from the same visibility rule as the rails:
/// a thing the profile has not reached is not found, one that cannot be placed is shown by name only, and a connected thing
/// the profile may not see is left out of every group.
/// </summary>
public sealed partial class UniverseContentsReadService
{
    /// <summary>Things listed per connected group; the group's count still tells the full number.</summary>
    public const int MaxRelatedPerGroup = 12;

    // The groups of connected things, in the order the page shows them. Each connection type reads one way from the thing
    // that holds the link ("Member of") and the other way from the thing it points to ("Members").
    private static readonly IReadOnlyList<(string Type, string OutKey, string OutLabel, string InKey, string InLabel)> ConnectionGroups =
    [
        (RelationshipType.MemberOf, "member_of", "Member of", "members", "Members"),
        (RelationshipType.Allegiance, "allegiance", "Allegiance", "sworn", "Sworn to this"),
        (RelationshipType.ParentOrganization, "part_of", "Part of", "parts", "Parts"),
        (RelationshipType.HasParts, "parts", "Parts", "part_of", "Part of"),
        (RelationshipType.LocatedIn, "located_in", "Located in", "places_inside", "Places inside"),
        (RelationshipType.PartOf, "part_of", "Part of", "parts", "Parts"),
        (RelationshipType.Residence, "lives_in", "Lives in", "residents", "Residents"),
        (RelationshipType.OccursAt, "took_place_at", "Took place at", "events_here", "Events here"),
        (RelationshipType.ParticipantIn, "took_part_in", "Took part in", "participants", "Who took part"),
        (RelationshipType.Participant, "participants", "Who took part", "took_part_in", "Took part in"),
        (RelationshipType.CausedBy, "caused_by", "Caused by", "led_to", "Led to"),
        (RelationshipType.Causes, "led_to", "Led to", "caused_by", "Caused by"),
    ];

    private static string KindLabel(string kind) => kind switch
    {
        FictionalEntityType.Character => "Character",
        FictionalEntityType.Organization => "Faction",
        FictionalEntityType.Location => "Place",
        FictionalEntityType.Event => "Event",
        FictionalEntityType.Object => "Object",
        FictionalEntityType.Species => "Species",
        FictionalEntityType.Concept => "Concept",
        _ => kind,
    };

    private static string KindKey(string kind) =>
        SectionOrder.FirstOrDefault(section => section.Kind == kind).Key ?? "characters";

    /// <summary>Null when the thing is unknown, belongs to another Universe, is hidden, or has not been reached yet.</summary>
    public async Task<UniverseThingDto?> BuildThingAsync(
        string universeQid,
        Guid thingId,
        IReadOnlySet<string> visibleWorkQids,
        Profile? profile,
        CancellationToken ct = default)
    {
        var all = await entities.GetByUniverseAsync(universeQid, ct).ConfigureAwait(false);
        var classified = await ClassifyAsync(universeQid, all, visibleWorkQids, profile, ct).ConfigureAwait(false);
        var found = classified.Shown.FirstOrDefault(item => item.Entity.Id == thingId);
        if (found.Entity is null)
        {
            return null;
        }

        var entity = found.Entity;
        var detailsHidden = found.NoPlotDetails;
        var shownById = classified.Shown.ToDictionary(item => item.Entity.Id);
        var qidToId = classified.Shown
            .Where(item => !item.Entity.AddedByUser)
            .GroupBy(item => item.Entity.WikidataQid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Entity.Id, StringComparer.OrdinalIgnoreCase);

        // Pictures for this thing and everything it is connected to, in one read.
        var related = await BuildRelatedAsync(entity, classified, shownById, qidToId, ct).ConfigureAwait(false);
        var pictureIds = new[] { entity.Id }.Concat(related.SelectMany(group => group.Members.Select(member => member.Id)));
        var pictures = await LoadPicturesAsync(pictureIds, ct).ConfigureAwait(false);
        pictures.TryGetValue(entity.Id, out var own);

        var (description, source) = detailsHidden
            ? ((string?)null, (string?)null)
            : await DescribeAsync(entity, universeQid, ct).ConfigureAwait(false);
        var userLinks = classified.UserLinks.TryGetValue(entity.Id, out var mine) ? mine : null;

        return new UniverseThingDto
        {
            Id = entity.Id,
            UniverseQid = universeQid,
            UniverseLabel = entity.FictionalUniverseLabel ?? string.Empty,
            Label = entity.Label,
            KindKey = KindKey(entity.EntitySubType),
            KindLabel = KindLabel(entity.EntitySubType),
            Qid = entity.AddedByUser ? null : entity.WikidataQid,
            AddedByUser = entity.AddedByUser,
            PictureUrl = own?.PictureUrl,
            PictureSource = own?.PictureSource,
            LogoUrl = own?.LogoUrl,
            Description = description,
            DescriptionSource = source,
            Aliases = detailsHidden ? [] : userLinks?.Aliases ?? [],
            DetailsHidden = detailsHidden,
            AppearsIn = detailsHidden ? [] : BuildAppearsIn(classified.VisibleLinks[entity.Id], classified.View.Story),
            Related = related
                .Select(group => new UniverseThingRelatedGroupDto
                {
                    Key = group.Key,
                    Label = group.Label,
                    Count = group.Members.Count,
                    Things = group.Members
                        .Take(MaxRelatedPerGroup)
                        .Select(member => ToLink(member, pictures))
                        .ToList(),
                })
                .ToList(),
        };
    }

    private sealed record RelatedGroup(string Key, string Label, IReadOnlyList<FictionalEntity> Members);

    private async Task<IReadOnlyList<RelatedGroup>> BuildRelatedAsync(
        FictionalEntity entity,
        Classified classified,
        IReadOnlyDictionary<Guid, (FictionalEntity Entity, bool NoPlotDetails)> shownById,
        IReadOnlyDictionary<string, Guid> qidToId,
        CancellationToken ct)
    {
        var groups = new List<RelatedGroup>();

        void Add(string key, string label, IEnumerable<FictionalEntity> members)
        {
            var distinct = members
                .Where(member => member.Id != entity.Id)
                .DistinctBy(member => member.Id)
                .OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 0)
            {
                return;
            }

            var existing = groups.FindIndex(group => group.Key == key);
            if (existing >= 0)
            {
                distinct = groups[existing].Members.Concat(distinct)
                    .DistinctBy(member => member.Id)
                    .OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                groups[existing] = new RelatedGroup(key, label, distinct);
                return;
            }

            groups.Add(new RelatedGroup(key, label, distinct));
        }

        FictionalEntity? Resolve(string qid) =>
            qidToId.TryGetValue(qid, out var id) && shownById.TryGetValue(id, out var item) ? item.Entity : null;

        // Links the Library already holds from Wikidata and Fandom Lore (a hand-added thing has no Wikidata ID, so none).
        if (!entity.AddedByUser)
        {
            var edges = await relationships.GetByEntityAsync(entity.WikidataQid, ct).ConfigureAwait(false);
            foreach (var (type, outKey, outLabel, inKey, inLabel) in ConnectionGroups)
            {
                var outgoing = edges
                    .Where(edge => edge.RelationshipTypeValue == type
                        && string.Equals(edge.SubjectQid, entity.WikidataQid, StringComparison.OrdinalIgnoreCase))
                    .Select(edge => Resolve(edge.ObjectQid))
                    .OfType<FictionalEntity>();
                Add(outKey, outLabel, outgoing);

                var incoming = edges
                    .Where(edge => edge.RelationshipTypeValue == type
                        && string.Equals(edge.ObjectQid, entity.WikidataQid, StringComparison.OrdinalIgnoreCase))
                    .Select(edge => Resolve(edge.SubjectQid))
                    .OfType<FictionalEntity>();
                Add(inKey, inLabel, incoming);
            }
        }

        // The person's own "belongs to" link, both ways ("Belongs to" on the thing, "Belongs here" on the one it names).
        if (classified.UserLinks.TryGetValue(entity.Id, out var mine) && mine.BelongsToEntityId is { } parentId
            && shownById.TryGetValue(parentId, out var parent))
        {
            Add("belongs_to", "Belongs to", [parent.Entity]);
        }

        Add(
            "belongs_here",
            "Belongs here",
            classified.UserLinks
                .Where(pair => pair.Value.BelongsToEntityId == entity.Id && shownById.ContainsKey(pair.Key))
                .Select(pair => shownById[pair.Key].Entity));

        return groups;
    }

    private static UniverseThingLinkDto ToLink(FictionalEntity member, IReadOnlyDictionary<Guid, EntityPictures> pictures)
    {
        pictures.TryGetValue(member.Id, out var picture);
        return new UniverseThingLinkDto
        {
            Id = member.Id,
            Label = member.Label,
            KindKey = KindKey(member.EntitySubType),
            PictureUrl = picture?.PictureUrl,
            LogoUrl = member.EntitySubType is FictionalEntityType.Organization or FictionalEntityType.Object ? picture?.LogoUrl : null,
            AddedByUser = member.AddedByUser,
        };
    }

    private static IReadOnlyList<UniverseThingWorkDto> BuildAppearsIn(IEnumerable<FictionalEntityWorkLink> links, OwnedStory story)
    {
        var works = new List<(int Year, UniverseThingWorkDto Work)>();
        foreach (var group in links.GroupBy(link => link.WorkQid, StringComparer.OrdinalIgnoreCase))
        {
            var described = story.Describe(group.Key);
            if (described is null)
            {
                continue;
            }

            var year = story.RowsFor(group.Key)
                .Select(row => int.TryParse(row.Year, out var parsed) ? parsed : int.MaxValue)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            works.Add((year, new UniverseThingWorkDto
            {
                WorkQid = group.Key,
                Label = described.Label,
                MediaType = described.MediaType,
                CoverUrl = described.CoverUrl,
                Role = group.Select(link => link.AppearanceRole).FirstOrDefault(role => !string.IsNullOrWhiteSpace(role)),
            }));
        }

        return works
            .OrderBy(item => item.Year)
            .ThenBy(item => item.Work.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Work)
            .ToList();
    }

    // Your own wording wins, then Wikidata's, then (when the Universe has an approved Fandom source) Fandom's.
    private async Task<(string? Text, string? Source)> DescribeAsync(FictionalEntity entity, string universeQid, CancellationToken ct)
    {
        if (entity.AddedByUser)
        {
            return (Clean(entity.Description), string.IsNullOrWhiteSpace(entity.Description) ? null : "you");
        }

        var stored = await entities.GetUserDetailsAsync(entity.Id, ct).ConfigureAwait(false);
        if (stored?.Description is { Length: > 0 } mine)
        {
            return (mine.Trim(), "you");
        }

        if (Clean(entity.Description) is { } text)
        {
            return (text, "wikidata");
        }

        var lore = await pluginLore.GetEntitiesAsync(universeQid, approvedOnly: true, ct).ConfigureAwait(false);
        var fandom = lore.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.Description)
            && string.Equals(item.WikidataQid, entity.WikidataQid, StringComparison.OrdinalIgnoreCase));
        return fandom is null ? (null, null) : (Clean(fandom.Description), "fandom");
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
