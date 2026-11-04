using System.Text.Json;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

/// <summary>How a gallery's cover changes.</summary>
public enum ViewCoverMode
{
    /// <summary>The cover is left as it is.</summary>
    Keep,

    /// <summary>Back to Automatic: Library picks one from the gallery's photos.</summary>
    Automatic,

    /// <summary>A photo of the gallery is the Preferred cover.</summary>
    Item,
}

/// <summary>
/// What the person has changed on one gallery (a manual or Smart View gallery, a saved Documents view, or a Vault image gallery), held until
/// Save changes (spec photos-documents-editor WP4). Pure: it builds the Review rows and the Engine request; it calls nothing.
/// A new gallery has no <see cref="Original"/>; its first Save creates it.
/// </summary>
public sealed class ViewGalleryEditStage
{
    private string _name;
    private string? _description;
    private string? _soundtrack;
    private string? _ruleJson;
    private bool _smart;

    public ViewGalleryEditStage(ViewGalleryDto? original, VaultImageGalleryDto? vaultOriginal, bool newSmart = false)
    {
        Original = original;
        VaultOriginal = vaultOriginal;
        _name = original?.Name ?? vaultOriginal?.Name ?? string.Empty;
        _description = original?.Description ?? vaultOriginal?.Description;
        _soundtrack = original?.SoundtrackPlaylistId?.ToString("D");
        _ruleJson = original?.SmartRuleJson ?? vaultOriginal?.SmartRuleJson;
        _smart = original is not null ? original.Kind == ViewGalleryKind.Smart
            : vaultOriginal is not null ? vaultOriginal.Kind == VaultImageGalleryKinds.Smart : newSmart;
    }

    public ViewGalleryDto? Original { get; }

    public VaultImageGalleryDto? VaultOriginal { get; }

    public bool IsVault => VaultOriginal is not null || _vaultNew;

    private bool _vaultNew;

    /// <summary>Marks a gallery that does not exist yet as a Vault image gallery.</summary>
    public ViewGalleryEditStage AsNewVault()
    {
        _vaultNew = true;
        return this;
    }

    public bool IsNew => Original is null && VaultOriginal is null;

    /// <summary>A saved Documents view: a Smart gallery whose rule is the filters set on the Documents page. It is renamed and described here; its filters change on that page.</summary>
    public bool IsDocumentView => Original is { Kind: ViewGalleryKind.Smart } && ViewSmartGalleryRules.IsDocumentView(Original.SmartRuleJson);

    public Guid? Id => Original?.Id ?? VaultOriginal?.Id;

    public string OriginalName => Original?.Name ?? VaultOriginal?.Name ?? string.Empty;

    public string Name => _name;

    public string? Description => _description;

    public string? Soundtrack => _soundtrack;

    public string? RuleJson => _ruleJson;

    public bool IsSmart => _smart;

    public ViewCoverMode CoverMode { get; private set; }

    public Guid? CoverItemId { get; private set; }

    /// <summary>Photos already chosen for a new gallery (New gallery from the selection bar): they go in when it is created.</summary>
    public List<LocalAssetDto> StartingMembers { get; } = [];

    /// <summary>Photos taken out of the gallery by this Save (they stay in the library).</summary>
    public HashSet<Guid> Removed { get; } = [];

    public event Action? Changed;

    public void SetName(string? value) { _name = (value ?? string.Empty).Trim(); Raise(); }

    public void SetDescription(string? value) { _description = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Raise(); }

    public void SetSoundtrack(string? playlistId) { _soundtrack = string.IsNullOrWhiteSpace(playlistId) ? null : playlistId; Raise(); }

    public void SetSmart(bool value) { _smart = value; Raise(); }

    public void SetRule(string? json) { _ruleJson = json; Raise(); }

    public void SetCover(Guid itemId) { CoverMode = ViewCoverMode.Item; CoverItemId = itemId; Raise(); }

