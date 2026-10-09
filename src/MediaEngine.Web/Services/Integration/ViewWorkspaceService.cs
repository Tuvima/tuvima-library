using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed class ViewWorkspaceService(IEngineApiClient api)
{
    private bool _initialized;
    private bool _galleriesLoaded;

    public ViewScopeResolutionDto? Scopes { get; private set; }
    public ViewPreferencesDto? Preferences { get; private set; }
    public ViewScopeKind ScopeKind => Scopes?.Scope.Kind ?? Preferences?.Scope ?? ViewScopeKind.Shared;
    // A resolved Mine scope carries the owner profile as response context, but
    // scopeProfileId is a request discriminator only for explicit Profile scope.
    public Guid? ScopeProfileId => ScopeKind == ViewScopeKind.Profile
        ? Scopes?.Scope.ProfileId ?? Preferences?.ScopeProfileId
        : null;
    public ViewTimelineDensity Density => Preferences?.TimelineDensity ?? ViewTimelineDensity.Comfortable;
    public bool ViewerInfoOpen => Preferences?.ViewerInfoOpen ?? true;
    public IReadOnlyList<ViewGalleryDto> OwnedGalleries { get; private set; } = [];
    public IReadOnlyList<ViewGalleryDto> SharedGalleries { get; private set; } = [];
    public IReadOnlyList<Guid> PendingNewGalleryItems { get; private set; } = [];

    public void Reset()
    {
        _initialized = false;
        _galleriesLoaded = false;
        Scopes = null;
        Preferences = null;
        OwnedGalleries = [];
        SharedGalleries = [];
        PendingNewGalleryItems = [];
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        var preferencesTask = api.GetViewPreferencesAsync(ct);
        var scopesTask = api.GetViewScopesAsync(ct: ct);
        await Task.WhenAll(preferencesTask, scopesTask);
        Preferences = await preferencesTask;
        Scopes = await scopesTask;
        _initialized = true;
    }

    public async Task<bool> SelectScopeAsync(ViewScopeKind kind, Guid? profileId, CancellationToken ct = default)
    {
        profileId = kind == ViewScopeKind.Profile ? profileId : null;
        var saved = await api.UpdateViewPreferencesAsync(kind, profileId, Density, ViewerInfoOpen, ct);
        if (saved is null)
        {
            return false;
        }

        Preferences = saved;
        Scopes = await api.GetViewScopesAsync(kind, profileId, ct);
        return Scopes is not null;
    }

    public async Task<bool> SetDensityAsync(ViewTimelineDensity density, CancellationToken ct = default)
    {
        var saved = await api.UpdateViewPreferencesAsync(ScopeKind, ScopeProfileId, density, ViewerInfoOpen, ct);
        if (saved is null)
        {
            return false;
        }

        Preferences = saved;
        return true;
    }

    public async Task<bool> SetViewerInfoOpenAsync(bool open, CancellationToken ct = default)
    {
        if (Preferences is not null)
        {
            Preferences = Preferences with { ViewerInfoOpen = open };
        }
        var saved = await api.UpdateViewPreferencesAsync(ScopeKind, ScopeProfileId, Density, open, ct);
        if (saved is null)
        {
            return false;
        }
        Preferences = saved;
        return true;
    }

    public async Task LoadGalleriesAsync(bool force = false, CancellationToken ct = default)
    {
        if (_galleriesLoaded && !force)
        {
            return;
        }

        var result = await api.GetViewGalleriesAsync(ct);
        OwnedGalleries = result?.Owned.OrderBy(gallery => gallery.SortOrder).ThenBy(gallery => gallery.Name).ToList() ?? [];
        SharedGalleries = result?.SharedWithYou.OrderBy(gallery => gallery.Name).ToList() ?? [];
        _galleriesLoaded = true;
    }

    public void StageNewGalleryItems(IReadOnlyCollection<Guid> itemIds) => PendingNewGalleryItems = [.. itemIds.Distinct()];

    public IReadOnlyList<Guid> TakePendingNewGalleryItems()
    {
        var result = PendingNewGalleryItems;
        PendingNewGalleryItems = [];
        return result;
    }
}

public sealed class ViewAssetDragService
{
    public IReadOnlyList<Guid> AssetIds { get; private set; } = [];
    public bool HasItems => AssetIds.Count > 0;

    public void Begin(IEnumerable<Guid> assetIds) => AssetIds = [.. assetIds.Distinct()];
    public void Clear() => AssetIds = [];
}
