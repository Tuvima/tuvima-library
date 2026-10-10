using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed class ViewWorkspaceService(IEngineApiClient api)
{
    private bool _initialized;
    private bool _galleriesLoaded;

    public ViewScopeResolutionDto? Scopes { get; private set; }
    public ViewPreferencesDto? Preferences { get; private set; }
    /// <summary>Other households a server administrator can open (read-only); empty for everyone else.</summary>
    public ViewOtherPeopleDto? OtherPeople { get; private set; }
    public ViewScopeKind ScopeKind => Scopes?.Scope.Kind ?? Preferences?.Scope ?? ViewScopeKind.Shared;
    // A resolved Mine scope carries the owner profile as response context, but
    // scopeProfileId is a request discriminator only for explicit Profile scope.
    public Guid? ScopeProfileId => ScopeKind.CarriesProfileId()
        ? Scopes?.Scope.ProfileId ?? Preferences?.ScopeProfileId
        : null;
    /// <summary>True when <paramref name="profileId"/> is a person in another household (browsed read-only through "Other people").</summary>
    public bool IsOtherPerson(Guid? profileId) => OtherPersonName(profileId) is not null;

    public string? OtherPersonName(Guid? profileId) => profileId is { } id
        ? OtherPeople?.Households.SelectMany(household => household.People).FirstOrDefault(person => person.ProfileId == id)?.DisplayName
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
        OtherPeople = null;
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
        if (Scopes?.CanBrowseOtherPeople == true)
        {
            OtherPeople = await api.GetViewOtherPeopleAsync(ct);
        }
        _initialized = true;
    }

    public async Task<bool> SelectScopeAsync(ViewScopeKind kind, Guid? profileId, CancellationToken ct = default)
    {
        profileId = kind.CarriesProfileId() ? profileId : null;
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
