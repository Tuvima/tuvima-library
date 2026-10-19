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
    private async Task<IReadOnlyList<OwnedFormatViewModel>> LoadOwnedFormatsAsync(
        Guid workId,
        LibraryItemDetail detail,
        CancellationToken ct,
        Guid? profileId = null,
        IReadOnlyList<Guid>? authorizedAssetIds = null)
    {
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<OwnedFormatRow>(new CommandDefinition(
            """
            SELECT e.id AS EditionId,
                   e.format_label AS FormatLabel,
                   ma.id AS AssetId,
                   ma.file_path_root AS FilePathRoot,
                   (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url', 'cover') LIMIT 1) AS AssetCoverUrl,
                   (SELECT value FROM canonical_values WHERE entity_id = e.id AND key IN ('cover_url', 'cover') LIMIT 1) AS EditionCoverUrl,
                   (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'runtime' LIMIT 1) AS Runtime,
                   (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'page_count' LIMIT 1) AS PageCount,
                   (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key = 'narrator' ORDER BY ordinal LIMIT 1) AS Narrator,
                   us.progress_pct AS ProgressPct
            FROM editions e
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            LEFT JOIN user_states us ON us.asset_id = ma.id
                                    AND us.user_id = @defaultOwnerUserId
            WHERE e.work_id = @workId
              AND ma.status = 'Normal'
              AND (@filterAssets=0 OR ma.id IN @authorizedAssetIds)
            ORDER BY COALESCE(e.format_label, ''), ma.file_path_root;
            """,
            new
            {
                workId,
                defaultOwnerUserId = profileId ?? DefaultOwnerUserId,
                filterAssets = authorizedAssetIds is null ? 0 : 1,
                authorizedAssetIds = authorizedAssetIds is { Count: > 0 }
                    ? authorizedAssetIds.Select(GuidSql.ToBlob).ToArray()
                    : [GuidSql.ToBlob(Guid.Empty)],
            },
            cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            return
            [
                new OwnedFormatViewModel
                {
                    Id = workId.ToString("D"),
                    FormatType = ToFormatType(detail.MediaType, null),
                    DisplayName = ToFormatDisplay(detail.MediaType, null),
                    CoverUrl = detail.CoverUrl,
                    Runtime = detail.Runtime,
                    Progress = null,
                    PrimaryContributor = detail.Narrator ?? detail.Author ?? detail.Director,
                    Actions = BuildFormatActions(workId, ToFormatType(detail.MediaType, null)),
                }
            ];
        }

        return rows.Select(row =>
        {
            var format = ToFormatType(detail.MediaType, row.FormatLabel ?? Path.GetExtension(row.FilePathRoot));
            return new OwnedFormatViewModel
            {
                Id = row.EditionId.ToString("D"),
                FormatType = format,
                DisplayName = ToFormatDisplay(detail.MediaType, row.FormatLabel),
                CoverUrl = StringHelpers.FirstNonBlankOr(string.Empty, row.AssetCoverUrl, row.EditionCoverUrl, detail.CoverUrl),
                PrimaryContributor = row.Narrator ?? detail.Narrator ?? detail.Author ?? detail.Director,
                FileFormat = Path.GetExtension(row.FilePathRoot)?.TrimStart('.').ToUpperInvariant(),
                Runtime = row.Runtime ?? detail.Runtime,
                PageCount = int.TryParse(row.PageCount, out var pages) ? pages : null,
                Progress = BuildFormatProgress(row.ProgressPct),
                Actions = BuildFormatActions(workId, format),
            };
        }).ToList();
    }

    private async Task<WorkContributorResult> BuildWorkContributorsAsync(Guid workId, LibraryItemDetail detail, DetailEntityType entityType, CancellationToken ct)
    {
        var cast = entityType is DetailEntityType.Movie or DetailEntityType.TvSeason or DetailEntityType.TvShow
            ? await _personCredits.BuildForWorkAsync(workId, ct)
            : [];

        return new WorkContributorResult(cast);
    }

    private async Task<IReadOnlyList<CreditGroupViewModel>> BuildContributorGroupsAsync(
        Guid workId,
        LibraryItemDetail detail,
        DetailEntityType entityType,
        IReadOnlyList<CastCreditDto> cast,
        IReadOnlyDictionary<string, string> canonicalValues,
        CancellationToken ct)
    {
        if (entityType == DetailEntityType.TvEpisode)
        {
            return await BuildTvCreditsAsync(workId, ct);
        }

        var groups = new List<CreditGroupViewModel>();
        async Task AddTextCreditAsync(string title, CreditGroupType type, string? value, string role, string canonicalArrayKey)
        {
            var entries = await LoadContributorEntriesAsync(workId, canonicalArrayKey, value, canonicalValues, ct);
            if (entries.Count == 0)
            {
                return;
            }

            var credits = new List<EntityCreditViewModel>();
            foreach (var entry in entries)
            {
                var name = entry.Name;
                var qid = NormalizeQid(entry.Qid);
                var person = string.IsNullOrWhiteSpace(qid) ? null : await _persons.FindByQidAsync(qid, ct);
                person ??= await _persons.FindByNameAsync(name, ct);
                var imageUrl = person is null
                    ? entries.Count == 1
                        ? StringHelpers.FirstNonBlankOr(string.Empty,
                            GetValue(canonicalValues, $"{canonicalArrayKey}_headshot_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_image_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_profile_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_photo_url"),
                            GetValue(canonicalValues, "headshot_url"))
                        : string.Empty
                    : ApiImageUrls.BuildPersonHeadshotUrl(person.Id, person.LocalHeadshotPath, person.HeadshotUrl);
                credits.Add(new EntityCreditViewModel
                {
                    EntityId = BuildPersonCreditEntityId(person?.Id, qid ?? person?.WikidataQid, name),
                    EntityType = RelatedEntityType.Person,
                    DisplayName = person?.Name ?? name,
                    ImageUrl = imageUrl,
                    FallbackInitials = Initials(person?.Name ?? name),
                    PrimaryRole = role,
                    SortOrder = entry.SortOrder,
                    IsPrimary = entry.SortOrder == 0,
                    IsCanonical = !string.IsNullOrWhiteSpace(qid ?? person?.WikidataQid),
                });
            }

            groups.Add(new CreditGroupViewModel
            {
                Title = title,
                GroupType = type,
                Credits = credits,
            });
        }

        await AddTextCreditAsync("Authors", CreditGroupType.Authors, detail.Author, "Author", "author");
        await AddTextCreditAsync("Narrators", CreditGroupType.Narrators, detail.Narrator, "Narrator", "narrator");
        if (detail.MediaType.Equals("Music", StringComparison.OrdinalIgnoreCase))
        {
            await AddTextCreditAsync("Artists", CreditGroupType.PrimaryArtists, detail.Artist, "Artist", "artist");
        }

        await AddTextCreditAsync("Directors", CreditGroupType.Directors, detail.Director, "Director", "director");
        await AddTextCreditAsync("Writers", CreditGroupType.Writers, detail.Writer, "Writer", "screenwriter");
        await AddTextCreditAsync("Composers", CreditGroupType.MusicCredits, detail.Composer, "Composer", "composer");
        await AddTextCreditAsync("Illustrators", CreditGroupType.Illustrators, detail.Illustrator, "Illustrator", "illustrator");

        if (cast.Count > 0)
        {
            var castCredits = cast.Select((credit, index) => new EntityCreditViewModel
            {
                EntityId = BuildPersonCreditEntityId(credit.PersonId, credit.WikidataQid, credit.Name),
                EntityType = RelatedEntityType.Person,
                DisplayName = credit.Name,
                ImageUrl = credit.HeadshotUrl,
                FallbackInitials = Initials(credit.Name),
                PrimaryRole = "Actor",
                CharacterName = credit.Characters.FirstOrDefault()?.CharacterName,
                CharacterEntityId = credit.Characters.FirstOrDefault()?.FictionalEntityId.ToString("D"),
                CharacterImageUrl = credit.Characters.FirstOrDefault()?.PortraitUrl,
                SortOrder = index,
                IsPrimary = index < 8,
                IsCanonical = !string.IsNullOrWhiteSpace(credit.WikidataQid),
            }).ToList();

            if (castCredits.Count > 0)
            {
                groups.Add(new CreditGroupViewModel
                {
                    Title = "Actors",
                    GroupType = CreditGroupType.Cast,
                    Credits = castCredits,
                });
            }
        }

        if (entityType is DetailEntityType.TvEpisode)
        {
            await AddTextCreditAsync("Guest Stars", CreditGroupType.Cast, GetValue(canonicalValues, MetadataFieldConstants.GuestStar), "Guest Star", MetadataFieldConstants.GuestStar);
        }

        return ApplyContributorGroupPresentation(entityType, groups);
    }

    private static IReadOnlyList<CreditGroupViewModel> ApplyContributorGroupPresentation(
        DetailEntityType entityType,
        IReadOnlyList<CreditGroupViewModel> groups)
    {
        return groups
            .Where(group => ShouldShowContributorGroup(entityType, group))
            .Select(group =>
            {
                var presentation = ResolveGroupPresentation(entityType, group);
                return new CreditGroupViewModel
                {
                    Title = presentation.Title,
                    GroupType = group.GroupType,
                    Credits = group.Credits,
                    DisplayPriority = presentation.Priority,
                    IsInitiallyExpanded = presentation.IsInitiallyExpanded,
                    InitialVisibleCount = presentation.InitialVisibleCount,
                };
            })
            .OrderBy(group => group.DisplayPriority)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldShowContributorGroup(
        DetailEntityType entityType,
        CreditGroupViewModel group)
    {
        if (entityType is DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode)
        {
            return true;
        }

        if (entityType == DetailEntityType.Audiobook)
        {
            return group.GroupType is CreditGroupType.Authors or CreditGroupType.Narrators;
        }

        if (entityType == DetailEntityType.MusicAlbum)
        {
            return group.GroupType is CreditGroupType.PrimaryArtists
                or CreditGroupType.FeaturedArtists
                or CreditGroupType.MusicCredits;
        }

        return true;
    }

    private static (string Title, int Priority, bool IsInitiallyExpanded, int InitialVisibleCount) ResolveGroupPresentation(
        DetailEntityType entityType,
        CreditGroupViewModel group)
    {
        var isVideo = entityType is DetailEntityType.Movie or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode or DetailEntityType.Universe;
        if (isVideo)
        {
            if (group.GroupType == CreditGroupType.Directors)
            {
                return ("Director", 0, true, 2);
            }

            if (group.GroupType == CreditGroupType.Cast)
            {
                return ("Actors", 1, true, 12);
            }

            if (group.GroupType == CreditGroupType.Writers)
            {
                return ("Writers", 2, false, 4);
            }

            if (group.GroupType == CreditGroupType.Producers)
            {
                return ("Producers", 3, false, 4);
            }

            if (group.GroupType == CreditGroupType.MusicCredits)
            {
                return ("Music", 4, false, 3);
            }

            return (group.Title, 8, false, 4);
        }

        if (entityType is DetailEntityType.Book or DetailEntityType.Audiobook or DetailEntityType.Work)
        {
            return group.GroupType switch
            {
                CreditGroupType.Authors => (group.Title, 0, true, 8),
                CreditGroupType.Narrators => (group.Title, 1, true, 6),
                CreditGroupType.Illustrators => ("Contributors", 3, false, 4),
                CreditGroupType.Writers => ("Contributors", 3, false, 4),
                CreditGroupType.MusicCredits => ("Contributors", 4, false, 3),
                _ => (group.Title, 8, false, 4),
            };
        }

        if (entityType is DetailEntityType.ComicIssue or DetailEntityType.ComicSeries)
        {
            return group.GroupType switch
            {
                CreditGroupType.Writers => (group.Title, 0, true, 6),
                CreditGroupType.Illustrators => ("Artists", 1, true, 8),
                CreditGroupType.CreativeTeam => (group.Title, 2, false, 4),
                _ => (group.Title, 8, false, 4),
            };
        }

        if (entityType == DetailEntityType.MusicAlbum)
        {
            return group.GroupType switch
            {
                CreditGroupType.PrimaryArtists => (group.Title, 0, true, 8),
                CreditGroupType.FeaturedArtists => (group.Title, 1, true, 6),
                CreditGroupType.MusicCredits => ("Composers/Producers", 3, false, 4),
                _ => (group.Title, 8, false, 4),
            };
        }

        return group.GroupType == CreditGroupType.RelatedPeople
            ? (group.Title, 0, true, 8)
            : (group.Title, 8, false, 4);
    }

    private static IReadOnlyList<CreditGroupViewModel> SplitCastGroups(IReadOnlyList<EntityCreditViewModel> credits)
    {
        if (credits.Count == 0)
        {
            return [];
        }

        return
        [
            new CreditGroupViewModel
            {
                Title = "Actors",
                GroupType = CreditGroupType.Cast,
                Credits = credits,
            },
        ];
    }

    private async Task<IReadOnlyList<ContributorEntry>> LoadContributorEntriesAsync(
        Guid workId,
        string canonicalArrayKey,
        string? fallbackValue,
        IReadOnlyDictionary<string, string> canonicalValues,
        CancellationToken ct)
    {
        var targetIds = await LoadContributorTargetIdsAsync(workId, ct);

        foreach (var targetId in targetIds)
        {
            var arrayEntries = await _canonicalArrays.GetValuesAsync(targetId, canonicalArrayKey, ct);
            var entries = DeduplicateContributorEntries(arrayEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .OrderBy(entry => entry.Ordinal)
                .Select(entry => new ContributorEntry(
                    entry.Value.Trim(),
                    NormalizeQid(entry.ValueQid),
                    entry.Ordinal))
                .ToList());
            entries = await PreferCollectivePseudonymContributorAsync(canonicalArrayKey, entries, ct);
            if (entries.Count > 0)
            {
                return entries;
            }
        }

        foreach (var targetId in targetIds)
        {
            var claimEntries = await LoadContributorEntriesFromClaimsAsync(targetId, canonicalArrayKey, ct);
            claimEntries = await PreferCollectivePseudonymContributorAsync(canonicalArrayKey, claimEntries, ct);
            if (claimEntries.Count > 0)
            {
                return claimEntries;
            }
        }

        if (string.IsNullOrWhiteSpace(fallbackValue))
        {
            return [];
        }

        var fallbackEntries = SplitNames(fallbackValue)
            .Select((name, index) => new ContributorEntry(
                name,
                ResolveCompanionQidFromCanonical(canonicalValues, canonicalArrayKey, name, index),
                index))
            .ToList();

        return DeduplicateContributorEntries(fallbackEntries);
    }

    private async Task<IReadOnlyList<ContributorEntry>> PreferCollectivePseudonymContributorAsync(
        string canonicalArrayKey,
        IReadOnlyList<ContributorEntry> entries,
        CancellationToken ct)
    {
        if (!canonicalArrayKey.Equals("author", StringComparison.OrdinalIgnoreCase) || entries.Count <= 1)
        {
            return entries;
        }

        foreach (var entry in entries.OrderBy(entry => entry.SortOrder))
        {
            var qid = NormalizeQid(entry.Qid);
            if (string.IsNullOrWhiteSpace(qid))
            {
                continue;
            }

            var person = await _persons.FindByQidAsync(qid, ct);
            if (person?.IsPseudonym != true)
            {
                continue;
            }

            return entries
                .Where(candidate => string.Equals(NormalizeQid(candidate.Qid), qid, StringComparison.OrdinalIgnoreCase))
                .Select((candidate, index) => candidate with { SortOrder = index })
                .ToList();
        }

        return entries;
    }

    private async Task<IReadOnlyList<Guid>> LoadContributorTargetIdsAsync(Guid workId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<ContributorTargetRow>(new CommandDefinition(
            """
            -- readiness: detail page of one item opened by id
            SELECT w.id AS WorkId,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   MIN(ma.id) AS AssetId
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN editions e ON e.work_id = w.id
            LEFT JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id = @workId
            GROUP BY w.id, gp.id, p.id;
            """,
            new { workId },
            cancellationToken: ct));

        if (row is null)
        {
            return [workId];
        }

        var ids = new List<Guid>();
        AddId(row.RootWorkId);
        AddId(row.WorkId);
        AddId(row.AssetId);
        return ids;

        void AddId(Guid? id)
        {
            if (id.HasValue && !ids.Contains(id.Value))
            {
                ids.Add(id.Value);
            }
        }
    }

    private async Task<IReadOnlyList<ContributorEntry>> LoadContributorEntriesFromClaimsAsync(
        Guid entityId,
        string canonicalArrayKey,
        CancellationToken ct)
    {
        var qidKey = canonicalArrayKey + MetadataFieldConstants.CompanionQidSuffix;
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<ContributorClaimRow>(new CommandDefinition(
            """
            SELECT mc.rowid       AS RowNumber,
                   mc.claim_key   AS ClaimKey,
                   mc.claim_value AS ClaimValue
            FROM metadata_claims mc
            WHERE mc.entity_id = @entityId
              AND mc.claim_key IN @claimKeys
              AND NULLIF(mc.claim_value, '') IS NOT NULL
            ORDER BY mc.rowid;
            """,
            new { entityId, claimKeys = new[] { canonicalArrayKey, qidKey } },
            cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        var nameClaims = rows
            .Where(row => row.ClaimKey.Equals(canonicalArrayKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var qidClaims = rows
            .Where(row => row.ClaimKey.Equals(qidKey, StringComparison.OrdinalIgnoreCase))
            .Select(row => ParseQidLabel(row.ClaimValue))
            .ToList();

        var qidByName = qidClaims
            .Where(parsed => !string.IsNullOrWhiteSpace(parsed.Label) && !string.IsNullOrWhiteSpace(parsed.Qid))
            .GroupBy(parsed => parsed.Label!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Qid, StringComparer.OrdinalIgnoreCase);

        var entries = new List<ContributorEntry>();
        if (qidByName.Count > 0)
        {
            foreach (var parsed in qidClaims)
            {
                var name = StringHelpers.FirstNonBlankOr(string.Empty, parsed.Label, parsed.Qid);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    entries.Add(new ContributorEntry(name, parsed.Qid, entries.Count));
                }
            }

            foreach (var claim in nameClaims)
            {
                var name = claim.ClaimValue.Trim();
                if (string.IsNullOrWhiteSpace(name)
                    || LooksLikeAggregateContributorName(name)
                    || qidByName.ContainsKey(name))
                {
                    continue;
                }

                entries.Add(new ContributorEntry(name, null, entries.Count));
            }

            return DeduplicateContributorEntries(entries);
        }

        for (var i = 0; i < nameClaims.Count; i++)
        {
            var name = nameClaims[i].ClaimValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            qidByName.TryGetValue(name, out var qid);
            qid ??= i < qidClaims.Count ? qidClaims[i].Qid : null;
            entries.Add(new ContributorEntry(name, qid, i));
        }

        foreach (var parsed in qidClaims)
        {
            var name = StringHelpers.FirstNonBlankOr(string.Empty, parsed.Label, parsed.Qid);
            if (!string.IsNullOrWhiteSpace(name))
            {
                entries.Add(new ContributorEntry(name, parsed.Qid, entries.Count));
            }
        }

        return DeduplicateContributorEntries(entries);
    }
}
