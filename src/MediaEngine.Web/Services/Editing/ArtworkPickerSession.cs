using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One tab of the Artwork tool: the slot it edits and what it is called.</summary>
public sealed record ArtworkPickerTab(string Slot, string Label, string Level = ArtworkPickerRules.ThisItemLevel);

/// <summary>The tabs of one level of the Artwork tool (the item's own pictures, its seasons' pictures, its episodes' stills).</summary>
public sealed record ArtworkPickerGroup(string Level, IReadOnlyList<ArtworkPickerTab> Tabs);

/// <summary>A picture already saved for the item and slot (one version), as the picker lists it, with the facts the Artwork tool shows.</summary>
public sealed record ArtworkPickerPicture(
    Guid AssetId,
    string ThumbnailUrl,
    string ContentUrl,
    string Caption,
    bool IsCurrent,
    Guid LinkId = default,
    int? Width = null,
    int? Height = null,
    long? FileSizeBytes = null,
    string? ContentType = null,
    string Source = "Saved",
    string? FileName = null);

/// <summary>The shape a role's pictures have; the tool draws thumbnails and the preview at it.</summary>
public enum ArtworkRoleShape
{
    Portrait,
    Landscape,
    Wide,
    Square,
}

/// <summary>Which tabs the picker offers and how a slot maps to the Engine's artwork roles.</summary>
public static class ArtworkPickerRules
{
    private static readonly string[] SlotOrder = ["Background", "CoverArt", "Headshot", "CharacterPortrait", "Logo", "SeasonPoster", "SeasonThumb", "EpisodeStill"];

    /// <summary>The tabs for the slots an item can hold: Background, Poster (Cover for books, comics and music) and Logo, or the season and episode pictures.</summary>
    public static IReadOnlyList<ArtworkPickerTab> TabsFor(IReadOnlyList<string> slots, string? mediaType)
    {
        var cover = IsCoverMedia(mediaType) ? "Cover" : "Poster";
        return slots
            .Where(slot => RoleFor(slot) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(slot => Rank(slot))
            .Select(slot => new ArtworkPickerTab(Canonical(slot), LabelFor(slot, cover), LevelOf(slot)))
            .ToList();
    }

    /// <summary>The level that holds the item's own pictures.</summary>
    public const string ThisItemLevel = "This item";

    public const string SeasonsLevel = "Seasons";

    public const string EpisodesLevel = "Episodes";

    /// <summary>The level a slot belongs to: the item's own pictures, those of the seasons below it, or the stills of the episodes below that.</summary>
    public static string LevelOf(string? slot) => slot?.Trim().ToLowerInvariant() switch
    {
        "seasonposter" or "seasonthumb" => SeasonsLevel,
        "episodestill" => EpisodesLevel,
        _ => ThisItemLevel,
    };

    /// <summary>The tabs grouped by level, in the order This item, Seasons, Episodes; a level with no tab is left out.</summary>
    public static IReadOnlyList<ArtworkPickerGroup> GroupByLevel(IReadOnlyList<ArtworkPickerTab> tabs) =>
        new[] { ThisItemLevel, SeasonsLevel, EpisodesLevel }
            .Select(level => new ArtworkPickerGroup(level, tabs.Where(tab => tab.Level == level).ToList()))
            .Where(group => group.Tabs.Count > 0)
            .ToList();

    /// <summary>The Engine's artwork role a slot belongs to, or null for a slot the picker does not edit.</summary>
    public static string? RoleFor(string? slot) => slot?.Trim().ToLowerInvariant() switch
    {
        "coverart" or "seasonposter" or "episodestill" => "Primary",
        "background" or "seasonthumb" => "Background",
        "headshot" or "characterportrait" => "Main picture",
        "logo" => "Logo",
        _ => null,
    };

    public static string LabelFor(string slot, string coverLabel = "Poster") => slot.Trim().ToLowerInvariant() switch
    {
        "coverart" => coverLabel,
        "headshot" => "Photo",
        "characterportrait" => "Main picture",
        "background" => "Background",
        "logo" => "Logo",
        "seasonposter" => "Season poster",
        "seasonthumb" => "Season thumbnail",
        "episodestill" => "Episode still",
        _ => slot,
    };

    /// <summary>The real shape of a role's pictures, so thumbnails and the preview are never cropped to something else.</summary>
    public static ArtworkRoleShape ShapeFor(string? slot, string? mediaType) => slot?.Trim().ToLowerInvariant() switch
    {
        "background" or "seasonthumb" or "episodestill" => ArtworkRoleShape.Landscape,
        "logo" => ArtworkRoleShape.Wide,
        "coverart" when mediaType is not null
            && (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase)) => ArtworkRoleShape.Square,
        _ => ArtworkRoleShape.Portrait,
    };

