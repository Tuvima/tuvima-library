using System.Text.Json;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.Collections;

public sealed record CollectionPersonalMediaListResult(
    bool Found,
    bool Allowed,
    IReadOnlyList<CollectionPersonalMediaSourceDto> Sources);

public sealed record CollectionPersonalMediaWriteResult(
    bool Found,
    bool Allowed,
    CollectionPersonalMediaSourceDto? Source = null,
    string? Error = null,
    bool WouldEmptyCollection = false);

/// <summary>
/// Coordinates Custom Collection personal-media sources without resolving or
/// persisting individual LocalAsset membership. Read projections are filtered
/// again for the current viewer by the persistence authorization query.
/// </summary>
public sealed class CollectionPersonalMediaService(
    ICollectionRepository collections,
    IProfileRepository profiles,
    IViewProfileRepository viewProfiles,
    IViewGalleryRepository galleries,
    ICollectionViewSourceRepository sources,
    IAuthorizationEvaluator authorization,
    IViewResourceAuthorizationService viewAuthorization)
{
    private static readonly IReadOnlySet<string> ViewRuleFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "media_type", "file_type", "orientation", "duration", "captured_date",
        "people", "place", "device", "tags", "favorite", "owner", "source",
    };

    public async Task<IReadOnlyList<CollectionGalleryReferenceDto>> ListEligibleGalleriesAsync(
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(authority, write: true, ct))
        {
            return [];
        }

        if (!await CanWriteSourceAsync(authority, CollectionViewSourceKind.SmartRule, null, ct))
        {
            return [];
        }

        var ownerProfileId = authority.ActiveProfileId!.Value;
        var policy = await viewProfiles.GetPolicyAsync(ownerProfileId, ct);
        if (!policy.ViewEnabled)
        {
            return [];
        }

        // A saved Documents view is a private shelf of documents, so it is never offered as a Collection source.
        var owned = (await galleries.GetOwnedAsync(ownerProfileId, ct: ct))
            .Where(gallery => gallery.Kind != ViewGalleryKind.Smart || !ViewSmartGalleryRules.IsDocumentView(gallery.SmartRuleJson))
            .ToList();
        return owned.Select(gallery => new CollectionGalleryReferenceDto(
                gallery.Id,
                gallery.Name,
                gallery.Kind == ViewGalleryKind.Manual ? "manual" : "smart",
                gallery.UpdatedAt))
            .ToList();
    }

    public async Task<CollectionPersonalMediaListResult> ListForViewerAsync(
        Guid collectionId,
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(authority, write: false, ct))
        {
            return new(false, false, []);
        }

        var viewerProfileId = authority.ActiveProfileId!.Value;
        var collection = await collections.GetByIdAsync(collectionId, ct);
        if (collection is null)
        {
            return new(false, false, []);
        }

        var viewer = await profiles.GetByIdAsync(viewerProfileId, ct);
        if (!CollectionAccessPolicy.CanAccess(collection, viewer))
        {
            return new(true, false, []);
        }

        var projection = await sources.GetAuthorizedProjectionAsync([collectionId], viewerProfileId, ct);
        var authorized = new List<CollectionPersonalMediaSourceDto>(projection.Count);
        foreach (var source in projection)
        {
            if (await CanReadSourceAsync(authority, source, ct))
            {
                authorized.Add(ToDto(source));
            }
        }
        return new(true, true, authorized);
    }

    public async Task<CollectionPersonalMediaWriteResult> AddAsync(
        Guid collectionId,
        RequestAuthority authority,
        CollectionPersonalMediaSourceWriteRequest request,
        CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(authority, write: true, ct))
        {
            return new(true, false);
        }

        var ownerProfileId = authority.ActiveProfileId!.Value;
        var access = await CanEditCustomCollectionAsync(collectionId, authority, ct);
        if (!access.Found || !access.Allowed)
        {
            return new(access.Found, access.Allowed);
        }

        if (!TryValidateRequest(request, out var kind, out var rule, out var error))
        {
            return new(true, true, Error: error);
        }

        if (!await CanWriteSourceAsync(authority, kind, request.GalleryId, ct))
        {
            return new(true, false);
        }

        try
        {
            var source = kind == CollectionViewSourceKind.Gallery
                ? await sources.AddGalleryAsync(new(collectionId, ownerProfileId, request.GalleryId!.Value, request.Position), ct)
                : await sources.AddSmartRuleAsync(new(collectionId, ownerProfileId, rule!, request.Position), ct);
            return new(true, true, ToDto(source));
        }
        catch (InvalidOperationException exception)
        {
            return new(true, true, Error: exception.Message);
        }
    }

    public async Task<CollectionPersonalMediaWriteResult> UpdateAsync(
        Guid collectionId,
        Guid sourceId,
        RequestAuthority authority,
        CollectionPersonalMediaSourceWriteRequest request,
        CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(authority, write: true, ct))
        {
            return new(true, false);
        }

        var ownerProfileId = authority.ActiveProfileId!.Value;
        var access = await CanEditCustomCollectionAsync(collectionId, authority, ct);
        if (!access.Found || !access.Allowed)
        {
            return new(access.Found, access.Allowed);
        }

        var existing = (await sources.ListAsync(collectionId, ownerProfileId, ct))
            .FirstOrDefault(source => source.Id == sourceId);
        if (existing is null)
        {
            return new(false, true);
        }

        if (!await CanWriteSourceAsync(authority, existing.Kind, existing.GalleryId, ct))
        {
            return new(true, false);
        }

        if (!TryValidateRequest(request, out var kind, out var rule, out var error))
        {
            return new(true, true, Error: error);
        }

        if (!await CanWriteSourceAsync(authority, kind, request.GalleryId, ct))
        {
            return new(true, false);
        }

        try
        {
            var source = await sources.UpdateAsync(new(
                sourceId,
                collectionId,
                ownerProfileId,
                kind,
                kind == CollectionViewSourceKind.Gallery ? request.GalleryId : null,
                kind == CollectionViewSourceKind.SmartRule ? rule : null,
                request.Position), ct);
            return source is null
                ? new(false, true)
                : new(true, true, ToDto(source));
        }
        catch (InvalidOperationException exception)
        {
            return new(true, true, Error: exception.Message);
        }
    }

    public async Task<CollectionPersonalMediaWriteResult> RemoveAsync(
        Guid collectionId,
        Guid sourceId,
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(authority, write: true, ct))
        {
            return new(true, false);
        }

        var ownerProfileId = authority.ActiveProfileId!.Value;
        var access = await CanEditCustomCollectionAsync(collectionId, authority, ct);
        if (!access.Found || !access.Allowed)
        {
            return new(access.Found, access.Allowed);
        }

        var existing = (await sources.ListAsync(collectionId, ownerProfileId, ct))
            .FirstOrDefault(source => source.Id == sourceId);
        if (existing is null)
        {
            return new(false, true);
        }

        if (!await CanWriteSourceAsync(authority, existing.Kind, existing.GalleryId, ct))
        {
            return new(true, false);
        }

        try
        {
            // A manually curated collection keeps at least one member: the last personal-media source stays, and the
            // person is told to delete the collection instead (D17, D26). Playlists and rule-driven collections are not curated.
            var collection = await collections.GetByIdAsync(collectionId, ct);
            if (collection is not null && CollectionAccessPolicy.IsCuratedCollection(collection))
            {
                return await sources.RemoveUnlessLastMemberAsync(collectionId, sourceId, ownerProfileId, ct) switch
                {
                    CollectionViewSourceRemoval.Removed => new(true, true),
                    CollectionViewSourceRemoval.WouldLeaveCollectionEmpty => new(
                        true, true, Error: CollectionAccessPolicy.CuratedCollectionNeedsItemMessage, WouldEmptyCollection: true),
                    _ => new(false, true),
                };
            }

            var removed = await sources.RemoveAsync(collectionId, sourceId, ownerProfileId, ct);
            return new(removed, true);
        }
        catch (InvalidOperationException exception)
        {
            return new(true, true, Error: exception.Message);
        }
    }

    private async Task<(bool Found, bool Allowed)> CanEditCustomCollectionAsync(
        Guid collectionId,
        RequestAuthority authority,
        CancellationToken ct)
    {
        var collection = await collections.GetByIdAsync(collectionId, ct);
        if (collection is null)
        {
            return (false, false);
        }

        return (true,
            collection.CollectionType == CollectionType.Custom
            && authority.IsEffectiveAdministrator);
    }

    private async Task<bool> IsAllowedAsync(RequestAuthority authority, bool write, CancellationToken ct)
    {
        if (!authority.HasHumanContext || authority.ActiveProfileId is null)
        {
            return false;
        }

        var requirement = new AuthorizationRequirement(
            authority.HasApplicationContext
                ? write ? ApplicationPermissionIds.CollectionsWrite : ApplicationPermissionIds.CollectionsRead
                : null,
            AccountFeatureId.View,
            RequiresHumanContext: true,
            RequiresAdministrator: write,
            RequiresAdministratorSurfaceUnlock: write);
        if (!(await authorization.EvaluateAsync(authority, requirement, null, ct)).IsAllowed)
        {
            return false;
        }

        if (!write && authority.HasApplicationContext)
        {
            var viewRequirement = new AuthorizationRequirement(
                ApplicationPermissionIds.ViewPersonalRead,
                AccountFeatureId.View,
                RequiresHumanContext: true);
            return (await authorization.EvaluateAsync(authority, viewRequirement, null, ct)).IsAllowed;
        }
        return true;
    }

    private async Task<bool> CanReadSourceAsync(
        RequestAuthority authority,
        CollectionViewSourceProjection source,
        CancellationToken ct)
    {
        if (source.Kind == CollectionViewSourceKind.Gallery && source.GalleryId is { } galleryId)
        {
            return (await viewAuthorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId), ct)).IsAllowed;
        }

        if (source.Kind != CollectionViewSourceKind.SmartRule
            || source.OwnerProfileId != authority.ActiveProfileId)
        {
            return false;
        }

        return (await viewAuthorization.AuthorizeAsync(authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Search, null), ct)).IsAllowed;
    }

    private Task<bool> CanWriteSourceAsync(
        RequestAuthority authority,
        CollectionViewSourceKind kind,
        Guid? galleryId,
        CancellationToken ct)
    {
        var request = kind == CollectionViewSourceKind.Gallery && galleryId is { } id
            ? new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, id,
                ViewResourceAction.Manage)
            : new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, null,
                ViewResourceAction.Manage);
        return IsViewAllowedAsync(authority, request, ct);
    }

    private async Task<bool> IsViewAllowedAsync(
        RequestAuthority authority,
        ViewResourceRequest request,
        CancellationToken ct) =>
        (await viewAuthorization.AuthorizeAsync(authority, request, ct)).IsAllowed;

    public static bool TryValidateRequest(
        CollectionPersonalMediaSourceWriteRequest request,
        out CollectionViewSourceKind kind,
        out ViewSmartRuleDefinition? rule,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        kind = default;
        rule = null;
        error = null;

        if (request.AdditionalMembers is { Count: > 0 })
        {
            error = "Personal-media sources accept only a Gallery reference or a versioned View rule; individual asset IDs are not supported.";
            return false;
        }
        if (request.Position < 0)
        {
            error = "position cannot be negative.";
            return false;
        }

        if (string.Equals(request.Kind, CollectionPersonalMediaSourceKinds.Gallery, StringComparison.Ordinal))
        {
            kind = CollectionViewSourceKind.Gallery;
            if (!request.GalleryId.HasValue || request.GalleryId.Value == Guid.Empty
                || request.RuleDefinition is not null
                || request.RuleVersion.HasValue)
            {
                error = "A Gallery source requires gallery_id and cannot include a View rule.";
                return false;
            }
            return true;
        }

        if (!string.Equals(request.Kind, CollectionPersonalMediaSourceKinds.SmartRule, StringComparison.Ordinal))
        {
            error = "kind must be gallery or smart_rule.";
            return false;
        }

        kind = CollectionViewSourceKind.SmartRule;
        if (request.GalleryId.HasValue
            || request.RuleVersion != ViewSmartRuleDefinition.CurrentVersion
            || request.RuleDefinition is null
            || request.RuleDefinition.Version != ViewSmartRuleDefinition.CurrentVersion)
        {
            error = $"A smart View source requires rule_version {ViewSmartRuleDefinition.CurrentVersion} and one matching rule_definition.";
            return false;
        }

        var conditions = request.RuleDefinition.Groups.SelectMany(group => group.Conditions).ToList();
        if (conditions.Count == 0
            || conditions.Any(condition =>
                !ViewRuleFields.Contains(condition.Field)
                || string.IsNullOrWhiteSpace(condition.Op)
                || !IsValueFreeOperator(condition.Op)
                   && string.IsNullOrWhiteSpace(condition.Value)
                   && condition.Values is not { Length: > 0 }))
        {
            error = "A smart View source requires at least one complete condition from the View rule field catalog.";
            return false;
        }

        try
        {
            rule = ViewSmartRuleDefinition.Create(
                request.RuleVersion.Value,
                JsonSerializer.Serialize(request.RuleDefinition));
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsValueFreeOperator(string value) =>
        value is "known" or "unknown" or "has_any_value" or "has_no_known_value";

    private static CollectionPersonalMediaSourceDto ToDto(CollectionViewSource source) => new(
        source.Id,
        source.CollectionId,
        source.OwnerProfileId,
        ToContractKind(source.Kind),
        source.GalleryId,
        source.SmartRule?.Version,
        DeserializeRule(source.SmartRule?.Json),
        source.Position);

    private static CollectionPersonalMediaSourceDto ToDto(CollectionViewSourceProjection source) => new(
        source.SourceId,
        source.CollectionId,
        source.OwnerProfileId,
        ToContractKind(source.Kind),
        source.GalleryId,
        source.RuleVersion,
        DeserializeRule(source.RuleJson),
        source.Position);

    private static string ToContractKind(CollectionViewSourceKind kind) => kind switch
    {
        CollectionViewSourceKind.Gallery => CollectionPersonalMediaSourceKinds.Gallery,
        CollectionViewSourceKind.SmartRule => CollectionPersonalMediaSourceKinds.SmartRule,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CollectionRuleDefinitionDto? DeserializeRule(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CollectionRuleDefinitionDto>(json);
}
