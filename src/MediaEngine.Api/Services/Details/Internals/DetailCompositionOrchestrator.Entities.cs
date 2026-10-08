using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using static MediaEngine.Api.Services.Details.Internals.DetailViewModelBuilder;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<DetailPageViewModel?> BuildPersonAsync(
        Guid personId,
        DetailEntityType entityType,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        var person = await _persons.FindByIdAsync(personId, ct);
        if (person is null)
        {
            return null;
        }


        IReadOnlyList<string> personTags = [];
        using (var conn = _db.CreateConnection())
        {
            var overridesJson = conn.QueryFirstOrDefault<string?>(
                "SELECT display_overrides_json FROM persons WHERE id = @personId LIMIT 1;",
                new { personId });
            if (!string.IsNullOrWhiteSpace(overridesJson))
            {
                try
                {
                    var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(overridesJson);
                    if (overrides?.TryGetValue("name", out var displayName) == true && !string.IsNullOrWhiteSpace(displayName))
                    {
                        person.Name = displayName.Trim();
                    }

                    if (overrides?.TryGetValue("biography", out var displayBiography) == true && !string.IsNullOrWhiteSpace(displayBiography))
                    {
                        person.Biography = displayBiography.Trim();
                    }

                    if (overrides?.TryGetValue(MetadataFieldConstants.CustomTags, out var storedTags) == true)
                    {
                        personTags = LibraryTagCatalog.ParseDisplayValue(storedTags);
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed presentation state and retain provider values.
                }
            }
        }

        var credits = await _personCredits.GetLibraryCreditsAsync(personId, ct);
        var characterRoles = await _personCredits.GetCharacterRolesAsync(personId, ct);
        if (authorizedWorks is not null)
        {
            var visibleWorkIds = authorizedWorks.Select(work => work.WorkId).ToHashSet();
            credits = PersonLibraryCreditAuthorizationPolicy.Filter(credits, authorizedWorks);
            characterRoles = characterRoles
                .Where(role => role.WorkId.HasValue && visibleWorkIds.Contains(role.WorkId.Value))
                .ToList();
            if (credits.Count == 0)
            {
                return null;
            }
        }
        // A musical group exposes members. Old P527 data may have been written
        // as aliases before group/member semantics were separated, so group
        // pages intentionally ignore alias rows.
        var aliases = person.IsGroup
            ? []
            : await _persons.FindAliasesAsync(personId, ct);
        var groupMembers = await _personCredits.GetGroupMembersAsync(personId, person.IsGroup, ct);
        var memberOfGroups = person.IsGroup
            ? []
            : groupMembers;
        var wikipediaUrl = await _reader.LoadPersonWikipediaUrlAsync(personId, ct);
        var artworkAssets = await _entityAssets.GetByEntityAsync(personId.ToString(), null, ct);
        var banner = PreferredAssetUrl(artworkAssets, "Banner");
        var background = PreferredAssetUrl(artworkAssets, "Background");
        var logo = PreferredAssetUrl(artworkAssets, "Logo");
        var portrait = PreferredAssetUrl(artworkAssets, "Headshot")
            ?? ApiImageUrls.BuildPersonHeadshotUrl(person.Id, person.LocalHeadshotPath, person.HeadshotUrl);
        var relatedArt = credits.Select(c => c.CoverUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().Take(8).ToList();
        var groups = BuildPersonCreditGroups(credits, context);
        var mediaGroups = BuildPersonMediaGroups(credits, context);
        var displayRoles = BuildPersonDisplayRoles(credits, person.Roles, context);
        var ownedWorkCount = credits
            .Select(CreditDisplayId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var shortDescription = await _reader.LoadPersonShortDescriptionAsync(personId, person.WikidataQid, ct);
        var tvdbPersonId = _bridgeIds is null ? null
            : (await _bridgeIds.FindAsync(personId, BridgeIdKeys.TvdbPersonId, ct))?.IdValue;
        var personSourceLinks = BuildExternalSourceLinks(person.WikidataQid, wikipediaUrl, null).ToList();
        if (!string.IsNullOrWhiteSpace(tvdbPersonId) && tvdbPersonId.All(char.IsDigit))
        {
            personSourceLinks.Add(new ExternalSourceLinkViewModel
                {
                    Key = BridgeIdKeys.TvdbPersonId, Label = "View on TheTVDB",
                    Url = $"https://thetvdb.com/people/{tvdbPersonId}",
                    SourceName = "TheTVDB",
                });
        }

        return new DetailPageViewModel
        {
            Id = personId.ToString("D"),
            EntityType = entityType,
            PresentationContext = context,
            EditorTarget = new DetailEditorTarget
            {
                EntityId = personId.ToString("D"),
                EntityKind = "Person",
                ContainerMode = "Canonical",
                InitialTab = "details",
            },
            Title = person.Name,
            Subtitle = person.IsGroup ? "Group" : string.Join(" • ", displayRoles.Take(3)),
            Description = shortDescription,
            DescriptionAttribution = !string.IsNullOrWhiteSpace(tvdbPersonId)
                && string.IsNullOrWhiteSpace(person.WikidataQid)
                ? new DescriptionAttributionViewModel
                {
                    SourceName = "TheTVDB", SourceTitle = "Person profile",
                    SourceUrl = $"https://thetvdb.com/people/{tvdbPersonId}",
                    LicenseName = "TheTVDB terms", LicenseUrl = "https://thetvdb.com/tos",
                }
                : BuildWikipediaDescriptionAttribution(person.Biography, wikipediaUrl),
            SourceLinks = personSourceLinks,
            PersonDetails = BuildPersonDetails(person, displayRoles, wikipediaUrl, aliases, groupMembers, memberOfGroups),
            Facts = BuildPersonFacts(person, displayRoles),
            Artwork = BuildArtwork(entityType, background, banner, null, null, portrait, new Dictionary<string, string>(), relatedArt, 0, null, logo),
            Metadata = BuildPersonMetadata(displayRoles, ownedWorkCount)
                .Concat(personTags.Select(tag => new MetadataPill { Label = tag, Kind = "custom_tag" }))
                .ToList(),
            PrimaryActions = BuildPersonActions(personId, entityType, context),
            SecondaryActions = [],
            OverflowActions = BuildOverflowActions(personId, entityType, actionAuthorization),
            ContributorGroups = groups,
            PreviewContributors = groups.SelectMany(g => g.Credits).Take(12).ToList(),
            CharacterGroups = BuildPersonCharacterGroups(characterRoles),
            PreviewCharacters = BuildPersonCharacterGroups(characterRoles).SelectMany(g => g.Characters).Take(12).ToList(),
            Tabs = BuildTabs(entityType, context, isAdminView),
            MediaGroups = mediaGroups,
            PrimaryModule = BuildPrimaryModule(entityType, null, mediaGroups),
            IdentityStatus = ResolveIdentityStatus(person.WikidataQid, null, null),
            LibraryStatus = credits.Count > 0 ? LibraryStatus.Owned : LibraryStatus.Unknown,
            IsAdminView = isAdminView,
        };
    }

    private async Task<DetailPageViewModel?> BuildCharacterAsync(
        Guid characterId,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<CharacterDetailRow>(new CommandDefinition(
            """
            SELECT id AS Id,
                   label AS Label,
                   wikidata_qid AS WikidataQid,
                   fictional_universe_qid AS UniverseQid,
                   fictional_universe_label AS UniverseLabel,
                   image_url AS ImageUrl,
                   entity_sub_type AS EntitySubType
            FROM fictional_entities
            WHERE id = @id
            LIMIT 1;
            """,
            new { id = characterId },
            cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        if (authorizedWorks is not null)
        {
            var visibleWorkQids = authorizedWorks
                .Select(work => work.IdentityQid)
                .Where(qid => !string.IsNullOrWhiteSpace(qid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasVisibleAppearance = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM fictional_entity_work_links WHERE entity_id = @id AND work_qid IN @workQids);",
                new
                {
                    id = characterId,
                    workQids = visibleWorkQids.Count > 0 ? visibleWorkQids : ["__none__"],
                },
                cancellationToken: ct));
            if (hasVisibleAppearance == 0)
            {
                return null;
            }
        }

        var portraits = await conn.QueryAsync<CharacterPortraitRow>(new CommandDefinition(
            """
            SELECT id AS Id,
                   image_url AS ImageUrl,
                   local_image_path AS LocalImagePath,
                   is_default AS IsDefault
            FROM character_portraits
            WHERE fictional_entity_id = @id
            ORDER BY is_default DESC, updated_at DESC
            LIMIT 1;
            """,
            new { id = characterId },
            cancellationToken: ct));

        var portrait = portraits.Select(p => ApiImageUrls.BuildCharacterPortraitUrl(p.Id, p.LocalImagePath, p.ImageUrl)).FirstOrDefault();
        var artwork = BuildArtwork(DetailEntityType.Character, null, null, null, null, portrait ?? row.ImageUrl, new Dictionary<string, string>(), [], 0, null);
        var universeQid = ExtractQid(row.UniverseQid);
        IReadOnlyList<RelationshipGroup> relationships = string.IsNullOrWhiteSpace(row.UniverseQid)
            ? []
            : [new RelationshipGroup
            {
                Title = "Universe",
                Items = [new RelatedEntityChip
                {
                    Id = universeQid ?? row.UniverseQid!,
                    EntityType = RelatedEntityType.Universe,
                    Label = row.UniverseLabel ?? row.UniverseQid!,
                    Route = BuildUniverseExploreRoute(universeQid),
                }],
            }];

        return new DetailPageViewModel
        {
            Id = characterId.ToString("D"),
            EntityType = DetailEntityType.Character,
            PresentationContext = context,
            Title = row.Label,
            Subtitle = StringHelpers.FirstNonBlankOr(string.Empty, row.EntitySubType, "Character") + (string.IsNullOrWhiteSpace(row.UniverseLabel) ? "" : $" • {row.UniverseLabel}"),
            SourceLinks = BuildExternalSourceLinks(row.WikidataQid, null, null),
            Artwork = artwork,
            Metadata = [new MetadataPill { Label = "Character" }, .. MaybePill(row.UniverseLabel)],
            PrimaryActions = [new DetailAction { Key = "appearances", Label = "View Appearances", Icon = "auto_stories", IsPrimary = true }],
            OverflowActions = BuildOverflowActions(characterId, DetailEntityType.Character, actionAuthorization),
            RelationshipStrip = relationships,
            Tabs = BuildTabs(DetailEntityType.Character, context, isAdminView, hasUniverse: HasUniverseRelationship(relationships)),
            IdentityStatus = ResolveIdentityStatus(row.WikidataQid, null, null),
            LibraryStatus = LibraryStatus.Owned,
            IsAdminView = isAdminView,
        };
    }

    private async Task<DetailPageViewModel?> BuildUniverseAsync(
        Guid id,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rawRow = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            """
            SELECT id AS Id,
                   display_name AS DisplayName,
                   collection_type AS CollectionType,
                   wikidata_qid AS WikidataQid,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key IN ('description', 'overview') LIMIT 1) AS Description,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key = 'tagline' LIMIT 1) AS Tagline,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key IN ('background_url', 'background', 'hero_url', 'hero') LIMIT 1) AS BackgroundUrl,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key IN ('banner_url', 'banner', 'hero_url', 'hero') LIMIT 1) AS BannerUrl,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key IN ('cover_url', 'cover') LIMIT 1) AS CoverUrl,
                   (SELECT value FROM canonical_values WHERE entity_id = collections.id AND key IN ('logo_url', 'logo') LIMIT 1) AS LogoUrl,
                   NULL AS HeroBrandLabel,
                   NULL AS HeroBrandImageUrl
            FROM collections
            WHERE id = @id
            LIMIT 1;
            """,
            new { id = GuidSql.ToBlob(id) },
            cancellationToken: ct));

        if (rawRow is null)
        {
            return null;
        }

        var row = new CollectionDetailRow(
            Guid.Parse(StringValue(rawRow.Id) ?? id.ToString("D")),
            StringValue(rawRow.DisplayName),
            StringValue(rawRow.CollectionType),
            StringValue(rawRow.WikidataQid),
            StringValue(rawRow.Description),
            StringValue(rawRow.Tagline),
            StringValue(rawRow.CoverUrl),
            StringValue(rawRow.BackgroundUrl),
            StringValue(rawRow.BannerUrl),
            StringValue(rawRow.LogoUrl),
            StringValue(rawRow.HeroBrandLabel),
            StringValue(rawRow.HeroBrandImageUrl));

        var works = await LoadCollectionWorksAsync(
            id,
            rootWorkId: null,
            ct,
            authorizedAssetIds: authorizedWorks?.Select(work => work.AssetId).Distinct().ToList());
        works = FilterAuthorizedCollectionWorks(works, authorizedWorks);
        if (authorizedWorks is not null && works.Count == 0)
        {
            return null;
        }
        var relatedArt = works.Select(w => w.ArtworkUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().Take(10).ToList();
        var characterGroups = await BuildCollectionCharactersAsync(id, row.WikidataQid, authorizedWorks, ct);
        var contributorGroups = await BuildUniverseCastGroupsAsync(row.WikidataQid, authorizedWorks, ct);
        var relationships = await BuildUniverseRelationshipGroupsAsync(row.WikidataQid, ct);
        var mediaGroups = BuildCollectionMediaGroups(DetailEntityType.Universe, works, new HashSet<Guid>(), expectedTotal: null);

        return new DetailPageViewModel
        {
            Id = id.ToString("D"),
            EntityType = DetailEntityType.Universe,
            PresentationContext = context,
            Title = row.DisplayName ?? "Universe",
            Subtitle = $"Universe • {works.Count} item{(works.Count == 1 ? "" : "s")} in library",
            Description = row.Description,
            SourceLinks = BuildExternalSourceLinks(row.WikidataQid, null, null),
            Artwork = BuildArtwork(DetailEntityType.Universe, row.BackgroundUrl, row.BannerUrl, row.CoverUrl, row.CoverUrl, null, new Dictionary<string, string>(), relatedArt, 0, null),
            Metadata = [new MetadataPill { Label = "Universe" }, new MetadataPill { Label = $"{works.Count} items" }],
            PrimaryActions = [new DetailAction { Key = "timeline", Label = "Explore Timeline", Icon = "account_tree", Route = string.IsNullOrWhiteSpace(row.WikidataQid) ? null : $"/universe/{row.WikidataQid}/explore", IsPrimary = true }],
            OverflowActions = BuildOverflowActions(id, DetailEntityType.Universe, actionAuthorization),
            ContributorGroups = contributorGroups,
            PreviewContributors = BuildPreviewContributors(DetailEntityType.Universe, contributorGroups),
            CharacterGroups = characterGroups,
            PreviewCharacters = characterGroups.SelectMany(g => g.Characters).Take(12).ToList(),
            RelationshipStrip = relationships,
            Tabs = BuildTabs(DetailEntityType.Universe, context, isAdminView, hasUniverse: true),
            MediaGroups = mediaGroups,
            PrimaryModule = BuildPrimaryModule(DetailEntityType.Universe, null, mediaGroups),
            IdentityStatus = ResolveIdentityStatus(row.WikidataQid, null, null),
            LibraryStatus = LibraryStatus.Owned,
            IsAdminView = isAdminView,
        };
    }

}