    /// <summary>A picture counts as useful for a role from this many pixels on its longer side; below it the tool marks it "Small".</summary>
    public static int UsefulLongSide(string? slot) => slot?.Trim().ToLowerInvariant() switch
    {
        "background" or "seasonthumb" or "episodestill" => 1280,
        "logo" => 500,
        _ => 1200,
    };

    /// <summary>Whether a picture of these dimensions is below the role's useful size. Unknown dimensions are never marked.</summary>
    public static bool IsSmall(string? slot, int? width, int? height)
    {
        var longSide = Math.Max(width ?? 0, height ?? 0);
        return longSide > 0 && longSide < UsefulLongSide(slot);
    }

    /// <summary>The sentence that says which items a change reaches ("Only this episode's still changes").</summary>
    public static string ReachFor(string slot, string? mediaType, string? subjectKind)
    {
        var label = LabelFor(slot, IsCoverMedia(mediaType) ? "Cover" : "Poster").ToLowerInvariant();
        var level = LevelOf(slot);
        if (level == EpisodesLevel)
        {
            return "Only this episode's still changes.";
        }

        if (level == SeasonsLevel)
        {
            return $"Only this season's {label.Replace("season ", string.Empty, StringComparison.Ordinal)} changes.";
        }

        var isShow = subjectKind is null && string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase);
        if (isShow && slot.Equals("CoverArt", StringComparison.OrdinalIgnoreCase))
        {
            return "This show's poster; seasons without their own poster use it too.";
        }

        if (isShow && slot.Equals("Background", StringComparison.OrdinalIgnoreCase))
        {
            return "This show's background; seasons without their own background use it too.";
        }

        return $"Only this {NounFor(mediaType, subjectKind)}'s {label} changes.";
    }

    private static string NounFor(string? mediaType, string? subjectKind)
    {
        if (subjectKind is not null)
        {
            return subjectKind.ToLowerInvariant();
        }

        var media = mediaType ?? string.Empty;
        return media.Contains("movie", StringComparison.OrdinalIgnoreCase) ? "film"
            : media.Equals("TV", StringComparison.OrdinalIgnoreCase) ? "show"
            : media.Contains("audiobook", StringComparison.OrdinalIgnoreCase) ? "audiobook"
            : media.Contains("book", StringComparison.OrdinalIgnoreCase) ? "book"
            : media.Contains("comic", StringComparison.OrdinalIgnoreCase) ? "comic"
            : media.Contains("music", StringComparison.OrdinalIgnoreCase) ? "album"
            : media.Contains("person", StringComparison.OrdinalIgnoreCase) ? "person"
            : "item";
    }

    private static bool IsCoverMedia(string? mediaType) =>
        mediaType is not null
        && (mediaType is EditorSubjectKinds.Series or EditorSubjectKinds.Collection or EditorSubjectKinds.Universe
            || mediaType.Contains("book", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("music", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase));

    private static int Rank(string slot)
    {
        var index = Array.FindIndex(SlotOrder, candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? SlotOrder.Length : index;
    }

    private static string Canonical(string slot) =>
        SlotOrder.FirstOrDefault(candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase)) ?? slot;
}

