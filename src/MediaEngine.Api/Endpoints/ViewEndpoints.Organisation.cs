using MediaEngine.Api.Http;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Tags, correspondents, document types and custom fields: the lists themselves, how items are organised with them,
/// and the filters the Documents page uses. Visibility is decided in <see cref="IViewCatalogueRepository"/> from the
/// libraries the resolver approved here; nothing in this file widens that list.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapDocumentOrganisation(RouteGroupBuilder group)
    {
        group.MapGet("/catalogue", async (string? scope, Guid? scopeProfileId, string[]? kind,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            IProfileRepository profiles, CancellationToken ct) =>
        {
            try
            {
                var (access, refusal) = await ResolveCatalogueAccessAsync(
                    scope, scopeProfileId, true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var snapshot = catalogue.GetCatalogue(access.Viewer, kind, ct);
                var policy = new CatalogueManagePolicy(access.Authority, profiles);
                return Results.Ok(new ViewDocumentCatalogueDto(
                    await MapEntriesAsync(snapshot.Tags, policy, ToTagDto, ct),
                    await MapEntriesAsync(snapshot.Correspondents, policy, ToCorrespondentDto, ct),
                    await MapEntriesAsync(snapshot.DocumentTypes, policy, ToDocumentTypeDto, ct),
                    await MapEntriesAsync(snapshot.Fields, policy, ToFieldDto, ct)));
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).WithName("GetViewCatalogue").Produces<ViewDocumentCatalogueDto>();

        group.MapPost("/tags", async ([FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await CreateCatalogueEntryAsync(ViewCatalogueKind.Tag, request, services, ToTagDto))
            .WithName("CreateViewTag").Produces<ViewTagDto>();
        group.MapPut("/tags/{id:guid}", async (Guid id, [FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await UpdateCatalogueEntryAsync(ViewCatalogueKind.Tag, id, request, services, ToTagDto))
            .WithName("UpdateViewTag").Produces<ViewTagDto>();
        group.MapDelete("/tags/{id:guid}", async (Guid id, [AsParameters] CatalogueServices services) =>
            await DeleteCatalogueEntryAsync(ViewCatalogueKind.Tag, id, services))
            .WithName("DeleteViewTag").Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        group.MapPost("/correspondents", async ([FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await CreateCatalogueEntryAsync(ViewCatalogueKind.Correspondent, request, services, ToCorrespondentDto))
            .WithName("CreateViewCorrespondent").Produces<ViewCorrespondentDto>();
        group.MapPut("/correspondents/{id:guid}", async (Guid id, [FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await UpdateCatalogueEntryAsync(ViewCatalogueKind.Correspondent, id, request, services, ToCorrespondentDto))
            .WithName("UpdateViewCorrespondent").Produces<ViewCorrespondentDto>();
        group.MapDelete("/correspondents/{id:guid}", async (Guid id, [AsParameters] CatalogueServices services) =>
            await DeleteCatalogueEntryAsync(ViewCatalogueKind.Correspondent, id, services))
            .WithName("DeleteViewCorrespondent").Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        group.MapPost("/document-types", async ([FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await CreateCatalogueEntryAsync(ViewCatalogueKind.DocumentType, request, services, ToDocumentTypeDto))
            .WithName("CreateViewDocumentType").Produces<ViewDocumentTypeDto>();
        group.MapPut("/document-types/{id:guid}", async (Guid id, [FromBody] ViewCatalogueEntryRequest request, [AsParameters] CatalogueServices services) =>
            await UpdateCatalogueEntryAsync(ViewCatalogueKind.DocumentType, id, request, services, ToDocumentTypeDto))
            .WithName("UpdateViewDocumentType").Produces<ViewDocumentTypeDto>();
        group.MapDelete("/document-types/{id:guid}", async (Guid id, [AsParameters] CatalogueServices services) =>
            await DeleteCatalogueEntryAsync(ViewCatalogueKind.DocumentType, id, services))
            .WithName("DeleteViewDocumentType").Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);

        MapFieldRoutes(group);

        group.MapGet("/items/{id:guid}/document-details", async (Guid id, string? scope, Guid? scopeProfileId,
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
                return Missing();
            }

            if (await OtherKindBlockedAsync(id, assets, identity, preferences, ct))
            {
                return Missing();
            }

            return assets.Find(id, ct) is { MediaKind: LocalAssetMediaKinds.Document, DocumentInfo: { } info }
                ? Results.Ok(info) : Missing();
        }).WithName("GetViewDocumentDetails").Produces<LocalAssetDocumentInfoDto>();

        group.MapPut("/items/{id:guid}/document-details", async (Guid id, UpdateViewDocumentDetailsRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var write = new ViewDocumentDetailsWrite(
                    request.Title, request.DocumentDate, request.Correspondent, request.DocumentType,
                    request.ArchiveNumber, request.Notes, request.FilingState,
                    request.Tags ?? [], request.TagIds ?? [],
                    request.FieldValues?.Select(value => new ViewDocumentFieldWrite(
                        value.FieldId, value.TextValue, value.NumberValue, value.DateValue, value.BoolValue)).ToList());
                if (!await catalogue.UpdateDocumentDetailsAsync(access.Viewer, id, write, ct))
                {
                    return Missing();
                }

                return assets.Find(id, ct) is { DocumentInfo: { } info } ? Results.Ok(info) : Missing();
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("UpdateViewDocumentDetails").Produces<LocalAssetDocumentInfoDto>();

        group.MapPut("/items/{id:guid}/tags", async (Guid id,
            UpdateLocalAssetTagsRequest request, IViewRequestProfileContext identity,
            IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                return Access(decision.Outcome);
            }

            if (await DocumentsBlockedAsync(id, assets, identity, preferences, ct))
            {
                return Missing();
            }

            if (await OtherKindBlockedAsync(id, assets, identity, preferences, ct))
            {
                return Missing();
            }

            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    false, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                await catalogue.ReplaceItemTagsAsync(access.Viewer, id, request.Tags ?? [], request.TagIds ?? [], ct);
                return Results.Ok(assets.Find(id, ct));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("UpdateViewItemTags").Produces<LocalAssetDto>();

        group.MapGet("/tag-suggestions", async (string? q, int? limit,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            CancellationToken ct) =>
        {
            var (access, refusal) = await ResolveCatalogueAccessAsync(
                null, null, false, identity, preferences, resolver, authorization, ct);
            if (access is null)
            {
                return refusal!;
            }

            var take = PagedRequest.From(null, limit, defaultLimit: 20, maxLimit: 100).Limit;
            return Results.Ok(catalogue.GetTagSuggestions(access.Viewer, q, take, ct));
        }).WithName("GetViewTagSuggestions").Produces<IReadOnlyList<string>>();
    }

    /// <summary>The services every tag, correspondent, type and field route needs, bound once per request.</summary>
    public sealed record CatalogueServices(
        IViewRequestProfileContext Identity,
        IViewProfileRepository Preferences,
        IViewScopeResolver Resolver,
        IViewResourceAuthorizationService Authorization,
        IViewCatalogueRepository Catalogue,
        IProfileRepository Profiles,
        CancellationToken Ct);

    private static ViewTagDto ToTagDto(ViewCatalogueEntry entry, bool canManage) =>
        new(entry.Id, entry.Name, entry.Colour, entry.UsageCount, canManage);

    private static ViewCorrespondentDto ToCorrespondentDto(ViewCatalogueEntry entry, bool canManage) =>
        new(entry.Id, entry.Name, entry.Colour, entry.UsageCount, canManage);

    private static ViewDocumentTypeDto ToDocumentTypeDto(ViewCatalogueEntry entry, bool canManage) =>
        new(entry.Id, entry.Name, entry.UsageCount, canManage);

    private static async Task<(CatalogueAccess? Access, IResult? Refusal)> ResolveCatalogueChangeAccessAsync(CatalogueServices services) =>
        await ResolveCatalogueChangeAccessAsync(true, services.Identity, services.Preferences,
            services.Resolver, services.Authorization, services.Ct);

    private static async Task<IResult> CreateCatalogueEntryAsync(ViewCatalogueKind kind,
        ViewCatalogueEntryRequest request, CatalogueServices services, Func<ViewCatalogueEntry, bool, object> map)
    {
        try
        {
            var (access, refusal) = await ResolveCatalogueChangeAccessAsync(services);
            if (access is null)
            {
                return refusal!;
            }

            var entry = await services.Catalogue.CreateEntryAsync(access.Viewer, kind, request.Name, request.Colour, services.Ct);
            var canManage = await new CatalogueManagePolicy(access.Authority, services.Profiles)
                .CanManageAsync(entry.CreatedByProfileId, services.Ct);
            return Results.Ok(map(entry, canManage));
        }
        catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
            or ArgumentException)
        {
            return CatalogueFailure(exception);
        }
    }

    private static async Task<IResult> UpdateCatalogueEntryAsync(ViewCatalogueKind kind, Guid id,
        ViewCatalogueEntryRequest request, CatalogueServices services, Func<ViewCatalogueEntry, bool, object> map)
    {
        try
        {
            var (access, refusal) = await ResolveCatalogueChangeAccessAsync(services);
            if (access is null)
            {
                return refusal!;
            }

            var existing = services.Catalogue.FindEntry(access.Viewer, kind, id, services.Ct);
            if (existing is null)
            {
                return Missing();
            }

            if (!await new CatalogueManagePolicy(access.Authority, services.Profiles).CanManageAsync(existing.CreatedByProfileId, services.Ct))
            {
                return ApiErrors.Forbidden("Only the person who made this, or an administrator, can change it.");
            }

            var entry = await services.Catalogue.UpdateEntryAsync(access.Viewer, kind, id, request.Name, request.Colour, services.Ct);
            return Results.Ok(map(entry, true));
        }
        catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
            or ArgumentException)
        {
            return CatalogueFailure(exception);
        }
    }

    private static async Task<IResult> DeleteCatalogueEntryAsync(ViewCatalogueKind kind, Guid id, CatalogueServices services)
    {
        try
        {
            var (access, refusal) = await ResolveCatalogueChangeAccessAsync(services);
            if (access is null)
            {
                return refusal!;
            }

            var existing = services.Catalogue.FindEntry(access.Viewer, kind, id, services.Ct);
            if (existing is null)
            {
                return Missing();
            }

            if (!await new CatalogueManagePolicy(access.Authority, services.Profiles).CanManageAsync(existing.CreatedByProfileId, services.Ct))
            {
                return ApiErrors.Forbidden("Only the person who made this, or an administrator, can delete it.");
            }

            return await services.Catalogue.DeleteEntryAsync(access.Viewer, kind, id, services.Ct) ? Results.NoContent() : Missing();
        }
        catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
            or ArgumentException)
        {
            return CatalogueFailure(exception);
        }
    }

    private static void MapFieldRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/document-fields", async (ViewDocumentFieldRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            IProfileRepository profiles, CancellationToken ct) =>
        {
            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var entry = await catalogue.CreateFieldAsync(access.Viewer, request.Name, request.Kind, request.Choices, ct);
                return Results.Ok(ToFieldDto(entry, await new CatalogueManagePolicy(access.Authority, profiles)
                    .CanManageAsync(entry.CreatedByProfileId, ct)));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("CreateViewDocumentField").Produces<ViewDocumentFieldDto>();

        group.MapPut("/document-fields/{id:guid}", async (Guid id, ViewDocumentFieldRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            IProfileRepository profiles, CancellationToken ct) =>
        {
            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var existing = catalogue.FindEntry(access.Viewer, ViewCatalogueKind.Field, id, ct);
                if (existing is null)
                {
                    return Missing();
                }

                if (!await new CatalogueManagePolicy(access.Authority, profiles).CanManageAsync(existing.CreatedByProfileId, ct))
                {
                    return ApiErrors.Forbidden("Only the person who made this, or an administrator, can change it.");
                }

                if (!string.IsNullOrWhiteSpace(request.Kind)
                    && !string.Equals(request.Kind.Trim(), existing.FieldKind, StringComparison.OrdinalIgnoreCase))
                {
                    return ApiErrors.BadRequest("A field's kind cannot be changed after it is created.");
                }

                return Results.Ok(ToFieldDto(
                    await catalogue.UpdateFieldAsync(access.Viewer, id, request.Name, request.Choices, ct), true));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("UpdateViewDocumentField").Produces<ViewDocumentFieldDto>();

        group.MapDelete("/document-fields/{id:guid}", async (Guid id,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IViewCatalogueRepository catalogue,
            IProfileRepository profiles, CancellationToken ct) =>
        {
            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var existing = catalogue.FindEntry(access.Viewer, ViewCatalogueKind.Field, id, ct);
                if (existing is null)
                {
                    return Missing();
                }

                if (!await new CatalogueManagePolicy(access.Authority, profiles).CanManageAsync(existing.CreatedByProfileId, ct))
                {
                    return ApiErrors.Forbidden("Only the person who made this, or an administrator, can delete it.");
                }

                return await catalogue.DeleteEntryAsync(access.Viewer, ViewCatalogueKind.Field, id, ct)
                    ? Results.NoContent() : Missing();
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("DeleteViewDocumentField")
            .Produces<Microsoft.AspNetCore.Http.HttpResults.NoContent>(StatusCodes.Status204NoContent);
    }

    private static ViewDocumentFieldDto ToFieldDto(ViewCatalogueEntry entry, bool canManage) =>
        new(entry.Id, entry.Name, entry.FieldKind ?? ViewDocumentFieldKinds.Text, entry.Choices, entry.UsageCount, canManage);

    private sealed record CatalogueAccess(RequestAuthority Authority, ViewCatalogueViewer Viewer);

    /// <summary>
    /// <see cref="ResolveCatalogueAccessAsync(string?, Guid?, bool, IViewRequestProfileContext, IViewProfileRepository, IViewScopeResolver, IViewResourceAuthorizationService, CancellationToken)"/>
    /// for a route that changes something: tags, correspondents, types and fields, filing rules, Inbox filing and bulk
    /// edits. It first asks the same question as changing the person's own View settings, so a person may and an app
    /// (a paired device, whose photo scopes are read-only) is refused with 403 before anything is looked up.
    /// </summary>
    private static async Task<(CatalogueAccess? Access, IResult? Refusal)> ResolveCatalogueChangeAccessAsync(
        bool documentsOnly,
        IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
        IViewResourceAuthorizationService authorization, CancellationToken ct)
    {
        var authority = await identity.ResolveAuthorityAsync(ct);
        if (!authority.IsAuthenticated || authority.ActiveProfileId is null)
        {
            return (null, Unauthenticated());
        }

        var change = await authorization.AuthorizeAsync(authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage), ct);
        if (!change.IsAllowed)
        {
            return (null, Access(change.Outcome));
        }

        return await ResolveCatalogueAccessAsync(
            null, null, documentsOnly, identity, preferences, resolver, authorization, ct);
    }

    /// <summary>
    /// Decides who is asking and which libraries their catalogue reads may cover. With a scope that is the libraries
    /// of that one scope (what the Documents page is showing); without one it is every scope the person can open
    /// (Mine, Shared and household members), so the editor can offer anything they could already see. Other
    /// households ("Other people") are never included. Every library comes from the same authorization the item
    /// listings use. <paramref name="documentsOnly"/> routes are refused for a profile that may not use Documents.
    /// </summary>
    private static async Task<(CatalogueAccess? Access, IResult? Refusal)> ResolveCatalogueAccessAsync(
        string? scope, Guid? scopeProfileId, bool documentsOnly,
        IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
        IViewResourceAuthorizationService authorization, CancellationToken ct)
    {
        var authority = await identity.ResolveAuthorityAsync(ct);
        if (!authority.IsAuthenticated || authority.ActiveProfileId is not { } profileId)
        {
            return (null, Unauthenticated());
        }

        if (documentsOnly && !await MayUseDocumentsAsync(authority, preferences, ct))
        {
            return (null, DocumentsNotAllowed());
        }

        var libraries = new HashSet<Guid>();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var decision = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ParseScope(scope, scopeProfileId), ViewResourceKind.Search, null), ct);
            if (!decision.IsAllowed || decision.Scope is null)
            {
                return (null, Access(decision.Outcome));
            }

            libraries.UnionWith(decision.Scope.LibraryIds);
        }
        else
        {
            var resolution = await resolver.ResolveAsync(authority, ViewScopeRequest.Mine, true, ct);
            if (resolution is null)
            {
                return (null, Missing());
            }

            foreach (var option in resolution.AvailableScopes.Where(option => option.Kind != ViewScopeKind.OtherShared))
            {
                var decision = await authorization.AuthorizeAsync(authority,
                    new ViewResourceRequest(new ViewScopeRequest(option.Kind, option.ProfileId), ViewResourceKind.Search, null), ct);
                if (decision.IsAllowed && decision.Scope is { IsOtherHousehold: false })
                {
                    libraries.UnionWith(decision.Scope.LibraryIds);
                }
            }
        }

        var includeDocuments = documentsOnly || await MayUseDocumentsAsync(authority, preferences, ct);
        return (new CatalogueAccess(authority, new ViewCatalogueViewer(profileId, libraries, includeDocuments,
            await BlockedKindsAsync(authority, preferences, ct))), null);
    }

    private static async Task<IReadOnlyList<TDto>> MapEntriesAsync<TDto>(
        IReadOnlyList<ViewCatalogueEntry> entries, CatalogueManagePolicy policy,
        Func<ViewCatalogueEntry, bool, TDto> map, CancellationToken ct)
    {
        var result = new List<TDto>(entries.Count);
        foreach (var entry in entries)
        {
            result.Add(map(entry, await policy.CanManageAsync(entry.CreatedByProfileId, ct)));
        }

        return result;
    }

    private static IResult CatalogueFailure(Exception exception) => exception switch
    {
        ViewCatalogueConflictException => ApiErrors.Conflict(exception.Message),
        ViewCatalogueAccessException => Missing(),
        _ => ApiErrors.BadRequest(exception.Message),
    };

    /// <summary>
    /// Who may rename or delete a tag, correspondent, document type or field: the person who made it, a server
    /// administrator, or a household administrator for names made by someone in their own household (or by nobody).
    /// </summary>
    private sealed class CatalogueManagePolicy(RequestAuthority authority, IProfileRepository profiles)
    {
        private readonly Dictionary<Guid, Guid?> households = [];

        public async Task<bool> CanManageAsync(Guid? creatorProfileId, CancellationToken ct)
        {
            if (authority.IsEffectiveAdministrator
                || (creatorProfileId.HasValue && creatorProfileId == authority.ActiveProfileId))
            {
                return true;
            }

            if (!authority.IsEffectiveHouseholdAdministrator)
            {
                return false;
            }

            if (creatorProfileId is not { } creator)
            {
                return true;
            }

            if (!households.TryGetValue(creator, out var household))
            {
                household = (await profiles.GetByIdAsync(creator, ct))?.HouseholdId;
                households[creator] = household;
            }

            return household.HasValue && household == authority.AccountHouseholdId;
        }
    }

    /// <summary>Turns the Documents page's query values into one filter, or null when none were sent.</summary>
    internal static LocalAssetDocumentFilter? BuildDocumentFilter(
        Guid[]? tags, Guid? correspondent, Guid? documentType, DateOnly? from, DateOnly? to,
        string? filingState, string[]? fields, string? sort = null)
    {
        var fieldFilters = new List<LocalAssetFieldFilter>();
        foreach (var field in fields ?? [])
        {
            var separator = field.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || !Guid.TryParse(field.AsSpan(0, separator), out var fieldId) || separator == field.Length - 1)
            {
                throw new ArgumentException("A field filter must look like <field id>:<value>.");
            }

            fieldFilters.Add(new LocalAssetFieldFilter(fieldId, field[(separator + 1)..]));
        }

        if ((tags is null || tags.Length == 0) && correspondent is null && documentType is null && from is null
            && to is null && string.IsNullOrWhiteSpace(filingState) && fieldFilters.Count == 0 && string.IsNullOrWhiteSpace(sort))
        {
            return null;
        }

        return new LocalAssetDocumentFilter(
            tags is { Length: > 0 } ? tags : null, correspondent, documentType, from, to,
            string.IsNullOrWhiteSpace(filingState) ? null : filingState, fieldFilters.Count == 0 ? null : fieldFilters,
            string.IsNullOrWhiteSpace(sort) ? null : sort.Trim().ToLowerInvariant());
    }
}
