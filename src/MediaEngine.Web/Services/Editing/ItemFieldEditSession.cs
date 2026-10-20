using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Per-field editing for Editing Mode on a detail page. It loads the same editor field snapshot the Details
/// section uses, turns it into the same field presentations (so "editable", "locked provider fact" and "inherited"
/// mean exactly what they mean in the editor), and saves one field at a time. A save goes through
/// <c>PUT /library/items/{id}/display-overrides</c> (or the profile preference layer when the value is already a
/// profile override), exactly as the editor's inline field save does. Nothing is staged and nothing is undone:
/// a saved field can simply be edited again.
/// </summary>
public sealed class ItemFieldEditSession
{
    private readonly IEngineApiClient _api;
    private readonly DeviceContextService? _device;
    private readonly ILogger? _logger;
    private MediaEditorContextDto? _context;
    private MediaEditorScopeDto? _scope;
    private string? _mediaType;
    private Guid? _profileId;
    private IReadOnlyDictionary<string, string>? _profileOverrides;
    private ItemEditorPreferencesResponse? _preferences;
    private MediaEditorDetailsPresentation _presentation = new();

    public ItemFieldEditSession(IEngineApiClient api, DeviceContextService? device, ILogger? logger = null)
    {
        _api = api;
        _device = device;
        _logger = logger;
    }

    /// <summary>Raised whenever anything a pencil shows changes (load, open, typing, save).</summary>
    public event Action? Changed;

    /// <summary>Raised after a field was saved so the page can refresh what it shows.</summary>
    public event Func<string, Task>? Saved;

    public bool Loading { get; private set; }

    public bool Loaded { get; private set; }

    public string? LoadError { get; private set; }

    /// <summary>The asset types the active scope can hold artwork for, such as CoverArt, Background and Logo.</summary>
    public IReadOnlyList<string> ArtworkSlots => _scope?.ArtworkSlots ?? [];

    public bool CanEditArtwork => _scope?.CanEditArtwork == true;

    /// <summary>Whether Match and Identity applies to the active scope (the editor's own rule), so the banner only offers it where it works.</summary>
    public bool CanMatch => MediaEditorMatchRule.CanMatch(_context?.MediaType ?? _mediaType, _scope?.ScopeId);

    public Guid EntityId => _scope?.FieldEntityId ?? Guid.Empty;

    public MediaEditorDetailsPresentation Presentation => _presentation;

    /// <summary>Existing library genres, offered while typing a genre.</summary>
    public IReadOnlyList<string> GenreSuggestions { get; private set; } = [];

    /// <summary>Existing library tags, offered while typing a tag.</summary>
    public IReadOnlyList<string> TagSuggestions { get; private set; } = [];

    /// <summary>The key of the field being edited, or null when no field is open.</summary>
    public string? ActiveKey { get; private set; }

    /// <summary>Which pencil opened the editor ("hero", "overview"), so the same field shown twice opens one editor.</summary>
    public string? ActiveOwner { get; private set; }

    public string Draft { get; private set; } = string.Empty;

    public string Baseline { get; private set; } = string.Empty;

    public bool Saving { get; private set; }

    /// <summary>The last failed save for the open field; the typed value is kept.</summary>
    public string? Error { get; private set; }

    /// <summary>The field that was saved last, shown as an inline "Saved" until the next interaction.</summary>
    public string? LastSavedKey { get; private set; }

    public bool HasOpenDraft => ActiveKey is not null;

    /// <summary>True only when the typed value differs from what was loaded: that is unsaved input worth guarding.</summary>
    public bool IsDirty => ActiveKey is not null && !string.Equals(Draft, Baseline, StringComparison.Ordinal);