/// <summary>
/// What the Artwork tool does (universal editor spec D8), kept off the screen so it can be tested. Picking a picture only
/// previews it in the tool; "Use this artwork" stages it in the pending list; nothing becomes the chosen picture until Save changes.
/// Pictures that have to exist before they can be shown (an upload, a pasted link, a provider picture) are stored as unchosen pictures
/// of the item, which changes nothing on the page.
/// </summary>
public sealed class ArtworkPickerSession
{
    private const long MaximumUploadBytes = 10 * 1024 * 1024;
    private const string ProviderPrefix = "provider:";

    private readonly IEngineApiClient _api;
    private readonly EditSessionState _session;
    private readonly ItemFieldEditSession _fields;
    private readonly ArtworkEditPreview _preview;
    private readonly ILogger? _logger;
    private string? _pickedProviderId;

    public ArtworkPickerSession(
        IEngineApiClient api,
        EditSessionState session,
        ItemFieldEditSession fields,
        ArtworkEditPreview preview,
        ILogger? logger = null)
    {
        _api = api;
        _session = session;
        _fields = fields;
        _preview = preview;
        _logger = logger;
    }

    public event Action? Changed;

    public IReadOnlyList<ArtworkPickerTab> Tabs => ArtworkPickerRules.TabsFor(_fields.ArtworkSlots, _fields.MediaType);

