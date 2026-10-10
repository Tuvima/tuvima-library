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
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<IReadOnlyList<ContributorEntry>> LoadCollectionContributorEntriesAsync(
        Guid collectionId,
        string canonicalArrayKey,
        string? fallbackValue,
        IReadOnlyDictionary<string, string> canonicalValues,
        CancellationToken ct)
    {
        var arrayEntries = await _canonicalArrays.GetValuesAsync(collectionId, canonicalArrayKey, ct);
        var entries = DeduplicateContributorEntries(arrayEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .OrderBy(entry => entry.Ordinal)
            .Select(entry => new ContributorEntry(
                entry.Value.Trim(),
                NormalizeQid(entry.ValueQid),
                entry.Ordinal))
            .ToList());
        if (entries.Count > 0)
        {
            return entries;
        }

        entries = await LoadContributorEntriesFromClaimsAsync(collectionId, canonicalArrayKey, ct);
        if (entries.Count > 0)
        {
            return entries;
        }

        if (string.IsNullOrWhiteSpace(fallbackValue))
        {
            return [];
        }

        return DeduplicateContributorEntries(SplitNames(fallbackValue)
            .Select((name, index) => new ContributorEntry(
                name,
                ResolveCompanionQidFromCanonical(canonicalValues, canonicalArrayKey, name, index),
                index))
            .ToList());
    }

    private async Task<IReadOnlyList<CharacterGroupViewModel>> BuildCollectionCharactersAsync(
        Guid collectionId,
        string? qid,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CollectionCharacterRow>(new CommandDefinition(
            """
            SELECT fe.id AS Id,
                   fe.label AS Label,
                   fe.wikidata_qid AS WikidataQid,
                   fe.fictional_universe_qid AS UniverseQid,
                   fe.fictional_universe_label AS UniverseLabel,
                   fe.image_url AS ImageUrl,
                   fe.entity_sub_type AS EntitySubType,
                   cp.id AS PortraitId,
                   cp.image_url AS PortraitImageUrl,
                   cp.local_image_path AS PortraitLocalImagePath,
                   CASE WHEN cp.is_default = 1 THEN 1 ELSE 0 END AS PortraitIsDefault
            FROM fictional_entities fe
            LEFT JOIN character_portraits cp
                ON cp.fictional_entity_id = fe.id
               AND cp.id = (
                   SELECT cp2.id
                   FROM character_portraits cp2
                   WHERE cp2.fictional_entity_id = fe.id
                   ORDER BY cp2.is_default DESC, cp2.updated_at DESC, cp2.created_at DESC
                   LIMIT 1
               )
            WHERE fe.fictional_universe_qid = @qid
              AND fe.entity_sub_type = 'Character'
              AND (@restrictWorks = 0 OR EXISTS (
                  SELECT 1
                  FROM fictional_entity_work_links visible_link
                  WHERE visible_link.entity_id=fe.id
                    AND visible_link.work_qid IN @visibleWorkQids))
            ORDER BY fe.label
            LIMIT 24;
            """,
            new
            {
                qid,
                restrictWorks = authorizedWorks is null ? 0 : 1,
                visibleWorkQids = VisibleWorkQids(authorizedWorks),
            },
            cancellationToken: ct));

        var characters = rows.Select(row => new EntityCreditViewModel
        {
            EntityId = row.Id.ToString("D"),
            EntityType = RelatedEntityType.Character,
            DisplayName = row.Label,
            ImageUrl = ApiImageUrls.BuildCharacterPortraitUrl(row.PortraitId, row.PortraitLocalImagePath, row.PortraitImageUrl)
                ?? row.ImageUrl,
            FallbackInitials = Initials(row.Label),
            PrimaryRole = "Character",
            IsCanonical = !string.IsNullOrWhiteSpace(row.WikidataQid),
        }).ToList();

        return characters.Count == 0
            ? []
            : [new CharacterGroupViewModel { Title = "Characters", GroupType = CharacterGroupType.MainCharacters, Characters = characters }];
    }

    private async Task<IReadOnlyList<CreditGroupViewModel>> BuildUniverseCastGroupsAsync(
        string? qid,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<UniversePerformerRow>(new CommandDefinition(
            """
            SELECT cpl.rowid AS LinkOrder,
                   p.id AS PersonId,
                   p.name AS PersonName,
                   p.wikidata_qid AS PersonQid,
                   p.headshot_url AS HeadshotUrl,
                   p.local_headshot_path AS LocalHeadshotPath,
                   fe.id AS CharacterId,
                   fe.label AS CharacterName,
                   cp.id AS PortraitId,
                   cp.image_url AS PortraitImageUrl,
                   cp.local_image_path AS PortraitLocalImagePath,
                   CASE WHEN cp.is_default = 1 THEN 1 ELSE 0 END AS PortraitIsDefault
            FROM fictional_entities fe
            INNER JOIN character_performer_links cpl
                ON cpl.fictional_entity_id = fe.id
            INNER JOIN persons p
                ON p.id = cpl.person_id
            LEFT JOIN character_portraits cp
                ON cp.fictional_entity_id = fe.id
               AND cp.person_id = p.id
            WHERE fe.fictional_universe_qid = @qid
              AND (@restrictWorks = 0 OR cpl.work_qid IN @visibleWorkQids)
            ORDER BY cpl.rowid, fe.label, cp.is_default DESC;
            """,
            new
            {
                qid,
                restrictWorks = authorizedWorks is null ? 0 : 1,
                visibleWorkQids = VisibleWorkQids(authorizedWorks),
            },
            cancellationToken: ct))).ToList();

        var credits = rows
            .Where(row => row.PersonId.HasValue && !string.IsNullOrWhiteSpace(row.PersonName))
            .GroupBy(row => new
            {
                row.PersonId,
                row.PersonName,
                row.PersonQid,
                row.HeadshotUrl,
                row.LocalHeadshotPath,
            })
            .Select(group =>
            {
                var sourceOrder = group.Min(row => row.LinkOrder);
                var preferredCharacter = group
                    .OrderBy(row => row.LinkOrder)
                    .ThenByDescending(row => row.PortraitIsDefault)
                    .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.PortraitImageUrl))
                    .FirstOrDefault();

                return new EntityCreditViewModel
                {
                    EntityId = group.Key.PersonId!.Value.ToString("D"),
                    EntityType = RelatedEntityType.Person,
                    DisplayName = group.Key.PersonName ?? "Unknown",
                    ImageUrl = ApiImageUrls.BuildPersonHeadshotUrl(group.Key.PersonId.Value, group.Key.LocalHeadshotPath, group.Key.HeadshotUrl),
                    FallbackInitials = Initials(group.Key.PersonName ?? "Unknown"),
                    PrimaryRole = "Actor",
                    CharacterName = preferredCharacter?.CharacterName,
                    CharacterEntityId = preferredCharacter?.CharacterId.ToString("D"),
                    CharacterImageUrl = preferredCharacter is null
                        ? null
                        : ApiImageUrls.BuildCharacterPortraitUrl(
                            preferredCharacter.PortraitId,
                            preferredCharacter.PortraitLocalImagePath,
                            preferredCharacter.PortraitImageUrl),
                    SortOrder = (int)Math.Min(sourceOrder, int.MaxValue),
                    IsCanonical = !string.IsNullOrWhiteSpace(group.Key.PersonQid),
                };
            })
            .OrderBy(credit => credit.SortOrder)
            .ThenBy(credit => credit.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((credit, index) => new EntityCreditViewModel
            {
                EntityId = credit.EntityId,
                EntityType = credit.EntityType,
                DisplayName = credit.DisplayName,
                ImageUrl = credit.ImageUrl,
                FallbackInitials = credit.FallbackInitials,
                PrimaryRole = credit.PrimaryRole,
                SecondaryRole = credit.SecondaryRole,
                CharacterName = credit.CharacterName,
                CharacterEntityId = credit.CharacterEntityId,
                CharacterImageUrl = credit.CharacterImageUrl,
                SortOrder = index,
                IsPrimary = index < 8,
                IsCanonical = credit.IsCanonical,
                SourceName = credit.SourceName,
                SourceId = credit.SourceId,
            })
            .Take(24)
            .ToList();

        return credits.Count == 0
            ? []
            : ApplyContributorGroupPresentation(DetailEntityType.Universe, SplitCastGroups(credits));
    }

    private static string[] VisibleWorkQids(IReadOnlyList<DisplayWorkRow>? authorizedWorks)
    {
        if (authorizedWorks is null)
        {
            return ["__unrestricted__"];
        }

        var qids = authorizedWorks
            .Select(work => work.IdentityQid)
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        return qids.Length > 0 ? qids : ["__none__"];
    }

    private async Task<IReadOnlyList<RelationshipGroup>> BuildUniverseRelationshipGroupsAsync(string? qid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<UniverseRelationshipRow>(new CommandDefinition(
            """
            SELECT er.relationship_type AS RelationshipType,
                   er.subject_qid AS SubjectQid,
                   er.object_qid AS ObjectQid,
                   COALESCE(subject.label, er.subject_qid) AS SubjectLabel,
                   COALESCE(object.label, er.object_qid) AS ObjectLabel,
                   subject.entity_sub_type AS SubjectType,
                   object.entity_sub_type AS ObjectType
            FROM entity_relationships er
            INNER JOIN fictional_entities subject
                ON subject.wikidata_qid = er.subject_qid
               AND subject.fictional_universe_qid = @qid
            INNER JOIN fictional_entities object
                ON object.wikidata_qid = er.object_qid
               AND object.fictional_universe_qid = @qid
            ORDER BY er.relationship_type, SubjectLabel, ObjectLabel
            LIMIT 60;
            """,
            new { qid },
            cancellationToken: ct))).ToList();

        return rows
            .GroupBy(row => row.RelationshipType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RelationshipGroup
            {
                Title = ToRelationshipGroupTitle(group.Key),
                Items = group.Take(12).Select(row => new RelatedEntityChip
                {
                    Id = row.SubjectQid,
                    EntityType = RelatedEntityType.Character,
                    Label = $"{row.SubjectLabel} {FormatRelationshipLabel(row.RelationshipType)} {row.ObjectLabel}",
                }).ToList(),
            })
            .ToList();
    }



    private static IReadOnlyList<EntityCreditViewModel> BuildPreviewContributors(
        DetailEntityType entityType,
        IReadOnlyList<CreditGroupViewModel> groups)
    {
        var cast = CreditsFor(groups, CreditGroupType.Cast);
        var directors = CreditsFor(groups, CreditGroupType.Directors);
        var authors = CreditsFor(groups, CreditGroupType.Authors);
        var narrators = CreditsFor(groups, CreditGroupType.Narrators);
        var writers = CreditsFor(groups, CreditGroupType.Writers);
        var illustrators = CreditsFor(groups, CreditGroupType.Illustrators);
        var artists = CreditsFor(groups, CreditGroupType.PrimaryArtists);
        var featuredArtists = CreditsFor(groups, CreditGroupType.FeaturedArtists);
        var musicCredits = CreditsFor(groups, CreditGroupType.MusicCredits);

        var preview = entityType switch
        {
            DetailEntityType.Movie => directors.Take(1).Concat(cast.Take(5)).ToList(),
            DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => cast.Take(5).ToList(),
            DetailEntityType.Book => authors.Take(2).ToList(),
            DetailEntityType.Audiobook => authors.Take(2).Concat(narrators.Take(2)).ToList(),
            DetailEntityType.Work => authors.Take(2).Concat(narrators.Take(2)).ToList(),
            DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => writers.Take(2).Concat(illustrators.Take(2)).ToList(),
            DetailEntityType.MusicAlbum => artists.Take(2).Concat(featuredArtists.Take(2)).Concat(musicCredits.Take(1)).ToList(),
            DetailEntityType.Universe or DetailEntityType.MovieSeries or DetailEntityType.BookSeries => cast.Take(6).ToList(),
            _ => [],
        };

        preview = DeduplicatePreviewCredits(preview).ToList();
        return preview.Count > 0
            ? preview
            : DeduplicatePreviewCredits(groups.SelectMany(g => g.Credits).OrderBy(c => c.SortOrder)).Take(6).ToList();
    }

    private static IReadOnlyList<EntityCreditViewModel> CreditsFor(
        IReadOnlyList<CreditGroupViewModel> groups,
        CreditGroupType groupType)
        => groups
            .Where(group => group.GroupType == groupType)
            .SelectMany(group => group.Credits)
            .OrderBy(credit => credit.SortOrder)
            .ToList();

    private static IEnumerable<EntityCreditViewModel> DeduplicatePreviewCredits(IEnumerable<EntityCreditViewModel> credits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in credits)
        {
            var key = !string.IsNullOrWhiteSpace(credit.EntityId)
                ? credit.EntityId
                : $"{credit.EntityType}:{credit.DisplayName}";
            if (seen.Add(key))
            {
                yield return credit;
            }
        }
    }

    private static string ToRelationshipGroupTitle(string relationshipType)
        => string.Join(' ', relationshipType.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static string FormatRelationshipLabel(string relationshipType) => relationshipType switch
    {
        "father" => "is father of",
        "mother" => "is mother of",
        "spouse" => "is spouse of",
        "sibling" => "is sibling of",
        "child" => "is child of",
        "opponent" => "opposes",
        "student_of" => "is student of",
        "member_of" => "is member of",
        "residence" => "resides in",
        "located_in" => "is located in",
        "part_of" => "is part of",
        "head_of" => "leads",
        "parent_organization" => "is parent organization of",
        "has_parts" => "has part",
        "creator" => "created",
        "performer" => "performed by",
        "same_as" => "is same as",
        "significant_person" => "is significant to",
        "affiliation" => "is affiliated with",
        "based_on" => "is based on",
        "derivative_work" => "is derivative of",
        "inspired_by" => "is inspired by",
        _ => relationshipType.Replace('_', ' '),
    };

    private static string BuildCollectionSubtitle(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlyDictionary<string, string> values)
    {
        if (entityType == DetailEntityType.MusicAlbum)
        {
            return FormatContributorList(StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(values, "album_artist"),
                GetValue(values, "artist"),
                works.Select(w => w.Artist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))))
                ?? "Album";
        }

        if (entityType == DetailEntityType.ComicSeries)
        {
            return OwnedCollectionCountLabel(entityType, works);
        }

        var types = works.Select(w => FormatEntityType(InferMediaItemEntityType(w))).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
        return $"{FormatEntityType(entityType)} • {OwnedCollectionCountLabel(entityType, works)} • {string.Join(", ", types)}";
    }

    private static string OwnedCollectionCountLabel(DetailEntityType entityType, IReadOnlyList<CollectionWorkSummary> works)
    {
        var ownedCount = works.Count(work => work.IsOwned);
        var totalCount = works.Count;
        var noun = CollectionItemNoun(entityType, totalCount);

        return totalCount > ownedCount
            ? $"{ownedCount} of {totalCount} {noun} owned"
            : $"{ownedCount} owned {noun}";
    }

    private static string CollectionItemNoun(DetailEntityType entityType, int count) =>
        entityType switch
        {
            DetailEntityType.ComicSeries => count == 1 ? "issue" : "issues",
            DetailEntityType.MusicAlbum => count == 1 ? "track" : "tracks",
            _ => count == 1 ? "item" : "items",
        };

    private static IReadOnlyList<CreditGroupViewModel> BuildPersonCreditGroups(IReadOnlyList<PersonLibraryCreditDto> credits, DetailPresentationContext context)
        => credits
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Role) ? "Credits" : c.Role)
            .OrderBy(g => PersonRolePriority(g.Key, context))
            .Select(g => new CreditGroupViewModel
            {
                Title = g.Key,
                GroupType = CreditGroupType.RelatedPeople,
                Credits = g.Select((credit, index) => new EntityCreditViewModel
                {
                    EntityId = credit.WorkId.ToString("D"),
                    EntityType = RelatedEntityType.Series,
                    DisplayName = credit.Title,
                    ImageUrl = credit.CoverUrl,
                    FallbackInitials = Initials(credit.Title),
                    PrimaryRole = credit.Role,
                    SecondaryRole = credit.MediaType,
                    CharacterName = credit.Characters.FirstOrDefault()?.CharacterName,
                    SortOrder = index,
                }).ToList(),
            }).ToList();

    private static IReadOnlyList<CharacterGroupViewModel> BuildPersonCharacterGroups(IReadOnlyList<PersonCharacterRoleDto> roles)
    {
        var characters = roles.Select(role => new EntityCreditViewModel
        {
            EntityId = role.FictionalEntityId.ToString("D"),
            EntityType = RelatedEntityType.Character,
            DisplayName = role.CharacterName ?? "Character",
            ImageUrl = role.PortraitUrl,
            FallbackInitials = Initials(role.CharacterName ?? "Character"),
            PrimaryRole = "Character",
            SecondaryRole = role.WorkTitle,
        }).ToList();

        return characters.Count == 0
            ? []
            : [new CharacterGroupViewModel { Title = "Characters", GroupType = CharacterGroupType.MainCharacters, Characters = characters }];
    }

    private static IReadOnlyList<MediaGroupingViewModel> BuildPersonMediaGroups(IReadOnlyList<PersonLibraryCreditDto> credits, DetailPresentationContext context)
        => credits
            .GroupBy(c => string.Equals(c.AssociationType, "ThroughGroup", StringComparison.OrdinalIgnoreCase)
                ? $"With {StringHelpers.FirstNonBlank(c.ViaGroupName, "group")}"
                : PersonMediaGroupKey(c.MediaType, context))
            .OrderBy(g => PersonMediaGroupPriority(g.Key, context))
            .Select(g => new MediaGroupingViewModel
            {
                Key = g.Key.ToLowerInvariant().Replace(" ", "-").Replace("&", "and"),
                Title = g.Key,
                Items = g
                    .GroupBy(CreditDisplayId, StringComparer.OrdinalIgnoreCase)
                    .Select(creditGroup => BuildPersonMediaItem(creditGroup.ToList(), context))
                    .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            }).ToList();

    private static MediaGroupingItemViewModel BuildPersonMediaItem(
        IReadOnlyList<PersonLibraryCreditDto> credits,
        DetailPresentationContext context)
    {
        var representative = credits[0];
        var roles = credits
            .Select(credit => NormalizePersonRole(credit.Role))
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(role => PersonRolePriority(role, context))
            .ThenBy(PersonRoleRank)
            .ThenBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var characters = credits
            .SelectMany(credit => credit.Characters)
            .Select(character => character.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        var characterSummary = characters.Count switch
        {
            0 => null,
            1 => characters[0],
            _ => string.Join(", ", characters.Take(2)) + (characters.Count > 2 ? $" +{characters.Count - 2}" : string.Empty),
        };
        var roleSummary = roles.Count == 0 ? null : string.Join(", ", roles);
        var entityType = MapCreditToEntityType(representative);

        return new MediaGroupingItemViewModel
        {
            Id = CreditDisplayId(representative),
            EntityType = entityType,
            Title = representative.Title,
            Subtitle = string.Join(" · ", new[] { StringHelpers.FirstNonBlankOr(string.Empty, characterSummary, roleSummary), representative.Year }.Where(v => !string.IsNullOrWhiteSpace(v))),
            ArtworkUrl = representative.CoverUrl,
            Lane = DetailLane(entityType),
            Roles = roles,
            Metadata = roles.Select(role => new MetadataPill { Label = role, Kind = "role" }).ToList(),
            Actions = [new DetailAction { Key = "open", Label = "Open", Icon = "open_in_new", Route = BuildCreditRoute(representative) }],
            IsOwned = true,
        };
    }

    private static IReadOnlyList<MetadataPill> BuildPersonMetadata(IReadOnlyList<string> roles, int ownedWorkCount)
        => roles
            .Take(4)
            .Select(role => new MetadataPill { Label = role })
            .Append(new MetadataPill { Label = $"{ownedWorkCount} {(ownedWorkCount == 1 ? "title" : "titles")} in library" })
            .ToList();

    private static IReadOnlyList<string> BuildPersonDisplayRoles(
        IReadOnlyList<PersonLibraryCreditDto> credits,
        IReadOnlyList<string> fallbackRoles,
        DetailPresentationContext context)
    {
        var mediaRoles = credits
            .Where(credit => !string.Equals(credit.AssociationType, "ThroughGroup", StringComparison.OrdinalIgnoreCase))
            .Select(credit => NormalizePersonRole(credit.Role))
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .GroupBy(role => role!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => PersonRolePriority(group.Key, context))
            .ThenBy(group => PersonRoleRank(group.Key))
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();

        if (mediaRoles.Count > 0)
        {
            return mediaRoles;
        }

        return fallbackRoles
            .Select(NormalizePersonRole)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(role => PersonRolePriority(role, context))
            .ThenBy(PersonRoleRank)
            .ThenBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizePersonRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var normalized = role.Trim().Replace('_', ' ').Replace('-', ' ');
        return normalized.ToLowerInvariant() switch
        {
            "screenwriter" => "Writer",
            "writer" => "Writer",
            "voice actor" => "Voice Actor",
            "voiceactor" => "Voice Actor",
            "primary artist" => "Artist",
            "featured artist" => "Performer",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant()),
        };
    }

    private static int PersonRoleRank(string role) => role.ToLowerInvariant() switch
    {
        "author" => 0,
        "actor" => 1,
        "director" => 2,
        "writer" => 3,
        "producer" => 4,
        "artist" => 5,
        "illustrator" => 6,
        "narrator" => 7,
        "voice actor" => 8,
        "performer" => 9,
        "composer" => 10,
        _ => 50,
    };

    private static DescriptionAttributionViewModel? BuildWikipediaDescriptionAttribution(
        string? description,
        string? wikipediaUrl,
        DateTimeOffset? retrievedAt = null,
        bool isModifiedOrSummarized = false)
    {
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(wikipediaUrl))
        {
            return null;
        }

        return new DescriptionAttributionViewModel
        {
            SourceName = "Wikipedia",
            SourceTitle = "article text",
            SourceUrl = wikipediaUrl,
            LicenseName = "CC BY-SA 4.0",
            LicenseUrl = "https://creativecommons.org/licenses/by-sa/4.0/",
            RetrievedAt = retrievedAt,
            IsModifiedOrSummarized = isModifiedOrSummarized,
            Notice = "Text from Wikipedia is available under the Creative Commons Attribution-ShareAlike 4.0 License; additional terms may apply.",
        };
    }

    private static DescriptionAttributionViewModel? BuildDescriptionAttribution(
        DescriptionSelection selection,
        LibraryItemDetail detail,
        IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(selection.Text) || selection.IsGeneratedFallback)
        {
            return null;
        }

        var descriptionProvider = GetCanonicalProviderId(detail,
            selection.SourceKey ?? MetadataFieldConstants.Description);
        if (Guid.TryParse(descriptionProvider, out var descriptionProviderId)
            && descriptionProviderId == WellKnownProviders.Tvdb)
        {
            var episodeId = GetValue(values, BridgeIdKeys.TvdbEpisodeId);
            var seasonId = GetValue(values, BridgeIdKeys.TvdbSeasonId);
            var seriesId = GetValue(values, BridgeIdKeys.TvdbId);
            return new DescriptionAttributionViewModel
            {
                SourceName = "TheTVDB",
                SourceTitle = "TV metadata",
                SourceUrl = !string.IsNullOrWhiteSpace(episodeId)
                    ? $"https://thetvdb.com/episodes/{episodeId}"
                    : !string.IsNullOrWhiteSpace(seasonId)
                        ? $"https://thetvdb.com/seasons/{seasonId}"
                        : !string.IsNullOrWhiteSpace(seriesId)
                            ? $"https://thetvdb.com/series/{seriesId}" : "https://thetvdb.com",
                LicenseName = "TheTVDB terms",
                LicenseUrl = "https://thetvdb.com/tos",
                RetrievedAt = GetCanonicalLastScoredAt(detail,
                    selection.SourceKey ?? MetadataFieldConstants.Description),
                Notice = "Metadata from TheTVDB.",
            };
        }

        if (IsComicIssueDescriptionSourceKey(selection.SourceKey))
        {
            var winningProviderId = GetCanonicalProviderId(detail, selection.SourceKey!);
            var isComicVine = Guid.TryParse(winningProviderId, out var providerId)
                && providerId == WellKnownProviders.ComicVine;
            if (!isComicVine)
            {
                return null;
            }

            var sourceUrl = ResolveComicVineIssueUrl(values);
            return new DescriptionAttributionViewModel
            {
                SourceName = "Comic Vine",
                SourceTitle = "issue synopsis",
                SourceUrl = sourceUrl,
                LicenseName = "Comic Vine API Terms",
                LicenseUrl = "https://comicvine.gamespot.com/api/",
                RetrievedAt = GetCanonicalLastScoredAt(detail, selection.SourceKey!),
                IsModifiedOrSummarized = false,
                Notice = "Issue synopsis from Comic Vine; use is governed by Comic Vine API terms.",
            };
        }

        return BuildWikipediaDescriptionAttribution(
            selection.Text,
            GetValue(values, "wikipedia_url"),
            GetCanonicalLastScoredAt(detail, selection.SourceKey ?? MetadataFieldConstants.Description));
    }

    private static bool IsComicIssueDescriptionSourceKey(string? key) =>
        string.Equals(key, MetadataFieldConstants.IssueDescription, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "issue_overview", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ExternalSourceLinkViewModel> BuildExternalSourceLinks(
        string? wikidataQid,
        string? wikipediaUrl,
        SequencePlacementViewModel? sequence,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var identifiers = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var qid = ExtractQid(FirstText(
            wikidataQid,
            values is null ? null : GetValue(values, BridgeIdKeys.WikidataQid)));
        if (!string.IsNullOrWhiteSpace(qid))
        {
            identifiers[BridgeIdKeys.WikidataQid] = qid;
        }

        if (!string.IsNullOrWhiteSpace(wikipediaUrl))
        {
            identifiers["wikipedia_url"] = wikipediaUrl.Trim();
        }

        var mediaType = values is not null
                        && !string.IsNullOrWhiteSpace(GetValue(values, MetadataFieldConstants.ShowName))
            ? "TV"
            : null;
        var links = _providerSourceLinks.Resolve(identifiers, mediaType).ToList();

        if (values is not null)
        {
            var method = GetValue(values, MetadataFieldConstants.QidResolutionMethod);
            var rollupPresentation = method?.Trim().ToLowerInvariant() switch
            {
                "tv_show_rollup" => (Key: "wikidata-show", Label: "TV show on Wikidata", Tooltip: "Open the root TV show identity used for every owned episode."),
                "music_album_rollup" => (Key: "wikidata-album", Label: "Album on Wikidata", Tooltip: "Open the album identity used for every owned track."),
                "comic_series_rollup" => (Key: "wikidata-series", Label: "Series/run on Wikidata", Tooltip: "Open the series or run identity used for this comic issue."),
                _ => default,
            };

            if (!string.IsNullOrWhiteSpace(rollupPresentation.Key))
            {
                var wikidataLinkIndex = links.FindIndex(link => string.Equals(
                    link.Key,
                    BridgeIdKeys.WikidataQid,
                    StringComparison.OrdinalIgnoreCase));
                if (wikidataLinkIndex >= 0)
                {
                    var link = links[wikidataLinkIndex];
                    links[wikidataLinkIndex] = new ExternalSourceLinkViewModel
                    {
                        Key = rollupPresentation.Key,
                        Label = rollupPresentation.Label,
                        Url = link.Url,
                        SourceName = link.SourceName,
                        Tooltip = rollupPresentation.Tooltip,
                    };
                }
            }
        }

        var seriesQid = ExtractQid(FirstText(sequence?.SourceContainerId, sequence?.ContainerId));
        if (!string.IsNullOrWhiteSpace(seriesQid)
            && !string.Equals(seriesQid, qid, StringComparison.OrdinalIgnoreCase))
        {
            var seriesLink = _providerSourceLinks.Resolve(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BridgeIdKeys.WikidataQid] = seriesQid,
                    },
                    mediaType)
                .FirstOrDefault();
            if (seriesLink is not null
                && !links.Any(link => string.Equals(link.Url, seriesLink.Url, StringComparison.OrdinalIgnoreCase)))
            {
                links.Add(new ExternalSourceLinkViewModel
                {
                    Key = "wikidata-series",
                    Label = "Series on Wikidata",
                    Url = seriesLink.Url,
                    SourceName = seriesLink.SourceName,
                    Tooltip = $"Sequence source for {sequence?.ContainerTitle ?? "this series"}",
                });
            }
        }

        return links;
    }

    private static string? BuildWikidataEntityUrl(string? qid)
        => IsWikidataQid(qid) ? $"https://www.wikidata.org/wiki/{NormalizeSequenceContainerId(qid)}" : null;


    private static string? ResolveComicVineIssueUrl(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        return FirstText(
            NormalizeExternalUrl(GetValue(values, MetadataFieldConstants.IssueSourceUrl)),
            BuildComicVineIssueUrl(GetValue(values, BridgeIdKeys.ComicVineId)));
    }

    private static string? NormalizeExternalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : null;
    }

    private static string? BuildComicVineIssueUrl(string? comicVineId)
    {
        if (string.IsNullOrWhiteSpace(comicVineId))
        {
            return null;
        }

        var id = comicVineId.Trim();
        var delimiter = id.IndexOf("::", StringComparison.Ordinal);
        if (delimiter >= 0)
        {
            id = id[..delimiter].Trim();
        }

        return id.All(char.IsDigit)
            ? $"https://comicvine.gamespot.com/issue/4000-{Uri.EscapeDataString(id)}/"
            : null;
    }

    private static PersonDetailFacts BuildPersonDetails(
        MediaEngine.Domain.Entities.Person person,
        IReadOnlyList<string> displayRoles,
        string? wikipediaUrl,
        IReadOnlyList<MediaEngine.Domain.Entities.Person> aliases,
        IReadOnlyList<PersonGroupMemberDto> groupMembers,
        IReadOnlyList<PersonGroupMemberDto> memberOfGroups)
        => new()
        {
            WikidataQid = person.WikidataQid,
            WikidataUrl = BuildWikidataEntityUrl(person.WikidataQid),
            Biography = person.Biography,
            Occupation = person.Occupation,
            Roles = displayRoles,
            DateOfBirth = person.DateOfBirth,
            DateOfDeath = person.DateOfDeath,
            PlaceOfBirth = person.PlaceOfBirth,
            PlaceOfDeath = person.PlaceOfDeath,
            Nationality = person.Nationality,
            IsPseudonym = person.IsPseudonym,
            IsGroup = person.IsGroup,
            CreatedAt = person.CreatedAt,
            EnrichedAt = person.EnrichedAt,
            ExternalLinks = BuildPersonExternalLinks(person, wikipediaUrl),
            Aliases = aliases.Select(alias => new PersonRelatedLink
            {
                Id = alias.Id.ToString("D"),
                Name = alias.Name,
                Subtitle = alias.IsPseudonym ? "Pen name" : null,
                ImageUrl = ApiImageUrls.BuildPersonHeadshotUrl(
                    alias.Id,
                    alias.LocalHeadshotPath,
                    alias.HeadshotUrl),
                Route = $"/details/person/{alias.Id:D}",
            }).ToList(),
            GroupMembers = groupMembers.Select(member => new PersonRelatedLink
            {
                Id = member.Id.ToString("D"),
                Name = member.Name,
                Subtitle = member.DateRange,
                ImageUrl = member.HeadshotUrl,
                Route = $"/details/person/{member.Id:D}",
            }).ToList(),
            MemberOfGroups = memberOfGroups.Select(group => new PersonRelatedLink
            {
                Id = group.Id.ToString("D"),
                Name = group.Name,
                Subtitle = group.DateRange,
                ImageUrl = group.HeadshotUrl,
                Route = $"/details/person/{group.Id:D}",
            }).ToList(),
        };

    private static IReadOnlyList<PersonExternalLink> BuildPersonExternalLinks(MediaEngine.Domain.Entities.Person person, string? wikipediaUrl)
    {
        var links = new List<PersonExternalLink>();
        AddPersonExternalLink(links, "website", "Website", person.Website, "WEB");
        AddPersonExternalLink(links, "wikipedia", "Wikipedia", wikipediaUrl, "W");
        AddPersonExternalLink(links, "instagram", "Instagram", BuildSocialUrl("instagram", person.Instagram), "IG");
        AddPersonExternalLink(links, "twitter", "X", BuildSocialUrl("twitter", person.Twitter), "X");
        AddPersonExternalLink(links, "tiktok", "TikTok", BuildSocialUrl("tiktok", person.TikTok), "TT");
        AddPersonExternalLink(links, "mastodon", "Mastodon", BuildSocialUrl("mastodon", person.Mastodon), "M");
        return links;
    }

    private static void AddPersonExternalLink(List<PersonExternalLink> links, string key, string label, string? url, string iconLabel)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        links.Add(new PersonExternalLink
        {
            Key = key,
            Label = label,
            Url = url,
            IconLabel = iconLabel,
        });
    }

    private static string? BuildSocialUrl(string platform, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        var isUrl = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isUrl)
        {
            return value;
        }

        var handle = value.TrimStart('@');
        return platform switch
        {
            "instagram" => $"https://instagram.com/{handle}",
            "twitter" => $"https://x.com/{handle}",
            "tiktok" => $"https://tiktok.com/@{handle}",
            "mastodon" when value.Contains('@') && value.Contains('.') => BuildMastodonUrl(value),
            _ => value,
        };
    }

    private static string BuildMastodonUrl(string value)
    {
        var parts = value.Split('@', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? $"https://{parts[1]}/@{parts[0]}" : value;
    }

    private static IReadOnlyList<DetailAction> BuildPersonActions(Guid personId, DetailEntityType entityType, DetailPresentationContext context)
        => [new DetailAction { Key = "view-works", Label = "View Works", Icon = "collections", IsPrimary = true }];

    private static string? PreferredAssetUrl(IReadOnlyList<MediaEngine.Domain.Entities.EntityAsset> assets, string assetType)
        => assets.FirstOrDefault(a => a.AssetTypeValue.Equals(assetType, StringComparison.OrdinalIgnoreCase) && a.IsPreferred)?.ImageUrl
           ?? assets.FirstOrDefault(a => a.AssetTypeValue.Equals(assetType, StringComparison.OrdinalIgnoreCase))?.ImageUrl;

    private static CanonicalIdentityStatus ResolveIdentityStatus(string? qid, string? status, double? confidence)
    {
        if (!string.IsNullOrWhiteSpace(qid))
        {
            return CanonicalIdentityStatus.WikidataLinked;
        }

        if (status?.Contains("review", StringComparison.OrdinalIgnoreCase) == true || confidence is < 0.7)
        {
            return CanonicalIdentityStatus.NeedsReview;
        }

        return CanonicalIdentityStatus.ProviderMatched;
    }

    private static SequenceLabels ResolveSequenceLabels(DetailEntityType type) => type switch
    {
        DetailEntityType.TvEpisode or DetailEntityType.TvSeason or DetailEntityType.TvShow =>
            new("Show", "Episode", "Episodes", "Season"),
        DetailEntityType.ComicIssue or DetailEntityType.ComicSeries =>
            new("Volume", "Issue", "Issues", null),
        DetailEntityType.Movie or DetailEntityType.MovieSeries =>
            new("Movie Series", "Movie", "Movies", null),
        DetailEntityType.Audiobook =>
            new("Series", "Audiobook", "Audiobooks", null),
        _ => new("Series", "Book", "Books", null),
    };

    private static string? ResolveSequencePositionLabel(DetailEntityType type, string? positionLabel, string? episodeLabel)
        => type == DetailEntityType.TvEpisode
            ? FirstText(episodeLabel, positionLabel)
            : positionLabel;

    private static (string? Key, string? Title) ResolveSequenceGroup(DetailEntityType type, string? rawGroup)
    {
        if (type is not (DetailEntityType.TvEpisode or DetailEntityType.TvSeason or DetailEntityType.TvShow))
        {
            return (null, null);
        }

        var value = FirstText(rawGroup, "1")!;
        var normalized = NormalizeSequenceOrdinal(value);
        return ($"season-{normalized}", $"Season {normalized}");
    }

    private static string? FormatSequencePositionText(DetailEntityType type, string? rawPosition, int? position)
    {
        var value = position?.ToString(CultureInfo.InvariantCulture) ?? NormalizeSequenceOrdinal(rawPosition);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return type == DetailEntityType.TvEpisode ? $"E{value}" : value;
    }

    private static string NormalizeSequenceOrdinal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return int.TryParse(trimmed.TrimStart('0'), out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static List<SequenceItemViewModel> NormalizeSequenceItems(IEnumerable<SequenceItemViewModel> items, DetailEntityType entityType)
        => items.Select(item =>
        {
            var positionText = item.PositionText ?? FormatSequencePositionText(entityType, item.PositionLabel, item.PositionNumber);
            var group = string.IsNullOrWhiteSpace(item.GroupKey) && string.IsNullOrWhiteSpace(item.GroupTitle)
                ? ResolveSequenceGroup(entityType, null)
                : (item.GroupKey, item.GroupTitle);
            return new SequenceItemViewModel
            {
                Id = item.Id,
                EntityType = item.EntityType,
                Title = item.Title,
                ArtworkUrl = item.ArtworkUrl,
                EpisodeStillUrl = item.EpisodeStillUrl,
                EpisodeStillWidthPx = item.EpisodeStillWidthPx,
                EpisodeStillHeightPx = item.EpisodeStillHeightPx,
                Route = item.Route,
                Description = item.Description,
                Duration = item.Duration,
                PublicationDate = item.PublicationDate,
                PositionNumber = item.PositionNumber,
                PositionSort = item.PositionSort,
                PositionLabel = item.PositionLabel,
                PositionText = positionText,
                GroupKey = group.Item1,
                GroupTitle = group.Item2,
                MembershipScope = item.MembershipScope,
                IsCurrent = item.IsCurrent,
                IsOwned = item.IsOwned,
                ProgressState = item.ProgressState,
                EpisodeContext = item.EpisodeContext,
                ProgressPercent = item.ProgressPercent,
                PositionSeconds = item.PositionSeconds,
                DurationSeconds = item.DurationSeconds,
                RemainingSeconds = item.RemainingSeconds,
                ProgressLabel = item.ProgressLabel,
                CoveredWith = item.CoveredWith,
            };
        }).ToList();

    private static IReadOnlyList<SequenceGroupViewModel> BuildSequenceGroups(
        IReadOnlyList<SequenceItemViewModel> items,
        string fallbackTitle,
        int? mainSequenceExpectedTotal = null)
    {
        var grouped = items
            .GroupBy(item => item.GroupKey ?? "all", StringComparer.OrdinalIgnoreCase)
            .Select(group => new SequenceGroupViewModel
            {
                Key = group.Key,
                Title = group.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.GroupTitle))?.GroupTitle ?? fallbackTitle,
                TotalKnownItems = string.Equals(group.Key, "main-sequence", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(group.Key, "all", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(group.Count(), mainSequenceExpectedTotal ?? 0)
                    : group.Count(),
                HasAuthoritativeTotal = mainSequenceExpectedTotal.HasValue
                    && (string.Equals(group.Key, "main-sequence", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(group.Key, "all", StringComparison.OrdinalIgnoreCase)),
                Items = group.ToList(),
                OwnedCount = group.Where(item => item.IsOwned).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                CompletedCount = group.Where(item => item.IsOwned && item.ProgressState == LibraryProgressState.Completed).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            })
            .OrderBy(group => SequenceGroupSort(group.Key))
            .ThenBy(group => TryParseSeriesPosition(group.Key.Replace("season-", string.Empty, StringComparison.OrdinalIgnoreCase)) ?? int.MaxValue)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return grouped.Count == 0
            ? [new SequenceGroupViewModel
            {
                Key = "all",
                Title = fallbackTitle,
                TotalKnownItems = Math.Max(items.Count, mainSequenceExpectedTotal ?? 0),
                HasAuthoritativeTotal = mainSequenceExpectedTotal.HasValue,
                Items = items,
                OwnedCount = items.Where(item => item.IsOwned).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                CompletedCount = items.Where(item => item.IsOwned && item.ProgressState == LibraryProgressState.Completed).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            }]
            : grouped;
    }

    private static int SequenceGroupSort(string key)
        => key switch
        {
            "main-sequence" => 0,
            "supplementary" => 1,
            "collected-content" => 2,
            "broader-context" => 3,
            "unpositioned" => 1,
            _ => 4,
        };

}