    /// <summary>Loads (or reloads) the editor field snapshot for the item shown on the page.</summary>
    public async Task LoadAsync(
        Guid editorEntityId,
        string? mediaType,
        string? initialScope,
        Guid? profileId,
        CancellationToken ct = default)
    {
        Loading = true;
        LoadError = null;
        Changed?.Invoke();
        try
        {
            _mediaType = mediaType;
            _profileId = profileId;
            _context = await _api.GetMediaEditorContextAsync(editorEntityId, ct);
            if (_context is null)
            {
                LoadError = "This item's editable fields could not be loaded.";
                Loaded = false;
                return;
            }

            var scopeId = !string.IsNullOrWhiteSpace(initialScope) ? initialScope : _context.InitialScope;
            var scopes = _context.Scopes.OrderBy(scope => scope.Order).ToList();
            _scope = scopes.FirstOrDefault(scope => string.Equals(scope.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
                     ?? scopes.FirstOrDefault();

            _preferences = null;
            _profileOverrides = null;
            if (_scope is not null && profileId is { } profile)
            {
                _preferences = await _api.GetItemEditorPreferencesAsync(_scope.FieldEntityId, profile, ct);
                _profileOverrides = ProfileOverridesFrom(_preferences);
            }

            Loaded = _scope is not null;
            if (!Loaded)
            {
                LoadError = "This item has nothing editable here.";
            }

            Rebuild();
            await LoadSuggestionsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The page moved on while the snapshot was loading; there is nothing to show or report.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Editing Mode could not load the editor fields for {EntityId}", editorEntityId);
            LoadError = "This item's editable fields could not be loaded.";
            Loaded = false;
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    private async Task LoadSuggestionsAsync(CancellationToken ct)
    {
        try
        {
            var genres = _api.GetItemEditorSuggestionsAsync("genre", null, ct);
            var tags = _api.GetItemEditorSuggestionsAsync("tags", null, ct);
            await Task.WhenAll(genres, tags);
            GenreSuggestions = genres.Result;
            TagSuggestions = tags.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions only help typing; the fields work without them.
            _logger?.LogDebug(ex, "Editing Mode suggestions were unavailable");
            GenreSuggestions = [];
            TagSuggestions = [];
        }
    }

    /// <summary>Finds the first of the given keys that the active scope presents (editable or not).</summary>
    public MediaEditorDetailsFieldPresentation? Find(params string[] keys)
    {
        foreach (var key in keys)
        {
            var field = AllFields().FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>All presented fields once each: heading, synopsis, then the editor's primary and additional fields.</summary>
    public IReadOnlyList<MediaEditorDetailsFieldPresentation> AllFields() => EnumerateFields().ToList();

    /// <summary>The empty Tags row the editor offers when no tags exist yet and tags can be added.</summary>
    public MediaEditorDetailsFieldPresentation? EmptyTagsField => _presentation.CanAddTags
        ? new MediaEditorDetailsFieldPresentation(
            "custom_tags", "custom_tags", "Tags", string.Empty, "Label", "text", "Override", "Library override", null,
            true, false, true, 2, string.Empty, string.Empty, false)
        : null;

    public bool IsEditing(string key, string owner) =>
        ActiveKey is not null
        && string.Equals(ActiveKey, key, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ActiveOwner, owner, StringComparison.Ordinal);

    /// <summary>Opens one field. Only one field is open at a time; a second request is refused until the first closes.</summary>
    public bool Begin(MediaEditorDetailsFieldPresentation field, string owner)
    {
        if (!EditorDeviceRule.CanEdit(_device))
        {
            Error = EditorDeviceRule.Message;
            Changed?.Invoke();
            return false;
        }

        if (Saving || ActiveKey is not null || !CanEditField(field))
        {
            return false;
        }

        ActiveKey = field.Key;
        ActiveOwner = owner;
        Baseline = field.RawValue ?? field.Value ?? string.Empty;
        Draft = Baseline;
        Error = null;
        LastSavedKey = null;
        Changed?.Invoke();
        return true;
    }

    public void UpdateDraft(string? value)
    {
        if (ActiveKey is null)
        {
            return;
        }

        Draft = value ?? string.Empty;
        Error = null;
        Changed?.Invoke();
    }

    /// <summary>Closes the open field and drops what was typed.</summary>
    public void Cancel()
    {
        if (Saving)
        {
            return;
        }

        Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops any open field without a save (leaving Editing Mode after the user chose to discard).</summary>
    public void Discard()
    {
        Clear();
        LastSavedKey = null;
        Changed?.Invoke();
    }

    /// <summary>Saves the open field. On failure the field stays open with the typed value and an inline error.</summary>
    public async Task<bool> SaveAsync()
    {
        if (ActiveKey is null || Saving)
        {
            return false;
        }

        if (!EditorDeviceRule.CanEdit(_device))
        {
            Error = EditorDeviceRule.Message;
            Changed?.Invoke();
            return false;
        }

        var field = AllFields().FirstOrDefault(candidate => string.Equals(candidate.Key, ActiveKey, StringComparison.OrdinalIgnoreCase))
                    ?? (string.Equals(ActiveKey, "custom_tags", StringComparison.OrdinalIgnoreCase) ? EmptyTagsField : null);
        var scope = _scope;
        if (field is null || scope is null || !CanEditField(field))
        {
            Error = "This field cannot be changed for this item.";
            Changed?.Invoke();
            return false;
        }

        var overrideKey = string.IsNullOrWhiteSpace(field.OverrideKey) ? field.Key : field.OverrideKey;
        if (!scope.FieldSnapshot.AllowedOverrideKeys.Contains(overrideKey, StringComparer.OrdinalIgnoreCase))
        {
            Error = $"{field.Label} cannot be overridden for this item.";
            Changed?.Invoke();
            return false;
        }

        var isTags = MediaEditorTagRules.IsLibraryTagsKey(overrideKey);
        var profileLayer = !isTags && string.Equals(field.SourceLabel, "This profile", StringComparison.OrdinalIgnoreCase);
        var value = Draft.Trim();
        if (isTags)
        {
            value = MediaEditorTagRules.NormalizeLibraryTags(Draft, out var tagError);
            if (tagError is not null)
            {
                Error = tagError;
                Changed?.Invoke();
                return false;
            }
        }

        Saving = true;
        Error = null;
        Changed?.Invoke();
        var savedKey = field.Key;
        try
        {
            var saved = profileLayer
                ? await SaveProfileLayerAsync(scope.FieldEntityId, overrideKey, value)
                : await SaveLibraryLayerAsync(scope, overrideKey, value, isTags);
            if (!saved)
            {
                Error ??= _api.LastError ?? "This field could not be saved.";
                return false;
            }

            Clear();
            LastSavedKey = savedKey;
            Rebuild();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Editing Mode could not save {Field} for {EntityId}", savedKey, scope.FieldEntityId);
            Error = "This field could not be saved.";
            return false;
        }
        finally
        {
            Saving = false;
            Changed?.Invoke();
        }

        if (Saved is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<string, Task>>())
            {
                await handler(savedKey);
            }
        }

        return true;
    }

    private async Task<bool> SaveLibraryLayerAsync(MediaEditorScopeDto scope, string overrideKey, string value, bool isTags)
    {
        var saved = await _api.SaveItemDisplayOverridesAsync(
            scope.FieldEntityId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [overrideKey] = value });
        if (!saved)
        {
            return false;
        }

        var snapshot = scope.FieldSnapshot;
        if (string.IsNullOrWhiteSpace(value) && !isTags)
        {
            snapshot.DisplayOverrides.Remove(overrideKey);
            _context?.DisplayOverrides.Remove(overrideKey);
        }
        else
        {
            snapshot.DisplayOverrides[overrideKey] = value;
            if (_context is not null)
            {
                _context.DisplayOverrides[overrideKey] = value;
            }
        }

        return true;
    }

    private async Task<bool> SaveProfileLayerAsync(Guid entityId, string overrideKey, string value)
    {
        if (_profileId is not { } profileId)
        {
            Error = "Choose a profile before saving this local override.";
            return false;
        }

        var baseline = _preferences ?? await _api.GetItemEditorPreferencesAsync(entityId, profileId);
        if (baseline is null)
        {
            Error = _api.LastError ?? "Profile preferences could not be loaded.";
            return false;
        }

        var overrides = baseline.DisplayOverrides
            .Where(pair => !MediaEditorTagRules.IsLibraryTagsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            overrides.Remove(overrideKey);
        }
        else
        {
            overrides[overrideKey] = value;
        }

        var result = await _api.SaveItemEditorPreferencesAsync(
            entityId,
            profileId,
            new ItemEditorPreferencesRequest
            {
                ExpectedRevision = baseline.Revision,
                DisplayOverrides = overrides,
                LocalTags = baseline.LocalTags.ToList(),
            });
        if (!result.Saved)
        {
            Error = result.Conflict
                ? "New library changes are available. Close this field and try again."
                : result.Error ?? "This field could not be saved.";
            return false;
        }

        _preferences = result.Preferences ?? baseline;
        _profileOverrides = ProfileOverridesFrom(_preferences);
        return true;
    }

    private bool CanEditField(MediaEditorDetailsFieldPresentation field) =>
        field.CanOverride
        && !string.Equals(field.Provenance, "Inherited", StringComparison.OrdinalIgnoreCase)
        && _scope?.CanEditFields == true;

    private IEnumerable<MediaEditorDetailsFieldPresentation> EnumerateFields()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<MediaEditorDetailsFieldPresentation?> { _presentation.Heading, _presentation.Synopsis };
        ordered.AddRange(_presentation.Fields);
        ordered.AddRange(_presentation.AdditionalFields);
        ordered.AddRange(_presentation.Facts);
        foreach (var field in ordered)
        {
            if (field is not null && seen.Add(field.Key))
            {
                yield return field;
            }
        }
    }

    private void Rebuild()
    {
        if (_scope is null || _context is null)
        {
            _presentation = new();
            return;
        }

        var scopes = _context.Scopes.ToDictionary(scope => scope.ScopeId, StringComparer.OrdinalIgnoreCase);
        var mediaType = !string.IsNullOrWhiteSpace(_context.MediaType) ? _context.MediaType : _mediaType;
        _presentation = MediaEditorDetailsPresenter.Build(_scope, scopes, mediaType, draft: null, _profileOverrides);
    }

    private void Clear()
    {
        ActiveKey = null;
        ActiveOwner = null;
        Draft = string.Empty;
        Baseline = string.Empty;
        Error = null;
    }

    private static IReadOnlyDictionary<string, string>? ProfileOverridesFrom(ItemEditorPreferencesResponse? preferences) =>
        preferences?.DisplayOverrides
            .Where(pair => !MediaEditorTagRules.IsLibraryTagsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