    public string? ActiveSlot { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>A plain sentence about the last thing that could not be done; empty when all is well.</summary>
    public string? Message { get; private set; }

    public bool SavedLoading { get; private set; }

    /// <summary>The last read of the saved pictures did not come back (offline or the Engine did not answer); the phone editor offers Try again.</summary>
    public bool SavedLoadFailed { get; private set; }

    public IReadOnlyList<ArtworkPickerPicture> Saved { get; private set; } = [];

    private readonly Dictionary<string, IReadOnlyList<ArtworkPickerPicture>> _bySlot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The subject kind for the sentence that says what a change reaches (null for a title).</summary>
    public string? SubjectKind => _fields.SubjectKind;

    public string? MediaType => _fields.MediaType;

    /// <summary>How many versions the item has saved for a role (zero until the roles have loaded).</summary>
    public int VersionCount(string slot) => _bySlot.TryGetValue(slot, out var versions) ? versions.Count : 0;

    /// <summary>The asset that is Preferred for a role now: the staged choice if there is one, else the saved one. Null when none.</summary>
    public Guid? PreferredAssetId(string slot)
    {
        if (_preview.StagedTargetId(slot) is { } staged)
        {
            return Guid.TryParse(staged, out var asset) ? asset : null;
        }

        return _bySlot.TryGetValue(slot, out var versions) ? versions.FirstOrDefault(version => version.IsCurrent)?.AssetId : null;
    }

    /// <summary>The Preferred version of a role, or null (no artwork, or a removal is staged).</summary>
    public ArtworkPickerPicture? PreferredVersion(string slot) =>
        PreferredAssetId(slot) is { } id && _bySlot.TryGetValue(slot, out var versions)
            ? versions.FirstOrDefault(version => version.AssetId == id)
            : null;

    /// <summary>
    /// The versions of a role for the strip: the Preferred one first, the others in the order they were added (the order the Engine lists them).
    /// Clicking one makes it Preferred and moves it to the front (spec D19).
    /// </summary>
    public IReadOnlyList<ArtworkPickerPicture> Versions(string slot)
    {
        if (!_bySlot.TryGetValue(slot, out var versions))
        {
            return [];
        }

        var preferred = PreferredAssetId(slot);
        return preferred is null
            ? versions
            : versions.Where(version => version.AssetId == preferred).Concat(versions.Where(version => version.AssetId != preferred)).ToList();
    }

    /// <summary>The picture a role card shows: the staged or picked picture, else the saved Preferred one.</summary>
    public string? RolePictureUrl(string slot) => _preview.ShownUrl(slot);

    public bool ProviderLoading { get; private set; }

    public ProviderArtworkDiscoveryDto? Provider { get; private set; }

    /// <summary>Whether a picture is picked for the active slot and waits for "Use this artwork".</summary>
    public bool HasPick => ActiveSlot is not null && _preview.HasPick(ActiveSlot);

    /// <summary>The id of the picked picture (a saved picture's id, or "provider:" and a candidate id), or null.</summary>
    public string? PickedTarget => ActiveSlot is null ? null : _preview.PickedTarget(ActiveSlot);

    /// <summary>The picture the page shows for the active slot right now: the pick, else the staged picture, else the one the item has.</summary>
    public string? ShownUrl => ActiveSlot is null ? null : _preview.ShownUrl(ActiveSlot);

    /// <summary>The change staged for the active slot, if any.</summary>
    public bool IsStaged => ActiveSlot is not null && _preview.IsStaged(ActiveSlot);

    public bool IsRemoval => ActiveSlot is not null && _preview.IsRemoval(ActiveSlot);

    /// <summary>Opens the picker on a slot (the poster or cover tab when the slot is not one this item has).</summary>
    public async Task OpenAsync(string? slot)
    {
        var tabs = Tabs;
        var target = tabs.FirstOrDefault(tab => string.Equals(tab.Slot, slot, StringComparison.OrdinalIgnoreCase))?.Slot
                     ?? tabs.FirstOrDefault(tab => ArtworkPickerRules.RoleFor(tab.Slot) == "Primary")?.Slot
                     ?? tabs.FirstOrDefault()?.Slot;
        await SelectSlotAsync(target);
    }

    /// <summary>Moves to another tab. A pick that was not used is dropped.</summary>
    public async Task SelectSlotAsync(string? slot)
    {
        if (ActiveSlot is not null && !string.Equals(ActiveSlot, slot, StringComparison.OrdinalIgnoreCase))
        {
            _preview.ClearPicks();
            _pickedProviderId = null;
        }

        ActiveSlot = slot;
        Provider = null;
        Saved = [];
        Message = null;
        Changed?.Invoke();
        if (slot is not null)
        {
            await LoadSavedAsync();
        }
    }

    /// <summary>Whether a change is staged for a role (it shows "Changed, not saved" on its card).</summary>
    public bool IsSlotStaged(string slot) => _preview.IsStaged(slot);

    /// <summary>Drops the picture picked in the Add artwork panel without adding it (Back to the role).</summary>
    public void DiscardPick()
    {
        if (ActiveSlot is { } slot)
        {
            _preview.ClearPick(slot);
            _pickedProviderId = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Closes the picker: unused picks go away.</summary>
    public void Close()
    {
        _preview.ClearPicks();
        _pickedProviderId = null;
        ActiveSlot = null;
    }

    /// <summary>
    /// The item being edited changed while the picker is open (spec D7): a pick that was not used is dropped, the tabs follow the new item,
    /// and the picker stays on the same kind of picture when the new item has it. Pictures already staged for either item stay staged.
    /// </summary>
    public async Task FollowItemAsync()
    {
        var slot = ActiveSlot;
        _preview.ClearPicks();
        _pickedProviderId = null;
        ActiveSlot = null;
        Provider = null;
        Saved = [];
        Message = null;
        await OpenAsync(slot);
    }

    /// <summary>Try again after the saved pictures did not come back: the plain line goes and the read is made again; nothing staged is touched.</summary>
    public Task RetryLoadSavedAsync()
    {
        Message = null;
        return LoadSavedAsync();
    }

    /// <summary>Lists the pictures already saved for the item and slot.</summary>
    public async Task LoadSavedAsync()
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        if (slot is null || role is null || _fields.ArtworkEntityId == Guid.Empty)
        {
            return;
        }

        SavedLoading = true;
        SavedLoadFailed = false;
        Changed?.Invoke();
        try
        {
            var workspace = await _api.GetEntityArtworkAsync(
                _fields.ArtworkEntityKind, _fields.ArtworkEntityId, _fields.MediaType, null, _fields.ArtworkSlots);
            if (!string.Equals(slot, ActiveSlot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var tab in Tabs)
            {
                var versions = VersionsOf(workspace, tab.Slot);
                _bySlot[tab.Slot] = versions;
                _preview.SetCurrent(tab.Slot, versions.FirstOrDefault(picture => picture.IsCurrent)?.ContentUrl);
            }

            Saved = VersionsOf(workspace, slot);
            if (workspace is null)
            {
                SavedLoadFailed = true;
                Message = "Saved pictures could not be loaded.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "The saved pictures for {Slot} could not be loaded", slot);
            SavedLoadFailed = true;
            Message = "Saved pictures could not be loaded.";
        }
        finally
        {
            SavedLoading = false;
            Changed?.Invoke();
        }
    }

    internal static IReadOnlyList<ArtworkPickerPicture> VersionsOf(ArtworkEntityWorkspaceDto? workspace, string slot)
    {
        var role = ArtworkPickerRules.RoleFor(slot);
        return (workspace?.Variants ?? [])
            .Where(variant => string.Equals(variant.Role, role, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(variant.SourceAssetType)
                    || string.Equals(variant.SourceAssetType, slot, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(variant => variant.ArtworkAssetId)
            .Select(variant => new ArtworkPickerPicture(
                variant.ArtworkAssetId,
                variant.ThumbnailUrl,
                variant.ContentUrl,
                Caption(variant),
                variant.IsPreferred,
                variant.LinkId,
                variant.Width,
                variant.Height,
                variant.FileSizeBytes,
                variant.ContentType,
                SourceOf(variant),
                variant.FileName))
            .ToList();
    }

    /// <summary>
    /// Clicking a version makes it Preferred (spec D19): it is staged for Save changes and moves to the front of the strip.
    /// Clicking the version that is saved as Preferred while another is staged takes the staged choice back.
    /// </summary>
    public void MakePreferred(ArtworkPickerPicture version)
    {
        var slot = ActiveSlot;
        if (slot is null || _fields.ArtworkEntityId == Guid.Empty || PreferredAssetId(slot) == version.AssetId)
        {
            return;
        }

        _pickedProviderId = null;
        _preview.ClearPick(slot);
        if (version.IsCurrent)
        {
            UndoStaged();
            return;
        }

        var assetId = version.AssetId.ToString("D");
        _session.Pending.SetArtwork(_fields.ArtworkEntityId, slot, assetId, before: BeforeText(slot));
        _preview.RememberStaged(slot, assetId, version.ContentUrl);
        Changed?.Invoke();
    }

    /// <summary>
    /// "Delete this version" (after the person agreed): removes the picture from this item at once. Delete is the one artwork action that
    /// does not wait for Save changes (spec 3b). A staged choice of the deleted picture is taken back.
    /// </summary>
    public async Task<bool> DeleteVersionAsync(ArtworkPickerPicture version)
    {
        if (ActiveSlot is not { } slot || Busy || version.LinkId == Guid.Empty)
        {
            return false;
        }

        Busy = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            if (!await _api.RemoveArtworkLinkAsync(version.LinkId))
            {
                Message = "That picture could not be deleted. Try again.";
                return false;
            }

            if (_preview.StagedTargetId(slot) == version.AssetId.ToString("D"))
            {
                UndoStaged();
            }

            _preview.ClearPick(slot);
            await LoadSavedAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "A picture for {Slot} could not be deleted", slot);
            Message = "That picture could not be deleted. Try again.";
            return false;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Asks the item's providers for pictures for the active slot.</summary>
    public async Task LoadProviderAsync()
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        if (slot is null || role is null || ProviderLoading)
        {
            return;
        }

        ProviderLoading = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            Provider = await _api.DiscoverProviderArtworkAsync(
                _fields.EditorEntityId,
                _fields.ScopeId,
                role,
                new ProviderArtworkDiscoveryRequestDto(Page: 1, PageSize: 48, SourceAssetType: slot));
            if (Provider is null)
            {
                Message = "Provider artwork could not be loaded. Check the provider connection and try again.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "Provider artwork for {Slot} could not be loaded", slot);
            Provider = null;
            Message = "Provider artwork could not be loaded. Check the provider connection and try again.";
        }
        finally
        {
            ProviderLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Picks a provider picture: the page previews its thumbnail, nothing is written until it is used.</summary>
    public void PickProvider(ProviderArtworkCandidateDto candidate)
    {
        if (ActiveSlot is null || candidate.AlreadyAdded)
        {
            return;
        }

        _pickedProviderId = candidate.Id;
        _preview.Pick(ActiveSlot, ProviderPrefix + candidate.Id, candidate.ThumbnailUrl);
        Changed?.Invoke();
    }

    /// <summary>Stores a picture the person chose from their computer as an unchosen picture of the item, and picks it.</summary>
    public async Task<bool> UploadAsync(IBrowserFile file)
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        if (slot is null || role is null || Busy)
        {
            return false;
        }

        if (file.ContentType is not ("image/jpeg" or "image/png"))
        {
            return Fail("Choose a JPEG or PNG image.");
        }

        if (file.Size > MaximumUploadBytes)
        {
            return Fail("Artwork must be 10 MB or smaller.");
        }

        return await StoreAsync(slot, async () =>
        {
            await using var stream = file.OpenReadStream(MaximumUploadBytes);
            return await _api.UploadCanonicalArtworkAsync(
                _fields.ArtworkEntityKind, _fields.ArtworkEntityId, role, stream, file.Name,
                _session.ActiveItem?.Title, _fields.MediaType, null, slot, preferred: false);
        }, "That image could not be uploaded.");
    }

    /// <summary>Stores a picture from a pasted web address as an unchosen picture of the item, and picks it.</summary>
    public async Task<bool> PasteLinkAsync(string? address)
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        var url = address?.Trim();
        if (slot is null || role is null || Busy)
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Fail("Paste a web address that starts with http:// or https://.");
        }

        return await StoreAsync(slot, () => _api.AddArtworkFromUrlAsync(
            _fields.ArtworkEntityKind, _fields.ArtworkEntityId,
            new ArtworkFromUrlRequest(url!, role, Preferred: false, EntityLabel: _session.ActiveItem?.Title, MediaType: _fields.MediaType, SourceAssetType: slot)),
            "That link could not be used.");
    }

    /// <summary>Whether "Choose from Library" can be offered: a role is open and the item has an artwork owner to link a Library picture to (every kind, a Universe and its things included).</summary>
    public bool CanChooseFromLibrary => ActiveSlot is not null && _fields.ArtworkEntityId != Guid.Empty;

    /// <summary>
    /// What the Library's picture picker opens on for the active role: the Library's own pictures, the ones already used by this item first, then
    /// the ones of its family and its other things (a Universe's characters, places and Series), then everything else. Null when no role is open.
    /// </summary>
    public ArtworkAssetQuery? LibraryQuery()
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        if (slot is null || role is null || !CanChooseFromLibrary)
        {
            return null;
        }

        return new ArtworkAssetQuery(
            TargetEntityType: _fields.ArtworkEntityKind,
            TargetEntityId: _fields.ArtworkEntityId,
            TargetRole: role,
            TargetSourceAssetType: slot,
            PickerScope: ArtworkPickerScope.Recommended,
            Limit: 48);
    }

    /// <summary>The item the Library's picker adds a chosen picture to; its words are shown in the picker's title.</summary>
    public ArtworkLibraryItemDto? LibraryTarget() =>
        CanChooseFromLibrary
            ? new ArtworkLibraryItemDto(
                _fields.ArtworkEntityId,
                _fields.ArtworkEntityKind,
                _session.ActiveItem?.Title ?? string.Empty,
                _fields.MediaType,
                Year: null,
                Subtitle: null,
                ImageUrl: null,
                PrimaryAssetType: null,
                AssetTypes: [],
                VariantCount: 0,
                OwnedWorkCount: 0,
                UsesLegacyPersonImage: false)
            : null;

    /// <summary>
    /// The Library's picker added these pictures to the item as unchosen versions (the same as an upload: nothing on the page changes). The
    /// first one is picked here, ready for "Add version"; nothing becomes Preferred, and nothing is staged, until then.
    /// </summary>
    public async Task<bool> UseLibraryPicturesAsync(IReadOnlyList<Guid> added)
    {
        var slot = ActiveSlot;
        if (slot is null || added.Count == 0)
        {
            return false;
        }

        _pickedProviderId = null;
        await LoadSavedAsync();
        if (!string.Equals(slot, ActiveSlot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var first = added[0];
        _preview.Pick(slot, first.ToString("D"), Saved.FirstOrDefault(picture => picture.AssetId == first)?.ContentUrl);
        Changed?.Invoke();
        return true;
    }

    /// <summary>"Use this artwork": stages the picked picture. Nothing is saved until Save changes.</summary>
    public async Task<bool> UseAsync(bool makePreferred = true)
    {
        var slot = ActiveSlot;
        var target = PickedTarget;
        if (slot is null || target is null || Busy || _fields.ArtworkEntityId == Guid.Empty)
        {
            return false;
        }

        Busy = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            string? url = null;
            string assetId;
            if (target.StartsWith(ProviderPrefix, StringComparison.Ordinal))
            {
                var imported = await ImportProviderAsync(slot, _pickedProviderId ?? target[ProviderPrefix.Length..]);
                if (imported is null)
                {
                    return false;
                }

                assetId = imported.Value.ToString("D");
                await LoadSavedAsync();
                url = Saved.FirstOrDefault(picture => picture.AssetId == imported)?.ContentUrl;
            }
            else
            {
                assetId = target;
                url = Saved.FirstOrDefault(picture => picture.AssetId.ToString("D") == target)?.ContentUrl;
            }

            _pickedProviderId = null;
            if (!makePreferred)
            {
                // Added as one more version only: it is saved with the item but nothing becomes Preferred.
                _preview.ClearPick(slot);
                return true;
            }

            _session.Pending.SetArtwork(_fields.ArtworkEntityId, slot, assetId, before: BeforeText(slot));
            _preview.RememberStaged(slot, assetId, url);
            return true;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>"Remove artwork" (after the person agreed): stages leaving no picture chosen for the slot. The pictures stay saved.</summary>
    public void Remove()
    {
        if (ActiveSlot is not { } slot || _fields.ArtworkEntityId == Guid.Empty)
        {
            return;
        }

        _pickedProviderId = null;
        _session.Pending.SetArtwork(_fields.ArtworkEntityId, slot, EditSessionArtworkTargets.None, before: BeforeText(slot));
        _preview.RememberStaged(slot, EditSessionArtworkTargets.None, null);
        Changed?.Invoke();
    }

    /// <summary>Takes the staged choice for the active slot back out of the pending list.</summary>
    public void UndoStaged()
    {
        if (ActiveSlot is { } slot && _preview.StagedChangeId(slot) is { } changeId)
        {
            _session.Pending.Remove(changeId);
            Changed?.Invoke();
        }
    }

    private async Task<bool> StoreAsync(string slot, Func<Task<ArtworkAssetDto?>> store, string failure)
    {
        Busy = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            var stored = await store();
            if (stored is null)
            {
                return Fail(_api.LastError ?? failure);
            }

            if (!string.Equals(slot, ActiveSlot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _pickedProviderId = null;
            _preview.Pick(slot, stored.Id.ToString("D"), stored.ContentUrl);
            await LoadSavedAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger?.LogWarning(ex, "A picture for {Slot} could not be stored", slot);
            return Fail(failure);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    private async Task<Guid?> ImportProviderAsync(string slot, string candidateId)
    {
        var role = ArtworkPickerRules.RoleFor(slot);
        if (role is null)
        {
            return null;
        }

        try
        {
            var result = await _api.ImportProviderArtworkAsync(_fields.EditorEntityId, _fields.ScopeId, role, [candidateId]);
            if (result?.Items.FirstOrDefault()?.AssetId is { } assetId)
            {
                return assetId;
            }

            Message = result?.Items.FirstOrDefault()?.Error
                      ?? "That picture could not be added. Reload the provider pictures and try again.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "A provider picture for {Slot} could not be added", slot);
            Message = "That picture could not be added. Reload the provider pictures and try again.";
        }

        return null;
    }

    /// <summary>Where "Fetch all" finds the episodes under the item being edited; set by the editor host from the family tree.</summary>
    public Func<Task<IReadOnlyList<Guid>>>? EpisodeSource { get; set; }

    /// <summary>Whether "Fetch all stills" is offered: a show or season whose episodes the tree can list.</summary>
    public bool CanFetchAllStills => EpisodeSource is not null
        && (string.Equals(_fields.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        && Tabs.Any(tab => string.Equals(tab.Slot, "EpisodeStill", StringComparison.OrdinalIgnoreCase) || tab.Level == ArtworkPickerRules.EpisodesLevel);

    /// <summary>
    /// Fetches the provider's still for every episode under this item and stages each as a change (nothing is saved until Save changes).
    /// An episode the provider has no still for is skipped and counted in the sentence in <see cref="Message"/>.
    /// </summary>
    public async Task<int> FetchAllStillsAsync()
    {
        if (Busy || EpisodeSource is null)
        {
            return 0;
        }

        Busy = true;
        Message = null;
        Changed?.Invoke();
        var staged = 0;
        var skipped = 0;
        try
        {
            var episodes = await EpisodeSource();
            foreach (var episodeId in episodes)
            {
                var fetched = await FetchStillAsync(episodeId);
                if (fetched)
                {
                    staged++;
                }
                else
                {
                    skipped++;
                }
            }

            Message = episodes.Count == 0
                ? "No episodes were found to fetch pictures for."
                : skipped == 0
                    ? $"Added a picture for {staged} episodes. Save changes to keep them."
                    : $"Added a picture for {staged} episodes; {skipped} had none to offer. Save changes to keep them.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "Pictures for the episodes could not be fetched");
            Message = staged == 0
                ? "The episode pictures could not be fetched. Check the provider connection and try again."
                : $"Added a picture for {staged} episodes before the connection failed. Save changes to keep them.";
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }

        return staged;
    }

    private async Task<bool> FetchStillAsync(Guid episodeId)
    {
        var context = await _api.GetMediaEditorContextAsync(episodeId);
        var scope = context?.Scopes.FirstOrDefault(candidate => candidate.ArtworkSlots.Contains("EpisodeStill", StringComparer.OrdinalIgnoreCase));
        if (scope is null)
        {
            return false;
        }

        var owner = scope.ArtworkOwnerEntityId ?? episodeId;
        var found = await _api.DiscoverProviderArtworkAsync(
            owner, scope.ScopeId, "Primary", new ProviderArtworkDiscoveryRequestDto(PageSize: 12, SourceAssetType: "EpisodeStill"));
        var candidate = found?.Items.FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        var imported = await _api.ImportProviderArtworkAsync(owner, scope.ScopeId, "Primary", [candidate.Id]);
        if (imported?.Items.FirstOrDefault()?.AssetId is not { } assetId)
        {
            return false;
        }

        _session.Pending.SetArtwork(owner, "EpisodeStill", assetId.ToString("D"), "Episode picture", EditReviewRows.NoPictureYet);
        return true;
    }

    // Review's Before for a picture: the item has one saved for the slot, or none.
    private string BeforeText(string slot) =>
        _preview.HasCurrent(slot) ? "The current picture" : EditReviewRows.NoPictureYet;

    private bool Fail(string message)
    {
        Message = message;
        Changed?.Invoke();
        return false;
    }

    private static string SourceOf(ArtworkEntityVariantDto variant) =>
        string.Equals(variant.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
            ? "Uploaded"
            : string.IsNullOrWhiteSpace(variant.SourceProvider) ? "Saved" : variant.SourceProvider!;

    private static string Caption(ArtworkEntityVariantDto variant)
    {
        var source = SourceOf(variant);
        return variant.Width is > 0 && variant.Height is > 0 ? $"{source} · {variant.Width} × {variant.Height}" : source;
    }
}
