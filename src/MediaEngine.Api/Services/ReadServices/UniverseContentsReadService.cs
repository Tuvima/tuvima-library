using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds what a Universe holds besides its Works: seven kinds of thing as rails of picture tiles, and the Timeline
/// (Universe contents spec WP5). Everything is limited to what the profile may see, and the spoiler rule that already
/// protects Characters now protects every kind (spec P7): a thing the profile has not reached is left out (only the
/// number is told), and one that cannot be placed is listed by name only. A thing added by hand has no Work to be
/// spoiled by, so it is always shown. Concepts show only when an approved Fandom Lore source exists for the Universe,
/// or when added by hand (spec P8).
/// </summary>
public sealed partial class UniverseContentsReadService(
    IFictionalEntityRepository entities,
    IEntityAssetRepository assets,
    IPluginLoreRepository pluginLore,
    IEntityRelationshipRepository relationships,
    CharacterSpoilerService spoilers)
{
    /// <summary>Tiles sent per rail; the section's count still tells the full number.</summary>
    public const int MaxTilesPerSection = 40;

    /// <summary>The sections in the order the page shows them, with the screen name of each.</summary>
    internal static readonly IReadOnlyList<(string Key, string Label, string Kind)> SectionOrder =
    [
        ("characters", "Characters", FictionalEntityType.Character),
        ("factions", "Factions", FictionalEntityType.Organization),
        ("places", "Places", FictionalEntityType.Location),
        ("events", "Events", FictionalEntityType.Event),
        ("objects", "Objects", FictionalEntityType.Object),
        ("species", "Species", FictionalEntityType.Species),
        ("concepts", "Concepts", FictionalEntityType.Concept),
    ];

    /// <summary>Null when the Universe has nothing the profile may see.</summary>
    public async Task<UniverseContentsDto?> BuildAsync(
        string universeQid,
        IReadOnlySet<string> visibleWorkQids,
        Profile? profile,
        CancellationToken ct = default)
    {
        var all = await entities.GetByUniverseAsync(universeQid, ct).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return null;
        }

        var classified = await ClassifyAsync(universeQid, all, visibleWorkQids, profile, ct).ConfigureAwait(false);
        var shown = classified.Shown;
        var hiddenCount = classified.HiddenCount;
        var visibleLinks = classified.VisibleLinks;
        var story = classified.View.Story;

        if (shown.Count == 0)
        {
            return null;
        }

        var pictures = await LoadPicturesAsync(shown.Select(item => item.Entity.Id), ct).ConfigureAwait(false);

        var sections = new List<UniverseContentsSectionDto>();
        foreach (var (key, label, kind) in SectionOrder)
        {
            var members = shown
                .Where(item => item.Entity.EntitySubType == kind)
                .OrderBy(item => item.NoPlotDetails)
                .ThenBy(item => item.Entity.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            sections.Add(new UniverseContentsSectionDto
            {
                Key = key,
                Label = label,
                Count = members.Count,
                Tiles = members.Take(MaxTilesPerSection).Select(item => ToTile(item.Entity, item.NoPlotDetails, pictures)).ToList(),
            });
        }

        // Timeline: Events always, plus any other thing whose story date is known. Hand-added things carry no dates.
        var candidates = new List<UniverseTimelineBuilder.Candidate>();
        foreach (var (entity, noPlot) in shown)
        {
            if (noPlot || entity.AddedByUser)
            {
                continue;
            }

            var own = visibleLinks[entity.Id].ToList();
            var storyYear = own
                .Select(link => UniverseTimelineBuilder.ParseYear(link.StartTime) ?? UniverseTimelineBuilder.ParseYear(link.NarrativeTimeIndex))
                .Where(year => year is not null)
                .Min();
            if (entity.EntitySubType != FictionalEntityType.Event && storyYear is null)
            {
                continue;
            }

            int? releaseYear = null;
            string? workLabel = null;
            foreach (var link in own)
            {
                foreach (var row in story.RowsFor(link.WorkQid))
                {
                    if (int.TryParse(row.Year, out var year) && (releaseYear is null || year < releaseYear))
                    {
                        releaseYear = year;
                        workLabel = story.Describe(link.WorkQid)?.Label;
                    }
                }
            }

            var sectionKey = SectionOrder.First(section => section.Kind == entity.EntitySubType).Key;
            pictures.TryGetValue(entity.Id, out var picture);
            candidates.Add(new UniverseTimelineBuilder.Candidate(
                entity.Id, entity.Label, sectionKey, picture?.PictureUrl, storyYear, releaseYear, workLabel));
        }

        return new UniverseContentsDto
        {
            Sections = sections,
            Timeline = UniverseTimelineBuilder.Build(candidates),
            HiddenCount = hiddenCount,
        };
    }

    /// <summary>What a profile may see of a Universe's things: the ones shown (some by name only), how many are held back, and their visible appearances.</summary>
    private sealed record Classified(
        IReadOnlyList<(FictionalEntity Entity, bool NoPlotDetails)> Shown,
        int HiddenCount,
        ILookup<Guid, FictionalEntityWorkLink> VisibleLinks,
        IReadOnlyDictionary<Guid, FictionalEntityUserLinks> UserLinks,
        CharacterSpoilerView View);

    /// <summary>
    /// Applies the one visibility rule to every thing of a Universe: hand-hidden things are skipped, Concepts need Fandom Lore
    /// (unless added by hand), hand-added things are always shown, and everything else follows the spoiler rule.
    /// </summary>
    private async Task<Classified> ClassifyAsync(
        string universeQid,
        IReadOnlyList<FictionalEntity> all,
        IReadOnlySet<string> visibleWorkQids,
        Profile? profile,
        CancellationToken ct)
    {
        var allIds = all.Select(entity => entity.Id).ToList();
        var links = await entities.GetWorkLinksAsync(allIds, ct).ConfigureAwait(false);
        var visibleLinks = links.Where(link => visibleWorkQids.Contains(link.WorkQid)).ToLookup(link => link.FictionalEntityId);
        var userLinks = await entities.GetUserLinksAsync(allIds, ct).ConfigureAwait(false);
        var view = await spoilers.LoadAsync(profile, ct).ConfigureAwait(false);
        var showConcepts = (await pluginLore.GetApprovedSourcesAsync(universeQid, ct).ConfigureAwait(false)).Count > 0;

        var shown = new List<(FictionalEntity Entity, bool NoPlotDetails)>();
        var hiddenCount = 0;
        foreach (var entity in all)
        {
            if (userLinks.TryGetValue(entity.Id, out var mine) && mine.IsHidden)
            {
                continue;
            }

            if (entity.EntitySubType == FictionalEntityType.Concept && !showConcepts && !entity.AddedByUser)
            {
                continue;
            }

            if (entity.AddedByUser)
            {
                shown.Add((entity, false));
                continue;
            }

            var own = view.VisibleAppearances(visibleLinks[entity.Id].ToList(), entity.EntitySubType == FictionalEntityType.Character).ToList();
            if (own.Count == 0)
            {
                continue;
            }

            switch (view.Evaluate(own))
            {
                case CharacterVisibility.Hidden:
                    hiddenCount++;
                    break;
                case CharacterVisibility.NoPlotDetails:
                    shown.Add((entity, true));
                    break;
                default:
                    shown.Add((entity, false));
                    break;
            }
        }

        return new Classified(shown, hiddenCount, visibleLinks, userLinks, view);
    }

    private static UniverseContentsTileDto ToTile(
        FictionalEntity entity,
        bool noPlotDetails,
        IReadOnlyDictionary<Guid, EntityPictures> pictures)
    {
        pictures.TryGetValue(entity.Id, out var picture);
        return new UniverseContentsTileDto
        {
            Id = entity.Id,
            Label = entity.Label,
            Qid = entity.AddedByUser ? null : entity.WikidataQid,
            PictureUrl = picture?.PictureUrl,
            PictureSource = picture?.PictureSource,
            LogoUrl = entity.EntitySubType is FictionalEntityType.Organization or FictionalEntityType.Object ? picture?.LogoUrl : null,
            AddedByUser = entity.AddedByUser,
            NoPlotDetails = noPlotDetails,
        };
    }

    private sealed record EntityPictures(string? PictureUrl, string? PictureSource, string? LogoUrl);

    private async Task<IReadOnlyDictionary<Guid, EntityPictures>> LoadPicturesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var rows = await assets.GetPreferredByEntitiesAsync(ids.Select(id => id.ToString()).ToList(), ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, EntityPictures>();
        foreach (var group in rows.Where(row => Guid.TryParse(row.EntityId, out _)).GroupBy(row => Guid.Parse(row.EntityId)))
        {
            // Only pictures stored on this machine are addressed; nothing is fetched from a provider for the page.
            var main = group.FirstOrDefault(row => row.AssetTypeValue == "CharacterPortrait" && !string.IsNullOrWhiteSpace(row.LocalImagePath));
            var logo = group.FirstOrDefault(row => row.AssetTypeValue == "Logo" && !string.IsNullOrWhiteSpace(row.LocalImagePath));
            if (main is null && logo is null)
            {
                continue;
            }

            result[group.Key] = new EntityPictures(
                main is null ? null : $"/stream/artwork/{main.Id:D}?size=m",
                main is null ? null : SourceOf(main),
                logo is null ? null : $"/stream/artwork/{logo.Id:D}?size=m");
        }

        return result;
    }

    private static string SourceOf(EntityAsset asset)
    {
        if (asset.IsUserOverride)
        {
            return "you";
        }

        return asset.SourceProvider?.Contains("fandom", StringComparison.OrdinalIgnoreCase) == true ? "fandom" : "wikidata";
    }
}
