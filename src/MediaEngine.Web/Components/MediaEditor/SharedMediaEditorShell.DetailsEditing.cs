using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private string? _detailsInlineFieldKey;
    private string _detailsInlineDraft = string.Empty;
    private string _detailsInlineBaseline = string.Empty;
    private bool _detailsInlineSaving;
    private string? _detailsInlineError;
    private bool _detailsDialogEscapeSuppressed;
    private bool? _detailsDialogEscapeBeforeEdit;

    protected bool HasPendingDetailsInlineEdit => _detailsInlineFieldKey is not null;

    protected bool IsDetailsInlineEditing(string key) =>
        string.Equals(_detailsInlineFieldKey, key, StringComparison.OrdinalIgnoreCase);

    protected string DetailsInlineValue(string key) =>
        IsDetailsInlineEditing(key) ? _detailsInlineDraft : string.Empty;

    protected MediaEditorDetailsFieldPresentation? FindDetailsField(string key) =>
        new[] { DetailsHeading }
            .Concat(DetailsPrimaryFacts)
            .Concat(DetailsSecondaryFields)
            .Concat(DetailsAdditionalFields)
            .Append(DetailsSynopsis)
            .FirstOrDefault(field => field is not null
                && (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field.Label, "Title", StringComparison.OrdinalIgnoreCase) && string.Equals(key, "title", StringComparison.OrdinalIgnoreCase)));

    protected static string DetailsFactIcon(string icon) => icon switch
    {
        "CalendarToday" => AppMaterialIcons.Outlined.CalendarToday,
        "Event" => AppMaterialIcons.Outlined.Event,
        "Schedule" => AppMaterialIcons.Outlined.Schedule,
        "Star" => AppMaterialIcons.Outlined.StarOutline,
        "VerifiedUser" => AppMaterialIcons.Outlined.VerifiedUser,
        "ViewAgenda" => AppMaterialIcons.Outlined.ViewAgenda,
        "LiveTv" => AppMaterialIcons.Outlined.LiveTv,
        "Tag" => AppMaterialIcons.Outlined.Label,
        "Album" => AppMaterialIcons.Outlined.Album,
        "QueueMusic" => AppMaterialIcons.Outlined.QueueMusic,
        "MenuBook" => AppMaterialIcons.Outlined.MenuBook,
        "Headphones" => AppMaterialIcons.Outlined.Headphones,
        _ => AppMaterialIcons.Outlined.Info,
    };

    protected MediaEditorDetailsFieldPresentation EmptyDetailsTagsField => new(
        "custom_tags", "custom_tags", "Tags", string.Empty, "Label", "text", "Override", "Library override", null,
        CanAddDetailsTags, false, true, 2, string.Empty, string.Empty, false);

    protected Task BeginEmptyDetailsTagsAsync() => BeginDetailsInlineEditAsync(EmptyDetailsTagsField);

    protected async Task NavigateToDetailsLinkAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || _detailsInlineSaving)
        {
            return;
        }

        if (_navigationGuard.Intercept(location, HasPendingNavigationChanges))
        {
            StateHasChanged();
            return;
        }

        await CloseEditorAsync(_hasCommittedChanges);
        Navigation.NavigateTo(location);
    }

    protected async Task NavigateToDetailsFieldSourceAsync(MediaEditorDetailsFieldPresentation field)
    {
        if (string.IsNullOrWhiteSpace(field.SourceScopeId)
            || GetScopeById(field.SourceScopeId) is not { } sourceScope)
        {
            return;
        }

        var sourceNode = _navigator?.Nodes.FirstOrDefault(node =>
            node.EntityId == sourceScope.FieldEntityId
            && string.Equals(node.ScopeId, sourceScope.ScopeId, StringComparison.OrdinalIgnoreCase));
        if (sourceNode?.CanSelectAsEditorTarget == true)
        {
            await SelectNavigatorNodeAsync(sourceScope.FieldEntityId);
            return;
        }

        await SelectScopeAsync(sourceScope.ScopeId);
    }

    protected Task BeginDetailsInlineEditAsync(MediaEditorDetailsFieldPresentation field)
    {
        if (_detailsInlineSaving || IsInheritedDetailsField(field) || !field.CanOverride || ActiveScope?.CanEditFields != true)
        {
            return Task.CompletedTask;
        }

        _detailsInlineFieldKey = field.Key;
        _detailsInlineBaseline = field.RawValue ?? field.Value ?? string.Empty;
        _detailsInlineDraft = _detailsInlineBaseline;
        _detailsInlineError = null;
        return InvokeAsync(StateHasChanged);
    }

    protected Task UpdateDetailsInlineDraftAsync(string? value)
    {
        _detailsInlineDraft = value ?? string.Empty;
        _detailsInlineError = null;
        return InvokeAsync(StateHasChanged);
    }

    protected Task CancelDetailsInlineEditAsync()
    {
        ClearDetailsInlineEdit();
        return InvokeAsync(StateHasChanged);
    }

    protected async Task SaveDetailsInlineFieldAsync(MediaEditorDetailsFieldPresentation field)
    {
        if (!IsDetailsInlineEditing(field.Key) || _detailsInlineSaving || ActiveScope is null)
        {
            return;
        }

        var selectedScope = ActiveScope;
        var entityId = selectedScope.FieldEntityId;
        var overrideKey = string.IsNullOrWhiteSpace(field.OverrideKey) ? field.Key : field.OverrideKey;
        var isLibraryTags = string.Equals(overrideKey, "custom_tags", StringComparison.OrdinalIgnoreCase);
        var profileLayer = !isLibraryTags && string.Equals(field.SourceLabel, "This profile", StringComparison.OrdinalIgnoreCase);
        var snapshot = selectedScope.FieldSnapshot;
        if (IsInheritedDetailsField(field)
            || !field.CanOverride
            || !snapshot.AllowedOverrideKeys.Contains(overrideKey, StringComparer.OrdinalIgnoreCase))
        {
            _detailsInlineError = $"{field.Label} cannot be overridden for this item.";
            return;
        }

        _detailsInlineSaving = true;
        _detailsInlineError = null;
        StateHasChanged();
        try
        {
            string? validationError = null;
            var value = isLibraryTags
                ? NormalizeLibraryTagsForInlineSave(_detailsInlineDraft, out validationError)
                : _detailsInlineDraft.Trim();
            if (isLibraryTags && validationError is not null)
            {
                _detailsInlineError = validationError;
                return;
            }
            var saved = false;
            if (profileLayer)
            {
                if (Request.ActiveProfileId is null)
                {
                    _detailsInlineError = "Choose a profile before saving this local override.";
                    return;
                }

                if (!_profilePreferencesByWork.TryGetValue(entityId, out var preferences))
                {
                    preferences = await ApiClient.GetItemEditorPreferencesAsync(entityId, Request.ActiveProfileId.Value);
                    if (preferences is null)
                    {
                        _detailsInlineError = ApiClient.LastError ?? "Profile preferences could not be loaded.";
                        return;
                    }
                    if (CurrentEntityId == entityId)
                    {
                        _profilePreferencesByWork[entityId] = preferences;
                    }
                }

                var profileOverrides = new Dictionary<string, string>(preferences.DisplayOverrides, StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(value))
                {
                    profileOverrides.Remove(overrideKey);
                }
                else
                {
                    profileOverrides[overrideKey] = value;
                }
                saved = await SaveProfileEditorPreferencesAsync(
                    entityId,
                    profileOverrides,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                saved = await ApiClient.SaveItemDisplayOverridesAsync(
                    entityId,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [overrideKey] = value });
                if (saved)
                {
                    if (string.IsNullOrWhiteSpace(value) && !isLibraryTags)
                    {
                        snapshot.DisplayOverrides.Remove(overrideKey);
                        _editorContext?.DisplayOverrides.Remove(overrideKey);
                    }
                    else
                    {
                        snapshot.DisplayOverrides[overrideKey] = value;
                        if (_editorContext is not null)
                        {
                            _editorContext.DisplayOverrides[overrideKey] = value;
                        }
                    }
                }
            }

            if (!saved)
            {
                _detailsInlineError ??= ApiClient.LastError ?? "This field could not be saved.";
                return;
            }

            if (CurrentEntityId == entityId)
            {
                ClearDetailsInlineEdit();
                _hasCommittedChanges = true;
                // The override payload is already reflected in the active snapshot/preferences.
                // Avoid reapplying the whole scope here, which resets an unrelated Matching draft.
                var history = await ApiClient.GetItemHistoryWithStatusAsync(entityId);
                if (CurrentEntityId == entityId && ActiveScope?.ScopeId == selectedScope.ScopeId)
                {
                    _history = history.Items;
                    _historyError = history.Error;
                    var stateKey = BuildScopeStateKey(entityId, selectedScope.ScopeId);
                    if (_scopeStates.TryGetValue(stateKey, out var cached))
                    {
                        _scopeStates[stateKey] = new ScopeEditorState
                        {
                            Detail = cached.Detail,
                            CanonicalValues = cached.CanonicalValues,
                            Claims = cached.Claims,
                            History = history.Items,
                            HistoryError = history.Error,
                            Artwork = cached.Artwork,
                        };
                    }
                }
            }
        }
        finally
        {
            _detailsInlineSaving = false;
            StateHasChanged();
        }
    }

    protected async Task RevertDetailsInlineFieldAsync(MediaEditorDetailsFieldPresentation field)
    {
        if (_detailsInlineSaving || ActiveScope is null || IsInheritedDetailsField(field) || !field.CanRevert)
        {
            return;
        }

        _detailsInlineFieldKey = field.Key;
        _detailsInlineBaseline = field.RawValue ?? field.Value ?? string.Empty;
        _detailsInlineDraft = string.Empty;
        await SaveDetailsInlineFieldAsync(field);
    }

    protected string? DetailsInlineError => _detailsInlineError;

    private static bool IsInheritedDetailsField(MediaEditorDetailsFieldPresentation field) =>
        string.Equals(field.Provenance, "Inherited", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLibraryTagsForInlineSave(string value, out string? error)
    {
        var tags = string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (tags.Length > 30)
        {
            error = "Library tags are limited to 30 values.";
            return string.Empty;
        }
        if (tags.Any(tag => tag.Length > 64))
        {
            error = "Each library tag must be 64 characters or fewer.";
            return string.Empty;
        }
        error = null;
        return string.Join("; ", tags);
    }

    private void ClearDetailsInlineEdit()
    {
        _detailsInlineFieldKey = null;
        _detailsInlineDraft = string.Empty;
        _detailsInlineBaseline = string.Empty;
        _detailsInlineError = null;
    }

    protected string DetailsWritebackStatusText
    {
        get
        {
            return DetailsMetadataWritebackState switch
            {
                "enabled" => "Write-back enabled",
                "disabled" => "Write-back disabled",
                "limited" => "Write-back limited",
                _ => "Write-back unavailable",
            };
        }
    }

    protected async Task SynchronizeDetailsDialogEscapeAsync()
    {
        if (DialogContext is null)
        {
            return;
        }

        if (HasPendingDetailsInlineEdit && !_detailsDialogEscapeSuppressed)
        {
            _detailsDialogEscapeBeforeEdit = DialogContext.Options.CloseOnEscapeKey;
            await DialogContext.SetOptionsAsync(DialogContext.Options with { CloseOnEscapeKey = false });
            _detailsDialogEscapeSuppressed = true;
        }
        else if (!HasPendingDetailsInlineEdit && _detailsDialogEscapeSuppressed)
        {
            await DialogContext.SetOptionsAsync(DialogContext.Options with
            {
                CloseOnEscapeKey = _detailsDialogEscapeBeforeEdit,
            });
            _detailsDialogEscapeBeforeEdit = null;
            _detailsDialogEscapeSuppressed = false;
        }
    }

    protected string DetailsWritebackExplanation => DetailsMetadataWritebackState switch
    {
        "enabled" => "Metadata write-back is enabled by policy. This status does not confirm a write to this file.",
        "disabled" => "Metadata write-back is disabled by policy. Library display overrides remain available.",
        "limited" => "Metadata write-back is unavailable or limited for this scope. Library display overrides remain available.",
        _ => "Metadata write-back status is unavailable.",
    };
}