    public void UseAutomaticCover() { CoverMode = ViewCoverMode.Automatic; CoverItemId = null; Raise(); }

    public void RevertCover() { CoverMode = ViewCoverMode.Keep; CoverItemId = null; Raise(); }

    public void Remove(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            Removed.Add(id);
        }

        Raise();
    }

    public void KeepAgain(Guid id)
    {
        if (Removed.Remove(id))
        {
            Raise();
        }
    }

    /// <summary>The cover now: the staged choice, else the gallery's own.</summary>
    public Guid? EffectiveCover => CoverMode switch
    {
        ViewCoverMode.Item => CoverItemId,
        ViewCoverMode.Automatic => null,
        _ => Original?.CoverItemId,
    };

    public bool NameChanged => !IsNew && !string.Equals(_name, OriginalName, StringComparison.Ordinal);

    public bool DescriptionChanged => !IsNew && !string.Equals(_description ?? string.Empty, Original?.Description ?? VaultOriginal?.Description ?? string.Empty, StringComparison.Ordinal);

    public bool SoundtrackChanged => !IsNew && !string.Equals(_soundtrack ?? string.Empty, Original?.SoundtrackPlaylistId?.ToString("D") ?? string.Empty, StringComparison.Ordinal);

    public bool RuleChanged => !IsNew && _smart && !string.Equals(_ruleJson ?? string.Empty, Original?.SmartRuleJson ?? VaultOriginal?.SmartRuleJson ?? string.Empty, StringComparison.Ordinal);

    /// <summary>A new gallery is one change (its creation); otherwise one per changed field.</summary>
    public int ChangeCount => Rows().Count;

    public bool HasChanges => ChangeCount > 0;

    public IReadOnlyList<EditReviewRow> Rows()
    {
        var rows = new List<EditReviewRow>();
        var who = string.IsNullOrWhiteSpace(_name) ? "New gallery" : _name;
        var id = Id ?? Guid.Empty;
        void Add(string key, string what, string? before, string after) =>
            rows.Add(new EditReviewRow("gallery:" + key, null, id, Contracts.Editing.EditSessionChangeKinds.Details, EditReviewGroup.Metadata, who, what, before ?? EditReviewRows.AsItIsNow, after, null));

        if (IsNew)
        {
            Add("create", "Create gallery", null, StartingMembers.Count > 0
                ? $"Gallery \"{_name}\" with {StartingMembers.Count} {(StartingMembers.Count == 1 ? "photo" : "photos")}"
                : $"{(_smart ? "Smart" : "Manual")} gallery \"{_name}\"");
            return rows;
        }

        if (NameChanged) { Add("name", "Name", OriginalName, _name); }
        if (DescriptionChanged) { Add("description", "Description", Original?.Description ?? VaultOriginal?.Description, _description ?? "(empty)"); }
        if (RuleChanged) { Add("rule", "Rule", null, "Rule changed"); }
        if (SoundtrackChanged) { Add("soundtrack", "Soundtrack", null, _soundtrack is null ? "None" : "Playlist chosen"); }
        if (CoverMode != ViewCoverMode.Keep) { Add("cover", "Cover", null, CoverMode == ViewCoverMode.Automatic ? "Automatic" : "A photo from this gallery"); }
        if (Removed.Count > 0) { Add("members", "Photos", null, $"Remove {Removed.Count} {(Removed.Count == 1 ? "photo" : "photos")} from this gallery (they stay in your library)"); }
        return rows;
    }

    /// <summary>The request that creates or updates a View gallery.</summary>
    public ViewGalleryRequest ToViewRequest() => new(
        _name,
        _smart ? ViewGalleryKind.Smart : ViewGalleryKind.Manual,
        _description,
        _smart ? _ruleJson : null,
        CoverMode switch { ViewCoverMode.Item => CoverItemId, ViewCoverMode.Automatic => null, _ => Original?.CoverItemId },
        Original?.SortOrder ?? 0,
        Guid.TryParse(_soundtrack, out var playlist) ? playlist : null);

    /// <summary>The request that creates or updates a Vault image gallery.</summary>
    public VaultImageGalleryRequest ToVaultRequest() => new()
    {
        Name = _name,
        Description = _description,
        Kind = _smart ? VaultImageGalleryKinds.Smart : VaultImageGalleryKinds.Manual,
        SmartRuleJson = _smart ? _ruleJson : null,
        CoverImageId = CoverMode switch { ViewCoverMode.Item => CoverItemId, ViewCoverMode.Automatic => null, _ => VaultOriginal?.CoverImageId },
    };

    /// <summary>A plain reason the gallery cannot be saved yet, or null.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(_name) ? "Give the gallery a name."
        : _smart && string.IsNullOrWhiteSpace(_ruleJson) ? "A Smart gallery needs at least one rule."
        : null;

    public void Clear()
    {
        _name = OriginalName;
        _description = Original?.Description ?? VaultOriginal?.Description;
        _soundtrack = Original?.SoundtrackPlaylistId?.ToString("D");
        _ruleJson = Original?.SmartRuleJson ?? VaultOriginal?.SmartRuleJson;
        CoverMode = ViewCoverMode.Keep;
        CoverItemId = null;
        Removed.Clear();
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}

