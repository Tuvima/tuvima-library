using System.Globalization;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Persons;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using static MediaEngine.Api.Services.Collections.CollectionResponseFormatting;
using CollectionCreateRequest = MediaEngine.Contracts.Collections.CollectionCreateRequest;
using CollectionDto = MediaEngine.Contracts.Collections.CollectionDto;
using CollectionItemAddRequest = MediaEngine.Contracts.Collections.CollectionItemAddRequest;
using CollectionItemReorderRequest = MediaEngine.Contracts.Collections.CollectionItemReorderRequest;
using CollectionPreviewRequest = MediaEngine.Contracts.Collections.CollectionPreviewRequest;
using CollectionPreviewResponse = MediaEngine.Contracts.Collections.CollectionPreviewResponse;
using CollectionUpdateRequest = MediaEngine.Contracts.Collections.CollectionUpdateRequest;
using EnabledRequest = MediaEngine.Contracts.Collections.CollectionEnabledRequest;
using FeaturedRequest = MediaEngine.Contracts.Collections.CollectionFeaturedRequest;
using ParentCollectionDto = MediaEngine.Contracts.Collections.ParentCollectionDto;
using PlacementRequest = MediaEngine.Contracts.Collections.CollectionPlacementRequest;
using RelatedCollectionsResponse = MediaEngine.Contracts.Collections.RelatedCollectionsResponse;
using SeriesManifestViewDto = MediaEngine.Contracts.Collections.SeriesManifestViewDto;
using WorkDto = MediaEngine.Contracts.Collections.WorkDto;

namespace MediaEngine.Api.Endpoints;

