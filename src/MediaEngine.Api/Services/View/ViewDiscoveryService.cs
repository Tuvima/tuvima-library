using System.Globalization;
using System.Text;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed record ViewDiscoveryRequest(
    ViewScopeRequest Scope,
    int Limit,
    string? Search = null,
    string? Cursor = null);

public sealed record ViewPlacesResult(
    ViewAccessOutcome Outcome,
    ViewPlacesPageDto? Page = null,
    ResolvedViewScope? Scope = null);

public sealed record ViewPeopleResult(
    ViewAccessOutcome Outcome,
    ViewPeoplePageDto? Page = null,
    ResolvedViewScope? Scope = null);

public sealed record ViewAtlasResult(
    ViewAccessOutcome Outcome,
    ViewAtlasPageDto? Page = null,
    ResolvedViewScope? Scope = null);

public sealed record ViewPlaceMediaResult(
    ViewAccessOutcome Outcome,
    ViewPlaceMediaPageDto? Page = null,
    ResolvedViewScope? Scope = null);

/// <summary>
/// Applies the trusted View scope before any People or Places storage query.
/// The repository sees only the physical library IDs produced by authorization.
/// </summary>
public sealed class ViewDiscoveryService(
    IViewRequestProfileContext profileContext,
    IViewResourceAuthorizationService authorization,
    IViewDiscoveryRepository repository,
    ILocalAssetRepository? assets = null)
{
    public async Task<ViewPlacesResult> GetPlacesAsync(
        ViewDiscoveryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var decision = await AuthorizeAsync(request.Scope, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewPlacesResult(decision.Outcome);
        }

        var page = repository.QueryPlaces(new ViewPlaceDiscoveryQuery(
            decision.Scope.LibraryIds,
            request.Limit,
            request.Search,
            ViewDiscoveryCursorCodec.Decode(request.Cursor),
            decision.Scope.Kind == ViewScopeKind.Shared), ct);
        var items = page.Items.Select(item => new ViewPlaceDto(
            item.Key,
            item.Name,
            item.Latitude,
            item.Longitude,
            item.AssetCount,
            item.RepresentativeLibraryId,
            item.RepresentativeAssetId)).ToList();
        var capability = Capability(
            page.HasEligibleData,
            items.Count > 0,
            request.Search,
            "GPS and named location metadata",
            "Places appear when active photos or videos contain real GPS or named location metadata.");
        return new ViewPlacesResult(
            ViewAccessOutcome.Allowed,
            new ViewPlacesPageDto(
                items,
                ViewDiscoveryCursorCodec.Encode(page.NextCursor),
                page.HasMore,
                capability),
            decision.Scope);
    }

    public async Task<ViewPeopleResult> GetPeopleAsync(
        ViewDiscoveryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var decision = await AuthorizeAsync(request.Scope, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewPeopleResult(decision.Outcome);
        }

        var page = repository.QueryPeople(new ViewPeopleDiscoveryQuery(
            decision.Scope.LibraryIds,
            request.Limit,
            request.Search,
            ViewDiscoveryCursorCodec.Decode(request.Cursor),
            decision.Scope.Kind == ViewScopeKind.Shared), ct);
        var items = page.Items.Select(item => new ViewPersonDto(
            item.Key,
            item.DisplayName,
            item.AssetCount,
            item.RepresentativeLibraryId,
            item.RepresentativeAssetId,
            item.AnnotationKinds,
            item.ProvenanceSources,
            item.HasReviewedEvidence)).ToList();
        var capability = Capability(
            page.HasEligibleData,
            items.Count > 0,
            request.Search,
            "Named or reviewed person annotations",
            "People becomes available when named people metadata or reviewed face identities exist. Automatic face processing is not currently available.");
        return new ViewPeopleResult(
            ViewAccessOutcome.Allowed,
            new ViewPeoplePageDto(
                items,
                ViewDiscoveryCursorCodec.Encode(page.NextCursor),
                page.HasMore,
                capability),
            decision.Scope);
    }

    public async Task<ViewAtlasResult> GetAtlasAsync(
        ViewScopeRequest scope,
        string? search = null,
        int? year = null,
        string? mediaKind = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? timelineResolution = null,
        bool favoritesOnly = false,
        CancellationToken ct = default)
    {
        var decision = await AuthorizeAsync(scope, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewAtlasResult(decision.Outcome);
        }

        var page = repository.QueryAtlas(new ViewAtlasDiscoveryQuery(
            decision.Scope.LibraryIds,
            search,
            year,
            mediaKind,
            IncludeSharedLibraryAssets: decision.Scope.Kind == ViewScopeKind.Shared,
            From: from,
            To: to,
            TimelineResolution: timelineResolution ?? "year",
            FavoritesOnly: favoritesOnly), ct);
        var hotspots = page.Hotspots.Select(item => new ViewAtlasHotspotDto(
            item.Key, item.Name, item.Latitude, item.Longitude, item.AssetCount,
            item.ImageCount, item.VideoCount, item.EarliestAt, item.LatestAt,
            item.RepresentativeLibraryId, item.RepresentativeAssetId)).ToList();
        return new ViewAtlasResult(
            ViewAccessOutcome.Allowed,
            new ViewAtlasPageDto(
                hotspots,
                page.AvailableYears,
                page.MappedAssetCount,
                page.UnmappedAssetCount,
                Capability(page.HasEligibleData, hotspots.Count > 0, search,
                    "GPS and named location metadata",
                    "Atlas hotspots appear when active photos or videos contain real GPS metadata."),
                page.Timeline?.Select(bucket => new ViewAtlasTimelineBucketDto(
                    bucket.Start, bucket.AssetCount, bucket.ImageCount, bucket.VideoCount, bucket.End)).ToList(),
                page.EarliestAt,
                page.LatestAt, page.ImageCount, page.VideoCount),
            decision.Scope);
    }

    public async Task<ViewPlaceMediaResult> GetPlaceMediaAsync(
        ViewScopeRequest scope,
        string placeKey,
        int offset,
        int limit,
        int? year = null,
        string? mediaKind = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        bool favoritesOnly = false,
        CancellationToken ct = default)
    {
        var decision = await AuthorizeAsync(scope, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewPlaceMediaResult(decision.Outcome);
        }
        if (assets is null)
        {
            throw new InvalidOperationException("The asset repository is unavailable.");
        }

        var page = repository.QueryPlaceAssets(new ViewPlaceAssetDiscoveryQuery(
            decision.Scope.LibraryIds,
            placeKey,
            offset,
            limit,
            year,
            mediaKind,
            decision.Scope.Kind == ViewScopeKind.Shared,
            from,
            to,
            favoritesOnly), ct);
        var items = page.AssetIds
            .Select(id => assets.Find(id, ct))
            .Where(item => item is not null)
            .Cast<LocalAssetDto>()
            .ToList();
        return new ViewPlaceMediaResult(
            ViewAccessOutcome.Allowed,
            new ViewPlaceMediaPageDto(placeKey, page.PlaceName, items, page.Total, page.HasMore),
            decision.Scope);
    }

    private async Task<ViewAccessDecision> AuthorizeAsync(ViewScopeRequest scope, CancellationToken ct) =>
        await authorization.AuthorizeAsync(
            await profileContext.ResolveAuthorityAsync(ct).ConfigureAwait(false),
            new ViewResourceRequest(scope, ViewResourceKind.Search, null),
            ct).ConfigureAwait(false);

    private static void Validate(ViewDiscoveryRequest request)
    {
        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Discovery limit must be between 1 and 100.");
        }

        if (request.Search?.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Search cannot exceed 200 characters.");
        }
    }

    private static ViewDiscoveryCapabilityDto Capability(
        bool hasIndexedData,
        bool hasResults,
        string? search,
        string evidenceKind,
        string emptyMessage)
    {
        var state = hasResults
            ? ViewDiscoveryCapabilityStates.Available
            : hasIndexedData && !string.IsNullOrWhiteSpace(search)
                ? ViewDiscoveryCapabilityStates.NoMatches
                : ViewDiscoveryCapabilityStates.Empty;
        var message = state == ViewDiscoveryCapabilityStates.NoMatches
            ? "No indexed metadata matched this search."
            : state == ViewDiscoveryCapabilityStates.Available
                ? "Indexed local metadata is available."
                : emptyMessage;
        return new ViewDiscoveryCapabilityDto(
            state,
            hasIndexedData,
            AutomaticProcessingAvailable: false,
            message,
            [evidenceKind]);
    }
}

public static class ViewDiscoveryCursorCodec
{
    public static string? Encode(ViewDiscoveryCursor? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var raw = string.Create(CultureInfo.InvariantCulture, $"{cursor.AssetCount}:{cursor.Key}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ViewDiscoveryCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var separator = raw.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0
                || !int.TryParse(raw[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count < 1
                || string.IsNullOrWhiteSpace(raw[(separator + 1)..]))
            {
                throw new FormatException();
            }

            return new ViewDiscoveryCursor(count, raw[(separator + 1)..]);
        }
        catch (FormatException)
        {
            throw new ArgumentException("The discovery cursor is invalid.", nameof(value));
        }
    }
}
