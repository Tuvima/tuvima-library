using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static partial class ViewEndpoints
{
    public static IEndpointRouteBuilder MapViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/view").WithTags("View")
            .RequireAuthorization(AuthPolicies.Authenticated);

        group.MapGet("/library-capacity", async (string? area, IConfigurationLoader configuration,
            IViewRequestProfileContext identity, IAccountAccessDecisionService accounts, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }
            var normalizedArea = string.IsNullOrWhiteSpace(area) ? "view" : area.Trim().ToLowerInvariant();
            var config = configuration.LoadLibraries();
            var candidates = new List<(string Label, string Path)>();
            if (normalizedArea == "view")
            {
                if (!(await accounts.EvaluateFeatureAsync(authority, AccountFeatureId.View, ct)).IsAllowed)
                {
                    return Results.Forbid();
                }
                var location = config.StorageLocations.FirstOrDefault(value =>
                    string.Equals(value.Id, config.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase));
                if (location is not null)
                {
                    candidates.Add(("View", Path.Combine(location.Path, config.ViewStorage.RelativeRoot)));
                }
            }
            else
            {
                var libraries = normalizedArea == "collections"
                    ? config.Libraries
                    : config.Libraries.Where(library => string.Equals(library.Area, normalizedArea, StringComparison.OrdinalIgnoreCase));
                foreach (var library in libraries)
                {
                    var feature = library.Area.ToLowerInvariant() switch { "read" => AccountFeatureId.Read, "watch" => AccountFeatureId.Watch, "listen" => AccountFeatureId.Listen, _ => AccountFeatureId.View };
                    if (!Guid.TryParse(library.Id, out var libraryId)
                        || !(await accounts.EvaluateFeatureAsync(authority, feature, ct)).IsAllowed
                        || !(await accounts.EvaluateLibraryAsync(authority, libraryId, ct)).IsAllowed)
                    {
                        continue;
                    }
                    candidates.AddRange(library.Sources.Where(source => !string.IsNullOrWhiteSpace(source.Path)).Select(source => (library.Name, source.Path)));
                }
            }

            var locations = candidates.GroupBy(candidate => CapacityRoot(candidate.Path), StringComparer.OrdinalIgnoreCase)
                .Where(grouping => !string.IsNullOrWhiteSpace(grouping.Key))
                .Select(grouping => Capacity(grouping.Select(value => value.Label).Distinct().ToArray(), grouping.Key!))
                .ToList();
            return Results.Ok(new LibraryCapacityDto(normalizedArea, locations, DateTimeOffset.UtcNow));
        }).WithName("GetLibraryCapacity").Produces<LibraryCapacityDto>();

        group.MapGet("/scopes", async (string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewScopeResolver resolver, IViewResourceAuthorizationService authorization,
            ViewStorageService storage, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            if (authority.ActiveProfileId is { } activeProfileId)
            {
                var featureAccess = await authorization.AuthorizePersonalScopeBootstrapAsync(authority, ct);
                if (featureAccess != ViewAccessOutcome.Allowed)
                {
                    return Access(featureAccess);
                }

                var policy = await preferences.GetPolicyAsync(activeProfileId, ct);
                if (policy.ViewEnabled)
                {
                    await storage.EnsurePersonalSpaceAsync(activeProfileId, ct);
                }
            }
            var requested = authority.ActiveProfileId is { } profileId
                ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
                : ParseScope(scope, scopeProfileId);
            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(requested, ViewResourceKind.Search, null,
                    AllowStaleSelectionFallback: string.IsNullOrWhiteSpace(scope)), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            var result = await resolver.ResolveAsync(authority, requested,
                allowStaleSelectionFallback: string.IsNullOrWhiteSpace(scope), ct);
            return result is null ? Missing() : Results.Ok(ToContract(result) with
            {
                // Only a server administrator signed in as a person sees "Other people" in the scope picker.
                CanBrowseOtherPeople = authority.PrincipalKind == PrincipalKind.Human && authority.IsEffectiveAdministrator,
                CanViewDocuments = await MayUseDocumentsAsync(authority, preferences, ct),
                CanFileSharedInbox = ViewSharedInboxAccess.CanFile(authority) && await MayUseDocumentsAsync(authority, preferences, ct),
            });
        }).WithName("GetViewScopes").Produces<ViewScopeResolutionDto>();

        group.MapGet("/preferences", async (IViewRequestProfileContext identity,
            IViewProfileRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct);
            return access.IsAllowed ? Results.Ok(ToContract(await repository.GetPreferencesAsync(profileId, ct)))
                : Access(access.Outcome);
        })
            .WithName("GetViewPreferences").Produces<ViewPreferencesDto>();

        group.MapPut("/preferences", async (ViewPreferencesRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewScopeResolver resolver, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            try
            {
                var access = await authorization.AuthorizeAsync(authority,
                    new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null,
                        ViewResourceAction.Manage), ct);
                if (!access.IsAllowed)
                {
                    return Access(access.Outcome);
                }

                var resolution = await resolver.ResolveAsync(authority,
                    ParseScope(request.Scope, request.ScopeProfileId), false, ct);
                if (resolution is null)
                {
                    return Missing();
                }

                // Looking at another household through "Other people" is never remembered as the last scope, so
                // signing in again never reopens (and records) someone else's photos by itself.
                var remembered = resolution.Scope.IsOtherHousehold
                    ? await repository.GetPreferencesAsync(profileId, ct)
                    : null;
                var value = remembered is not null
                    ? new ViewProfilePreferences(profileId,
                        remembered.LastScopeKind ?? ViewScopeKind.Mine,
                        remembered.LastScopeKind is null ? null : remembered.LastScopeProfileId,
                        request.TimelineDensity, DateTimeOffset.UtcNow, request.ViewerInfoOpen)
                    : new ViewProfilePreferences(profileId,
                        resolution.Scope.Kind,
                        PreferenceScopeProfileId(resolution.Scope.Kind, resolution.Scope.ProfileId),
                        request.TimelineDensity, DateTimeOffset.UtcNow, request.ViewerInfoOpen);
                await repository.SavePreferencesAsync(value, ct);
                return Results.Ok(ToContract(value));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("UpdateViewPreferences").Produces<ViewPreferencesDto>();

        group.MapGet("/assets", async (string? scope, Guid? scopeProfileId,
            int? limit, string? cursor, DateTimeOffset? anchorBefore, string? q, string[]? kind,
            bool? favorite, bool? hidden, Guid? galleryId, string? lifecycle,
            bool? withoutLocation, string? personKey, DateTimeOffset? from, DateTimeOffset? to,
            Guid[]? tag, Guid? correspondent, Guid? docType, DateOnly? docFrom, DateOnly? docTo,
            string? filing, string[]? field, string? docSort,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization,
            IViewQueryOrchestrator queries, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            try
            {
                var preferenceAccess = await AuthorizeDefaultScopePreferenceAsync(
                    authority, scope, authorization, ct);
                if (preferenceAccess is not null)
                {
                    return Access(preferenceAccess.Outcome);
                }

                var requested = authority.ActiveProfileId is { } profileId
                    ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
                    : ParseScope(scope, scopeProfileId);
                var allowedKinds = await KindsWithoutBlockedDocumentsAsync(authority, kind, preferences, ct);
                if (allowedKinds is null)
                {
                    return DocumentsNotAllowed();
                }

                var documentFilter = BuildDocumentFilter(tag, correspondent, docType, docFrom, docTo, filing, field, docSort);
                var result = await queries.QueryAsync(new ViewAssetQueryRequest(
                    requested, PagedRequest.From(0, limit, 120, 500).Limit, cursor, q, allowedKinds,
                    favorite == true, hidden == true, hidden == true, galleryId,
                    ParseLifecycle(lifecycle),
                    AllowStaleSelectionFallback: string.IsNullOrWhiteSpace(scope),
                    AnchorBefore: anchorBefore, WithoutLocation: withoutLocation == true,
                    PersonKey: personKey, From: from, To: to, DocumentFilter: documentFilter), ct);
                return Access(result.Outcome, result.Page is { } timelinePage
                    ? timelinePage with { Items = ViewItemCapabilities.ForViewer(timelinePage.Items, authority.ActiveProfileId) }
                    : null);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Unprocessable(exception.Message); }
        }).WithName("GetViewAssets").Produces<ViewAssetTimelinePageDto>();

        group.MapGet("/assets/timeline-index", async (string? scope, Guid? scopeProfileId,
            string? q, string[]? kind, bool? favorite, bool? hidden, Guid? galleryId, string? lifecycle,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization,
            IViewQueryOrchestrator queries, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            try
            {
                var preferenceAccess = await AuthorizeDefaultScopePreferenceAsync(
                    authority, scope, authorization, ct);
                if (preferenceAccess is not null)
                {
                    return Access(preferenceAccess.Outcome);
                }

                var requested = authority.ActiveProfileId is { } profileId
                    ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
                    : ParseScope(scope, scopeProfileId);
                // The index counts and searches the same rows as the list, so a profile without Documents never
                // gets document-derived counts or document-text matches from it either.
                var allowedKinds = await KindsWithoutBlockedDocumentsAsync(authority, kind, preferences, ct);
                if (allowedKinds is null)
                {
                    return DocumentsNotAllowed();
                }

                var result = await queries.IndexAsync(new ViewAssetQueryRequest(
                    requested, 120, Search: q, MediaKinds: allowedKinds,
                    FavoritesOnly: favorite == true,
                    IncludeHidden: hidden == true,
                    HiddenOnly: hidden == true,
                    GalleryId: galleryId,
                    Lifecycle: ParseLifecycle(lifecycle),
                    AllowStaleSelectionFallback: string.IsNullOrWhiteSpace(scope)), ct);
                return Access(result.Outcome, result.Index);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Unprocessable(exception.Message); }
        }).WithName("GetViewTimelineIndex").Produces<ViewTimelineIndexDto>();

        group.MapGet("/folders", async (string? scope, Guid? scopeProfileId,
            Guid? sourceId, string? path, bool? recursive, string? q, int? offset, int? limit,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ViewFolderService folders,
            CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            try
            {
                var preferenceAccess = await AuthorizeDefaultScopePreferenceAsync(
                    authority, scope, authorization, ct);
                if (preferenceAccess is not null)
                {
                    return Access(preferenceAccess.Outcome);
                }

                var requested = authority.ActiveProfileId is { } profileId
                    ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
                    : ParseScope(scope, scopeProfileId);
                var decision = await authorization.AuthorizeAsync(authority,
                    new ViewResourceRequest(requested, ViewResourceKind.Search, null,
                        AllowStaleSelectionFallback: string.IsNullOrWhiteSpace(scope)), ct);
                if (!decision.IsAllowed || decision.Scope is null)
                {
                    return Access(decision.Outcome);
                }

                var page = PagedRequest.From(offset, limit, 100, 200);
                // A profile without the Documents permission never sees document files, even inside a folder: the
                // service leaves them out of the source counts, folder counts and pages themselves.
                var folderPage = await folders.QueryAsync(authority.ActiveProfileId ?? Guid.Empty,
                    decision.Scope, sourceId, path,
                    recursive == true, q, page.Offset, page.Limit,
                    await MayUseDocumentsAsync(authority, preferences, ct), ct);
                return Results.Ok(folderPage);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("GetViewFolders").Produces<ViewFolderPageDto>();

        group.MapPut("/folders/pin", async (ViewFolderPinRequest request,
            IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization, ViewFolderService folders,
            CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            try
            {
                var requested = ParseScope(request.Scope, request.ScopeProfileId);
                var decision = await authorization.AuthorizeAsync(authority,
                    new ViewResourceRequest(requested, ViewResourceKind.Folder, null,
                        ViewResourceAction.Manage), ct);
                if (!decision.IsAllowed || decision.Scope is null)
                {
                    return Access(decision.Outcome);
                }

                await folders.SetPinAsync(profileId, decision.Scope, request.SourceId,
                    request.RelativePath, request.Pinned, ct);
                return Results.NoContent();
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("SetViewFolderPin")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        group.MapPut("/folders/timeline-policy", async (ViewFolderTimelinePolicyRequest request,
            IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization, ViewFolderService folders,
            CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            try
            {
                var requested = ParseScope(request.Scope, request.ScopeProfileId);
                var decision = await authorization.AuthorizeAsync(authority,
                    new ViewResourceRequest(requested, ViewResourceKind.FolderPolicy, null,
                        ViewResourceAction.Manage), ct);
                if (!decision.IsAllowed || decision.Scope is null)
                {
                    return Access(decision.Outcome);
                }

                await folders.SetTimelinePolicyAsync(profileId,
                    authority.IsEffectiveAdministrator,
                    decision.Scope, request.SourceId, request.RelativePath, request.IncludeInTimeline, ct);
                return Results.NoContent();
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException) { return Missing(); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("SetViewFolderTimelinePolicy")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        group.MapPost("/uploads", async (IFormFile file,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            ViewLibraryService service, ViewStorageService storage,
            [FromServices] PhoneBackupProfileService backup, [FromServices] IViewProfileRepository policies,
            CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Upload, null,
                    ViewResourceAction.Contribute), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            // A paired phone can browse as anyone in the household, so its backups always go to the one person
            // chosen for it, never to whoever it is browsing as. Browser uploads keep using the signed-in person.
            var target = await backup.ResolveUploadTargetAsync(authority, profileId, ct);
            if (target.ProfileId is not { } targetProfileId)
            {
                return ApiErrors.Conflict(PhoneBackupProfileService.ChooseBackupProfileCode,
                    "Choose whose photos this phone backs up in the app.");
            }

            if (targetProfileId != profileId && !(await policies.GetPolicyAsync(targetProfileId, ct)).ViewEnabled)
            {
                return ApiErrors.Conflict("backup_profile_view_off", "View is turned off for the person this phone backs up.");
            }

            // A document is refused before anything is stored or compared, for a person who may not use Documents and
            // for the person whose space it would land in, so no "already there" answer can mention an existing one.
            if (ViewLibraryService.IsDocumentFileName(file.FileName)
                && (!await MayUseDocumentsAsync(authority, policies, ct)
                    || !(await policies.GetPolicyAsync(targetProfileId, ct)).DocumentsEnabled))
            {
                return DocumentsNotAllowed();
            }

            profileId = targetProfileId;

            await storage.EnsurePersonalSpaceAsync(profileId, ct);

            if (file.Length <= 0)
            {
                return ApiErrors.BadRequest("No file was uploaded.");
            }

            try
            {
                await using var input = file.OpenReadStream();
                var result = await service.UploadAsync(profileId, file.FileName, input, ct);
                return Results.Ok(new ViewUploadResponseDto(
                    result.ItemId, result.ItemAdded, result.FilesAdded, result.SourcesAdded));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("UploadViewAsset").Produces<ViewUploadResponseDto>().DisableAntiforgery();

        group.MapGet("/items/{id:guid}", async (Guid id, string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var decision = await AuthorizeItemAsync(id, ViewResourceKind.Asset, ViewResourceAction.Read,
                scope, scopeProfileId, identity, preferences, authorization, ct);
            if (decision.IsAllowed && await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            return decision.IsAllowed && assets.Find(id, ct) is { } item
                ? Results.Ok(ViewItemCapabilities.ForViewer(item, (await identity.ResolveAuthorityAsync(ct)).ActiveProfileId))
                : Access(decision.Outcome);
        }).WithName("GetViewItem").Produces<LocalAssetDto>();

        group.MapGet("/items/{id:guid}/text", async (Guid id, string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var decision = await AuthorizeItemAsync(id, ViewResourceKind.Asset, ViewResourceAction.Read,
                scope, scopeProfileId, identity, preferences, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            if (assets.Find(id, ct) is not { } item)
            {
                return Missing();
            }

            var text = assets.GetDocumentText(id, MaximumDocumentTextCharacters, ct);
            return Results.Ok(new ViewDocumentTextDto(
                id, text?.Text ?? string.Empty, text?.Cut == true,
                item.DocumentInfo ?? new LocalAssetDocumentInfoDto(null, false, false, text is not null)));
        }).WithName("GetViewItemText").Produces<ViewDocumentTextDto>();

        group.MapGet("/items/{id:guid}/content", async (Guid id, string? role, string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, IViewResourceStore resources,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeItemAsync(id, ViewResourceKind.Original, ViewResourceAction.Read,
                scope, scopeProfileId, identity, preferences, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            try
            {
                var file = decision.Scope is null ? null : await resources.ResolveContentAsync(id,
                    string.IsNullOrWhiteSpace(role) ? LocalAssetFileRoles.Primary : role, decision.Scope, ct);
                return file is null || !File.Exists(file.FilePath)
                    ? Missing() : Results.File(file.FilePath, file.MimeType, enableRangeProcessing: true);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("GetViewItemContent").Produces(StatusCodes.Status200OK).RequireRateLimiting("streaming");

        group.MapGet("/items/{id:guid}/thumbnail", async (Guid id, string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, IViewResourceStore resources,
            ViewThumbnailService thumbnails, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var decision = await AuthorizeItemAsync(id, ViewResourceKind.Thumbnail, ViewResourceAction.Read,
                scope, scopeProfileId, identity, preferences, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            var file = decision.Scope is null ? null : await resources.ResolveContentAsync(
                id, LocalAssetFileRoles.Primary, decision.Scope, ct);
            if (file is null || !File.Exists(file.FilePath))
            {
                return Missing();
            }

            var thumbnail = await thumbnails.GetOrCreateAsync(id, file, ct);
            return thumbnail is null ? StreamEndpoints.CreateArtworkPlaceholderResult() : Results.File(thumbnail, "image/jpeg");
        }).WithName("GetViewItemThumbnail").Produces(StatusCodes.Status200OK).RequireRateLimiting("view_images");

        group.MapGet("/items/{id:guid}/preview", async (Guid id, string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, IViewResourceStore resources,
            ViewThumbnailService thumbnails, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeItemAsync(id, ViewResourceKind.Thumbnail, ViewResourceAction.Read,
                scope, scopeProfileId, identity, preferences, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }
            var file = decision.Scope is null ? null : await resources.ResolveContentAsync(
                id, LocalAssetFileRoles.Primary, decision.Scope, ct);
            if (file is null || !File.Exists(file.FilePath))
            {
                return Missing();
            }
            var preview = await thumbnails.GetOrCreatePreviewAsync(id, file, ct);
            return preview is null ? StreamEndpoints.CreateArtworkPlaceholderResult() : Results.File(preview, "image/jpeg");
        }).WithName("GetViewItemPreview").Produces(StatusCodes.Status200OK).RequireRateLimiting("view_images");

        MapFlag(group, "favorite", (repo, id, value, ct) => repo.SetFlagsAsync(id, value, null, ct));
        MapFlag(group, "hidden", (repo, id, value, ct) => repo.SetFlagsAsync(id, null, value, ct));
        MapLifecycle(group, "archive", LocalAssetLifecycleState.Archived);
        MapLifecycle(group, "trash", LocalAssetLifecycleState.Trashed);
        MapLifecycle(group, "restore", LocalAssetLifecycleState.Active);

        group.MapPut("/items/{id:guid}/description", async (Guid id,
            UpdateLocalAssetDescriptionRequest request, IViewRequestProfileContext identity,
            IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }
            try
            {
                if (!await assets.UpdateDescriptionAsync(id, request.Description, ct))
                {
                    return Missing();
                }
                return Results.Ok(assets.Find(id, ct));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("UpdateViewItemDescription").Produces<LocalAssetDto>();

        group.MapPut("/items/{id:guid}/captured-at", async (Guid id,
            UpdateLocalAssetCapturedAtRequest request, IViewRequestProfileContext identity,
            IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }
            try
            {
                if (!await assets.UpdateCapturedAtAsync(id, request.CapturedAt, request.ResetToEmbedded, ct))
                {
                    return Missing();
                }
                return Results.Ok(assets.Find(id, ct));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("UpdateViewItemCapturedAt").Produces<LocalAssetDto>();

        MapDocumentOrganisation(group);
        MapDocumentFiling(group);
        MapDocumentBulk(group);
        MapTrash(group);

        group.MapPut("/items/{id:guid}/people", async (Guid id,
            UpdateLocalAssetPeopleRequest request, IViewRequestProfileContext identity,
            IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets,
            ViewDiscoveryService discovery, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }
            try
            {
                var selected = (request.People ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var person in selected)
                {
                    var candidates = await discovery.GetPeopleAsync(
                        new ViewDiscoveryRequest(ViewScopeRequest.Mine, 100, person), ct);
                    if (candidates.Page?.Items.Any(candidate =>
                            string.Equals(candidate.DisplayName, person, StringComparison.OrdinalIgnoreCase)) != true)
                    {
                        return ApiErrors.BadRequest($"'{person}' is not an identified person in this View library.");
                    }
                }
                await assets.ReplacePeopleAsync(id, selected, ct);
                return Results.Ok(assets.Find(id, ct));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (InvalidOperationException) { return Missing(); }
        }).WithName("UpdateViewItemPeople").Produces<LocalAssetDto>();

        group.MapPut("/items/{id:guid}/location", async (Guid id,
            UpdateLocalAssetLocationRequest request, IViewRequestProfileContext identity,
            IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }
            try
            {
                var update = new LocalAssetLocationUpdate(request.Latitude, request.Longitude,
                    request.Name, request.City, request.Region, request.Country,
                    request.CountryCode, request.ResetToEmbedded);
                if (!await assets.UpdateLocationAsync(id, update, ct))
                {
                    return Missing();
                }
                return Results.Ok(assets.Find(id, ct));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("UpdateViewItemLocation").Produces<LocalAssetDto>();

        group.MapPost("/shared/contributions/preview", async (ViewSharedContributionPreviewRequest request,
            IViewRequestProfileContext identity, ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.PreviewAsync(authority, request, ct)); }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Unprocessable(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("PreviewViewSharedContribution").Produces<ViewSharedContributionPreviewDto>();

        group.MapPost("/shared/contributions", async (ViewSharedContributionSubmitRequest request,
            IViewRequestProfileContext identity, ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.SubmitAsync(authority, request, ct)); }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Conflict(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("SubmitViewSharedContribution").Produces<ViewSharedContributionDto>();

        group.MapGet("/shared/contributions", async (string? mode, string? status, int? offset, int? limit,
            IViewRequestProfileContext identity, ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            var page = PagedRequest.From(offset, limit, defaultLimit: 50, maxLimit: 100);
            try
            {
                return Results.Ok(await contributions.ListAsync(authority, mode ?? "mine", status,
                page.Offset, page.Limit, ct));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
        }).WithName("ListViewSharedContributions").Produces<ViewSharedContributionPageDto>();

        group.MapGet("/shared/contributions/{id:guid}", async (Guid id,
            IViewRequestProfileContext identity, ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.GetRequiredAsync(authority, id, false, ct)); }
            catch (UnauthorizedAccessException) { return Missing(); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("GetViewSharedContribution").Produces<ViewSharedContributionDto>();

        group.MapPost("/shared/contributions/{id:guid}/cancel", async (Guid id,
            ViewSharedContributionRevisionRequest request, IViewRequestProfileContext identity,
            ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.CancelAsync(authority, id, request.ExpectedRevision, ct)); }
            catch (UnauthorizedAccessException) { return Missing(); }
            catch (InvalidOperationException exception) { return ApiErrors.Conflict(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("CancelViewSharedContribution").Produces<ViewSharedContributionDto>();

        group.MapPost("/shared/contributions/{id:guid}/decision", async (Guid id,
            ViewSharedContributionDecisionRequest request, IViewRequestProfileContext identity,
            ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.DecideAsync(authority, id, request, ct)); }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Conflict(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("DecideViewSharedContribution").Produces<ViewSharedContributionDto>();

        group.MapPost("/shared/contributions/{id:guid}/retry", async (Guid id,
            ViewSharedContributionRevisionRequest request, IViewRequestProfileContext identity,
            ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.RetryAsync(authority, id, request.ExpectedRevision, ct)); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Conflict(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("RetryViewSharedContribution").Produces<ViewSharedContributionDto>();

        group.MapPost("/shared/items/direct", async (ViewSharedDirectAddRequest request,
            IViewRequestProfileContext identity, ViewSharedContributionService contributions, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return Unauthenticated();
            }

            try { return Results.Ok(await contributions.AddDirectAsync(authority, request, ct)); }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (UnauthorizedAccessException exception) { return ApiErrors.Forbidden(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Conflict(exception.Message); }
            catch (KeyNotFoundException) { return Missing(); }
        }).WithName("AddViewItemsDirectlyToSharedLibrary").Produces<ViewSharedContributionDto>();

        MapGalleries(group);

        // "Other people": the households a server administrator can open, read-only. Opening one is recorded.
        group.MapGet("/other-people", async (IViewRequestProfileContext identity,
            IViewOtherPeopleService otherPeople, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            // Server administrators only, signed in as a person: not household administrators, members or apps.
            return authority.PrincipalKind != PrincipalKind.Human || !authority.IsEffectiveAdministrator
                ? ApiErrors.Forbidden("Only a server administrator can see other households.")
                : Results.Ok(await otherPeople.ListAsync(authority, ct));
        }).WithName("GetViewOtherPeople")
            .WithSummary("List other households and their people for a server administrator (read-only browsing).")
            .RequireEffectiveAdministrator()
            .Produces<ViewOtherPeopleDto>();

        // "Who viewed your photos": read-only audit trail of server administrators opening the CALLER's own photos (plus, for
        // the household's own administrator, its Shared Library). Never another person's personal-space views.
        group.MapGet("/photo-views", async (int? offset, int? limit, IViewRequestProfileContext identity,
            IViewOtherPeopleService otherPeople, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.HasHumanContext)
            {
                return Unauthenticated();
            }

            // A person signed in as themselves only: a paired app's device token (DelegatedUserClient) is refused.
            if (authority.PrincipalKind != PrincipalKind.Human)
            {
                return ApiErrors.Forbidden("Only a signed-in person can see who viewed their photos.");
            }

            return Results.Ok(await otherPeople.ListViewsAsync(authority,
                PagedRequest.From(offset, limit, defaultLimit: 25, maxLimit: 100), ct));
        }).WithName("GetViewPhotoViews")
            .WithSummary("Page who opened the caller's own photos (and the household's Shared Library for its administrator), newest first.")
            .Produces<ViewPhotoViewsPageDto>();

        group.MapGet("/share-targets", async (IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization,
            IViewProfileRepository policies, IViewScopeStore scopes, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, null), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            var targets = await GetGalleryShareTargetsAsync(
                profileId, policies, scopes, ct).ConfigureAwait(false);
            return targets is null ? Missing() : Results.Ok(targets);
        }).WithName("GetViewGalleryShareTargets")
            .WithSummary("List enabled profiles eligible to receive an individual Gallery share.")
            .Produces<IReadOnlyList<ViewGalleryShareTargetDto>>();

        group.MapGet("/admin/profiles/{profileId:guid}/sources", async (
            Guid profileId, IProfileService profiles, IViewPersonalSpaceRepository spaces,
            ViewStorageService storage,
            CancellationToken ct) =>
        {
            if (await profiles.GetProfileAsync(profileId, ct) is null)
            {
                return ApiErrors.NotFound($"Profile '{profileId}' not found.");
            }

            var space = await spaces.GetByOwnerAsync(profileId, ct);
            if (space is null)
            {
                return Results.Ok(new ViewPersonalSpaceAdminReviewDto(profileId, null, [], []));
            }

            var sources = await spaces.GetSourcesAsync(space.Id, ct);
            var devices = await spaces.GetDevicesAsync(space.Id, ct);
            return Results.Ok(ToAdminReview(profileId, space, sources, devices, storage));
        })
        .WithName("GetViewProfileSources")
        .WithSummary("Review the persisted Personal Space sources and devices for one profile.")
        .Produces<ViewPersonalSpaceAdminReviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/admin/profiles/{profileId:guid}/sources", async (
            Guid profileId, CreateViewSourceRequest request, IProfileService profiles,
            ViewStorageService storage, ViewSourceIndexingHostedService indexing,
            ViewLibraryService viewLibrary, IViewProfileRepository policies, MediaEngine.Api.Services.Settings.ServerFolderBrowserService folders, HttpContext http, CancellationToken ct) =>
        {
            if (await profiles.GetProfileAsync(profileId, ct) is null)
            {
                return ApiErrors.NotFound($"Profile '{profileId}' not found.");
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiErrors.BadRequest("A source name is required.");
            }

            if (!(await policies.GetPolicyAsync(profileId, ct)).ViewEnabled)
            {
                return ApiErrors.BadRequest("Enable View for this profile before adding a source.");
            }

            if (request.StorageMode is not ("linked" or "managed"))
            {
                return ApiErrors.BadRequest("Choose managed or linked storage.");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.Path))
                {
                    var validation = folders.Validate(new MediaEngine.Contracts.Settings.ValidateServerFolderRequest
                    {
                        ManualPath = request.Path,
                        SelectionMode = MediaEngine.Contracts.Settings.ServerFolderSelectionModes.PersonalSpaceExisting,
                    }, await http.HiddenVaultFolderRootsAsync(ct));
                    if (!validation.CanSelect)
                    {
                        return ApiErrors.BadRequest(validation.Issues.First(x => x.Severity == "error").Message);
                    }
                }
                var space = await storage.EnsurePersonalSpaceAsync(profileId, ct);
                var source = string.Equals(request.StorageMode, "linked", StringComparison.OrdinalIgnoreCase)
                    ? await storage.AddLinkedSourceAsync(space, request.Name, request.Path ?? string.Empty,
                        request.IncludeSubdirectories, ct)
                        : string.IsNullOrWhiteSpace(request.Path)
                        ? await storage.EnsureManagedSourceAsync(space, request.Name, ViewSourceType.Folder,
                            $"managed:{Guid.NewGuid():N}", ct)
                        : await storage.ImportFolderAsync(space, request.Name, request.Path, ct);
                if (source.IncludeInTimeline != request.IncludeInTimeline)
                {
                    source = await storage.UpdateSourceAsync(space, source with { IncludeInTimeline = request.IncludeInTimeline }, ct);
                }

                await indexing.RefreshSourcesAsync(ct);
                // The hosted worker owns reconciliation and its cancellation lifetime.
                indexing.RequestReconcile(space.LibraryId);
                return Results.Ok(ToAdminSource(space, source, storage));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                               or IOException or UnauthorizedAccessException or NotSupportedException or MediaEngine.Api.Services.Settings.ServerFolderAccessException)
            {
                return ApiErrors.Unprocessable(exception.Message);
            }
        }).WithName("CreateViewProfileSource").Produces<ViewSourceAdminDto>().RequireEffectiveAdministrator();

        group.MapPut("/admin/profiles/{profileId:guid}/sources/{sourceId:guid}", async (
            Guid profileId, Guid sourceId, UpdateViewSourceRequest request,
            IViewPersonalSpaceRepository spaces, ViewStorageService storage,
            ViewSourceIndexingHostedService indexing, CancellationToken ct) =>
        {
            var space = await spaces.GetByOwnerAsync(profileId, ct);
            var source = space is null ? null : (await spaces.GetSourcesAsync(space.Id, ct))
                .FirstOrDefault(candidate => candidate.Id == sourceId);
            if (space is null || source is null)
            {
                return Missing();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiErrors.BadRequest("A source name is required.");
            }

            source = await spaces.UpsertSourceAsync(source with
            { Name = request.Name.Trim(), Enabled = request.Enabled, IncludeInTimeline = request.IncludeInTimeline }, ct);
            await indexing.RefreshSourcesAsync(ct);
            return Results.Ok(ToAdminSource(space, source, storage));
        }).WithName("UpdateViewProfileSource").Produces<ViewSourceAdminDto>().RequireEffectiveAdministrator();

        group.MapDelete("/admin/profiles/{profileId:guid}/sources/{sourceId:guid}", async (
            Guid profileId, Guid sourceId, IViewPersonalSpaceRepository spaces,
            ViewSourceIndexingHostedService indexing, CancellationToken ct) =>
        {
            var space = await spaces.GetByOwnerAsync(profileId, ct);
            if (space is null)
            {
                return Missing();
            }

            var source = (await spaces.GetSourcesAsync(space.Id, ct))
                .FirstOrDefault(candidate => candidate.Id == sourceId);
            if (source is null)
            {
                return Missing();
            }

            if (string.Equals(source.SourceKey, "builtin:browser-uploads", StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.Conflict("The built-in browser upload source cannot be detached.");
            }

            if (!await spaces.DeleteSourceAsync(space.Id, sourceId, ct))
            {
                return Missing();
            }

            await indexing.RefreshSourcesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteViewProfileSource").Produces(StatusCodes.Status204NoContent).RequireEffectiveAdministrator();

        group.MapPost("/admin/profiles/{profileId:guid}/reconcile", async (
            Guid profileId, IViewPersonalSpaceRepository spaces,
            ViewLibraryService service, CancellationToken ct) =>
        {
            var space = await spaces.GetByOwnerAsync(profileId, ct);
            return space is null || await service.ScanAsync(space.LibraryId, ct) is not { } result
                ? Missing()
                : Results.Ok(result);
        }).WithName("ReconcileViewPersonalSpace").Produces<LocalAssetScanResultDto>().RequireEffectiveAdministrator();

        group.MapGet("/admin/shared/sources", async (
            ViewSharedSourceService sources, ViewStorageService storage, CancellationToken ct) =>
            Results.Ok((await sources.GetSourcesAsync(ct))
                .Select(source => ToAdminSource(source, storage)).ToList()))
        .WithName("GetViewSharedSources")
        .WithSummary("Review sources owned by the server Shared Library.")
        .Produces<IReadOnlyList<ViewSourceAdminDto>>()
        .RequireEffectiveAdministrator();

        group.MapPost("/admin/shared/sources", async (
            CreateViewSourceRequest request, ViewSharedSourceService sources,
            ViewStorageService storage, ViewSourceIndexingHostedService indexing,
            MediaEngine.Api.Services.Settings.ServerFolderBrowserService folders,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiErrors.BadRequest("A source name is required.");
            }

            if (request.StorageMode is not ("linked" or "managed"))
            {
                return ApiErrors.BadRequest("Choose managed or linked storage.");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.Path))
                {
                    var validation = folders.Validate(new MediaEngine.Contracts.Settings.ValidateServerFolderRequest
                    {
                        ManualPath = request.Path,
                        SelectionMode = MediaEngine.Contracts.Settings.ServerFolderSelectionModes.PersonalSpaceExisting,
                    }, await http.HiddenVaultFolderRootsAsync(ct));
                    if (!validation.CanSelect)
                    {
                        return ApiErrors.BadRequest(validation.Issues.First(issue => issue.Severity == "error").Message);
                    }
                }
                var source = await sources.CreateAsync(request, ct);
                await indexing.RefreshSourcesAsync(ct);
                indexing.RequestReconcile(source.LibraryId);
                return Results.Ok(ToAdminSource(source, storage));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                               or IOException or UnauthorizedAccessException
                                               or DirectoryNotFoundException or NotSupportedException
                                               or MediaEngine.Api.Services.Settings.ServerFolderAccessException)
            {
                return ApiErrors.Unprocessable(exception.Message);
            }
        })
        .WithName("CreateViewSharedSource")
        .Produces<ViewSourceAdminDto>()
        .RequireEffectiveAdministrator();

        group.MapPut("/admin/shared/sources/{sourceId:guid}", async (
            Guid sourceId, UpdateViewSourceRequest request, ViewSharedSourceService sources,
            ViewStorageService storage, ViewSourceIndexingHostedService indexing,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiErrors.BadRequest("A source name is required.");
            }

            var source = await sources.UpdateAsync(sourceId, request, ct);
            if (source is null)
            {
                return Missing();
            }

            await indexing.RefreshSourcesAsync(ct);
            return Results.Ok(ToAdminSource(source, storage));
        })
        .WithName("UpdateViewSharedSource")
        .Produces<ViewSourceAdminDto>()
        .RequireEffectiveAdministrator();

        group.MapDelete("/admin/shared/sources/{sourceId:guid}", async (
            Guid sourceId, ViewSharedSourceService sources,
            ViewSourceIndexingHostedService indexing, CancellationToken ct) =>
        {
            var outcome = await sources.DeleteAsync(sourceId, ct);
            if (outcome == ViewSharedSourceDeleteOutcome.NotFound)
            {
                return Missing();
            }

            if (outcome == ViewSharedSourceDeleteOutcome.Protected)
            {
                return ApiErrors.Conflict("Built-in Shared Library sources cannot be detached.");
            }

            if (outcome == ViewSharedSourceDeleteOutcome.HasIndexedFiles)
            {
                return ApiErrors.Conflict("Detach requires an empty Shared source; move or remove its indexed files first.");
            }

            await indexing.RefreshSourcesAsync(ct);
            return Results.NoContent();
        })
        .WithName("DeleteViewSharedSource")
        .Produces(StatusCodes.Status204NoContent)
        .RequireEffectiveAdministrator();

        group.MapPost("/admin/shared/reconcile", async (
            IViewSharedLibraryRepository shared, ViewLibraryService service, CancellationToken ct) =>
        {
            var library = await shared.GetAsync(ct);
            return await service.ScanAsync(library.LibraryId, ct) is { } result
                ? Results.Ok(result)
                : Missing();
        })
        .WithName("ReconcileViewSharedLibrary")
        .Produces<LocalAssetScanResultDto>()
        .RequireEffectiveAdministrator();

        return app;
    }

    private static string? CapacityRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static LibraryCapacityLocationDto Capacity(IReadOnlyList<string> labels, string root)
    {
        var label = string.Join(" + ", labels);
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady
                ? new(label, drive.TotalSize, drive.AvailableFreeSpace, true)
                : new(label, null, null, false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(label, null, null, false);
        }
    }

    private static void MapGalleries(RouteGroupBuilder group)
    {
        group.MapGet("/galleries", async (IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization, IViewProfileRepository preferences,
            IViewGalleryRepository repository, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, null), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            // A saved Documents view is a shelf of documents, so a profile that may not use Documents never sees one.
            // Every other Gallery counts only the items that profile may see, and never names a document as its cover.
            var documents = await MayUseDocumentsAsync(authority, preferences, ct);
            return Results.Ok(new ViewGalleryListResponse(
                (await repository.GetOwnedAsync(profileId, documents, ct)).Where(gallery => documents || !IsDocumentView(gallery)).Select(gallery => ToContract(gallery, documents, assets)).ToList(),
                (await repository.GetSharedWithAsync(profileId, documents, ct)).Where(gallery => documents || !IsDocumentView(gallery)).Select(gallery => ToContract(gallery, documents, assets)).ToList()));
        }).WithName("GetViewGalleries").Produces<ViewGalleryListResponse>();

        group.MapPost("/galleries", async (ViewGalleryRequest request,
            IViewRequestProfileContext identity, IViewPersonalSpaceRepository spaces,
            IViewResourceAuthorizationService authorization, IViewProfileRepository preferences,
            IViewGalleryRepository repository, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var documents = await MayUseDocumentsAsync(authority, preferences, ct);
            if (!documents && request.Kind == ViewGalleryKind.Smart && ViewSmartGalleryRules.IsDocumentView(request.SmartRuleJson))
            {
                return DocumentsNotAllowed();
            }

            if (!documents && DocumentCoverRefusal(assets, request.CoverItemId, profileId) is { } coverRefusal)
            {
                return coverRefusal;
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, null,
                    ViewResourceAction.Manage), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            var space = await spaces.GetByOwnerAsync(profileId, ct);
            if (space is null)
            {
                return Missing();
            }

            try
            {
                var gallery = await repository.CreateAsync(new CreateViewGalleryCommand(
                    profileId, space.Id, request.Name, request.Kind,
                    request.Description, request.SmartRuleJson, request.CoverItemId,
                    request.SortOrder, request.SoundtrackPlaylistId), ct);
                return Results.Created($"/view/galleries/{gallery.Id:D}", ToContract(gallery, documents, assets));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("CreateViewGallery").Produces<ViewGalleryDto>(StatusCodes.Status201Created);

        group.MapGet("/galleries/{id:guid}", async (Guid id,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Read, identity, authorization, ct);
            if (decision.IsAllowed)
            {
                var documents = await MayUseDocumentsAsync(await identity.ResolveAuthorityAsync(ct), preferences, ct);
                if (await repository.GetAsync(id, documents, ct) is { } gallery)
                {
                    return IsDocumentView(gallery) && !documents
                        ? DocumentsNotAllowed()
                        : Results.Ok(ToContract(gallery, documents, assets));
                }
            }

            return Access(decision.Outcome);
        }).WithName("GetViewGallery").Produces<ViewGalleryDto>();

        group.MapPut("/galleries/{id:guid}", async (Guid id, ViewGalleryRequest request,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Manage, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            var authority = await identity.ResolveAuthorityAsync(ct);
            var documents = await MayUseDocumentsAsync(authority, preferences, ct);
            var existing = await repository.GetAsync(id, documents, ct);
            var coverItemId = request.CoverItemId;
            if (!documents)
            {
                if (request.Kind == ViewGalleryKind.Smart && ViewSmartGalleryRules.IsDocumentView(request.SmartRuleJson)
                    || existing is not null && IsDocumentView(existing))
                {
                    return DocumentsNotAllowed();
                }

                if (DocumentCoverRefusal(assets, coverItemId, authority.ActiveProfileId ?? Guid.Empty) is { } coverRefusal)
                {
                    return coverRefusal;
                }

                // This profile is never told about a document cover (it reads as none), so saving the Gallery with
                // "no cover" must not wipe the one the owner chose.
                if (coverItemId is null && existing?.CoverItemId is { } stored && assets.IsDocument(stored))
                {
                    coverItemId = stored;
                }
            }

            try
            {
                var gallery = await repository.UpdateAsync(new UpdateViewGalleryCommand(
                    id, request.Name, request.Description, request.Kind,
                    request.SmartRuleJson, coverItemId,
                    request.SortOrder, request.SoundtrackPlaylistId), ct);
                // The answer counts only what this profile may see, like every other read of the Gallery.
                return gallery is null ? Missing()
                    : Results.Ok(ToContract(documents ? gallery : await repository.GetAsync(id, documents, ct) ?? gallery,
                        documents, assets));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("UpdateViewGallery").Produces<ViewGalleryDto>();

        group.MapDelete("/galleries/{id:guid}", async (Guid id,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Manage, identity, authorization, ct);
            return !decision.IsAllowed ? Access(decision.Outcome)
                : await repository.DeleteAsync(id, ct) ? Results.NoContent() : Missing();
        }).WithName("DeleteViewGallery")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        // The item routes below leave documents out for a profile that may not use Documents (the page, the add,
        // the remove and the reorder alike), so a Gallery never tells that profile a document is in it.
        group.MapGet("/galleries/{id:guid}/items", async (Guid id,
            int? afterPosition, Guid? afterItemId, int? limit,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Read, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            var documents = await MayUseDocumentsAsync(await identity.ResolveAuthorityAsync(ct), preferences, ct);
            return Results.Ok(ToContract(await repository.GetItemsAsync(id, afterPosition, afterItemId,
                PagedRequest.From(0, limit, 100, 500).Limit, documents, ct)));
        }).WithName("GetViewGalleryItems").Produces<ViewGalleryItemPageDto>();

        group.MapPost("/galleries/{id:guid}/items", async (Guid id, ViewGalleryItemsRequest request,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Contribute, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            var authority = await identity.ResolveAuthorityAsync(ct);
            var documents = await MayUseDocumentsAsync(authority, preferences, ct);
            try { return Results.Ok(ToContract(await repository.AddItemsAsync(id, request.ItemIds, documents, ct))); }
            catch (UnauthorizedAccessException)
            {
                // Only the Gallery's owner is told why their own document is refused. Anyone else with Contribute or
                // Manage gets the answer they would get for an id that is not in the owner's space, so the answer
                // never confirms that an id is a document.
                return await repository.GetAsync(id, documents, ct) is { } gallery && gallery.OwnerProfileId == authority.ActiveProfileId
                    ? DocumentsNotAllowed()
                    : ApiErrors.BadRequest(UnknownGalleryItemMessage);
            }
            catch (InvalidOperationException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("AddViewGalleryItems").Produces<AddViewGalleryItemsResponseDto>();

        group.MapDelete("/galleries/{id:guid}/items", async (Guid id, [FromBody] ViewGalleryItemsRequest request,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Contribute, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            var documents = await MayUseDocumentsAsync(await identity.ResolveAuthorityAsync(ct), preferences, ct);
            return Results.Ok(new ViewItemsRemovedResponse(
                await repository.RemoveItemsAsync(id, request.ItemIds, documents, ct)));
        }).WithName("RemoveViewGalleryItems").Produces<ViewItemsRemovedResponse>();

        group.MapPut("/galleries/{id:guid}/items/{itemId:guid}/position", async (
            Guid id, Guid itemId, ViewGalleryPositionRequest request,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository preferences, IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Contribute, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            var documents = await MayUseDocumentsAsync(await identity.ResolveAuthorityAsync(ct), preferences, ct);
            return await repository.SetItemPositionAsync(id, itemId, request.Position, documents, ct)
                ? Results.NoContent() : Missing();
        }).WithName("ReorderViewGalleryItem")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        group.MapGet("/galleries/{id:guid}/shares", async (Guid id,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewGalleryRepository repository, CancellationToken ct) =>
        {
            var decision = await GalleryAccessAsync(id, ViewResourceAction.Manage, identity, authorization, ct);
            return !decision.IsAllowed ? Access(decision.Outcome)
                : Results.Ok((await repository.GetSharesAsync(id, ct)).Select(ToContract).ToList());
        }).WithName("GetViewGalleryShares").Produces<IReadOnlyList<ViewGalleryShareDto>>();

        group.MapPut("/galleries/{id:guid}/shares", async (Guid id, ViewGallerySharesRequest request,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewProfileRepository profiles, IViewScopeStore scopes,
            IViewGalleryRepository repository, CancellationToken ct) =>
        {
            if ((await identity.ResolveAuthorityAsync(ct)).ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var decision = await GalleryAccessAsync(id, ViewResourceAction.Manage, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            // A saved Documents view stays private: its rule can hold search words and it lists private documents.
            if (await repository.GetAsync(id, ct: ct) is { } shared && IsDocumentView(shared))
            {
                return ApiErrors.Forbidden("A saved Documents view is private and cannot be shared.");
            }

            var targets = await GetGalleryShareTargetsAsync(
                profileId, profiles, scopes, ct).ConfigureAwait(false);
            if (targets is null)
            {
                return Missing();
            }

            if (!TryValidateGalleryShares(request.Shares, profileId, targets, out var shares))
            {
                return ApiErrors.BadRequest("One or more selected profiles cannot receive Gallery shares.");
            }

            await repository.ReplaceSharesAsync(id,
                shares, ct);
            return Results.NoContent();
        }).WithName("UpdateViewGalleryShares")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);
    }

    internal static Guid? PreferenceScopeProfileId(ViewScopeKind kind, Guid? profileId) =>
        kind == ViewScopeKind.Profile ? profileId : null;

    internal static async Task<IReadOnlyList<ViewGalleryShareTargetDto>?> GetGalleryShareTargetsAsync(
        Guid callerProfileId,
        IViewProfileRepository policies,
        IViewScopeStore scopes,
        CancellationToken ct = default)
    {
        var callerPolicy = await policies.GetPolicyAsync(callerProfileId, ct).ConfigureAwait(false);
        if (!callerPolicy.ViewEnabled || !callerPolicy.ShareGalleries)
        {
            return null;
        }

        return (await scopes.GetProfilesAsync(ct).ConfigureAwait(false))
            .Where(profile => profile.Policy.ProfileId != callerProfileId
                && profile.Policy.ViewEnabled
                && profile.PersonalSpace is not null)
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Policy.ProfileId)
            .Select(profile => new ViewGalleryShareTargetDto(
                profile.Policy.ProfileId,
                profile.DisplayName,
                profile.AvatarColor,
                profile.AvatarUrl))
            .ToList();
    }

    internal static bool TryValidateGalleryShares(
        IReadOnlyCollection<ViewGalleryShareRequest>? requested,
        Guid callerProfileId,
        IReadOnlyCollection<ViewGalleryShareTargetDto> targets,
        out IReadOnlyCollection<(Guid ProfileId, ViewGallerySharePermission Permission)> shares)
    {
        shares = [];
        if (requested is null)
        {
            return false;
        }

        var eligible = targets.Select(target => target.ProfileId).ToHashSet();
        if (requested.Any(share => share.ProfileId == Guid.Empty
                || share.ProfileId == callerProfileId
                || !eligible.Contains(share.ProfileId)
                || !Enum.IsDefined(share.Permission))
            || requested.Select(share => share.ProfileId).Distinct().Count() != requested.Count)
        {
            return false;
        }

        shares = requested.Select(share => (share.ProfileId, share.Permission)).ToList();
        return true;
    }

    private static void MapFlag(RouteGroupBuilder group, string name,
        Func<ILocalAssetRepository, Guid, bool, CancellationToken, Task<bool>> operation) =>
        group.MapPut($"/items/{{id:guid}}/{name}", async (Guid id, SetLocalAssetFlagRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            // Favouriting or hiding a document would change what the Documents page shows, so it needs Documents.
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            return await operation(assets, id, request.Value, ct) ? Results.NoContent() : Missing();
        }).WithName($"SetViewItem{char.ToUpperInvariant(name[0])}{name[1..]}")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

    private static void MapLifecycle(RouteGroupBuilder group, string name, LocalAssetLifecycleState state) =>
        group.MapPost($"/items/{{id:guid}}/{name}", async (Guid id,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            // Archive, trash and restore move a document in or out of the Documents page, so they need Documents.
            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return DocumentsNotAllowed();
            }

            // An item with a file in a linked (read-only) folder cannot go to Trash. Restoring one that is already
            // there, and archiving, stay available.
            if (state == LocalAssetLifecycleState.Trashed && assets.GetReadOnlyItemIds([id], ct).Contains(id))
            {
                return ApiErrors.Conflict(ViewDocumentBulkReasonCodes.ReadOnlySource, ReadOnlyTrashMessage);
            }

            return await assets.SetLifecycleStateAsync(id, state, ct) ? Results.NoContent() : Missing();
        }).WithName($"{char.ToUpperInvariant(name[0])}{name[1..]}ViewItem")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

    /// <summary>What Move to Trash answers (409) for an item in a read-only folder.</summary>
    internal const string ReadOnlyTrashMessage = "This item is in a read-only folder, so it can't be moved to Trash.";

    internal static bool IsDocumentView(ViewGallery gallery) =>
        gallery.Kind == ViewGalleryKind.Smart && ViewSmartGalleryRules.IsDocumentView(gallery.SmartRuleJson);

    /// <summary>The longest stretch of document text the viewer text panel is sent.</summary>
    private const int MaximumDocumentTextCharacters = 200_000;

    /// <summary>A profile with no saved policy gets the default: Documents on, except for child profiles.</summary>
    private static async Task<bool> MayUseDocumentsAsync(
        RequestAuthority authority, IViewProfileRepository preferences, CancellationToken ct) =>
        authority.ActiveProfileId is not { } profileId
        || (await preferences.GetPolicyAsync(profileId, ct)).DocumentsEnabled;

    private static async Task<bool> DocumentsBlockedAsync(Guid id, ILocalAssetRepository assets,
        IViewRequestProfileContext identity, IViewProfileRepository preferences, CancellationToken ct) =>
        assets.IsDocument(id)
        && !await MayUseDocumentsAsync(await identity.ResolveAuthorityAsync(ct), preferences, ct);

    private static IResult DocumentsNotAllowed() =>
        ApiErrors.Forbidden("Documents are not available for this profile.");

    /// <summary>
    /// A profile that may not use Documents never gets document files back from a search or listing. Asking only for
    /// documents is refused (null); asking for everything is narrowed to the other kinds.
    /// </summary>
    private static async Task<string[]?> KindsWithoutBlockedDocumentsAsync(
        RequestAuthority authority, string[]? kinds, IViewProfileRepository preferences, CancellationToken ct)
    {
        if (await MayUseDocumentsAsync(authority, preferences, ct))
        {
            return kinds;
        }

        var requested = kinds?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        if (requested.Length == 0)
        {
            return [LocalAssetMediaKinds.Image, LocalAssetMediaKinds.Video, LocalAssetMediaKinds.Audio, LocalAssetMediaKinds.Other];
        }

        var remaining = requested.Where(value => !string.Equals(value.Trim(), LocalAssetMediaKinds.Document, StringComparison.OrdinalIgnoreCase)).ToArray();
        return remaining.Length == 0 ? null : remaining;
    }

    private static async Task<ViewAccessDecision> AuthorizeItemAsync(Guid id,
        ViewResourceKind kind, ViewResourceAction action, string? scope, Guid? scopeProfileId,
        IViewRequestProfileContext identity, IViewProfileRepository preferences,
        IViewResourceAuthorizationService authorization, CancellationToken ct)
    {
        var authority = await identity.ResolveAuthorityAsync(ct);
        if (!authority.IsAuthenticated)
        {
            return ViewAccessDecision.Unauthenticated();
        }

        var preferenceAccess = await AuthorizeDefaultScopePreferenceAsync(
            authority, scope, authorization, ct);
        if (preferenceAccess is not null)
        {
            return preferenceAccess;
        }

        var selected = authority.ActiveProfileId is { } profileId
            ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
            : ParseScope(scope, scopeProfileId);
        return await authorization.AuthorizeAsync(authority, new ViewResourceRequest(
            selected, kind, id, action, string.IsNullOrWhiteSpace(scope)), ct);
    }

    private static async Task<ViewAccessDecision> GalleryAccessAsync(Guid id, ViewResourceAction action,
        IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        await authorization.AuthorizeAsync(await identity.ResolveAuthorityAsync(ct),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, id, action), ct);

    private static async Task<ViewAccessDecision> AuthorizeOwnedItemAsync(Guid id,
        IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        await authorization.AuthorizeAsync(await identity.ResolveAuthorityAsync(ct),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Asset, id,
                ViewResourceAction.Manage), ct);

    private static async Task<ViewAccessDecision?> AuthorizeDefaultScopePreferenceAsync(
        RequestAuthority authority,
        string? scope,
        IViewResourceAuthorizationService authorization,
        CancellationToken ct)
    {
        if (authority.ActiveProfileId is null || !string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var decision = await authorization.AuthorizeAsync(authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct);
        return decision.IsAllowed ? null : decision;
    }

    private static async Task<ViewScopeRequest> GetScopeAsync(Guid profileId, string? scope,
        Guid? scopeProfileId, IViewProfileRepository repository, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return ParseScope(scope, scopeProfileId);
        }

        var saved = await repository.GetPreferencesAsync(profileId, ct);
        return saved.LastScopeKind.HasValue
            ? new ViewScopeRequest(saved.LastScopeKind.Value, saved.LastScopeProfileId)
            : ViewScopeRequest.Shared;
    }

    private static ViewScopeRequest ParseScope(string? value, Guid? profileId) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mine" => ViewScopeRequest.Mine,
            "shared" => ViewScopeRequest.Shared,
            "profile" when profileId.HasValue => ViewScopeRequest.ForProfile(profileId.Value),
            "othershared" when profileId.HasValue => ViewScopeRequest.ForOtherShared(profileId.Value),
            _ => throw new ArgumentException("Scope must be mine, shared, profile or othershared with scopeProfileId."),
        };

    private static LocalAssetLifecycleFilter ParseLifecycle(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "active" => LocalAssetLifecycleFilter.Active,
            "archived" => LocalAssetLifecycleFilter.Archived,
            "trashed" => LocalAssetLifecycleFilter.Trashed,
            "all" => LocalAssetLifecycleFilter.All,
            _ => throw new ArgumentException("Lifecycle must be active, archived, trashed, or all."),
        };

    private static IResult Access(ViewAccessOutcome outcome, object? value = null) => outcome switch
    {
        ViewAccessOutcome.Allowed when value is not null => Results.Ok(value),
        ViewAccessOutcome.Unauthenticated => Unauthenticated(),
        ViewAccessOutcome.Forbidden => ApiErrors.Forbidden("View access is not permitted."),
        _ => Missing(),
    };

    private static IResult Missing() => ApiErrors.NotFound("The View resource was not found.");
    private static IResult Unauthenticated() => ApiErrors.Problem(401, "Authentication required.", "A trusted View profile is required.");

    private static ViewScopeResolutionDto ToContract(ViewScopeResolution value) =>
        new(
            value.Scope.IsOtherHousehold && value.Scope.Kind == ViewScopeKind.Shared
                ? new ViewResolvedScopeDto(ViewScopeKind.OtherShared, value.Scope.OtherViaProfileId, false)
                : new ViewResolvedScopeDto(
                    value.Scope.Kind,
                    value.Scope.ProfileId,
                    value.Scope.WasFallback),
            value.AvailableScopes
                .Select(option => new ViewScopeOptionDto(
                    option.Kind,
                    option.ProfileId,
                    option.Label,
                    option.AvatarColor,
                    option.AvatarUrl))
                .ToList());

    private static ViewPreferencesDto ToContract(ViewProfilePreferences value) =>
        new(
            value.ProfileId,
            value.LastScopeKind,
            value.LastScopeProfileId,
            value.TimelineDensity,
            value.ViewerInfoOpen,
            value.UpdatedAt,
            value.DocumentsSort);

    /// <summary>
    /// A Gallery's cover is an item id. A profile that may not use Documents is never handed the id of a document
    /// (the Dashboard would fall back to the Gallery's first photo).
    /// </summary>
    internal static ViewGalleryDto ToContract(ViewGallery value, bool documents, ILocalAssetRepository assets) =>
        new(value.Id, value.OwnerProfileId, value.PersonalSpaceId, value.Name,
            value.Description, value.Kind, value.SmartRuleJson,
            !documents && value.CoverItemId is { } cover && assets.IsDocument(cover) ? null : value.CoverItemId,
            value.SortOrder, value.ItemCount, value.CreatedAt, value.UpdatedAt, value.SoundtrackPlaylistId);

    /// <summary>What a Gallery add answers (400) for an id that is not in the Gallery owner's Personal Space.</summary>
    private const string UnknownGalleryItemMessage = "Every Gallery item must belong to the Gallery owner's Personal Space.";

    /// <summary>
    /// A profile without Documents cannot name a document as a Gallery cover. The person whose document it is is told
    /// plainly (403); anyone else gets the answer for a cover that is not in the owner's space (400).
    /// </summary>
    private static IResult? DocumentCoverRefusal(ILocalAssetRepository assets, Guid? coverItemId, Guid actorProfileId) =>
        coverItemId is { } cover && assets.Find(cover) is { MediaKind: LocalAssetMediaKinds.Document } item
            ? item.OwnerProfileId == actorProfileId
                ? DocumentsNotAllowed()
                : ApiErrors.BadRequest("Gallery cover must belong to its owner's Personal Space.")
            : null;

    private static ViewGalleryItemPageDto ToContract(ViewGalleryItemPage value) =>
        new(value.Items.Select(item => new ViewGalleryItemDto(
                item.GalleryId, item.ItemId, item.Position, item.AddedAt)).ToList(),
            value.NextPosition, value.NextItemId, value.HasMore);

    private static AddViewGalleryItemsResponseDto ToContract(AddViewGalleryItemsResult value) =>
        new(value.Added, value.AlreadyPresent);

    private static ViewGalleryShareDto ToContract(ViewGalleryShare value) =>
        new(value.GalleryId, value.ProfileId, value.Permission, value.SharedAt);

    private static ViewPersonalSpaceAdminReviewDto ToAdminReview(
        Guid profileId,
        ViewPersonalSpace space,
        IReadOnlyList<ViewSource> sources,
        IReadOnlyList<ViewDevice> devices,
        ViewStorageService storage) =>
        new(
            profileId,
            new ViewPersonalSpaceAdminDto(
                space.Id, storage.GetProfileRoot(space), space.CreatedAt, space.UpdatedAt),
            sources.Select(source => ToAdminSource(space, source, storage)).ToList(),
            devices.Select(device => new ViewDeviceAdminDto(
                device.Id,
                device.SourceId,
                device.Name,
                device.Make,
                device.Model,
                device.LastBackupAt,
                BackupStateValue(device.BackupState),
                device.CreatedAt,
                device.UpdatedAt)).ToList());

    private static ViewSourceAdminDto ToAdminSource(
        ViewPersonalSpace space,
        ViewSource source,
        ViewStorageService storage) => new(
        source.Id,
        SourceTypeValue(source.SourceType),
        source.Name,
        source.StorageMode == ViewSourceStorageMode.Linked ? "linked" : "managed",
        storage.GetSourcePath(space, source),
        source.IncludeSubdirectories,
        source.Enabled,
        source.LastActivityAt,
        source.CreatedAt,
            source.UpdatedAt,
            source.IncludeInTimeline);

    private static ViewSourceAdminDto ToAdminSource(
        ViewSharedSource source,
        ViewStorageService storage) =>
        new(
            source.Id,
            source.SourceType.ToString().ToLowerInvariant(),
            source.Name,
            source.StorageMode.ToString().ToLowerInvariant(),
            storage.GetSharedSourcePath(source),
            source.IncludeSubdirectories,
            source.Enabled,
            source.LastActivityAt,
            source.CreatedAt,
            source.UpdatedAt,
            source.IncludeInTimeline);

    private static string SourceTypeValue(ViewSourceType value) => value switch
    {
        ViewSourceType.Folder => "folder",
        ViewSourceType.BrowserUpload => "browser_upload",
        ViewSourceType.DeviceImport => "device_import",
        ViewSourceType.MobileBackup => "mobile_backup",
        ViewSourceType.Network => "network",
        _ => "other",
    };

    private static string BackupStateValue(ViewDeviceBackupState value) => value switch
    {
        ViewDeviceBackupState.Idle => "idle",
        ViewDeviceBackupState.BackingUp => "backing_up",
        ViewDeviceBackupState.Complete => "complete",
        ViewDeviceBackupState.Error => "error",
        _ => "unknown",
    };
}