public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/collections")
                       .WithTags("Collections");

        group.MapCollectionPersonalMediaEndpoints();

        group.MapPut("/{id:guid}/display-overrides", async (
            Guid id,
            MediaEngine.Contracts.Items.ItemDisplayOverridesRequest body,
            ICollectionRepository collections,
            CollectionDisplayOverrideRepository overrides,
            IProfileRepository profiles,
            ISystemActivityRepository activity,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null) return ApiErrors.NotFound($"Collection '{id}' not found.");
            var profile = await ResolveActiveProfileAsync(profileId, profiles, httpContext, ct);
            if (!CollectionAccessPolicy.CanAccess(collection, profile))
                return ApiErrors.Forbidden("The active profile cannot access this collection.");
            if (body.Fields.Count == 0 || body.Fields.Keys.Any(key => key is not ("title" or "description")))
                return ApiErrors.BadRequest("Collection overrides support title and description only.");
            if (!await overrides.SaveAsync(id, body.Fields, ct))
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            await activity.LogAsync(new SystemActivityEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = id,
                EntityType = "Collection",
                Detail = "Saved collection display overrides.",
            }, ct);
            return Results.Ok();
        })
        .WithName("SaveCollectionDisplayOverrides")
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapGet("/{collectionId:guid}/series-manifest", async (
            Guid collectionId,
            ISeriesManifestRepository manifestRepo,
            CancellationToken ct) =>
        {
            var manifest = await manifestRepo.GetViewByCollectionIdAsync(collectionId, ct);
            return manifest is null
                ? ApiErrors.NotFound($"No series manifest found for collection '{collectionId}'.")
                : Results.Ok(manifest.ToContract());
        })
        .WithName("GetCollectionSeriesManifest")
        .WithSummary("Returns a Wikidata-backed ordered series manifest with owned and missing item states.")
        .Produces<SeriesManifestViewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "collectionId");

        group.MapGet("/", async (
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collections = await browseReadService.GetAllAsync(ct);
            var visibleProjection = await display.LoadWorksAsync(ct);
            var visibleWorkIds = visibleProjection.Select(work => work.WorkId).ToHashSet();
            return Results.Ok(collections
                .Select(collection => FilterCollection(collection, visibleWorkIds))
                .Where(collection => collection.Works.Count > 0)
                .ToList());
        })
        .WithName("GetAllCollections")
        .WithSummary("List all media collections with their works and canonical metadata values.")
        .Produces<List<CollectionDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/search", async (
            string? q,
            ICollectionSearchReadService searchReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var results = await searchReadService.SearchAsync(q, ct);
            var visibleProjection = await display.LoadWorksAsync(ct);
            var visibleWorkIds = visibleProjection.Select(work => work.WorkId).ToHashSet();
            return Results.Ok(results.Where(result => visibleWorkIds.Contains(result.WorkId)).ToList());
        })
        .WithName("SearchCollections")
        .WithSummary("Full-text search across all works. Returns up to 20 matching results.")
        .Produces<List<SearchResultDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);


        // GET /collections/{id}/related?limit= — cascading related collections: series → author → genre → explore.
        // GET /collections/parents — list all Parent Collections for top-level franchise navigation.
        // IMPORTANT: registered before /{id:guid} routes to avoid route conflicts.
        group.MapGet("/parents", async (
            ICollectionRepository collectionRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var allCollections = (await collectionRepo.GetAllAsync(ct))
                .Where(collection => collection.Works.Any(work => visibleWorkIds.Contains(work.Id)))
                .ToList();

            var parentIds = allCollections
                .Where(h => h.ParentCollectionId.HasValue)
                .Select(h => h.ParentCollectionId!.Value)
                .Distinct()
                .ToHashSet();

            var parents = allCollections
                .Where(h => parentIds.Contains(h.Id))
                .Select(h =>
                {
                    var children = allCollections.Where(c => c.ParentCollectionId == h.Id).ToList();
                    // Aggregate media types across all works in child collections
                    var mediaTypes = children
                        .SelectMany(c => c.Works)
                        .Where(work => visibleWorkIds.Contains(work.Id))
                        .Select(w => w.MediaType.ToString())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(t => t)
                        .ToList();

                    return new ParentCollectionDto
                    {
                        Id = h.Id,
                        UniverseId = h.UniverseId,
                        DisplayName = h.DisplayName,
                        Description = h.Description,
                        WikidataQid = h.WikidataQid,
                        ParentCollectionId = null,
                        UniverseStatus = h.UniverseStatus.ToStorageValue(),
                        CreatedAt = h.CreatedAt,
                        ChildCollectionCount = children.Count,
                        MediaTypes = string.Join(", ", mediaTypes),
                        TotalWorks = children.Sum(c => c.Works.Count(work => visibleWorkIds.Contains(work.Id))),
                    };
                })
                .OrderBy(h => h.DisplayName)
                .ToList();

            return Results.Ok(parents);
        })
        .WithName("GetParentCollections")
        .WithSummary("Returns all Parent Collections (franchise-level groupings).")
        .Produces<List<ParentCollectionDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/{id}/children — returns child Collections of a given parent.
        group.MapGet("/{id:guid}/children", async (Guid id, ICollectionRepository collectionRepo, CancellationToken ct) =>
        {
            var children = await collectionRepo.GetChildCollectionsAsync(id, ct);
            var result = children.Select(h => new CollectionChildSummary(
                h.Id,
                h.DisplayName,
                h.ParentCollectionId,
                h.CreatedAt,
                h.UniverseStatus.ToStorageValue())).ToList();

            return Results.Ok(result);
        })
        .WithName("GetCollectionChildren")
        .WithSummary("Returns child Collections of the given Parent Collection.")
        .Produces<List<CollectionChildSummary>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        // GET /collections/{id}/parent — returns the parent Collection of a given Collection (if any).
        group.MapGet("/{id:guid}/parent", async (Guid id, ICollectionRepository collectionRepo, CancellationToken ct) =>
        {
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!collection.ParentCollectionId.HasValue)
            {
                return Results.Ok(new CollectionParentResponse(null));
            }

            var parent = await collectionRepo.GetByIdAsync(collection.ParentCollectionId.Value, ct);
            if (parent is null)
            {
                return Results.Ok(new CollectionParentResponse(null));
            }

            return Results.Ok(new CollectionParentResponse(
                new ParentCollectionSummary(
                    parent.Id,
                    parent.DisplayName,
                    parent.CreatedAt,
                    parent.UniverseStatus.ToStorageValue())));
        })
        .WithName("GetCollectionParent")
        .WithSummary("Returns the Parent Collection of the given Collection, if any.")
        .Produces<CollectionParentResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        group.MapGet("/{id:guid}/related", async (
            Guid id,
            int? limit,
            ICollectionRepository collectionRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var dtos = (await collectionRepo.GetAllAsync(ct))
                .Select(collection => FilterCollection(collection.ToContract(), visibleWorkIds))
                .Where(collection => collection.Works.Count > 0)
                .ToList();

            var target = dtos.FirstOrDefault(h => h.Id == id);
            if (target is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var page = PagedRequest.From(null, limit, defaultLimit: 20);
            int take = page.Limit;

            var targetSeries = GetCanonical(target.Works.FirstOrDefault(), "series");
            var targetAuthor = GetCanonical(target.Works.FirstOrDefault(), "author");
            var targetGenre = GetCanonical(target.Works.FirstOrDefault(), "genre");

            var result = new List<CollectionDto>();
            var seen = new HashSet<Guid> { id };
            string reason = string.Empty;
            string title = string.Empty;

            // Stage 1: same series
            if (!string.IsNullOrWhiteSpace(targetSeries))
            {
                var matches = dtos
                    .Where(h => !seen.Contains(h.Id) &&
                           string.Equals(GetCanonical(h.Works.FirstOrDefault(), "series"),
                               targetSeries, StringComparison.OrdinalIgnoreCase))
                    .Take(take)
                    .ToList();
                if (matches.Count > 0)
                {
                    result.AddRange(matches);
                    matches.ForEach(h => seen.Add(h.Id));
                    reason = "Same Series";
                    title = $"More in {targetSeries}";
                }
            }

            // Stage 2: same author
            if (result.Count < take && !string.IsNullOrWhiteSpace(targetAuthor))
            {
                var matches = dtos
                    .Where(h => !seen.Contains(h.Id) &&
                           string.Equals(GetCanonical(h.Works.FirstOrDefault(), "author"),
                               targetAuthor, StringComparison.OrdinalIgnoreCase))
                    .Take(take - result.Count)
                    .ToList();
                if (matches.Count > 0)
                {
                    if (result.Count == 0) { reason = "Same Creator"; title = $"More by {targetAuthor}"; }
                    result.AddRange(matches);
                    matches.ForEach(h => seen.Add(h.Id));
                }
            }

            // Stage 3: same genre
            if (result.Count < take && !string.IsNullOrWhiteSpace(targetGenre))
            {
                var targetGenreFirst = targetGenre.Split(',', ';')[0].Trim();
                var matches = dtos
                    .Where(h => !seen.Contains(h.Id) &&
                           (GetCanonical(h.Works.FirstOrDefault(), "genre") ?? string.Empty)
                               .Contains(targetGenreFirst, StringComparison.OrdinalIgnoreCase))
                    .Take(take - result.Count)
                    .ToList();
                if (matches.Count > 0)
                {
                    if (result.Count == 0) { reason = "Shared Metadata"; title = $"More {targetGenreFirst}"; }
                    result.AddRange(matches);
                    matches.ForEach(h => seen.Add(h.Id));
                }
            }

            if (result.Count == 0)
            {
                title = "Related media";
            }

            return Results.Ok(new RelatedCollectionsResponse
            {
                SectionTitle = title,
                Reason = reason,
                Collections = result,
            });
        })
        .WithName("GetRelatedCollections")
        .WithSummary("Related collections via cascade: series → author → genre → explore.")
        .Produces<RelatedCollectionsResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        // ── Group Detail ─────────────────────────────────────────────────────────

        // GET /collections/{collectionId}/group-detail — collection header + child works for sub-page rendering.
        group.MapGet("/{collectionId:guid}/group-detail", async (
            Guid collectionId,
            ICollectionRepository collectionRepo,
            ICanonicalValueRepository canonicalRepo,
            ICanonicalValueArrayRepository canonicalArrayRepo,
            IPersonRepository personRepo,
            IPersonCreditReadService personCreditReadService,
            [FromServices] AppleRetailClient appleRetailClient,
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            AlbumTrackManifestService manifestService,
            CancellationToken ct) =>
        {
            var collection = await collectionRepo.GetCollectionWithWorksAsync(collectionId, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{collectionId}' not found.");
            }

            var visibleProjection = await display.LoadWorksAsync(ct);
            var visibleWorkIds = visibleProjection.Select(work => work.WorkId).ToHashSet();
            var visibleWorks = collection.Works
                .Where(work => visibleWorkIds.Contains(work.Id))
                .ToList();
            if (visibleWorks.Count == 0)
            {
                return ApiErrors.NotFound($"Collection '{collectionId}' not found.");
            }

            // Determine primary media type from the works.
            var primaryMediaType = visibleWorks
                .GroupBy(w => w.MediaType.ToString())
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            bool isTv = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase);
            bool isMusic = string.Equals(primaryMediaType, "Music", StringComparison.OrdinalIgnoreCase);

            // Phase 4 — resolve the topmost Work id for the collection by walking the
            // parent_work_id chain from any of the collection's works (they all share
            // the same root parent in a ContentGroup collection). Parent-scope canonical
            // values (author, cover, genre, network, year) live on this row.
            Guid? rootParentWorkId = null;
            IReadOnlyList<CanonicalValue> parentCvs = [];
            if (visibleWorks.Count > 0)
            {
                var rid = await browseReadService.GetRootWorkIdAsync(visibleWorks[0].Id, ct);
                if (rid.HasValue)
                {
                    rootParentWorkId = rid.Value;
                    parentCvs = await canonicalRepo.GetByEntityAsync(rid.Value, ct);
                }
            }

            string? ParentCv(string key) =>
                parentCvs.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

            var rootWorkQid = collection.WikidataQid ?? ParentCv(BridgeIdKeys.WikidataQid);
            var primaryAssetIds = visibleProjection
                .Where(work => visibleWorkIds.Contains(work.WorkId))
                .GroupBy(work => work.WorkId)
                .ToDictionary(group => group.Key, group => (Guid?)group.First().AssetId);

            // Build per-work DTOs.
            var workDtos = visibleWorks
                .OrderBy(w => w.Ordinal ?? int.MaxValue)
                .ThenBy(w => w.Id)
                .Select(w =>
                {
                    var workDto = w.ToContract();
                    string? title = (isTv ? GetCanonical(workDto, "episode_title") : null)
                                         ?? GetCanonical(workDto, "title")
                                         ?? $"Work {w.Id.ToString("N")[..8]}";
                    string? year = GetCanonical(workDto, "release_year")
                                         ?? GetCanonical(workDto, "year");
                    string? duration = GetCanonical(workDto, "duration_seconds")
                                         ?? GetCanonical(workDto, "duration_sec")
                                         ?? GetCanonical(workDto, "duration")
                                         ?? GetCanonical(workDto, "runtime");
                    var durationSeconds = isMusic ? NormalizeAudioDurationSeconds(duration) : null;
                    var displayDuration = isMusic ? FormatAudioDuration(durationSeconds, duration) : duration;
                    var primaryAssetId = primaryAssetIds.GetValueOrDefault(w.Id);
                    string? coverUrl = BuildCoverStreamUrl(w, primaryAssetId);
                    string? backgroundUrl = BuildBackgroundStreamUrl(w, primaryAssetId);
                    string? bannerUrl = null;
                    string? season = GetCanonical(workDto, "season_number");
                    string? episode = GetCanonical(workDto, "episode_number");
                    string? trackNumber = GetCanonical(workDto, "track_number");
                    string? discNumber = GetCanonical(workDto, "disc_number");
                    string? appleMusicId = GetCanonical(workDto, BridgeIdKeys.AppleMusicId);
                    string? description = GetCanonical(workDto, "description");
                    string? director = GetCanonical(workDto, "director");
                    string? writer = GetCanonical(workDto, "writer");
                    string? releaseDate = NormalizeReleaseDate(
                        GetCanonical(workDto, "release_date")
                        ?? GetCanonical(workDto, "date")
                        ?? GetCanonical(workDto, "year"));

                    // Derive a display status from wikidata_status / match_level.
                    string status = w.WikidataStatus switch
                    {
                        WikidataLinkStatus.Confirmed => "Verified",
                        WikidataLinkStatus.Skipped => "Unlinked",
                        _ => "Provisional",
                    };

                    // Pipeline stage stubs — state is derived from match/wikidata status.
                    var stage1 = new LibraryPipelineStageDto
                    {
                        State = w.MatchLevel is WorkMatchLevel.RetailOnly
                            or WorkMatchLevel.Work
                            or WorkMatchLevel.Edition
                            ? "done"
                            : "pending",
                        Label = "Retail",
                    };
                    var stage2 = new LibraryPipelineStageDto
                    {
                        State = w.WikidataStatus == WikidataLinkStatus.Confirmed ? "done" : "pending",
                        Label = "Wikidata",
                    };
                    var stage3 = new LibraryPipelineStageDto
                    {
                        State = "pending",
                        Label = "Universe",
                    };

                    return new CollectionGroupWorkDto
                    {
                        WorkId = w.Id,
                        AssetId = primaryAssetIds.GetValueOrDefault(w.Id),
                        Title = title,
                        Ordinal = w.Ordinal,
                        Year = year,
                        Duration = displayDuration,
                        DurationSeconds = durationSeconds,
                        CoverUrl = coverUrl,
                        BackgroundUrl = backgroundUrl,
                        BannerUrl = bannerUrl,
                        HeroUrl = null,
                        WikidataQid = w.WikidataQid,
                        Season = season,
                        Episode = episode,
                        TrackNumber = trackNumber,
                        DiscNumber = ParseNullableInt(discNumber),
                        AppleMusicId = appleMusicId,
                        Status = status,
                        Description = description,
                        Director = director,
                        Writer = writer,
                        ReleaseDate = releaseDate,
                        PlaybackSummary = BuildPlaybackSummaryFromWork(workDto),
                        Stage1 = stage1,
                        Stage2 = stage2,
                        Stage3 = stage3,
                    };
                })
                .ToList();

            // Collection-level header canonical values come from the topmost Work row.
            // Phase 4 — parent-scoped fields (author, director, artist, genre, cover,
            // network) live on the root parent Work, not on individual child works.
            string? collectionCreator = ParentCv("author") ?? ParentCv("artist");
            string? collectionDirector = isTv ? null : ParentCv("director");
            string? collectionWriter = ParentCv("writer");
            string? collectionGenre = ParentCv("genre");
            string? collectionNetwork = isTv ? ParentCv("network") : null;
            string? collectionDescription = ParentCv("description");
            string? collectionTagline = ParentCv("tagline");
            string? collectionReleaseDate = NormalizeReleaseDate(
                ParentCv("release_date")
                ?? ParentCv("date")
                ?? ParentCv("year"));
            var paletteRow = rootParentWorkId.HasValue
                ? await browseReadService.GetAssetPaletteAsync(rootParentWorkId.Value, ct)
                : null;
            var collectionPalette = ResolvePalette(parentCvs, paletteRow);

            // Resolve cover URL as a /stream/ endpoint. Cover art is downloaded
            // to disk by CoverArtWorker and served via StreamEndpoints. We need
            // the root parent work's asset_id to build the URL.
            string? collectionCover = null;
            string? collectionBackground = null;
            string? collectionBanner = null;
            if (rootParentWorkId.HasValue)
            {
                if (await browseReadService.GetRepresentativeAssetIdAsync(rootParentWorkId.Value, ct) is { } rootAssetId)
                {
                    var rootAssetStr = rootAssetId.ToString("D");
                    collectionCover = $"/stream/{rootAssetStr}/cover";
                    collectionBackground = $"/stream/{rootAssetStr}/background";
                }
            }

            // Year range from all works.
            var years = workDtos
                .Where(w => !string.IsNullOrWhiteSpace(w.Year))
                .Select(w => w.Year!)
                .Distinct()
                .OrderBy(y => y)
                .ToList();
            string? yearRange = years.Count switch
            {
                0 => null,
                1 => years[0],
                _ => $"{years[0]}–{years[^1]}",
            };

            // Build the response — TV uses seasons grouping, Music uses album grouping, others use flat works list.
            List<CollectionGroupSeasonDto> seasons = [];
            List<CollectionGroupWorkDto> flatWorks = [];
            var collectionChildJson = ParentCv(MetadataFieldConstants.ChildEntitiesJson);

            if (isTv)
            {
                seasons = workDtos
                    .GroupBy(w => int.TryParse(w.Season, out var sn) ? sn : 0)
                    .OrderBy(g => g.Key)
                    .Select(g => new CollectionGroupSeasonDto
                    {
                        SeasonNumber = g.Key,
                        SeasonLabel = $"Season {g.Key}",
                        Episodes = g.OrderBy(e => int.TryParse(e.Episode, out var en) ? en : e.Ordinal ?? int.MaxValue).ToList(),
                    })
                    .ToList();
            }
            else if (isMusic)
            {
                collectionChildJson = await manifestService.EnsureAlbumTrackManifestAsync(
                    rootParentWorkId,
                    collectionCreator,
                    StringHelpers.FirstNonBlank(ParentCv(MetadataFieldConstants.Album), ParentCv(MetadataFieldConstants.Title), collection.DisplayName),
                    collectionChildJson,
                    parentCvs,
                    ct);

                // Music: tracks are already within one album collection, show as flat list with track ordering
                var ownedTracks = workDtos
                    .OrderBy(w => int.TryParse(w.TrackNumber, out var tn) ? tn : w.Ordinal ?? int.MaxValue)
                    .ToList();
                flatWorks = AlbumTrackManifestService.MergeUnownedMusicTracks(ownedTracks, collectionChildJson, collectionCover);
            }
            else
            {
                flatWorks = workDtos;
            }

            // Top billed cast for TV and Movies — read the Parent-scoped
            // cast_member array (P161) and resolve each entry to a Person
            // record so the Dashboard can open the people drawer on click.
            // Capped at 10 entries to match the design.
            var topCast = new List<CastCreditDto>();
            bool hasCast = (isTv || string.Equals(primaryMediaType, "Movies", StringComparison.OrdinalIgnoreCase))
                           && rootParentWorkId.HasValue;
            if (hasCast)
            {
                topCast = await personCreditReadService.BuildForCollectionRootAsync(
                    rootParentWorkId!.Value,
                    rootWorkQid,
                    ct);
            }

            var response = new CollectionGroupDetailDto
            {
                CollectionId = collection.Id,
                DisplayName = collection.DisplayName ?? $"Collection {collection.Id.ToString("N")[..8]}",
                RootWorkId = rootParentWorkId,
                WikidataQid = rootWorkQid,
                PrimaryMediaType = primaryMediaType,
                CoverUrl = collectionCover,
                BackgroundUrl = collectionBackground,
                BannerUrl = collectionBanner,
                DominantColors = collectionPalette.DominantColors,
                PrimaryColor = collectionPalette.PrimaryColor,
                SecondaryColor = collectionPalette.SecondaryColor,
                AccentColor = collectionPalette.AccentColor,
                Description = collectionDescription,
                Tagline = collectionTagline,
                Creator = collectionCreator,
                Director = collectionDirector,
                Writer = collectionWriter,
                ReleaseDate = collectionReleaseDate,
                YearRange = yearRange,
                Genre = collectionGenre,
                Network = collectionNetwork,
                SeasonCount = isTv ? seasons.Count : null,
                TopCast = topCast,
                TotalItems = isMusic ? flatWorks.Count : collection.Works.Count,
                Seasons = seasons,
                Works = flatWorks,
            };

            return Results.Ok(response);
        })
        .WithName("GetCollectionGroupDetail")
        .WithSummary("Returns collection header metadata and child works sorted by sequence for sub-page rendering. TV works are grouped by season.")
        .Produces<CollectionGroupDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "collectionId");

        // GET /collections/artist-group-detail?collection_ids=id1,id2,... — combined multi-collection detail for artist-level drill-down.
        group.MapGet("/artist-group-detail", async (
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "collection_ids")] string collectionIdsParam,
            ICollectionRepository collectionRepo,
            IPersonRepository personRepo,
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            AlbumTrackManifestService manifestService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(collectionIdsParam))
            {
                return ApiErrors.BadRequest("collection_ids parameter is required");
            }

            var collectionIds = collectionIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();

            if (collectionIds.Count == 0)
            {
                return ApiErrors.BadRequest("No valid collection IDs provided");
            }

            // Load all collections and build album-based seasons
            var allSeasons = new List<CollectionGroupSeasonDto>();
            string? combinedCreator = null;
            string? combinedGenre = null;
            int totalItems = 0;
            var allYears = new List<string>();

            int albumIndex = 0;
            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var collectionsById = (await collectionRepo.GetCollectionsWithWorksAsync(collectionIds, ct))
                .ToDictionary(collection => collection.Id);
            foreach (var collectionId in collectionIds)
            {
                if (!collectionsById.TryGetValue(collectionId, out var collection))
                {
                    continue;
                }
                var visibleWorks = collection.Works
                    .Where(work => visibleWorkIds.Contains(work.Id))
                    .ToList();
                if (visibleWorks.Count == 0)
                {
                    continue;
                }

                // Build owned track DTOs from collection.Works.
                var primaryAssetIds = await browseReadService.GetPrimaryAssetIdsAsync(visibleWorks.Select(w => w.Id), ct);
                var ownedTracks = visibleWorks
                    .OrderBy(w => w.Ordinal ?? int.MaxValue)
                    .ThenBy(w => w.Id)
                    .Select(w =>
                    {
                        var wDto = w.ToContract();
                        var duration = GetCanonical(wDto, "duration_seconds")
                            ?? GetCanonical(wDto, "duration_sec")
                            ?? GetCanonical(wDto, "duration")
                            ?? GetCanonical(wDto, "runtime");
                        var durationSeconds = NormalizeAudioDurationSeconds(duration);
                        return new CollectionGroupWorkDto
                        {
                            WorkId = w.Id,
                            AssetId = primaryAssetIds.GetValueOrDefault(w.Id),
                            Title = GetCanonical(wDto, "title") ?? $"Track {w.Id.ToString("N")[..8]}",
                            Ordinal = w.Ordinal,
                            Year = GetCanonical(wDto, "release_year") ?? GetCanonical(wDto, "year"),
                            Duration = FormatAudioDuration(durationSeconds, duration),
                            DurationSeconds = durationSeconds,
                            CoverUrl = BuildCoverStreamUrl(w, primaryAssetIds.GetValueOrDefault(w.Id)),
                            WikidataQid = w.WikidataQid,
                            TrackNumber = GetCanonical(wDto, "track_number"),
                            DiscNumber = ParseNullableInt(GetCanonical(wDto, "disc_number")),
                            AppleMusicId = GetCanonical(wDto, BridgeIdKeys.AppleMusicId),
                            Status = w.WikidataStatus switch
                            {
                                WikidataLinkStatus.Confirmed => "Verified",
                                WikidataLinkStatus.Skipped => "Unlinked",
                                _ => "Provisional",
                            },
                            IsOwned = true,
                        };
                    })
                    .ToList();

                // Per-album cover, year, and child_entities_json from this collection's first work.
                string? albumCover = null;
                string? albumYear = null;
                string? childJson = null;
                if (visibleWorks.Count > 0)
                {
                    var firstWorkDto = visibleWorks[0].ToContract();
                    combinedCreator ??= GetCanonical(firstWorkDto, "artist")
                                       ?? GetCanonical(firstWorkDto, "author");
                    combinedGenre ??= GetCanonical(firstWorkDto, "genre");
                    albumCover = BuildCoverStreamUrl(
                        visibleWorks[0],
                        primaryAssetIds.GetValueOrDefault(visibleWorks[0].Id));
                    albumYear = GetCanonical(firstWorkDto, "release_year") ?? GetCanonical(firstWorkDto, "year");

                    // child_entities_json may be on any track in the album (album-level claim attached
                    // to whichever track was being processed when Stage 2 ran). Try each in order.
                    foreach (var w in visibleWorks)
                    {
                        var dto = w.ToContract();
                        childJson = GetCanonical(dto, MetadataFieldConstants.ChildEntitiesJson);
                        if (!string.IsNullOrWhiteSpace(childJson))
                        {
                            break;
                        }
                    }
                }

                // Merge unowned tracks from child_entities_json.
                var mergedTracks = AlbumTrackManifestService.MergeUnownedMusicTracks(ownedTracks, childJson, albumCover);

                if (mergedTracks.Any(t => t.IsOwned && !string.IsNullOrWhiteSpace(t.Year)))
                {
                    allYears.AddRange(mergedTracks.Where(t => t.IsOwned && !string.IsNullOrWhiteSpace(t.Year)).Select(t => t.Year!));
                }

                allSeasons.Add(new CollectionGroupSeasonDto
                {
                    SeasonNumber = albumIndex,
                    SeasonLabel = collection.DisplayName ?? $"Album {albumIndex + 1}",
                    CoverUrl = albumCover,
                    AlbumCollectionId = collection.Id,
                    Year = albumYear,
                    Episodes = mergedTracks,
                });

                totalItems += mergedTracks.Count(t => t.IsOwned);
                albumIndex++;
            }

            var years = allYears.Distinct().OrderBy(y => y).ToList();
            string? yearRange = years.Count switch
            {
                0 => null,
                1 => years[0],
                _ => $"{years[0]}–{years[^1]}",
            };

            // Resolve artist photo via the persons table.
            string? artistPhotoUrl = null;
            Guid? artistPersonId = null;
            if (!string.IsNullOrWhiteSpace(combinedCreator))
            {
                try
                {
                    var person = await personRepo.FindByNameAsync(combinedCreator, ct);
                    if (person is not null)
                    {
                        artistPersonId = person.Id;
                        if (!string.IsNullOrEmpty(person.LocalHeadshotPath) || !string.IsNullOrEmpty(person.HeadshotUrl))
                        {
                            artistPhotoUrl = $"/persons/{person.Id}/headshot";
                        }
                    }
                }
                catch { /* best-effort lookup */ }
            }

            var response = new CollectionGroupDetailDto
            {
                CollectionId = collectionIds[0],
                DisplayName = combinedCreator ?? "Unknown Artist",
                PrimaryMediaType = "Music",
                CoverUrl = null, // artist view header uses ArtistPhotoUrl, not an album cover
                Creator = combinedCreator,
                YearRange = yearRange,
                Genre = combinedGenre,
                TotalItems = totalItems,
                Seasons = allSeasons,
                Works = [],
                ArtistPhotoUrl = artistPhotoUrl,
                ArtistPersonId = artistPersonId,
            };

            return Results.Ok(response);
        })
        .WithName("GetArtistGroupDetail")
        .WithSummary("Returns combined multi-collection detail for artist-level drill-down in the Music tab. Each collection becomes an album 'season'.")
        .Produces<CollectionGroupDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/artist-detail-by-name?artistName=X — Artist drill-down for system-view mode.
        // Queries works directly from canonical_values, grouped by album, returning the same CollectionGroupDetailDto shape.
        group.MapGet("/artist-detail-by-name", async (
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "artistName")] string? artistName,
            ICollectionBrowseReadService browseReadService,
            IPersonRepository personRepo,
            IDisplayProjectionReadService display,
            AlbumTrackManifestService manifestService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return ApiErrors.BadRequest("artistName parameter is required");
            }

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var rows = (await browseReadService.GetArtistWorksAsync(artistName, ct))
                .Where(row => visibleWorkIds.Contains(row.WorkId))
                .ToList();
            var albumMap = new Dictionary<string, List<CollectionGroupWorkDto>>(StringComparer.OrdinalIgnoreCase);
            var albumCovers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var albumYears = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var albumChildJson = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? combinedCreator = null;
            string? combinedGenre = null;
            var allYears = new List<string>();

            foreach (var row in rows)
            {
                var rawDuration = row.DurationSecondsValue ?? row.Duration ?? row.Runtime;
                var durationSeconds = NormalizeAudioDurationSeconds(rawDuration);
                var displayDuration = FormatAudioDuration(durationSeconds, rawDuration);

                combinedCreator ??= row.Artist;
                combinedGenre ??= row.Genre;

                var year = row.ReleaseYear ?? row.YearValue;
                if (!string.IsNullOrWhiteSpace(year))
                {
                    allYears.Add(year);
                }

                var albumKey = row.Album ?? "Unknown Album";
                if (!albumMap.TryGetValue(albumKey, out var tracks))
                {
                    tracks = [];
                    albumMap[albumKey] = tracks;
                }
                if (!albumCovers.ContainsKey(albumKey))
                {
                    albumCovers[albumKey] = row.Cover;
                }

                if (!albumYears.ContainsKey(albumKey) || string.IsNullOrWhiteSpace(albumYears[albumKey]))
                {
                    albumYears[albumKey] = year;
                }

                if (!albumChildJson.ContainsKey(albumKey) || string.IsNullOrWhiteSpace(albumChildJson[albumKey]))
                {
                    albumChildJson[albumKey] = row.ChildEntitiesJson;
                }

                tracks.Add(new CollectionGroupWorkDto
                {
                    WorkId = row.WorkId,
                    AssetId = row.AssetId,
                    Title = row.Title ?? $"Track {row.WorkId.ToString("N")[..8]}",
                    Year = year,
                    Duration = displayDuration,
                    DurationSeconds = durationSeconds,
                    CoverUrl = row.Cover,
                    TrackNumber = row.TrackNumber,
                    DiscNumber = ParseNullableInt(row.DiscNumber),
                    AppleMusicId = row.AppleMusicId,
                    Status = "Provisional",
                    IsOwned = true,
                });
            }

            var years = allYears.Distinct().OrderBy(y => y).ToList();
            string? yearRange = years.Count switch
            {
                0 => null,
                1 => years[0],
                _ => $"{years[0]}–{years[^1]}",
            };

            int totalItems = 0;
            var seasons = albumMap.Select((kvp, idx) =>
            {
                var albumKey = kvp.Key;
                var albumCover = albumCovers.TryGetValue(albumKey, out var c) ? c : null;
                var albumYear = albumYears.TryGetValue(albumKey, out var y) ? y : null;
                var childJson = albumChildJson.TryGetValue(albumKey, out var j) ? j : null;
                var merged = AlbumTrackManifestService.MergeUnownedMusicTracks(kvp.Value, childJson, albumCover);
                totalItems += merged.Count(t => t.IsOwned);
                return new CollectionGroupSeasonDto
                {
                    SeasonNumber = idx,
                    SeasonLabel = albumKey,
                    CoverUrl = albumCover,
                    Year = albumYear,
                    AlbumCollectionId = null, // by-name lookup has no concrete collection id
                    Episodes = merged,
                };
            }).ToList();

            // Resolve artist photo via the persons table.
            string? artistPhotoUrl = null;
            Guid? artistPersonId = null;
            if (!string.IsNullOrWhiteSpace(combinedCreator))
            {
                try
                {
                    var person = await personRepo.FindByNameAsync(combinedCreator, ct);
                    if (person is not null)
                    {
                        artistPersonId = person.Id;
                        if (!string.IsNullOrEmpty(person.LocalHeadshotPath) || !string.IsNullOrEmpty(person.HeadshotUrl))
                        {
                            artistPhotoUrl = $"/persons/{person.Id}/headshot";
                        }
                    }
                }
                catch { /* best-effort lookup */ }
            }

            var response = new CollectionGroupDetailDto
            {
                CollectionId = Guid.Empty,
                DisplayName = artistName,
                PrimaryMediaType = "Music",
                CoverUrl = null,
                Creator = combinedCreator,
                YearRange = yearRange,
                Genre = combinedGenre,
                TotalItems = totalItems,
                Seasons = seasons,
                Works = [],
                ArtistPhotoUrl = artistPhotoUrl,
                ArtistPersonId = artistPersonId,
            };

            return Results.Ok(response);
        })
        .WithName("GetArtistDetailByName")
        .WithSummary("Returns artist drill-down detail by artist name, querying directly from canonical values. Used when system-view collections are active and ContentGroup collections are unavailable.")
        .Produces<CollectionGroupDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/system-view-detail?groupField=album&groupValue=The%20Record&mediaType=Music
        // Generic grouped detail endpoint for non-routed system views such as music albums/artists.
        // TV shows use /details/tvshow/{id} and the unified detail composer instead of this endpoint.
        group.MapGet("/system-view-detail", async (
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "groupField")] string? groupField,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "groupValue")] string? groupValue,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "mediaType")] string? mediaType,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "artistName")] string? artistName,
            ICanonicalValueRepository canonicalRepo,
            [FromServices] AppleRetailClient appleRetailClient,
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            AlbumTrackManifestService manifestService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(groupField) || string.IsNullOrWhiteSpace(groupValue))
            {
                return ApiErrors.BadRequest("groupField and groupValue parameters are required");
            }

            // Determine the secondary grouping field and sort fields based on the primary group
            var (secondaryGroup, sortFields) = groupField.ToLowerInvariant() switch
            {
                "show_name" => ("season_number", "season_number, episode_number, title"),
                "artist" => ("album", "album, CAST(track_number AS INTEGER), title"),
                "album" => ((string?)null, "CAST(track_number AS INTEGER), title"),
                "series" => ((string?)null, "CAST(series_index AS INTEGER), title"),
                _ => ((string?)null, "title"),
            };

            // Label for secondary groups
            var secondaryLabelPrefix = groupField.ToLowerInvariant() switch
            {
                "show_name" => "Season ",
                "artist" => (string?)null, // use album name directly
                _ => null,
            };

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var rows = (await browseReadService.GetSystemViewDetailWorksAsync(
                groupField,
                groupValue,
                mediaType,
                artistName,
                ct))
                .Where(row => visibleWorkIds.Contains(row.WorkId))
                .ToList();
            // sectionKey → owned CollectionGroupWorkDtos. Unowned items are merged after
            // the reader loop using child_entities_json from the parent.
            var sectionMap = new Dictionary<string, List<CollectionGroupWorkDto>>(StringComparer.OrdinalIgnoreCase);
            string? combinedCreator = null;
            string? combinedCover = null;
            string? combinedBackground = null;
            string? combinedBanner = null;
            string? combinedHero = null;
            string? combinedLogo = null;
            string? combinedPrimaryColor = null;
            string? combinedSecondaryColor = null;
            string? combinedAccentColor = null;
            string? combinedGenre = null;
            string? combinedNetwork = null;
            Guid? combinedRootWorkId = null;
            var allYears = new List<string>();
            int totalItems = 0;
            // Collect child_entities_json from any owned work that carries it.
            string? collectedChildJson = null;
            var isMusicAlbumGroup = string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
                && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var workId = row.WorkId;
                var assetId = row.AssetId;
                var rootWorkId = row.RootWorkId;
                var title = row.Title;
                var episodeTitle = row.EpisodeTitle;
                var cover = string.IsNullOrWhiteSpace(row.Cover) && assetId.HasValue
                    ? $"/stream/{assetId.Value:D}/cover"
                    : row.Cover;
                var background = row.Background;
                string? banner = null;
                var hero = row.Hero;
                var logo = row.Logo;
                var primaryColor = row.PrimaryColor;
                var secondaryColor = row.SecondaryColor;
                var accentColor = row.AccentColor;
                var genre = row.Genre;
                var durationSecondsValue = row.DurationSecondsValue;
                var duration = row.Duration;
                var runtime = row.Runtime;
                var rawDuration = durationSecondsValue ?? duration ?? runtime;
                var durationSeconds = isMusicAlbumGroup ? NormalizeAudioDurationSeconds(rawDuration) : null;
                var displayDuration = isMusicAlbumGroup ? FormatAudioDuration(durationSeconds, rawDuration) : rawDuration;
                var releaseYear = row.ReleaseYear;
                var yearVal = row.YearValue;
                var episodeNum = row.EpisodeNumber;
                var trackNum = row.TrackNumber;
                var discNum = row.DiscNumber;
                var appleMusicId = row.AppleMusicId;
                var seqIndex = row.SeriesIndex;
                var childJson = row.ChildEntitiesJson;

                // Accumulate the first non-null child_entities_json we encounter —
                // it may appear on any owned sibling in the same group.
                collectedChildJson ??= string.IsNullOrWhiteSpace(childJson) ? null : childJson;

                // Determine creator (author, director, artist, or network for TV)
                var creator = row.Author;
                var directorVal = row.Director;
                var artistVal = row.Artist;
                var networkVal = row.Network;
                // For TV, prefer network over director as the header creator
                if (mediaType == "TV")
                {
                    creator ??= networkVal ?? directorVal ?? artistVal;
                }
                else
                {
                    creator ??= directorVal ?? artistVal;
                }

                combinedCreator ??= creator;
                combinedCover ??= cover;
                combinedBackground ??= background;
                combinedBanner ??= banner;
                combinedHero ??= hero;
                combinedLogo ??= logo;
                combinedPrimaryColor ??= primaryColor;
                combinedSecondaryColor ??= secondaryColor;
                combinedAccentColor ??= accentColor;
                combinedGenre ??= genre;
                combinedRootWorkId ??= rootWorkId;

                combinedNetwork ??= networkVal;

                var year = releaseYear ?? yearVal;
                if (!string.IsNullOrWhiteSpace(year))
                {
                    allYears.Add(year);
                }

                // Build group key for sections
                string sectionKey;
                if (secondaryGroup is not null)
                {
                    var secVal = secondaryGroup switch
                    {
                        "season_number" => row.SeasonNumber,
                        "album" => row.Album,
                        _ => null,
                    };
                    sectionKey = secVal ?? "Unknown";
                }
                else
                {
                    sectionKey = "_flat";
                }

                if (!sectionMap.TryGetValue(sectionKey, out var items))
                {
                    items = [];
                    sectionMap[sectionKey] = items;
                }

                items.Add(new CollectionGroupWorkDto
                {
                    WorkId = workId,
                    AssetId = assetId,
                    Title = episodeTitle ?? title ?? $"Item {workId.ToString("N")[..8]}",
                    Year = year,
                    Duration = displayDuration,
                    DurationSeconds = durationSeconds,
                    CoverUrl = cover,
                    BackgroundUrl = background,
                    BannerUrl = banner,
                    HeroUrl = hero,
                    Episode = episodeNum,
                    TrackNumber = trackNum,
                    DiscNumber = ParseNullableInt(discNum),
                    AppleMusicId = appleMusicId,
                    Ordinal = int.TryParse(seqIndex, out var si) ? si : null,
                    Status = "Provisional",
                    IsOwned = true,
                });

                totalItems++;
            }

            // M-083: Merge unowned items from child_entities_json.
            // For TV shows the JSON has an "episodes" array grouped by season;
            // for music it has "tracks"; for comics "issues". We use the same
            // child-entity parsing used by MergeUnownedMusicTracks.
            IReadOnlyList<CanonicalValue> rootCanonicals = [];
            if (isMusicAlbumGroup && combinedRootWorkId.HasValue)
            {
                rootCanonicals = await canonicalRepo.GetByEntityAsync(combinedRootWorkId.Value, ct);
                collectedChildJson ??= FirstCanonicalValue(rootCanonicals, MetadataFieldConstants.ChildEntitiesJson);
                collectedChildJson = await manifestService.EnsureAlbumTrackManifestAsync(
                    combinedRootWorkId,
                    combinedCreator,
                    groupValue,
                    collectedChildJson,
                    rootCanonicals,
                    ct);
            }

            if (!string.IsNullOrWhiteSpace(collectedChildJson))
            {
                manifestService.MergeUnownedChildEntities(
                    sectionMap,
                    collectedChildJson,
                    groupField,
                    secondaryGroup,
                    combinedCover);
            }

            var paletteRow = combinedRootWorkId.HasValue
                ? await browseReadService.GetAssetPaletteAsync(combinedRootWorkId.Value, ct)
                : null;
            var palette = ResolvePalette(rootCanonicals, paletteRow);
            combinedPrimaryColor ??= palette.PrimaryColor;
            combinedSecondaryColor ??= palette.SecondaryColor;
            combinedAccentColor ??= palette.AccentColor;
            var rootWikidataQid = FirstCanonicalValue(rootCanonicals, BridgeIdKeys.WikidataQid);
            var rootDescription = FirstCanonicalValue(rootCanonicals, MetadataFieldConstants.Description);
            var rootTagline = FirstCanonicalValue(rootCanonicals, MetadataFieldConstants.Tagline);
            var rootReleaseDate = NormalizeReleaseDate(
                FirstCanonicalValue(rootCanonicals, "release_date", "date", "year"));

            var years = allYears.Distinct().OrderBy(y => y).ToList();
            string? yearRange = years.Count switch
            {
                0 => null,
                1 => years[0],
                _ => $"{years[0]}–{years[^1]}",
            };

            // Build seasons/sections if we have a secondary group
            List<CollectionGroupSeasonDto> seasons;
            List<CollectionGroupWorkDto> flatWorks;

            // Recalculate totalItems to include unowned rows added during merge.
            totalItems = sectionMap.Values.Sum(v => v.Count);

            if (secondaryGroup is not null && sectionMap.Count > 0 && !sectionMap.ContainsKey("_flat"))
            {
                seasons = sectionMap
                    .OrderBy(kvp => int.TryParse(kvp.Key, out var n) ? n : int.MaxValue)
                    .ThenBy(kvp => kvp.Key)
                    .Select((kvp, idx) => new CollectionGroupSeasonDto
                    {
                        SeasonNumber = int.TryParse(kvp.Key, out var sn) ? sn : idx,
                        SeasonLabel = secondaryLabelPrefix is not null
                            ? $"{secondaryLabelPrefix}{kvp.Key}"
                            : kvp.Key,
                        Episodes = kvp.Value,
                    })
                    .ToList();
                flatWorks = [];
            }
            else
            {
                seasons = [];
                flatWorks = sectionMap.Values.SelectMany(v => v).ToList();
                if (isMusicAlbumGroup)
                {
                    flatWorks = flatWorks
                        .OrderBy(item => int.TryParse(item.TrackNumber, out var trackNumber) ? trackNumber : item.Ordinal ?? int.MaxValue)
                        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            var response = new CollectionGroupDetailDto
            {
                CollectionId = Guid.Empty,
                DisplayName = groupValue,
                RootWorkId = combinedRootWorkId,
                WikidataQid = rootWikidataQid,
                PrimaryMediaType = mediaType ?? "Unknown",
                CoverUrl = combinedCover,
                BackgroundUrl = combinedBackground,
                BannerUrl = combinedBanner,
                HeroUrl = combinedHero,
                LogoUrl = combinedLogo,
                DominantColors = palette.DominantColors,
                PrimaryColor = combinedPrimaryColor,
                SecondaryColor = combinedSecondaryColor,
                AccentColor = combinedAccentColor,
                Description = rootDescription,
                Tagline = rootTagline,
                Creator = combinedCreator,
                ReleaseDate = rootReleaseDate,
                YearRange = yearRange,
                Genre = combinedGenre,
                Network = combinedNetwork,
                TotalItems = totalItems,
                Seasons = seasons,
                Works = flatWorks,
            };

            return Results.Ok(response);
        })
        .WithName("GetSystemViewGroupDetail")
        .WithSummary("Generic system-view drill-down. Returns works grouped by a secondary field for any group field (show_name, series, album, artist).")
        .Produces<CollectionGroupDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // ── Content Groups ───────────────────────────────────────────────────────

        // GET /collections/content-groups — structural collections that have child works (albums, TV series, book series, movie series).
        group.MapGet("/content-groups", async (
            ICollectionRepository collectionRepo,
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collections = await collectionRepo.GetContentGroupsAsync(ct);
            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var primaryAssetIds = await browseReadService.GetPrimaryAssetIdsAsync(
                collections.SelectMany(collection => collection.Works)
                    .Where(work => visibleWorkIds.Contains(work.Id))
                    .Select(work => work.Id),
                ct);

            var dtos = collections.Select(h =>
            {
                var visibleWorks = h.Works.Where(work => visibleWorkIds.Contains(work.Id)).ToList();
                // Mixed-format groups belong to cross-media Collections rather than
                // being presented as a lane-local shelf based on a simple majority.
                var mediaTypeGroups = visibleWorks
                    .GroupBy(w => w.MediaType.ToString())
                    .OrderByDescending(g => g.Count())
                    .ToList();
                var primaryMediaType = mediaTypeGroups.Count > 1
                    ? "CrossMedia"
                    : mediaTypeGroups.FirstOrDefault()?.Key ?? "Unknown";

                // Cover from the first work that has a media asset.
                string? cover = null;
                string? background = null;
                string? banner = null;
                string? logo = null;
                foreach (var w in visibleWorks)
                {
                    var primaryAssetId = primaryAssetIds.GetValueOrDefault(w.Id);
                    cover = BuildCoverStreamUrl(w, primaryAssetId);
                    background = BuildBackgroundStreamUrl(w, primaryAssetId);
                    logo = BuildLogoStreamUrl(w, primaryAssetId);
                    if (cover is not null || background is not null || banner is not null || logo is not null)
                    {
                        break;
                    }
                }

                // Creator from first work.
                var firstDto = visibleWorks.Count > 0 ? visibleWorks[0].ToContract() : null;
                string? creator = GetCanonical(firstDto, "author")
                                  ?? GetCanonical(firstDto, "artist");
                string? releaseDate = NormalizeReleaseDate(
                    GetCanonical(firstDto, "release_date")
                    ?? GetCanonical(firstDto, "date")
                    ?? GetCanonical(firstDto, "year"));
                var previewItems = visibleWorks
                    .Select(work =>
                    {
                        var dto = work.ToContract();
                        var primaryAssetId = primaryAssetIds.GetValueOrDefault(work.Id);
                        var coverUrl = BuildCoverStreamUrl(work, primaryAssetId);
                        var backgroundUrl = BuildBackgroundStreamUrl(work, primaryAssetId);
                        string? bannerUrl = null;
                        var imageUrl = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
                            ? backgroundUrl ?? bannerUrl ?? coverUrl
                            : coverUrl ?? backgroundUrl ?? bannerUrl;
                        var title = GetCanonical(dto, "title") ?? h.DisplayName ?? "Untitled";
                        var description = GetCanonical(dto, "short_description")
                            ?? GetCanonical(dto, "description");
                        var mediaKind = DisplayMediaRules.NormalizeDisplayKind(primaryMediaType);
                        return new
                        {
                            Work = work,
                            ImageUrl = imageUrl,
                            Shape = ResolveContentGroupPreviewShape(
                                primaryMediaType,
                                imageUrl,
                                coverUrl,
                                backgroundUrl,
                                bannerUrl,
                                ParseNullableInt(GetCanonical(dto, "cover_width_px")),
                                ParseNullableInt(GetCanonical(dto, "cover_height_px"))),
                            Title = title,
                            Description = description,
                            Facts = DisplayFactBuilder.Build(
                                mediaKind,
                                title,
                                year: GetCanonical(dto, "release_year") ?? GetCanonical(dto, "year"),
                                author: GetCanonical(dto, "author"),
                                artist: GetCanonical(dto, "artist"),
                                contentRating: GetCanonical(dto, "content_rating") ?? GetCanonical(dto, "certification"),
                                runtime: GetCanonical(dto, "runtime"),
                                duration: GetCanonical(dto, "duration") ?? GetCanonical(dto, "duration_seconds"),
                                pageCount: GetCanonical(dto, "page_count"),
                                starRating: GetCanonical(dto, "rating") ?? GetCanonical(dto, "star_rating")),
                            Position = GetCanonical(dto, "series_position")
                                ?? GetCanonical(dto, "episode_number")
                                ?? work.Ordinal?.ToString(CultureInfo.InvariantCulture),
                        };
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
                    .OrderBy(item => item.Work.Ordinal is null)
                    .ThenBy(item => item.Work.Ordinal)
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(item => new ContentGroupPreviewItemDto(
                        item.Work.Id,
                        item.Title,
                        item.ImageUrl!,
                        item.Shape,
                        item.Position,
                        item.Description,
                        item.Facts))
                    .ToList();
                var contentYears = visibleWorks
                    .Select(work => ParseDisplayYear(
                        GetCanonical(work.ToContract(), "release_year")
                        ?? GetCanonical(work.ToContract(), "year")))
                    .Where(year => year.HasValue)
                    .Select(year => year!.Value)
                    .ToList();

                return new ContentGroupDto
                {
                    CollectionId = h.Id,
                    DisplayName = h.DisplayName ?? $"Collection {h.Id.ToString("N")[..8]}",
                    WikidataQid = h.WikidataQid,
                    PrimaryMediaType = primaryMediaType,
                    WorkCount = visibleWorks.Count,
                    DistinctTitleCount = CountDistinctWorkTitles(visibleWorks),
                    PreviewItems = previewItems,
                    CoverUrl = cover,
                    BackgroundUrl = background,
                    BannerUrl = banner,
                    HeroUrl = null,
                    LogoUrl = logo,
                    CoverAspectClass = GetCanonical(firstDto, "cover_aspect_class"),
                    SquareAspectClass = GetCanonical(firstDto, "square_aspect_class"),
                    BackgroundAspectClass = GetCanonical(firstDto, "background_aspect_class"),
                    BannerAspectClass = GetCanonical(firstDto, "banner_aspect_class"),
                    CoverWidthPx = ParseNullableInt(GetCanonical(firstDto, "cover_width_px")),
                    CoverHeightPx = ParseNullableInt(GetCanonical(firstDto, "cover_height_px")),
                    SquareWidthPx = ParseNullableInt(GetCanonical(firstDto, "square_width_px")),
                    SquareHeightPx = ParseNullableInt(GetCanonical(firstDto, "square_height_px")),
                    BackgroundWidthPx = ParseNullableInt(GetCanonical(firstDto, "background_width_px")),
                    BackgroundHeightPx = ParseNullableInt(GetCanonical(firstDto, "background_height_px")),
                    BannerWidthPx = ParseNullableInt(GetCanonical(firstDto, "banner_width_px")),
                    BannerHeightPx = ParseNullableInt(GetCanonical(firstDto, "banner_height_px")),
                    Description = h.Description ?? GetCanonical(firstDto, "description"),
                    Tagline = GetCanonical(firstDto, "tagline"),
                    Creator = creator,
                    Director = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : GetCanonical(firstDto, "director"),
                    Writer = GetCanonical(firstDto, "writer"),
                    ReleaseDate = releaseDate,
                    UniverseStatus = h.UniverseStatus.ToStorageValue(),
                    CreatedAt = h.CreatedAt,
                    Network = GetCanonical(firstDto, "network"),
                    Year = GetCanonical(firstDto, "release_year") ?? GetCanonical(firstDto, "year"),
                    EarliestYear = contentYears.Count > 0 ? contentYears.Min() : null,
                    LatestYear = contentYears.Count > 0 ? contentYears.Max() : null,
                    SeasonCount = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
                        ? h.Works
                            .Select(work => GetCanonical(work.ToContract(), "season_number"))
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count()
                        : null,
                };
            })
            .Where(group => group.WorkCount > 0)
            .OrderBy(d => d.DisplayName)
            .ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetContentGroups")
        .WithSummary("Returns structural collections that contain works (albums, TV series, book series, movie series), grouped by primary media type.")
        .Produces<List<ContentGroupDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/system-views?mediaType=&groupField= — System view collections resolved as content groups.
        // Used by library container views (By Show, By Artist, By Album) that are driven by System collections
        // rather than ContentGroup collections.
        group.MapGet("/system-views", async (
            string? mediaType,
            string? groupField,
            ICollectionBrowseReadService browseReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var result = await browseReadService.GetSystemViewGroupsAsync(
                mediaType, groupField, ct, visibleWorkIds);
            var normalizedGroups = NormalizeSystemViewGroups(result, mediaType, groupField);
            return Results.Ok(normalizedGroups
                .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList());
        })
        .WithName("GetSystemViewGroups")
        .WithSummary("Resolves built-in browse views (By Show, By Artist, By Album) as dynamic content groups for the library container views.")
        .Produces<List<ContentGroupDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // ── Managed Collection endpoints (managed collections surface) ──────────────────────────────

        // GET /collections/managed — all non-Universe collections for the managed collections surface.
        group.MapGet("/managed", async (
            Guid? profileId,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            return Results.Ok(await catalogReadService.GetManagedAsync(activeProfile, ct, allowedWorkIds));
        })
        .WithName("GetManagedCollections")
        .WithSummary("List authored collections accessible to the active profile.")
        .Produces<List<ManagedCollectionDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/catalog — classified collection catalog for the Collections hub.
        group.MapGet("/catalog", async (
            Guid? profileId,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            var catalog = await catalogReadService.GetCatalogAsync(activeProfile, ct, allowedWorkIds);
            return Results.Ok(catalog);
        })
        .WithName("GetCollectionCatalog")
        .WithSummary("Returns all collections visible to the active profile with server-side family and lane classification for the Collections hub.")
        .Produces<List<CollectionManagementCatalogDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/managed/counts — type → count for stats bar.
        group.MapPost("/reconcile", async (
            MediaEngine.Contracts.Collections.CollectionBackfillRequest? body,
            CollectionBackfillService backfillService,
            CancellationToken ct) =>
        {
            body ??= new MediaEngine.Contracts.Collections.CollectionBackfillRequest();
            var result = await backfillService.RunAsync(
                new MediaEngine.Api.Services.CollectionBackfillRequest(body.DryRun, body.BatchSize, body.MaxItems),
                ct);
            return Results.Ok(new CollectionBackfillResponse(
                result.CandidateCount,
                result.ProcessedCount,
                result.AssignedCount,
                result.CreatedCollectionCount,
                result.AlreadyAssignedCount,
                result.SkippedCount,
                result.FailedCount,
                result.ElapsedMs));
        })
        .WithName("ReconcileCollections")
        .WithSummary("Repairs missing collection shelf assignments for already-ingested media.")
        .Produces<CollectionBackfillResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        group.MapGet("/managed/counts", async (
            Guid? profileId,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            var counts = (await catalogReadService.GetManagedAsync(activeProfile, ct, allowedWorkIds))
                .GroupBy(collection => collection.CollectionType)
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Count());
            return Results.Ok(counts);
        })
        .WithName("GetManagedCollectionCounts")
        .WithSummary("Returns authored collection count grouped by type for the active profile.")
        .Produces<Dictionary<string, int>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/{id}/items?limit=20 — curated item preview.
        group.MapGet("/media-lookup", async (
            string? q,
            string? mediaTypes,
            Guid? collectionId,
            int? offset,
            int? limit,
            Guid? profileId,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            ICollectionMediaLookupReadService mediaLookupReadService,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            HashSet<Guid> existingWorkIds = [];
            if (collectionId.HasValue)
            {
                var collection = await collectionRepo.GetByIdAsync(collectionId.Value, ct);
                if (collection is null)
                {
                    return ApiErrors.NotFound($"Collection '{collectionId.Value}' not found.");
                }

                if (!CollectionAccessPolicy.CanAccess(collection, activeProfile))
                {
                    return Results.Forbid();
                }

                var existingItems = await collectionRepo.GetCollectionItemsAsync(collectionId.Value, 1000, ct);
                existingWorkIds = (await catalogReadService.GetDisplayWorkIdsAsync(
                        existingItems.Select(item => item.WorkId), ct))
                    .ToHashSet();
            }

            var page = PagedRequest.From(offset, limit, defaultLimit: 24);
            var allowedWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var results = await mediaLookupReadService.LookupAsync(
                q, mediaTypes, existingWorkIds, allowedWorkIds, page.Offset, page.Limit, ct);
            return Results.Ok(results);
        })
        .WithName("LookupCollectionMedia")
        .WithSummary("Searches local owned media for curated collection membership.")
        .Produces<List<CollectionMediaLookupDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/{id:guid}/summary", async (
            Guid id,
            Guid? profileId,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            var summary = await catalogReadService.GetSummaryAsync(id, activeProfile, ct, allowedWorkIds);
            return summary is null
                ? ApiErrors.NotFound($"Collection '{id}' not found or not visible to the active profile.")
                : Results.Ok(summary);
        })
        .WithName("GetCollectionSummary")
        .WithSummary("Returns the Collections hub summary for one visible collection.")
        .Produces<CollectionManagementCatalogDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        group.MapGet("/{id:guid}/items", async (
            Guid id,
            int? limit,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var page = PagedRequest.From(null, limit, defaultLimit: 20);
            var take = page.Limit;
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            var result = await catalogReadService.GetItemsAsync(id, activeProfile, take, ct, allowedWorkIds);
            if (!result.Found)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (result.Forbidden)
            {
                return Results.Forbid();
            }

            return Results.Ok(result.Items);
        })
        .WithName("GetCollectionItems")
        .WithSummary("Returns curated items for a collection with resolved work metadata.")
        .Produces<List<CollectionItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        group.MapPost("/{id:guid}/items", async (
            Guid id,
            CollectionItemAddRequest body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType)
                || collection.Resolution != CollectionResolution.Materialized)
            {
                return ApiErrors.BadRequest("Only saved/manual collections support direct item membership.");
            }
            if (body.WorkId == Guid.Empty)
            {
                return ApiErrors.BadRequest("work_id is required.");
            }

            var collectionWorkId = await catalogReadService.ResolveMembershipWorkIdAsync(body.WorkId, ct);
            var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
            if (!allowedWorkIds.Contains(collectionWorkId))
            {
                return ApiErrors.Forbidden("The requested work is outside the caller's available libraries.");
            }
            var existingItems = await collectionRepo.GetCollectionItemsAsync(id, 1000, ct);
            var existingDisplayWorkIds = await catalogReadService.GetDisplayWorkIdsAsync(existingItems.Select(item => item.WorkId), ct);
            if (existingDisplayWorkIds.Contains(collectionWorkId))
            {
                return Results.Ok();
            }

            var nextSortOrder = existingItems.Count == 0
                ? 1
                : existingItems.Max(item => item.SortOrder) + 1;

            await collectionRepo.AddCollectionItemAsync(new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = id,
                WorkId = collectionWorkId,
                SortOrder = nextSortOrder,
                AddedAt = DateTimeOffset.UtcNow,
            }, ct);

            return Results.Ok();
        })
        .WithName("AddCollectionItem")
        .WithSummary("Adds a work to a saved/manual collection.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapDelete("/{id:guid}/items/{itemId:guid}", async (
            Guid id,
            Guid itemId,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType)
                || collection.Resolution != CollectionResolution.Materialized)
            {
                return ApiErrors.BadRequest("Only saved/manual collections support direct item membership.");
            }

            var existingItems = await collectionRepo.GetCollectionItemsAsync(id, 1000, ct);
            if (!existingItems.Any(item => item.Id == itemId))
            {
                return ApiErrors.NotFound($"Item '{itemId}' not found in collection '{id}'.");
            }

            await collectionRepo.RemoveCollectionItemAsync(itemId, ct);
            return Results.Ok();
        })
        .WithName("RemoveCollectionItem")
        .WithSummary("Removes a work from a saved/manual collection.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapPut("/{id:guid}/items/reorder", async (
            Guid id,
            CollectionItemReorderRequest body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType)
                || collection.Resolution != CollectionResolution.Materialized)
            {
                return ApiErrors.BadRequest("Only saved/manual collections support direct item ordering.");
            }

            var requestedIds = body.ItemIds.Where(itemId => itemId != Guid.Empty).Distinct().ToList();
            var existingItems = await collectionRepo.GetCollectionItemsAsync(id, 1000, ct);
            var existingIds = existingItems.Select(item => item.Id).ToHashSet();
            if (requestedIds.Count != existingItems.Count || requestedIds.Any(itemId => !existingIds.Contains(itemId)))
            {
                return ApiErrors.BadRequest("item_ids must include every item in this collection exactly once.");
            }

            await collectionRepo.ReorderCollectionItemsAsync(id, requestedIds, ct);
            return Results.Ok();
        })
        .WithName("ReorderCollectionItems")
        .WithSummary("Reorders saved/manual collection items.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // Collection artwork slots mirror the shared editor: poster, background, and transparent logo.
        group.MapGet("/{id:guid}/artwork/{slot}", async (
            Guid id,
            string slot,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanAccess(collection, activeProfile))
            {
                return Results.Forbid();
            }

            if (!TryGetCollectionArtwork(collection, slot, out var artworkPath, out var artworkMimeType))
            {
                return ApiErrors.BadRequest("Artwork slot must be poster, background, or logo.");
            }

            if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath))
            {
                return ApiErrors.NotFound($"Collection '{id}' has no {slot} artwork.");
            }

            var bytes = await File.ReadAllBytesAsync(artworkPath, ct);
            return Results.File(
                bytes,
                string.IsNullOrWhiteSpace(artworkMimeType) ? GetCollectionArtworkMimeType(artworkPath) : artworkMimeType,
                Path.GetFileName(artworkPath));
        })
        .WithName("GetCollectionArtwork")
        .WithSummary("Serves one custom collection artwork slot.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        group.MapPost("/{id:guid}/artwork/{slot}", async (
            Guid id,
            string slot,
            HttpRequest request,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            TuvimaDataPaths dataPaths,
            ArtworkAssetService artworkAssets,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            if (!TryGetCollectionArtwork(collection, slot, out var currentPath, out _))
            {
                return ApiErrors.BadRequest("Artwork slot must be poster, background, or logo.");
            }

            if (!request.HasFormContentType)
            {
                return ApiErrors.BadRequest("Expected multipart form data.");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("No file uploaded.");
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return ApiErrors.BadRequest("Artwork must be 5 MB or smaller.");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var mimeType = NormalizeCollectionArtworkMimeType(file.ContentType, extension);
            if (mimeType is null)
            {
                return ApiErrors.BadRequest("Artwork must be a JPEG or PNG image.");
            }

            if (slot.Equals("logo", StringComparison.OrdinalIgnoreCase) && mimeType != "image/png")
            {
                return ApiErrors.BadRequest("Collection logos must be transparent PNG images.");
            }

            var normalizedSlot = slot.ToLowerInvariant();
            var role = normalizedSlot switch
            {
                "background" => "Background",
                "logo" => "Logo",
                _ => "Primary",
            };
            await using var upload = file.OpenReadStream();
            var canonical = await artworkAssets.UploadAsync(
                upload,
                extension,
                "Collection",
                id,
                new ArtworkLinkRequest(Guid.Empty, role, Preferred: true, EntityLabel: collection.DisplayName),
                "user_upload",
                null,
                ct);
            var targetPath = artworkAssets.ResolveContentPath(canonical.Id, null)
                ?? throw new InvalidOperationException("Canonical artwork was stored without readable content.");

            await collectionRepo.UpdateCollectionArtworkAsync(id, normalizedSlot, targetPath, mimeType, ct);
            return Results.Ok(new CollectionArtworkUploadResponse($"/collections/{id}/artwork/{normalizedSlot}", normalizedSlot));
        })
        .WithName("UploadCollectionArtwork")
        .WithSummary("Uploads one explicit artwork slot for a managed or structural collection.")
        .Produces<CollectionArtworkUploadResponse>(StatusCodes.Status200OK)
        .DisableAntiforgery()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapDelete("/{id:guid}/artwork/{slot}", async (
            Guid id,
            string slot,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            ArtworkAssetService artworkAssets,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            if (!TryGetCollectionArtwork(collection, slot, out var artworkPath, out _))
            {
                return ApiErrors.BadRequest("Artwork slot must be poster, background, or logo.");
            }

            var role = slot.ToLowerInvariant() switch
            {
                "background" => "Background",
                "logo" => "Logo",
                _ => "Primary",
            };
            var workspace = await artworkAssets.GetEntityAsync("Collection", id, ct);
            foreach (var link in workspace.Variants.Where(variant =>
                         string.Equals(variant.Role, role, StringComparison.OrdinalIgnoreCase)
                         && variant.IsPreferred))
            {
                await artworkAssets.RemoveLinkAsync(link.LinkId, ct);
            }

            await collectionRepo.UpdateCollectionArtworkAsync(id, slot, null, null, ct);
            return Results.Ok();
        })
        .WithName("DeleteCollectionArtwork")
        .WithSummary("Clears one explicit collection artwork slot and restores any derived fallback.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // PUT /collections/{id}/enabled — toggle collection visibility.
        group.MapPut("/{id:guid}/enabled", async (
            Guid id,
            EnabledRequest body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            await collectionRepo.UpdateCollectionEnabledAsync(id, body.Enabled, ct);
            return Results.Ok();
        })
        .WithName("UpdateCollectionEnabled")
        .WithSummary("Toggle a collection's enabled state.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // PUT /collections/{id}/featured — toggle collection featured state.
        group.MapPut("/{id:guid}/featured", async (
            Guid id,
            FeaturedRequest body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            await collectionRepo.UpdateCollectionFeaturedAsync(id, body.Featured, ct);
            return Results.Ok();
        })
        .WithName("UpdateCollectionFeatured")
        .WithSummary("Toggle a collection's featured state.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // ── Parameterized Collection endpoints ─────────────────────────────────────────

        // GET /collections/resolve/{id}?limit= — evaluate collection rules, return items
        group.MapGet("/resolve/{id:guid}", async (
            Guid id,
            int? limit,
            ICollectionRepository collectionRepo,
            ICollectionBrowseReadService browseReadService,
            ICollectionMediaLookupReadService mediaLookupReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();

            // For materialized collections, return works directly
            if (collection.Resolution == CollectionResolution.Materialized)
            {
                var collectionWithWorks = await collectionRepo.GetCollectionWithWorksAsync(id, ct);
                if (collectionWithWorks is null)
                {
                    return ApiErrors.NotFound($"Collection '{id}' not found.");
                }

                var take = limit ?? 0;
                var authorizedWorks = collectionWithWorks.Works
                    .Where(work => visibleWorkIds.Contains(work.Id));
                var works = take > 0 ? authorizedWorks.Take(take).ToList() : authorizedWorks.ToList();
                var primaryAssetIds = await browseReadService.GetPrimaryAssetIdsAsync(works.Select(w => w.Id), ct);
                var items = works.Select(w =>
                {
                    var dto = w.ToContract();
                    return new CollectionResolvedItemDto
                    {
                        EntityId = w.Id,
                        Title = GetCanonical(dto, "title") ?? $"Work {w.Id.ToString("N")[..8]}",
                        Creator = GetCanonical(dto, "author") ?? GetCanonical(dto, "director") ?? GetCanonical(dto, "artist"),
                        MediaType = w.MediaType.ToString(),
                        CoverUrl = BuildCoverStreamUrl(w, primaryAssetIds.GetValueOrDefault(w.Id)),
                        Year = GetCanonical(dto, "year"),
                    };
                }).ToList();

                return Results.Ok(items);
            }

            // For query-resolved collections, evaluate rules
            var definition = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            if (definition.AllConditions.Count == 0)
            {
                return Results.Ok(new List<CollectionResolvedItemDto>());
            }

            var entityIds = browseReadService.EvaluateRules(
                definition,
                collection.SortField,
                collection.SortDirection.ToStorageValue(),
                limit ?? 0,
                secondarySortField: collection.SecondarySortField,
                secondarySortDirection: collection.SecondarySortDirection?.ToStorageValue())
                .Where(visibleWorkIds.Contains)
                .ToList();

            var resolved = await mediaLookupReadService.ResolveMetadataAsync(entityIds, ct);
            return Results.Ok(resolved);
        })
        .WithName("ResolveCollection")
        .WithSummary("Evaluate a collection's rules and return matching items.")
        .Produces<List<CollectionResolvedItemDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        // GET /collections/resolve/by-name?name=All%20Songs&limit=200
        // Resolves a System collection by display name and returns matching items.
        // This path includes visible items that are still in the metadata pipeline while
        // intersecting the result with the caller's current catalogue authority.
        // Used by the library flat views (All Songs) to show music even before the
        // retail/Wikidata pipeline completes.  Fields are read from both the asset-level
        // and the root parent Work-level canonical_values rows so that parent-scoped
        // fields (artist, album, cover_url) are correctly resolved.
        group.MapGet("/resolve/by-name", async (
            string? name,
            int? limit,
            ICollectionBrowseReadService browseReadService,
            ICollectionMediaLookupReadService mediaLookupReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ApiErrors.BadRequest("name parameter is required");
            }

            var builtInDefinition = BuiltInBrowseCollectionCatalog.FindByName(name);
            var collection = builtInDefinition?.ToCollection();

            if (collection is null)
            {
                return ApiErrors.NotFound($"No dynamic browse view found with name '{name}'");
            }

            var ruleDefinition = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            if (ruleDefinition.AllConditions.Count == 0)
            {
                return Results.Ok(new List<CollectionResolvedItemDto>());
            }

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var entityIds = browseReadService.EvaluateRules(
                ruleDefinition,
                collection.SortField,
                collection.SortDirection.ToStorageValue(),
                0)
                .Where(visibleWorkIds.Contains)
                .Take(limit ?? 200)
                .ToList();

            var resolved = await mediaLookupReadService.ResolveMetadataAsync(entityIds, ct);
            return Results.Ok(resolved);
        })
        .WithName("ResolveCollectionByName")
        .WithSummary("Resolves a System collection by display name and returns items, reading both asset-level and parent-Work-level canonical values. Bypasses the libraryItem visibility filter so in-flight items are included.")
        .Produces<List<CollectionResolvedItemDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /collections/by-location/{location} — collections placed at a location
        group.MapGet("/by-location/{location}", async (
            string location,
            ICollectionPlacementRepository placementRepo,
            ICollectionRepository collectionRepo,
            HttpContext httpContext,
            CatalogueResourceAuthorizationService resourceAuthorization,
            CancellationToken ct) =>
        {
            var placements = await placementRepo.GetByLocationAsync(location, ct);
            var result = new List<CollectionLocationPlacementSummary>();
            var collectionsById = (await collectionRepo.GetByIdsAsync(
                    placements.Select(placement => placement.CollectionId),
                    ct))
                .ToDictionary(collection => collection.Id);

            foreach (var p in placements)
            {
                if (!collectionsById.TryGetValue(p.CollectionId, out var collection)
                    || !collection.IsEnabled)
                {
                    continue;
                }
                if (await resourceAuthorization.EvaluateEntityAsync(
                        httpContext,
                        "Collection",
                        collection.Id,
                        ApplicationPermissionIds.LibraryRead,
                        ct) != CatalogueResourceAccess.Allowed)
                {
                    continue;
                }

                result.Add(new CollectionLocationPlacementSummary(
                    collection.Id,
                    collection.DisplayName ?? $"Collection {collection.Id.ToString("N")[..8]}",
                    collection.CollectionType.ToStorageValue(),
                    collection.IconName,
                    p.Location,
                    p.Position,
                    p.DisplayLimit,
                    p.DisplayMode));
            }

            return Results.Ok(result);
        })
        .WithName("GetCollectionsByLocation")
        .WithSummary("Returns all collections placed at a specific UI location, ordered by position.")
        .Produces<List<CollectionLocationPlacementSummary>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // POST /collections/preview — evaluate rules without saving
        group.MapPost("/preview", async (
            CollectionPreviewRequest body,
            ICollectionBrowseReadService browseReadService,
            CollectionCatalogReadService catalogReadService,
            ICollectionMediaLookupReadService mediaLookupReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var definition = body.RuleDefinition.ToDomain();
            if (definition.AllConditions.Count == 0)
            {
                return Results.Ok(new CollectionPreviewResponse(0, []));
            }

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var matchedWorkIds = browseReadService.EvaluateRules(
                definition,
                body.SortField,
                body.SortDirection,
                0,
                body.Query,
                body.SecondarySortField,
                body.SecondarySortDirection)
                .Where(visibleWorkIds.Contains)
                .ToList();
            var displayWorkIds = await catalogReadService.GetDisplayWorkIdsAsync(matchedWorkIds, ct);
            var total = displayWorkIds.Count;
            var entityIds = displayWorkIds
                .Take(body.Limit > 0 ? body.Limit : 20)
                .ToList();

            var mediaTypeCounts = await mediaLookupReadService.CountMediaTypesAsync(displayWorkIds, ct);
            var resolved = await mediaLookupReadService.ResolveMetadataAsync(entityIds, ct);
            return Results.Ok(new CollectionPreviewResponse(total, resolved)
            {
                MediaTypeCounts = mediaTypeCounts,
            });
        })
        .WithName("PreviewCollection")
        .WithSummary("Evaluate collection rules and return matching items without saving.")
        .Produces<CollectionPreviewResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        // POST /collections — create a new collection
        group.MapPost("/", async (
            CollectionCreateRequest body,
            Guid? profileId,
            ICollectionRepository collectionRepo,
            ICollectionPlacementRepository placementRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            CollectionCatalogReadService catalogReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return ApiErrors.BadRequest("Collection name is required.");
            }

            var normalizedCollectionType = string.Equals(body.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
                ? CollectionTypeNames.Playlist
                : body.CollectionType;
            if (!CollectionAccessPolicy.IsManagedCollectionType(normalizedCollectionType))
            {
                return ApiErrors.BadRequest($"Collection type '{body.CollectionType}' is reserved for browse-only system data.");
            }

            var isCuratedCollection = string.Equals(
                normalizedCollectionType,
                CollectionTypeNames.Custom,
                StringComparison.OrdinalIgnoreCase);
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var requestedOwnerKind = AggregateStateSerializer.ParseContainerOwnerKind(body.OwnerKind);
            var requestedAudience = AggregateStateSerializer.ParseContainerAudience(body.Audience);
            if (activeProfile is null && requestedOwnerKind == ContainerOwnerKind.Profile)
            {
                return ApiErrors.BadRequest("An active profile is required to create a profile-owned collection.");
            }
            if (isCuratedCollection && !CollectionAccessPolicy.CanManageCuratedCollections(hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            var normalizedVisibility = isCuratedCollection
                ? CollectionAccessPolicy.SharedVisibility
                : CollectionAccessPolicy.NormalizeVisibility(body.Visibility);
            if (string.Equals(normalizedVisibility, CollectionAccessPolicy.SharedVisibility, StringComparison.OrdinalIgnoreCase)
                && !CollectionAccessPolicy.CanManageSharedCollections(hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            var definition = body.RuleDefinition.ToDomain();
            var ruleJson = definition.AllConditions.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(definition)
                : null;

            var ruleHash = definition.AllConditions.Count > 0
                ? CollectionRuleEvaluator.ComputeRuleHash(definition)
                : null;

            var membershipMode = string.Equals(body.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
                ? ContainerMembershipMode.Smart
                : AggregateStateSerializer.ParseContainerMembershipMode(body.MembershipMode);
            if (membershipMode == ContainerMembershipMode.Smart && definition.AllConditions.Count == 0)
            {
                return ApiErrors.BadRequest("Smart membership requires at least one complete rule.");
            }
            var resolution = membershipMode == ContainerMembershipMode.Smart
                ? CollectionResolution.Query
                : CollectionResolution.Materialized;

            if (ruleHash is not null)
            {
                var duplicate = await collectionRepo.FindByRuleHashAsync(ruleHash, ct);
                if (duplicate is not null && duplicate.IsEnabled)
                {
                    return ApiErrors.Conflict($"These rules already define '{duplicate.DisplayName ?? "an existing collection"}' ({duplicate.Id}).");
                }
            }

            if (resolution == CollectionResolution.Query && body.WorkIds.Count > 0)
            {
                return ApiErrors.BadRequest("Rule-driven collections cannot store direct item membership.");
            }

            var resolvedWorkIds = new List<Guid>();
            if (body.WorkIds.Count > 0)
            {
                foreach (var workId in body.WorkIds.Where(id => id != Guid.Empty).Distinct())
                {
                    resolvedWorkIds.Add(await catalogReadService.ResolveMembershipWorkIdAsync(workId, ct));
                }
                resolvedWorkIds = resolvedWorkIds.Distinct().ToList();
                var allowedWorkIds = (await display.LoadWorksAsync(ct)).Select(work => work.WorkId).ToHashSet();
                if (resolvedWorkIds.Any(workId => !allowedWorkIds.Contains(workId)))
                {
                    return ApiErrors.Forbidden("One or more requested works are outside the caller's available libraries.");
                }
            }

            var collection = new Collection
            {
                Id = Guid.NewGuid(),
                DisplayName = body.Name,
                Description = body.Description,
                IconName = body.IconName,
                IsEnabled = true,
                MinItems = 0,
                RuleJson = ruleJson,
                RuleHash = ruleHash,
                SortField = body.SortField,
                SecondarySortField = body.SecondarySortField,
                SecondarySortDirection = string.IsNullOrWhiteSpace(body.SecondarySortField)
                    || string.IsNullOrWhiteSpace(body.SecondarySortDirection)
                    ? null
                    : AggregateStateSerializer.ParseCollectionSortDirection(body.SecondarySortDirection),
                CreatedAt = DateTimeOffset.UtcNow,
                MembershipMode = membershipMode,
                PrimaryArea = string.Equals(normalizedCollectionType, CollectionTypeNames.Playlist, StringComparison.OrdinalIgnoreCase)
                    ? CollectionPrimaryArea.Listen
                    : AggregateStateSerializer.ParseCollectionPrimaryArea(body.PrimaryArea),
                OwnerKind = requestedOwnerKind,
                Audience = requestedAudience,
            };
            collection.RestoreDefinition(
                AggregateStateSerializer.ParseCollectionType(normalizedCollectionType),
                CollectionScope.Library,
                resolution,
                CollectionMatchMode.All,
                AggregateStateSerializer.ParseCollectionSortDirection(body.SortDirection),
                CollectionUniverseStatus.Unknown);
            CollectionAccessPolicy.ApplyOwnership(collection, activeProfile?.Id);
            var selectedAudienceProfileIds = collection.OwnerKind == ContainerOwnerKind.Profile
                && collection.Audience == ContainerAudience.SelectedProfiles
                    ? body.SelectedProfileIds
                        .Where(id => id != Guid.Empty && (activeProfile is null || id != activeProfile.Id))
                        .Distinct()
                        .ToList()
                    : [];
            if (collection.Audience == ContainerAudience.SelectedProfiles && selectedAudienceProfileIds.Count == 0)
            {
                return ApiErrors.BadRequest("Choose at least one other profile for the selected-profile audience.");
            }
            collection.ReplaceAudienceProfiles(selectedAudienceProfileIds);

            var initialItems = resolvedWorkIds
                .Select((workId, index) => new CollectionItem
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collection.Id,
                    WorkId = workId,
                    SortOrder = index + 1,
                    AddedAt = DateTimeOffset.UtcNow,
                })
                .ToList();

            await collectionRepo.CreateManagedCollectionAsync(collection, initialItems, ct);
            await collectionRepo.ReplaceAudienceProfileIdsAsync(collection.Id, selectedAudienceProfileIds, ct);

            // Create placements
            if (body.Placements is { Count: > 0 })
            {
                foreach (var p in body.Placements)
                {
                    await placementRepo.UpsertAsync(new CollectionPlacement
                    {
                        Id = Guid.NewGuid(),
                        CollectionId = collection.Id,
                        Location = p.Location,
                        Position = p.Position,
                        DisplayLimit = p.DisplayLimit,
                        DisplayMode = p.DisplayMode,
                        IsVisible = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    }, ct);
                }
            }

            return Results.Created($"/collections/{collection.Id}", new CollectionCreatedResponse(collection.Id, collection.DisplayName));
        })
        .WithName("CreateCollection")
        .WithSummary("Create a new collection with rules and optional placements.")
        .Produces<CollectionCreatedResponse>(StatusCodes.Status201Created)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        // PUT /collections/{id} — update collection
        group.MapPut("/{id:guid}", async (
            Guid id,
            CollectionUpdateRequest body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
            {
                return ApiErrors.BadRequest($"Collection type '{collection.CollectionType}' is browse-only and cannot be edited here.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            if (body.Name is not null)
            {
                collection.DisplayName = body.Name;
            }

            if (body.Description is not null)
            {
                collection.Description = body.Description;
            }

            if (body.IconName is not null)
            {
                collection.IconName = body.IconName;
            }

            if (!string.IsNullOrWhiteSpace(body.MembershipMode))
            {
                collection.MembershipMode = AggregateStateSerializer.ParseContainerMembershipMode(body.MembershipMode);
            }
            if (!string.IsNullOrWhiteSpace(body.PrimaryArea) && collection.CollectionType != CollectionType.Playlist)
            {
                collection.PrimaryArea = AggregateStateSerializer.ParseCollectionPrimaryArea(body.PrimaryArea);
            }
            if (!string.IsNullOrWhiteSpace(body.OwnerKind))
            {
                collection.OwnerKind = AggregateStateSerializer.ParseContainerOwnerKind(body.OwnerKind);
            }
            if (!string.IsNullOrWhiteSpace(body.Audience))
            {
                collection.Audience = AggregateStateSerializer.ParseContainerAudience(body.Audience);
            }

            if (collection.OwnerKind == ContainerOwnerKind.Profile && activeProfile is null)
            {
                return ApiErrors.BadRequest("An active profile is required to own this collection.");
            }

            if (body.SortField is not null)
            {
                collection.SortField = body.SortField;
            }

            if (body.SortDirection is not null)
            {
                collection.ChangeRuleOrdering(
                    collection.MatchMode,
                    AggregateStateSerializer.ParseCollectionSortDirection(body.SortDirection));
            }

            collection.SecondarySortField = string.IsNullOrWhiteSpace(body.SecondarySortField)
                ? null
                : body.SecondarySortField;
            collection.SecondarySortDirection = collection.SecondarySortField is null
                || string.IsNullOrWhiteSpace(body.SecondarySortDirection)
                ? null
                : AggregateStateSerializer.ParseCollectionSortDirection(body.SecondarySortDirection);

            if (body.IsEnabled.HasValue)
            {
                collection.IsEnabled = body.IsEnabled.Value;
            }

            if (body.IsFeatured.HasValue)
            {
                collection.IsFeatured = body.IsFeatured.Value;
            }

            if (collection.CollectionType == CollectionType.Custom)
            {
                CollectionAccessPolicy.ApplyVisibility(
                    collection,
                    CollectionAccessPolicy.SharedVisibility,
                    activeProfile?.Id);
            }
            else if (!string.IsNullOrWhiteSpace(body.Visibility))
            {
                var normalizedVisibility = CollectionAccessPolicy.NormalizeVisibility(body.Visibility);
                if (string.Equals(normalizedVisibility, CollectionAccessPolicy.SharedVisibility, StringComparison.OrdinalIgnoreCase)
                    && !CollectionAccessPolicy.CanManageSharedCollections(hasCollectionsWrite: true))
                {
                    return ApiErrors.Forbidden("The active profile cannot publish shared collections.");
                }

                CollectionAccessPolicy.ApplyVisibility(collection, normalizedVisibility, activeProfile?.Id);
            }

            if (body.RuleDefinition is not null)
            {
                var definition = body.RuleDefinition.ToDomain();
                if (definition.AllConditions.Count > 0)
                {
                    collection.RuleJson = System.Text.Json.JsonSerializer.Serialize(definition);
                    collection.RuleHash = CollectionRuleEvaluator.ComputeRuleHash(definition);
                    var duplicate = await collectionRepo.FindByRuleHashAsync(collection.RuleHash, ct);
                    if (duplicate is not null && duplicate.Id != collection.Id && duplicate.IsEnabled)
                    {
                        return ApiErrors.Conflict($"These rules already define '{duplicate.DisplayName ?? "an existing collection"}' ({duplicate.Id}).");
                    }

                    collection.ChangeResolution(CollectionResolution.Query);
                    collection.MembershipMode = ContainerMembershipMode.Smart;
                }
                else
                {
                    collection.RuleJson = null;
                    collection.RuleHash = null;
                    collection.ChangeResolution(CollectionResolution.Materialized);
                    collection.MembershipMode = ContainerMembershipMode.Manual;
                }
            }

            if (collection.MembershipMode == ContainerMembershipMode.Smart && string.IsNullOrWhiteSpace(collection.RuleJson))
            {
                return ApiErrors.BadRequest("Smart membership requires at least one complete rule.");
            }

            CollectionAccessPolicy.ApplyOwnership(collection, activeProfile?.Id);

            var selectedAudienceProfileIds = collection.OwnerKind == ContainerOwnerKind.Profile
                && collection.Audience == ContainerAudience.SelectedProfiles
                    ? (body.SelectedProfileIds ?? collection.AudienceProfileIds)
                        .Where(id => id != Guid.Empty && (activeProfile is null || id != activeProfile.Id))
                        .Distinct()
                        .ToList()
                    : [];
            if (collection.Audience == ContainerAudience.SelectedProfiles && selectedAudienceProfileIds.Count == 0)
            {
                return ApiErrors.BadRequest("Choose at least one other profile for the selected-profile audience.");
            }
            collection.ReplaceAudienceProfiles(selectedAudienceProfileIds);

            collection.ModifiedAt = DateTimeOffset.UtcNow;
            await collectionRepo.UpsertAsync(collection, ct);
            await collectionRepo.ReplaceAudienceProfileIdsAsync(collection.Id, selectedAudienceProfileIds, ct);
            return Results.Ok();
        })
        .WithName("UpdateCollection")
        .WithSummary("Update a collection's rules, settings, or metadata.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // DELETE /collections/{id} — soft delete (disable)
        group.MapDelete("/{id:guid}", async (
            Guid id,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var activeProfile = await ResolveActiveProfileAsync(profileId, profileRepo, httpContext, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
            {
                return ApiErrors.BadRequest($"Collection type '{collection.CollectionType}' is browse-only and cannot be deleted here.");
            }

            if (collection.CollectionType == CollectionType.System)
            {
                return ApiErrors.BadRequest("System collections cannot be deleted.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                return Results.Forbid();
            }

            await collectionRepo.UpdateCollectionEnabledAsync(id, false, ct);
            return Results.Ok();
        })
        .WithName("DeleteCollection")
        .WithSummary("Soft-delete a collection by disabling it.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // GET /collections/field-values/{field} — distinct values for autocomplete
        group.MapGet("/field-values/{field}", async (
            string field,
            string? q,
            int? limit,
            ICollectionBrowseReadService browseReadService,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(null, limit, defaultLimit: 50);
            var values = await browseReadService.GetFieldValuesAsync(field, q, page.Limit, ct);
            return Results.Ok(values);
        })
        .WithName("GetFieldValues")
        .WithSummary("Returns distinct values for a metadata field (used for collection builder autocomplete).")
        .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        group.MapGet("/entity-field-values/{field}", async (
            string field,
            string? q,
            int? limit,
            ICollectionBrowseReadService browseReadService,
            CancellationToken ct) =>
        {
            var isGenericEntityField = StructuredDiscoveryFieldCatalog.IsEntityBacked(field);
            if (!isGenericEntityField
                && field is not ("person_qid" or "wikidata_franchise"))
            {
                return ApiErrors.BadRequest("The requested collection field is not entity-backed.");
            }

            var page = PagedRequest.From(null, limit, defaultLimit: 100);
            return Results.Ok(await browseReadService.GetEntityFieldValuesAsync(field, q, page.Limit, ct));
        })
        .WithName("GetEntityFieldValues")
        .WithSummary("Returns local QID-backed values and labels for the collection rule editor.")
        .Produces<IReadOnlyList<CollectionRuleValueDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        // GET /collections/{id}/placements
        group.MapGet("/{id:guid}/placements", async (
            Guid id,
            ICollectionPlacementRepository placementRepo,
            CancellationToken ct) =>
        {
            var placements = await placementRepo.GetByCollectionIdAsync(id, ct);
            return Results.Ok(placements.Select(p => new CollectionPlacementSummary(
                p.Id,
                p.Location,
                p.Position,
                p.DisplayLimit,
                p.DisplayMode,
                p.IsVisible)));
        })
        .WithName("GetCollectionPlacements")
        .WithSummary("Returns placements for a collection.")
        .Produces<IEnumerable<CollectionPlacementSummary>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
            .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        // PUT /collections/{id}/placements — replace all placements
        group.MapPut("/{id:guid}/placements", async (
            Guid id,
            List<PlacementRequest> body,
            ICollectionPlacementRepository placementRepo,
            CancellationToken ct) =>
        {
            await placementRepo.DeleteByCollectionIdAsync(id, ct);
            foreach (var p in body)
            {
                await placementRepo.UpsertAsync(new CollectionPlacement
                {
                    Id = Guid.NewGuid(),
                    CollectionId = id,
                    Location = p.Location,
                    Position = p.Position,
                    DisplayLimit = p.DisplayLimit,
                    DisplayMode = p.DisplayMode,
                    IsVisible = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }
            return Results.Ok();
        })
        .WithName("UpdateCollectionPlacements")
        .WithSummary("Replace all placements for a collection.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        return app;
    }

    // ── Request bodies ────────────────────────────────────────────────────────

    public sealed record EnabledRequest(bool Enabled);
    public sealed record FeaturedRequest(bool Featured);

    // ── Response bodies ───────────────────────────────────────────────────────
    // CollectionResolvedItemDto is an Api-internal model (MediaEngine.Api.Models), so this
    // response record stays here rather than in MediaEngine.Contracts.Collections, which may
    // only reference Domain. See PreviewCollection ("/collections/preview").

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<Profile?> ResolveActiveProfileAsync(
        Guid? profileId,
        IProfileRepository profileRepo,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var authority = await httpContext.RequestServices
            .GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(httpContext, ct);
        if (authority.ActiveProfileId is not { } activeProfileId
            || (profileId.HasValue && profileId.Value != activeProfileId))
        {
            return null;
        }

        return await profileRepo.GetByIdAsync(activeProfileId, ct);
    }

    private static CollectionDto FilterCollection(
        CollectionDto collection,
        IReadOnlySet<Guid> visibleWorkIds) =>
        new()
        {
            Id = collection.Id,
            UniverseId = collection.UniverseId,
            DisplayName = collection.DisplayName,
            ParentCollectionId = collection.ParentCollectionId,
            UniverseStatus = collection.UniverseStatus,
            CreatedAt = collection.CreatedAt,
            Works = collection.Works.Where(work => visibleWorkIds.Contains(work.Id)).ToList(),
        };

    private static bool TryGetCollectionArtwork(Collection collection, string slot, out string? path, out string? mimeType)
    {
        (path, mimeType) = slot.ToLowerInvariant() switch
        {
            "poster" => (collection.CoverArtworkPath, collection.CoverArtworkMimeType),
            "background" => (collection.BackgroundArtworkPath, collection.BackgroundArtworkMimeType),
            "logo" => (collection.LogoArtworkPath, collection.LogoArtworkMimeType),
            _ => (null, null),
        };
        return slot.Equals("poster", StringComparison.OrdinalIgnoreCase)
            || slot.Equals("background", StringComparison.OrdinalIgnoreCase)
            || slot.Equals("logo", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildCoverStreamUrl(Work? w, Guid? assetId = null)
    {
        return BuildArtworkStreamUrl(
            w,
            "cover",
            MetadataFieldConstants.CoverState,
            assetId,
            MetadataFieldConstants.CoverUrl,
            MetadataFieldConstants.Cover);
    }

    private static string? BuildBackgroundStreamUrl(Work? w, Guid? assetId = null)
    {
        return BuildArtworkStreamUrl(
            w,
            "background",
            "background_state",
            assetId,
            "background",
            "background_url");
    }

    private static string? BuildLogoStreamUrl(Work? w, Guid? assetId = null)
    {
        return BuildArtworkStreamUrl(
            w,
            "logo",
            "logo_state",
            assetId,
            "logo",
            "logo_url");
    }

    private static string? BuildArtworkStreamUrl(
        Work? work,
        string streamKind,
        string stateKey,
        Guid? fallbackAssetId,
        params string[] valueKeys)
    {
        if (work is null)
        {
            return null;
        }

        var value = FirstCanonicalValue(work, valueKeys);
        var state = FirstCanonicalValue(work, stateKey);
        var assetId = fallbackAssetId.GetValueOrDefault();
        if (assetId == Guid.Empty)
        {
            assetId = FirstOwnedAssetId(work);
        }

        return assetId != Guid.Empty
            ? DisplayArtworkUrlResolver.Resolve(value, assetId, streamKind, state)
            : SuppressExternalProviderArtworkUrl(value);
    }

    private static Guid FirstOwnedAssetId(Work work) =>
        work.Editions
            .SelectMany(edition => edition.MediaAssets)
            .Select(asset => asset.Id)
            .FirstOrDefault(id => id != Guid.Empty);

    private static string? SuppressExternalProviderArtworkUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? null
            : value;
    }

}