/// <summary>Reads and writes the rule builder's saved rule (kept apart from the screen so the editor and tests share one copy).</summary>
public static class ViewGalleryRuleJson
{
    public static CollectionRuleDefinitionViewModel Empty() => new() { Groups = [new CollectionRuleGroupViewModel { Conditions = [new CollectionRulePredicateViewModel()] }] };

    public static bool Complete(CollectionRulePredicateViewModel rule) =>
        !string.IsNullOrWhiteSpace(rule.Field)
        && (rule.Op is "known" or "unknown" or "has_any_value" or "has_no_known_value" || !string.IsNullOrWhiteSpace(rule.Value) || rule.Values is { Length: > 0 });

    public static string? Serialize(CollectionRuleDefinitionViewModel source)
    {
        var groups = source.Groups.Select(group => new CollectionRuleGroupDto
        {
            Id = group.Id,
            JoinWithPrevious = group.JoinWithPrevious,
            MatchMode = group.MatchMode,
            Conditions = group.Conditions.Where(Complete).Select(condition => new CollectionRulePredicateDto
            {
                Field = condition.Field,
                Op = condition.Op,
                Value = condition.Value,
                DisplayValue = condition.DisplayValue,
                Values = condition.Values,
                DisplayValues = condition.DisplayValues,
            }).ToList(),
        }).Where(group => group.Conditions.Count > 0).ToList();
        return groups.Count == 0 ? null : JsonSerializer.Serialize(new CollectionRuleDefinitionDto { Version = 1, Groups = groups });
    }

    public static CollectionRuleDefinitionViewModel Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty();
        }

        try
        {
            var dto = JsonSerializer.Deserialize<CollectionRuleDefinitionDto>(json, MediaEngineJson.CaseInsensitive);
            return dto is null ? Empty() : new CollectionRuleDefinitionViewModel
            {
                Version = dto.Version,
                Groups = dto.Groups.Select(group => new CollectionRuleGroupViewModel
                {
                    Id = group.Id,
                    JoinWithPrevious = group.JoinWithPrevious,
                    MatchMode = group.MatchMode,
                    Conditions = group.Conditions.Select(rule => new CollectionRulePredicateViewModel
                    {
                        Field = rule.Field,
                        Op = rule.Op,
                        Value = rule.Value,
                        DisplayValue = rule.DisplayValue,
                        Values = rule.Values,
                        DisplayValues = rule.DisplayValues,
                    }).ToList(),
                }).ToList(),
            };
        }
        catch (JsonException)
        {
            return Empty();
        }
    }
}
