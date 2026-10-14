using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed class ViewWorkspaceService(IEngineApiClient api)
{
    private bool _initialized;
    private bool _galleriesLoaded;
    private Task? _initializing;
    private int _generation;
    private TaskCompletionSource _ready = NewReady();

    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        _initializing = null;
        _generation++;
        _ready = NewReady();
        _galleriesLoaded = false;
        Scopes = null;
        Preferences = null;
        OtherPeople = null;
        OwnedGalleries = [];
        SharedGalleries = [];
        PendingNewGalleryItems = [];
    }

    /// <summary>
    /// Loads the View settings (preferences, scopes, other people) once. Callers that arrive while the load is
    /// running share it instead of asking the Engine again. The View shell owns this call; a page inside the shell
    /// waits for <see cref="WhenReadyAsync"/> instead of starting its own.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        if (_initializing is { } running)
        {
            return running;
        }

        var load = LoadAsync(_ready, _generation, ct);
        // A load that already ended (even by failing) is not kept, so the next call tries again.
        if (!load.IsCompleted)
        {
            _initializing = load;
        }

        return load;
    }

    /// <summary>
    /// Completes when the View settings have been loaded by <see cref="InitializeAsync"/>, whether or not the load
    /// found anything, so a page can wait for the shell without asking the Engine itself.
    /// </summary>
    public Task WhenReadyAsync() => _initialized ? Task.CompletedTask : _ready.Task;

    private async Task LoadAsync(TaskCompletionSource ready, int generation, CancellationToken ct)
    {
        try
        {
            var preferencesTask = api.GetViewPreferencesAsync(ct);
            var scopesTask = api.GetViewScopesAsync(ct: ct);
            await Task.WhenAll(preferencesTask, scopesTask);
            var preferences = await preferencesTask;
            var scopes = await scopesTask;
            var otherPeople = scopes?.CanBrowseOtherPeople == true ? await api.GetViewOtherPeopleAsync(ct) : null;
            // Settings that arrive after a reset (a different profile) belong to nobody now.
            if (generation == _generation)
            {
                Preferences = preferences;
                Scopes = scopes;
                OtherPeople = otherPeople;
                _initialized = true;
            }
        }
        finally
        {
            // A failed or cancelled load must not leave a waiting page spinning forever, and the next call may retry.
            if (generation == _generation)
            {
                _initializing = null;
            }

            ready.TrySetResult();
        }
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
