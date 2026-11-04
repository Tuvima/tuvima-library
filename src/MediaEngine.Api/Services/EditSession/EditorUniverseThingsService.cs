using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// What the universal editor's rail lists for a Universe (universal editor v4, D10 and WP11): the kinds with their counts and, for one kind,
/// its things searched and paged. The editor sees every thing, including ones the person hid and ones no Work reaches yet, because the
/// editor is where they are shown again; the spoiler rules that shape the Universe page do not apply to the person who edits it.
/// </summary>
public sealed class EditorUniverseThingsService(
    IFictionalEntityRepository entities,
    IEntityAssetRepository assets,
    IPluginLoreRepository pluginLore)
{
    public const int DefaultPageSize = 40;
    public const int MaxPageSize = 100;

    /// <summary>The name of one thing of a kind as it reads in "Add faction" and "Faction".</summary>
    public static string SingularOf(string sectionKey) => sectionKey switch
    {
        "characters" => "Character",
        "factions" => "Faction",
        "places" => "Place",
        "events" => "Event",
        "objects" => "Object",
        "species" => "Species",
        "concepts" => "Concept",
        _ => sectionKey,
    };

    /// <param name="universeId">The Universe's own editor id.</param>
    /// <param name="universeQid">Its Wikidata ID, which names the things it holds.</param>
    /// <param name="universeName">Its name on screen.</param>
    /// <param name="kindKey">The kind to list, or null for the kinds and their counts only.</param>
    public async Task<EditorUniverseThingsDto> GetAsync(
        Guid universeId,
        string universeQid,
        string universeName,
        string? kindKey,
        string? search,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var all = await entities.GetByUniverseAsync(universeQid, ct).ConfigureAwait(false);
        var hasFandomSource = (await pluginLore.GetApprovedSourcesAsync(universeQid, ct).ConfigureAwait(false)).Count > 0;
        var kinds = UniverseContentsReadService.SectionOrder
            .Select(section =>
            {
                var count = all.Count(thing => thing.EntitySubType == section.Kind);
                return new EditorUniverseKindDto
                {
                    Key = section.Key,
                    Label = section.Label,
                    Singular = SingularOf(section.Key),
                    Count = count,
                    Available = section.Kind != FictionalEntityType.Concept || hasFandomSource || count > 0,
                };
            })
            .ToList();

        var section = UniverseContentsReadService.SectionOrder.FirstOrDefault(entry => string.Equals(entry.Key, kindKey, StringComparison.OrdinalIgnoreCase));
        if (section.Key is null)
        {
            return new EditorUniverseThingsDto { UniverseId = universeId, UniverseQid = universeQid, UniverseName = universeName, Kinds = kinds };
        }

        var wanted = (search ?? string.Empty).Trim();
        var matching = all
            .Where(thing => thing.EntitySubType == section.Kind
                && (wanted.Length == 0 || thing.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(thing => thing.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var start = Math.Max(0, offset);
        var page = matching.Skip(start).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        var links = await entities.GetUserLinksAsync(page.Select(thing => thing.Id), ct).ConfigureAwait(false);
        var pictures = await LoadPicturesAsync(page, ct).ConfigureAwait(false);

        return new EditorUniverseThingsDto
        {
            UniverseId = universeId,
            UniverseQid = universeQid,
            UniverseName = universeName,
            Kinds = kinds,
            KindKey = section.Key,
            Total = matching.Count,
            Offset = start,
            HasMore = start + page.Count < matching.Count,
            Things = page
                .Select(thing => new EditorUniverseThingDto
                {
                    Id = thing.Id,
                    Label = thing.Label,
                    KindKey = section.Key,
                    PictureUrl = pictures.GetValueOrDefault(thing.Id),
                    AddedByUser = thing.AddedByUser,
                    Hidden = links.TryGetValue(thing.Id, out var mine) && mine.IsHidden,
                })
                .ToList(),
        };
    }

    // The small picture beside a name: the main picture, else the logo, only when it is stored on this machine.
    private async Task<IReadOnlyDictionary<Guid, string>> LoadPicturesAsync(IReadOnlyList<FictionalEntity> page, CancellationToken ct)
    {
        var rows = await assets.GetPreferredByEntitiesAsync(page.Select(thing => thing.Id.ToString()).ToList(), ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, string>();
        foreach (var group in rows.Where(row => Guid.TryParse(row.EntityId, out _)).GroupBy(row => Guid.Parse(row.EntityId)))
        {
            var picked = group.FirstOrDefault(row => row.AssetTypeValue == "CharacterPortrait" && !string.IsNullOrWhiteSpace(row.LocalImagePath))
                ?? group.FirstOrDefault(row => row.AssetTypeValue == "Logo" && !string.IsNullOrWhiteSpace(row.LocalImagePath));
            if (picked is not null)
            {
                result[group.Key] = $"/stream/artwork/{picked.Id:D}?size=s";
            }
        }

        return result;
    }
}
