using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Endpoints;

public static class PersonEndpoints
{
    public static IEndpointRouteBuilder MapPersonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/persons")
                       .WithTags("Persons");

        group.MapGet("/{id:guid}/editor", async (
            Guid id,
            Guid? profileId,
            HttpContext httpContext,
            IRequestAuthorityResolver authorityResolver,
            PersonEditorReadService editorData,
            CancellationToken ct) =>
        {
            var authority = await authorityResolver.ResolveAsync(httpContext, ct);
            if (profileId.HasValue && profileId != authority.ActiveProfileId)
            {
                return Results.Forbid();
            }
            var state = await editorData.GetAsync(id, authority.ActiveProfileId, ct);
            return state is null ? ApiErrors.NotFound($"Person '{id}' not found.") : Results.Ok(state);
        })
        .WithName("GetPersonEditorState")
        .WithSummary("Returns shared person presentation overrides, library-wide tags, and history.")
        .Produces<PersonEditorStateResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead, "Person", "id");

        group.MapPut("/{id:guid}/editor", async (
            Guid id,
            PersonEditorSaveRequest request,
            IPersonRepository personRepo,
            PersonEditorReadService editorData,
            CancellationToken ct) =>
        {
            if (await personRepo.FindByIdAsync(id, ct) is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var invalidKeys = request.DisplayOverrides.Keys
                .Where(key => key is not ("name" or "biography" or "sort_name"))
                .ToList();
            if (invalidKeys.Count > 0)
            {
                return ApiErrors.BadRequest($"Unsupported person override fields: {string.Join(", ", invalidKeys)}.");
            }

            if (!LibraryTagCatalog.TryNormalize(request.LocalTags, out _, out var tagError))
            {
                return ApiErrors.BadRequest(tagError ?? "Invalid library tags.");
            }

            var result = await editorData.SaveAsync(id, request, ct);

            return result.Saved
                ? Results.Ok(new PersonEditorSaveResponse(id, result.Revision))
                : ApiErrors.Conflict("The person changed while this editor was open.");
        })
        .WithName("SavePersonEditorState")
        .WithSummary("Saves refresh-safe person display overrides and library-wide tags.")
        .Produces<PersonEditorSaveResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "Person", "id");

        group.MapGet("/{id:guid}/artwork", async (
            Guid id,
            IPersonRepository personRepo,
            IEntityAssetRepository assetRepo,
            CancellationToken ct) =>
        {
            var person = await personRepo.FindByIdAsync(id, ct);
            if (person is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var assets = await assetRepo.GetByEntityAsync(id.ToString(), null, ct);
            var slots = new[] { "Headshot", "Background", "Logo" }
                .Select(assetType => new ArtworkSlotDto(assetType, assets
                    .Where(asset => string.Equals(asset.AssetTypeValue, assetType, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(asset => asset.IsPreferred)
                    .ThenByDescending(asset => asset.CreatedAt)
                    .Select(asset => new ArtworkVariantDto(
                        asset.Id,
                        assetType,
                        $"/stream/artwork/{asset.Id}",
                        asset.IsPreferred,
                        asset.SourceProvider ?? "Stored",
                        asset.SourceProvider,
                        string.Equals(asset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase),
                        asset.CreatedAt,
                        asset.WidthPx,
                        asset.HeightPx))
                    .ToList()))
                .ToList();

            var headshotSlot = slots.First(slot => slot.AssetType == "Headshot");
            var canonicalHeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(
                person.Id,
                person.LocalHeadshotPath,
                person.HeadshotUrl);
            if (headshotSlot.Variants.Count == 0 && canonicalHeadshotUrl is not null)
            {
                var dimensions = TryMeasureImage(person.LocalHeadshotPath);
                headshotSlot.Variants.Add(new ArtworkVariantDto(
                    Guid.Empty,
                    "Headshot",
                    canonicalHeadshotUrl,
                    true,
                    "Provider",
                    string.IsNullOrWhiteSpace(person.WikidataQid) ? null : "Wikidata",
                    false,
                    null,
                    dimensions?.Width,
                    dimensions?.Height));
            }
            return Results.Ok(new ArtworkEditorDto(id, slots));
        })
        .WithName("GetPersonArtwork")
        .Produces<ArtworkEditorDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.ArtworkRead, "Person", "id");

        group.MapPost("/{id:guid}/artwork/{assetType}", async (
            Guid id,
            string assetType,
            IPersonRepository personRepo,
            IEntityAssetRepository assetRepo,
            ISystemActivityRepository activityRepo,
            ArtworkScopeService artworkScopeService,
            HttpRequest httpRequest,
            CancellationToken ct) =>
        {
            if (await personRepo.FindByIdAsync(id, ct) is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var normalizedType = assetType.Trim() switch
            {
                "Headshot" or "headshot" => "Headshot",
                "Background" or "background" => "Background",
                "Logo" or "logo" => "Logo",
                _ => null,
            };
            if (normalizedType is null)
            {
                return ApiErrors.BadRequest("Person artwork supports Headshot, Background, and Logo.");
            }

            if (!httpRequest.HasFormContentType)
            {
                return ApiErrors.BadRequest("Expected multipart form data.");
            }

            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("No file provided.");
            }

            if (file.Length > BoundedHttpContent.MaximumImageBytes)
            {
                return ApiErrors.BadRequest("Artwork files must be 20 MB or smaller.");
            }

            if (!ArtworkScopeService.IsArtworkUploadAllowed(file.ContentType, normalizedType))
            {
                return ApiErrors.BadRequest(normalizedType == "Logo" ? "Logos must be PNG images." : "Only JPEG and PNG images are accepted.");
            }

            var variantId = Guid.NewGuid();
            var localPath = artworkScopeService.BuildArtworkUploadPath("Person", id, normalizedType, variantId, file.ContentType);
            AssetPathService.EnsureDirectory(localPath);
            await using (var input = file.OpenReadStream())
            {
                await BoundedHttpContent.CopyImageToFileAtomicallyAsync(input, localPath, ct);
            }

            var asset = new EntityAsset
            {
                Id = variantId,
                EntityId = id.ToString(),
                EntityType = "Person",
                AssetTypeValue = normalizedType,
                ImageUrl = $"/stream/artwork/{variantId}",
                LocalImagePath = localPath,
                SourceProvider = "user_upload",
                OwnerScope = "Person",
                IsPreferred = true,
                IsUserOverride = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await assetRepo.UpsertAsync(asset, ct);
            await assetRepo.SetPreferredAsync(asset.Id, ct);
            await activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.CoverArtSaved,
                EntityId = id,
                EntityType = "Person",
                Detail = $"Custom {normalizedType.ToLowerInvariant()} uploaded",
            }, ct);

            return Results.Ok(new ArtworkUploadResponse(id, normalizedType, variantId, asset.ImageUrl));
        })
        .WithName("UploadPersonArtwork")
        .Produces<ArtworkUploadResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "Person", "id")
        .DisableAntiforgery();

        // GET /persons/{id} — person detail including local headshot availability.
        group.MapGet("/{id:guid}", async (
            Guid id,
            IPersonRepository personRepo,
            IEntityAssetRepository assetRepo,
            IPersonCreditReadService personCreditReadService,
            PersonEditorReadService editorData,
            CancellationToken ct) =>
        {
            var person = await personRepo.FindByIdAsync(id, ct);
            if (person is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var groupMembers = await personCreditReadService.GetGroupMembersAsync(id, person.IsGroup, ct);
            var memberOfGroups = await personCreditReadService.GetGroupMembersAsync(id, false, ct);
            var preferredBackground = await assetRepo.GetPreferredAsync(id.ToString(), "Background", ct);
            var preferredLogo = await assetRepo.GetPreferredAsync(id.ToString(), "Logo", ct);
            var preferredHeadshot = await assetRepo.GetPreferredAsync(id.ToString(), "Headshot", ct);
            var displayOverrides = editorData.GetDisplayOverrides(id, ct);
            var displayName = displayOverrides.TryGetValue("name", out var nameOverride) ? nameOverride : person.Name;
            var displayBiography = displayOverrides.TryGetValue("biography", out var biographyOverride) ? biographyOverride : person.Biography;

            return Results.Ok(new PersonDetailResponse
            {
                Id = person.Id,
                Name = displayName,
                Roles = person.Roles,
                WikidataQid = person.WikidataQid,
                HeadshotUrl = preferredHeadshot is null ? ApiImageUrls.BuildPersonHeadshotUrl(person.Id, person.LocalHeadshotPath, person.HeadshotUrl) : $"/stream/artwork/{preferredHeadshot.Id}",
                Biography = displayBiography,
                Occupation = person.Occupation,
                DateOfBirth = person.DateOfBirth,
                DateOfDeath = person.DateOfDeath,
                PlaceOfBirth = person.PlaceOfBirth,
                PlaceOfDeath = person.PlaceOfDeath,
                Nationality = person.Nationality,
                Instagram = person.Instagram,
                Twitter = person.Twitter,
                TikTok = person.TikTok,
                Mastodon = person.Mastodon,
                Website = person.Website,
                HasLocalHeadshot = preferredHeadshot is not null || (!string.IsNullOrEmpty(person.LocalHeadshotPath)
                    && File.Exists(person.LocalHeadshotPath)),
                IsPseudonym = person.IsPseudonym,
                IsGroup = person.IsGroup,
                GroupMembers = person.IsGroup ? groupMembers : [],
                MemberOfGroups = person.IsGroup ? [] : memberOfGroups,
                BannerUrl = null,
                BackgroundUrl = preferredBackground is null ? null : $"/stream/artwork/{preferredBackground.Id}",
                LogoUrl = preferredLogo is null ? null : $"/stream/artwork/{preferredLogo.Id}",
                CreatedAt = person.CreatedAt,
                EnrichedAt = person.EnrichedAt,
            });
        })
        .Produces<PersonDetailResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Person", "id");

        // GET /persons/{id}/aliases — linked pseudonym and real-person entries.
        group.MapGet("/{id:guid}/aliases", async (
            Guid id,
            IPersonAliasReadService aliasReadService,
            CancellationToken ct) =>
        {
            var response = await aliasReadService.GetAliasesAsync(id, ct);
            return response is null
                ? ApiErrors.NotFound($"Person '{id}' not found.")
                : Results.Ok(response);
        })
        .WithName("GetPersonAliases")
        .WithSummary("Linked pseudonym and real-person entries for a given person.")
        .Produces<PersonAliasResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Person", "id");

        // GET /persons/{id}/headshot — serves the canonical person headshot asset.
        // Local files resolve only from Person.LocalHeadshotPath or .data/assets/people/{personId}/headshot.*.
        // Downloads and caches if no local file exists.
        group.MapGet("/{id:guid}/headshot", async (
            Guid id,
            string? size,
            IPersonRepository personRepo,
            IEntityAssetRepository assetRepo,
            IHttpClientFactory httpFactory,
            AssetPathService assetPaths,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.PersonHeadshots");
            var person = await personRepo.FindByIdAsync(id, ct);
            if (person is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var preferredHeadshot = await assetRepo.GetPreferredAsync(id.ToString(), "Headshot", ct);
            var preferredHeadshotPath = size?.Trim().ToLowerInvariant() switch
            {
                "s" => preferredHeadshot?.LocalImagePathSmall ?? preferredHeadshot?.LocalImagePathMedium ?? preferredHeadshot?.LocalImagePath,
                "m" => preferredHeadshot?.LocalImagePathMedium ?? preferredHeadshot?.LocalImagePathLarge ?? preferredHeadshot?.LocalImagePath,
                "l" => preferredHeadshot?.LocalImagePathLarge ?? preferredHeadshot?.LocalImagePath,
                _ => preferredHeadshot?.LocalImagePath,
            };
            if (!string.IsNullOrWhiteSpace(preferredHeadshotPath)
                && File.Exists(preferredHeadshotPath)
                && IsLikelyImageFile(preferredHeadshotPath))
            {
                return Results.File(preferredHeadshotPath, GetImageMimeTypeOrJpeg(preferredHeadshotPath));
            }

            // Try local headshot path first.
            if (!string.IsNullOrEmpty(person.LocalHeadshotPath)
                && File.Exists(person.LocalHeadshotPath)
                && IsLikelyImageFile(person.LocalHeadshotPath))
            {
                return Results.File(person.LocalHeadshotPath, GetImageMimeTypeOrJpeg(person.LocalHeadshotPath));
            }

            var canonicalPath = assetPaths.GetPersonHeadshotPath(id);
            if (File.Exists(canonicalPath) && IsLikelyImageFile(canonicalPath))
            {
                await personRepo.UpdateLocalHeadshotPathAsync(id, canonicalPath, ct);
                return Results.File(canonicalPath, GetImageMimeTypeOrJpeg(canonicalPath));
            }

            // No local file — download from Wikimedia and cache locally using AssetPathService.
            if (!string.IsNullOrEmpty(person.HeadshotUrl))
            {
                try
                {
                    using var client = httpFactory.CreateClient("headshot_download");
                    using var response = await client.GetAsync(person.HeadshotUrl, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        if (bytes.Length > 0 && IsLikelyImageBytes(bytes, contentType))
                        {
                            var localPath = assetPaths.GetPersonHeadshotPath(
                                id,
                                MediaMimeTypes.InferImageExtension(contentType) ?? MediaMimeTypes.InferImageExtension(person.HeadshotUrl) ?? ".jpg");
                            AssetPathService.EnsureDirectory(localPath);
                            await BoundedHttpContent.WriteFileAtomicallyAsync(localPath, bytes, ct);
                            await personRepo.UpdateLocalHeadshotPathAsync(id, localPath, ct);
                            return Results.File(bytes, contentType ?? GetImageMimeTypeOrJpeg(localPath), Path.GetFileName(localPath));
                        }

                        logger.LogDebug(
                            "Rejected person headshot for {PersonId} ({Name}) because response was not an image. ContentType={ContentType}, Bytes={ByteCount}",
                            id, person.Name, contentType, bytes.Length);
                    }
                    else
                    {
                        logger.LogDebug(
                            "Remote person headshot request failed for {PersonId} ({Name}) with HTTP {StatusCode}",
                            id, person.Name, (int)response.StatusCode);
                    }
                }
                catch
                {
                    // Download failed — fall through to 404
                }
            }

            logger.LogDebug(
                "No person headshot available for {PersonId} ({Name}). HasQid={HasQid}, HasRemoteUrl={HasRemoteUrl}, LocalPath={LocalPath}",
                id,
                person.Name,
                !string.IsNullOrWhiteSpace(person.WikidataQid),
                !string.IsNullOrWhiteSpace(person.HeadshotUrl),
                person.LocalHeadshotPath);

            return ApiErrors.NotFound("Headshot not available.");
        })
        .WithName("GetPersonHeadshot")
        .Produces(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.ArtworkRead, "Person", "id");

        // GET /persons/by-collection/{collectionId} — all persons linked to works in a collection.
        group.MapGet("/by-collection/{collectionId:guid}", async (
            Guid collectionId,
            IPersonAssetScopeReadService personScopeReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var persons = await personScopeReadService.GetByCollectionAsync(collectionId, ct);
            var assetIds = (await display.LoadWorksAsync(ct)).Select(work => work.AssetId).ToArray();
            var visiblePersonIds = await personScopeReadService.GetCanonicalPersonIdsForCollectionAsync(
                collectionId, assetIds, ct);
            return Results.Ok(persons.Where(person => visiblePersonIds.Contains(person.Id)).ToList());
        })
        .Produces<IReadOnlyList<PersonSummaryResponse>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "collectionId");

        // GET /persons/by-work/{workId} — all persons linked to a specific work.
        group.MapGet("/by-work/{workId:guid}", async (
            Guid workId,
            IPersonAssetScopeReadService personScopeReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var persons = await personScopeReadService.GetByWorkAsync(workId, ct);
            var assetIds = (await display.LoadWorksAsync(ct))
                .Where(work => work.WorkId == workId)
                .Select(work => work.AssetId)
                .ToArray();
            var visiblePersonIds = await personScopeReadService.GetCanonicalPersonIdsAsync(assetIds, ct);
            return Results.Ok(persons.Where(person => visiblePersonIds.Contains(person.Id)).ToList());
        })
        .Produces<IReadOnlyList<PersonSummaryResponse>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Work", "workId");

        // GET /persons/{id}/library-credits — role-aware owned work credits for a person.
        group.MapGet("/{id:guid}/library-credits", async (
            Guid id,
            IPersonRepository personRepo,
            IPersonCreditReadService personCreditReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var person = await personRepo.FindByIdAsync(id, ct);
            if (person is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var credits = await personCreditReadService.GetLibraryCreditsAsync(id, ct);
            return Results.Ok(PersonLibraryCreditAuthorizationPolicy.Filter(
                credits,
                await display.LoadWorksAsync(ct)));
        })
        .WithName("GetPersonLibraryCredits")
        .WithSummary("Owned work credits for a person, grouped client-side by role and media type.")
        .Produces<List<PersonLibraryCreditDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Person", "id");

        // GET /persons/{id}/works — all collections containing works by this person.
        group.MapGet("/{id:guid}/works", async (
            Guid id,
            IPersonRepository personRepo,
            ICollectionRepository collectionRepo,
            IPersonWorksReadService personWorksReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var person = await personRepo.FindByIdAsync(id, ct);
            if (person is null)
            {
                return ApiErrors.NotFound($"Person '{id}' not found.");
            }

            var collectionIds = await personWorksReadService.GetCollectionIdsForPersonAsync(id, ct);
            if (collectionIds.Count == 0)
            {
                return Results.Ok(Array.Empty<MediaEngine.Contracts.Collections.CollectionDto>());
            }

            var visibleWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var allCollections = await collectionRepo.GetAllAsync(ct);
            var dtos = allCollections
                .Where(h => collectionIds.Contains(h.Id))
                .Select(collection => collection.ToContract())
                .Select(collection => FilterCollection(collection, visibleWorkIds))
                .Where(collection => collection.Works.Count > 0)
                .ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetWorksByPerson")
        .WithSummary("All collections containing works linked to this person (author/narrator/director).")
        .Produces<List<MediaEngine.Contracts.Collections.CollectionDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Person", "id");

        // GET /persons/role-counts — count of persons per role.
        // Excludes Composer (absorbed into Artist/Performer in the UI).
        group.MapGet("/role-counts", async (
            bool? catalog,
            IDisplayProjectionReadService display,
            IPersonAssetScopeReadService personScopeReadService,
            CancellationToken ct) =>
        {
            var assetIds = (await display.LoadWorksAsync(ct)).Select(work => work.AssetId).ToArray();
            return Results.Ok(await personScopeReadService.GetCanonicalRoleCountsAsync(assetIds, ct));
        })
        .WithName("GetPersonRoleCounts")
        .WithSummary("Count of persons per role.")
        .Produces<IReadOnlyDictionary<string, int>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /persons/presence?ids=guid1,guid2,... — media type counts per person.
        group.MapGet("/presence", async (
            string ids,
            IPersonPresenceReadService presenceReadService,
            IPersonAssetScopeReadService personScopeReadService,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var personIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Take(500)
                .ToList();
            var visiblePersonIds = await personScopeReadService.GetCanonicalPersonIdsAsync(
                (await display.LoadWorksAsync(ct)).Select(work => work.AssetId).ToArray(),
                ct);
            var presence = await presenceReadService.GetPresenceAsync(
                personIds.Where(visiblePersonIds.Contains).ToList(), ct);
            return Results.Ok(presence);
        })
        .WithName("GetPersonPresence")
        .WithSummary("Media type counts per person.")
        .Produces<IReadOnlyDictionary<string, Dictionary<string, int>>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        // GET /persons?catalog=true&q=Le%20Guin&role=Author&limit=50
        group.MapGet("/", async (
            bool? catalog,
            string? q,
            string? role,
            string? lane,
            string? sort,
            int? offset,
            int? limit,
            IPersonRepository personRepo,
            IPersonAssetScopeReadService personScopeReadService,
            IDisplayProjectionReadService display,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 100, maxLimit: 500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = catalog == true
                ? await personRepo.ListCatalogPagedAsync(
                    q,
                    role,
                    page.Offset,
                    page.Limit + 1,
                    lane,
                    sort,
                    ct)
                : await personRepo.ListPagedAsync(role, page.Offset, page.Limit + 1, ct);

            // Filter out Composer role and fix groups incorrectly tagged as Narrator
            IEnumerable<MediaEngine.Domain.Entities.Person> filtered = all
                .Select(p =>
                {
                    var cleanedRoles = p.Roles
                        .Where(r => !r.Equals("Composer", StringComparison.OrdinalIgnoreCase))
                        .Where(r => !(p.IsGroup && r.Equals("Narrator", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    p.Roles = cleanedRoles;
                    return p;
                })
                .Where(p => p.Roles.Count > 0); // Exclude persons with no remaining roles
            var visiblePersonIds = await personScopeReadService.GetCanonicalPersonIdsAsync(
                (await display.LoadWorksAsync(ct)).Select(work => work.AssetId).ToArray(),
                ct);
            filtered = filtered.Where(person => visiblePersonIds.Contains(person.Id));

            var results = filtered
                .Select(p => new PersonListItemResponse(
                    id: p.Id,
                    name: p.Name,
                    roles: p.Roles,
                    wikidata_qid: p.WikidataQid,
                    headshot_url: ApiImageUrls.BuildPersonHeadshotUrl(p.Id, p.LocalHeadshotPath, p.HeadshotUrl),
                    // Presence is persisted with the person. Avoid up to 500 synchronous
                    // filesystem stats (especially costly on network storage) in one list request;
                    // the headshot endpoint handles a stale/missing path and remote fallback.
                    has_local_headshot: !string.IsNullOrWhiteSpace(p.LocalHeadshotPath),
                    is_pseudonym: p.IsPseudonym,
                    is_group: p.IsGroup,
                    biography: p.Biography,
                    occupation: p.Occupation))
                .ToList();

            var response = PagedResponse<PersonListItemResponse>.FromPage(results, page);
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.Persons");
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 1000)
            {
                logger.LogWarning(
                    "Large-list read {Operation} took {ElapsedMs} ms with query {Query}, role {Role}, offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    catalog == true ? "persons.catalog" : "persons.list",
                    sw.ElapsedMilliseconds,
                    q,
                    role,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }
            else
            {
                logger.LogDebug(
                    "Large-list read {Operation} took {ElapsedMs} ms with query {Query}, role {Role}, offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    catalog == true ? "persons.catalog" : "persons.list",
                    sw.ElapsedMilliseconds,
                    q,
                    role,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }

            return Results.Ok(response);
        })
        .WithName("ListPersons")
        .WithSummary("List persons, optionally filtered by role.")
        .Produces<PagedResponse<PersonListItemResponse>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        return app;
    }

    private static MediaEngine.Contracts.Collections.CollectionDto FilterCollection(
        MediaEngine.Contracts.Collections.CollectionDto collection,
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

    /// <summary>
    /// <see cref="MediaMimeTypes.GetImageMimeType"/> defaults unrecognized extensions to
    /// <c>application/octet-stream</c>; this call site previously defaulted to
    /// <c>image/jpeg</c>, so that fallback is preserved here explicitly.
    /// </summary>
    private static string GetImageMimeTypeOrJpeg(string path)
    {
        var mime = MediaMimeTypes.GetImageMimeType(path);
        return mime == "application/octet-stream" ? "image/jpeg" : mime;
    }

    private static bool IsLikelyImageFile(string path)
    {
        try
        {
            var header = new byte[32];
            using var stream = File.OpenRead(path);
            var read = stream.Read(header, 0, header.Length);
            return IsLikelyImageBytes(header[..read], null);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyImageBytes(byte[] bytes, string? contentType)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return true;
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47
            && bytes[1] == 0x49
            && bytes[2] == 0x46)
        {
            return true;
        }

        return bytes.Length >= 12
            && bytes[0] == 0x52
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x46
            && bytes[8] == 0x57
            && bytes[9] == 0x45
            && bytes[10] == 0x42
            && bytes[11] == 0x50;
    }

    private static (int Width, int Height)? TryMeasureImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var bitmap = SKBitmap.Decode(path);
            return bitmap is { Width: > 0, Height: > 0 }
                ? (bitmap.Width, bitmap.Height)
                : null;
        }
        catch
        {
            return null;
        }
    }

}

