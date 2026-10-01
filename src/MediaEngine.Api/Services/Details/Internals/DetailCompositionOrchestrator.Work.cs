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
    private async Task<DetailPageViewModel?> BuildWorkAsync(
        Guid workId,
        DetailEntityType requestedType,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        string? selectedContainerId,
        IReadOnlySet<Guid> favoriteWorkIds,
        Guid? profileId,
        IReadOnlyList<Guid>? authorizedAssetIds,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        using (var redirectConnection = _db.CreateConnection())
            workId = WorkRedirects.Resolve(redirectConnection, workId);
        if (authorizedAssetIds is { Count: 0 })
        {
            return null;
        }

        var preferredAssetId = authorizedAssetIds is { Count: > 0 }
            ? authorizedAssetIds[0]
            : Guid.Empty;
        var detail = preferredAssetId == Guid.Empty
            ? await _libraryItems.GetDetailAsync(workId, ct)
            : await _libraryItems.GetDetailAsync(workId, preferredAssetId, ct);
        if (detail is null)
        {
            return null;
        }

        var entityType = requestedType == DetailEntityType.Work ? InferWorkEntityType(detail.MediaType, detail) : requestedType;
        var ownedFormats = await LoadOwnedFormatsAsync(workId, detail, ct, profileId, authorizedAssetIds);
        var values = await LoadWorkCanonicalMapAsync(workId, detail, ct, authorizedAssetIds);
        var displayOverrides = await LoadWorkDisplayOverridesAsync(workId, ct);
        var displayTitle = ResolveDisplayTitleOverride(displayOverrides, entityType);
        var resolvedTitle = ResolveWorkDisplayTitle(displayTitle, detail, values, entityType);
        var artworkFallback = await LoadWorkArtworkFallbackAsync(workId, ct);
        var multiFormatState = ownedFormats.Count > 1
            ? MultiFormatState.MultipleFormatsSeparateProgress
            : MultiFormatState.SingleFormat;
        var ownedCoverUrls = ownedFormats
            .Select(f => f.CoverUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .ToList();
        var foregroundArtworkUrl = StringHelpers.FirstNonBlankOr(string.Empty,
            ownedCoverUrls.FirstOrDefault(),
            detail.CoverUrl,
            GetValue(values, "cover_url"),
            GetValue(values, "cover"),
            GetValue(values, "poster_url"),
            GetValue(values, "poster"),
            artworkFallback.CoverUrl,
            artworkFallback.SquareUrl);
        var backdropUrl = entityType == DetailEntityType.TvEpisode
            ? StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(values, "episode_still_url"))
            : StringHelpers.FirstNonBlankOr(string.Empty,
                detail.BackgroundUrl,
                detail.HeroUrl,
                artworkFallback.BackgroundUrl);
        var bannerUrl = entityType == DetailEntityType.TvEpisode ? null : StringHelpers.FirstNonBlankOr(string.Empty, detail.BannerUrl, artworkFallback.BannerUrl);
        if (entityType == DetailEntityType.TvEpisode)
        {
            foregroundArtworkUrl = backdropUrl;
        }

        var contributors = await BuildWorkContributorsAsync(workId, detail, entityType, ct);
        var characters = BuildCharacterGroupsFromCast(contributors.CastCredits);
        var contributorGroups = await BuildContributorGroupsAsync(workId, detail, entityType, contributors.CastCredits, values, ct);
        var managedCurrentArtworkUrl = await _reader.LoadManagedWorkCoverUrlAsync(
                workId,
                entityType,
                foregroundArtworkUrl,
                ct)
            ?? ownedCoverUrls.FirstOrDefault(IsManagedArtworkUrl);
        SequencePlacementViewModel? sequencePlacement = null;
        IReadOnlyList<CreditGroupViewModel> fullContributorGroups = contributorGroups;
        string? episodeSeriesLogoUrl = null;
        Guid? episodeShowId = null;
        if (entityType == DetailEntityType.TvEpisode)
        {
            episodeShowId = Guid.TryParse(selectedContainerId, out var selectedShowId)
                ? selectedShowId
                : string.IsNullOrWhiteSpace(selectedContainerId)
                    ? await ResolveTvSeriesRootWorkIdAsync(workId, ct)
                    : null;
        }

        if (episodeShowId is { } showId)
        {
            var showModel = await BuildCollectionAsync(
                showId,
                DetailEntityType.TvShow,
                context,
                isAdminView,
                actionAuthorization,
                favoriteWorkIds,
                ct,
                workId,
                profileId,
                authorizedWorks: authorizedWorks);
            sequencePlacement = showModel?.SequencePlacement;
            if (sequencePlacement is null || !sequencePlacement.OrderedItems.Any(item => item.Id == workId.ToString("D") && item.IsOwned))
            {
                return null;
            }

            fullContributorGroups = showModel!.FullContributorGroups;
            episodeSeriesLogoUrl = showModel.Artwork.LogoUrl;
        }

        if (entityType == DetailEntityType.TvEpisode
            && string.IsNullOrWhiteSpace(episodeSeriesLogoUrl))
        {
            episodeSeriesLogoUrl = await LoadTvSeriesLogoForEpisodeAsync(workId, ct);
        }

        sequencePlacement ??= await BuildSequencePlacementAsync(
            workId,
            detail,
            entityType,
            selectedContainerId,
            managedCurrentArtworkUrl,
            resolvedTitle,
            ct);
        var artwork = BuildArtwork(
            entityType,
            backdropUrl,
            bannerUrl,
            foregroundArtworkUrl,
            foregroundArtworkUrl,
            null,
            values,
            entityType == DetailEntityType.TvEpisode ? [] : ownedCoverUrls,
            ownedFormats.Count,
            detail.ArtworkSource,
            episodeSeriesLogoUrl);
        var mediaGroups = await BuildWorkMediaGroupsAsync(workId, entityType, profileId, ct);
        var heroProgress = BuildHeroProgress(entityType, detail.Runtime, ownedFormats)
            ?? BuildAudiobookHeroProgress(entityType, detail.Runtime, mediaGroups);
        var descriptionSelection = ResolveLongDescription(detail, values, entityType);
        var longDescription = descriptionSelection.Text is { } descriptionText ? DescriptionText.Normalize(descriptionText) : null;
        var displayDescription = ResolveDisplayOverride(displayOverrides, "description");
        var displayTagline = ResolveDisplayOverride(displayOverrides, "tagline");
        var displaySubtitle = ResolveDisplayOverride(displayOverrides, MetadataFieldConstants.Subtitle);
        var displayProjection = ApplyDetailScalarOverrides(detail, values, displayOverrides);
        var factsDetail = displayProjection.Detail;
        var factValues = displayProjection.Values;

        var semanticTagline = entityType == DetailEntityType.TvEpisode ? null : StringHelpers.FirstNonBlank(displayTagline, GetValue(values, MetadataFieldConstants.Tagline));
        var semanticSubtitle = entityType is DetailEntityType.Book or DetailEntityType.Audiobook or DetailEntityType.ComicIssue or DetailEntityType.Work
            ? StringHelpers.FirstNonBlank(displaySubtitle, GetValue(values, MetadataFieldConstants.Subtitle))
            : null;
        var secondaryTitle = ResolveSecondaryTitleText(
            entityType, values, semanticTagline, semanticSubtitle, displayDescription ?? longDescription);
        var descriptionAttribution = displayDescription is null
            ? BuildDescriptionAttribution(descriptionSelection, detail, values)
            : BuildLocalDescriptionAttribution();
        var relationships = BuildRelationshipStrip(detail, sequencePlacement);

        return new DetailPageViewModel
        {
            Id = workId.ToString("D"),
            EntityType = entityType,
            PresentationContext = context,
            EditorTarget = new DetailEditorTarget
            {
                EntityId = workId.ToString("D"),
                EntityKind = "Work",
                ContainerMode = IsCanonicalContainerEntity(entityType) ? "Canonical" : "Singular",
                InitialTab = "details",
            },
            Title = resolvedTitle,
            Subtitle = BuildSubtitle(detail, entityType, values, multiFormatState),
            Tagline = semanticTagline,
            SecondaryTitleText = secondaryTitle.Text,
            SecondaryTitleTextKind = secondaryTitle.Kind,
            SecondaryTitleTextHasMore = secondaryTitle.HasMore,
            Description = displayDescription ?? longDescription,
            DescriptionAttribution = descriptionAttribution,
            SourceLinks = BuildExternalSourceLinks(detail.WikidataQid, GetValue(values, "wikipedia_url"), sequencePlacement, values),
            Facts = BuildWorkFacts(factsDetail, entityType, factValues, contributorGroups),
            UsesEpisodeArtwork = entityType == DetailEntityType.TvEpisode,
            Artwork = artwork,
            HeroBrand = BuildHeroBrand(
                entityType,
                StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, "network"), GetValue(values, "studio"), GetValue(values, "broadcaster")),
                StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, "network_logo_url"), GetValue(values, "network_logo"), GetValue(values, "studio_logo_url"), GetValue(values, "broadcaster_logo_url"))),
            Progress = heroProgress,
            OwnedFormats = ownedFormats,
            MultiFormatState = multiFormatState,
            SyncCapability = BuildSyncCapability(workId, ownedFormats, multiFormatState),
            SequencePlacement = sequencePlacement,
            Metadata = BuildMetadataPills(factsDetail, entityType, factValues, ownedFormats),
            PrimaryActions = BuildPrimaryActions(
                workId,
                entityType,
                context,
                ownedFormats,
                heroProgress,
                FormatSeasonEpisode(detail.SeasonNumber, detail.EpisodeNumber)),
            SecondaryActions = BuildSecondaryActions(workId, entityType, favoriteWorkIds.Contains(workId), ownedFormats),
            OverflowActions = BuildOverflowActions(workId, entityType, actionAuthorization,
                await IngestionAvailability.IsUpdatingAsync(_db, workId, ct)),
            ContributorGroups = contributorGroups,
            FullContributorGroups = fullContributorGroups,
            PreviewContributors = BuildPreviewContributors(entityType, contributorGroups),
            CharacterGroups = characters,
            PreviewCharacters = characters.SelectMany(g => g.Characters).Take(12).ToList(),
            RelationshipStrip = relationships,
            Tabs = BuildTabs(
                entityType,
                context,
                isAdminView,
                sequencePlacement is not null,
                HasUniverseRelationship(relationships)),
            MediaGroups = mediaGroups,
            PrimaryModule = BuildPrimaryModule(entityType, sequencePlacement, mediaGroups),
            IdentityStatus = ResolveIdentityStatus(detail.WikidataQid, detail.Status, detail.Confidence),
            LibraryStatus = LibraryStatus.Owned,
            IsAdminView = isAdminView,
        };
    }

    private async Task<DetailPageViewModel?> BuildBookSeriesAsync(
        Guid seriesId,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlySet<Guid> favoriteWorkIds,
        Guid? profileId,
        string? selectedContainerId,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct)
    {
        var canonicalSeries = await BuildCollectionAsync(
            seriesId,
            DetailEntityType.BookSeries,
            context,
            isAdminView,
            actionAuthorization,
            favoriteWorkIds,
            ct,
            profileId: profileId,
            selectedContainerId: selectedContainerId,
            authorizedWorks: authorizedWorks);
        if (canonicalSeries is not null || _collectionBrowse is null)
        {
            return canonicalSeries;
        }

        var groups = await _collectionBrowse.GetSystemViewGroupsAsync("Books", "series", ct).ConfigureAwait(false);
        var group = groups.FirstOrDefault(candidate =>
            SystemViewGroupIdentity.CreateId(candidate, "Books", "series") == seriesId);
        if (group is null || group.PreviewItems.Count == 0)
        {
            return null;
        }

        DetailPageViewModel? seedDetail = null;
        foreach (var preview in group.PreviewItems)
        {
            seedDetail = await BuildWorkAsync(
                preview.WorkId,
                DetailEntityType.Book,
                context,
                isAdminView,
                actionAuthorization,
                selectedContainerId: selectedContainerId,
                favoriteWorkIds: favoriteWorkIds,
                profileId: profileId,
                authorizedAssetIds: null,
                authorizedWorks: authorizedWorks,
                ct: ct);
            if (seedDetail is not null)
            {
                break;
            }
        }

        if (seedDetail is null)
        {
            return null;
        }

        var placement = seedDetail.SequencePlacement;
        var seriesTitle = FormatSequenceContainerTitle(placement?.ContainerTitle)
            ?? FormatSequenceContainerTitle(group.DisplayName)
            ?? group.DisplayName;
        var ownedItems = group.PreviewItems
            .DistinctBy(item => item.WorkId)
            .Select(item => new MediaGroupingItemViewModel
            {
                Id = item.WorkId.ToString("D"),
                EntityType = DetailEntityType.Book,
                Title = item.Title,
                ArtworkUrl = item.ImageUrl,
                IsOwned = true,
                Actions =
                [
                    new DetailAction
                    {
                        Key = "open",
                        Label = "Open",
                        Route = $"/details/work/{item.WorkId:D}?context=read",
                    },
                ],
            })
            .ToList();
        var knownItems = placement?.OrderedItems.Count ?? ownedItems.Count;
        var hasMissingItems = placement?.OrderedItems.Any(item => !item.IsOwned) == true;
        var description = placement?.ContainerDescription;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = ownedItems.Count == 1
                ? $"1 book from {seriesTitle} is in your library."
                : $"{ownedItems.Count} books from {seriesTitle} are in your library.";
        }

        return new DetailPageViewModel
        {
            Id = seriesId.ToString("D"),
            EntityType = DetailEntityType.BookSeries,
            PresentationContext = context,
            Title = seriesTitle,
            Subtitle = group.Creator ?? seedDetail.Facts?.Authors.FirstOrDefault(),
            Tagline = knownItems > ownedItems.Count
                ? $"{ownedItems.Count} of {knownItems} known entries owned"
                : $"{ownedItems.Count} owned {(ownedItems.Count == 1 ? "book" : "books")}",
            Description = description,
            Facts = new DetailFactsViewModel
            {
                MediaKind = "Book Series",
                Year = group.Year,
                Genres = seedDetail.Facts?.Genres ?? [],
                Authors = seedDetail.Facts?.Authors ?? [],
                Series = seriesTitle,
            },
            Artwork = BuildArtwork(
                DetailEntityType.BookSeries,
                null,
                null,
                null,
                null,
                null,
                new Dictionary<string, string>(),
                ownedItems.Select(item => item.ArtworkUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().ToList(),
                0,
                null),
            SequencePlacement = placement,
            Metadata =
            [
                new MetadataPill
                {
                    Label = $"{ownedItems.Count} owned",
                    Kind = "count",
                },
            ],
            PrimaryActions = BuildCollectionActions(seriesId, DetailEntityType.BookSeries, context, null, []),
            OverflowActions = BuildOverflowActions(seriesId, DetailEntityType.BookSeries, actionAuthorization),
            ContributorGroups = seedDetail.ContributorGroups,
            PreviewContributors = seedDetail.PreviewContributors,
            Tabs = BuildTabs(
                DetailEntityType.BookSeries,
                context,
                isAdminView,
                placement is not null,
                hasUniverse: false),
            PrimaryModule = BuildPrimaryModule(DetailEntityType.BookSeries, placement, []),
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "owned-books",
                    Title = "Books in your library",
                    Items = ownedItems,
                    OwnedCount = ownedItems.Count,
                    TotalCount = Math.Max(knownItems, ownedItems.Count),
                    MissingCount = Math.Max(0, knownItems - ownedItems.Count),
                    CompletionPercent = knownItems <= 0 ? 0 : ownedItems.Count * 100d / knownItems,
                },
            ],
            IdentityStatus = seedDetail.IdentityStatus,
            LibraryStatus = hasMissingItems ? LibraryStatus.PartiallyOwned : LibraryStatus.Owned,
            IsAdminView = isAdminView,
        };
    }

    private async Task<string?> LoadTvSeriesLogoForEpisodeAsync(Guid episodeWorkId, CancellationToken ct)
    {
        var rootWorkId = await ResolveTvSeriesRootWorkIdAsync(episodeWorkId, ct);
        if (!rootWorkId.HasValue)
        {
            return null;
        }

        var logo = await _entityAssets.GetPreferredAsync(rootWorkId.Value.ToString("D"), "Logo", ct);
        return logo is null ? null : $"/stream/artwork/{logo.Id:D}";
    }

    private async Task<Guid?> ResolveTvSeriesRootWorkIdAsync(Guid episodeWorkId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rootWorkId = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            WITH RECURSIVE ancestors(id, parent_work_id, depth) AS (
                SELECT id, parent_work_id, 0
                FROM works
                WHERE id = @episodeWorkId
                UNION ALL
                SELECT parent.id, parent.parent_work_id, child.depth + 1
                FROM works parent
                INNER JOIN ancestors child ON parent.id = child.parent_work_id
                WHERE child.depth < 32
            )
            SELECT id
            FROM ancestors
            ORDER BY depth DESC
            LIMIT 1;
            """,
            new { episodeWorkId = GuidSql.ToBlob(episodeWorkId) },
            cancellationToken: ct));
        return rootWorkId.HasValue && rootWorkId.Value != episodeWorkId
            ? rootWorkId
            : null;
    }

    internal static DetailScalarProjection ApplyDetailScalarOverrides(
        LibraryItemDetail detail,
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyDictionary<string, string> displayOverrides)
    {
        string? Override(string key) => displayOverrides.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

        var releaseDate = Override("release_date")
            ?? Override("air_date")
            ?? Override("first_air_date")
            ?? Override("publication_date")
            ?? Override("original_publication_date")
            ?? Override("edition_release_date");
        var runtime = Override("runtime") ?? Override("duration");
        var displayFields = canonicalValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        if (Override("genre") is { } genre) displayFields[MetadataFieldConstants.Genre] = genre;
        if (Override("content_rating") is { } contentRating) displayFields["content_rating"] = contentRating;
        if (Override("language") is { } language) displayFields[MetadataFieldConstants.Language] = language;
        if (displayOverrides.TryGetValue(MetadataFieldConstants.CustomTags, out var customTags))
        {
            displayFields[MetadataFieldConstants.CustomTags] = customTags.Trim();
        }

        return new DetailScalarProjection(
            detail with
            {
                Year = Override("year") ?? detail.Year,
                ReleaseDate = releaseDate ?? detail.ReleaseDate,
                Rating = Override("rating") ?? detail.Rating,
                Runtime = runtime ?? detail.Runtime,
                Language = Override("language") ?? detail.Language,
                Genre = Override("genre") ?? detail.Genre,
            },
            displayFields);
    }

    internal sealed record DetailScalarProjection(
        LibraryItemDetail Detail,
        IReadOnlyDictionary<string, string> Values);

}
