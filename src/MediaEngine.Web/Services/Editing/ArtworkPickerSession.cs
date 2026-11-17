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
    string? FileName = null,
    bool InOwnFolder = false);

/// <summary>One picture to make Preferred: the item, the slot, and the picture (an asset id, or <see cref="EditSessionArtworkTargets.None"/>).</summary>
public sealed record ArtworkChoice(Guid EntityId, string Slot, string TargetId);

/// <summary>
/// Makes pictures the Preferred ones through the Engine's edit session, the same check and save as Save changes (so the same permission,
/// refusals and write-back). The editor host builds it, so staging and saving stay in one place; nothing is staged for Review.
/// </summary>
public delegate Task<EditSessionSaveOutcome> ArtworkChangeSaver(IReadOnlyList<ArtworkChoice> choices);

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

    /// <summary>
    /// Whether a picture lives in the person's own folder or inside their own file (the Engine's source is a local one: the file's embedded picture,
    /// a picture found beside it, or a source that is not named). Library never deletes such a picture: its menu offers Hide, not Delete. A picture
    /// Library downloaded from a provider or the person uploaded can be deleted (artwork spec v8 D4).
    /// </summary>
    public static bool IsInOwnFolder(string? sourceProvider) =>
        string.IsNullOrWhiteSpace(sourceProvider)
        || sourceProvider.Trim().ToLowerInvariant() is "embedded" or "folder" or "library_scanner"
        || sourceProvider.StartsWith("local", StringComparison.OrdinalIgnoreCase);

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
/// What the Artwork tool does (universal editor v7 D9). Choosing a picture changes the item at once: a version clicked, a provider picture,
/// an upload, a pasted link and a Library picture each go through the Engine's edit session (the same check, permission and write-back as Save
/// changes) as one artwork change, nothing waits in the pending list, and there is no Undo: the earlier pictures stay in the versions row, so
/// picking one again reverses it. Pictures that have to exist before they can be chosen (an upload, a pasted link, a provider picture) are
/// stored as unchosen pictures of the item first, then chosen. While a change is going through the preview shows a busy state; when it fails the
/// previous picture stays and <see cref="Failure"/> says what failed, with <see cref="RetryChangeAsync"/> behind Try again.
/// </summary>
public sealed class ArtworkPickerSession
{
    private const long MaximumUploadBytes = 10 * 1024 * 1024;
    private const string ChangeFailed = "The picture was not changed.";

    private readonly IEngineApiClient _api;
    private readonly EditSessionState _session;
    private readonly ItemFieldEditSession _fields;
    private readonly ArtworkEditPreview _preview;
    private readonly ArtworkChangeSaver _applyChoices;
    private readonly ILogger? _logger;
    private Func<Task<bool>>? _retry;

    public ArtworkPickerSession(
        IEngineApiClient api,
        EditSessionState session,
        ItemFieldEditSession fields,
        ArtworkEditPreview preview,
        ArtworkChangeSaver applyChoices,
        ILogger? logger = null)
    {
        _api = api;
        _session = session;
        _fields = fields;
        _preview = preview;
        _applyChoices = applyChoices;
        _logger = logger;
    }

    public event Action? Changed;

    /// <summary>Raised each time a picture change went through, so the editor can refresh the page behind it when the window closes.</summary>
    public event Action? Applied;

    public IReadOnlyList<ArtworkPickerTab> Tabs => ArtworkPickerRules.TabsFor(_fields.ArtworkSlots, _fields.MediaType);

    public string? ActiveSlot { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>A picture change is going through the Engine: the preview shows a short busy state while the buttons stay where they are.</summary>
    public bool Applying { get; private set; }

    /// <summary>One plain line about the last picture change that did not go through (what failed); null when the last one did. The previous picture stays.</summary>
    public string? Failure { get; private set; }

    /// <summary>Whether Try again can repeat the change that failed.</summary>
    public bool CanRetryChange => Failure is not null && _retry is not null;

    /// <summary>A plain sentence about the last thing that could not be done; empty when all is well.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            // Any later sentence replaces the one about the saved pictures, so Try again never sits beside an unrelated message.
            _message = value;
            SavedLoadFailed = false;
        }
    }

    private string? _message;

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

    /// <summary>The asset that is Preferred for a role now, or null when none.</summary>
    public Guid? PreferredAssetId(string slot) =>
        _bySlot.TryGetValue(slot, out var versions) ? versions.FirstOrDefault(version => version.IsCurrent)?.AssetId : null;

    /// <summary>The Preferred version of a role, or null (no artwork).</summary>
    public ArtworkPickerPicture? PreferredVersion(string slot) =>
        PreferredAssetId(slot) is { } id && _bySlot.TryGetValue(slot, out var versions)
            ? versions.FirstOrDefault(version => version.AssetId == id)
            : null;

    /// <summary>
    /// The versions of a role for the strip: the Preferred one first, the others in the order they were added (the order the Engine lists them).
    /// Clicking one makes it Preferred at once and moves it to the front (spec D19).
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

    /// <summary>The picture a role card shows: the item's Preferred one.</summary>
    public string? RolePictureUrl(string slot) => _preview.ShownUrl(slot);

    public bool ProviderLoading { get; private set; }

    public ProviderArtworkDiscoveryDto? Provider { get; private set; }

    /// <summary>Opens the picker on a slot (the poster or cover tab when the slot is not one this item has).</summary>
    public async Task OpenAsync(string? slot)
    {
        var tabs = Tabs;
        var target = tabs.FirstOrDefault(tab => string.Equals(tab.Slot, slot, StringComparison.OrdinalIgnoreCase))?.Slot
                     ?? tabs.FirstOrDefault(tab => ArtworkPickerRules.RoleFor(tab.Slot) == "Primary")?.Slot
                     ?? tabs.FirstOrDefault()?.Slot;
        await SelectSlotAsync(target);
    }

    /// <summary>Moves to another tab.</summary>
    public async Task SelectSlotAsync(string? slot)
    {
        ActiveSlot = slot;
        Provider = null;
        Saved = [];
        Message = null;
        ClearFailure();
        Changed?.Invoke();
        if (slot is not null)
        {
            await LoadSavedAsync();
        }
    }

    /// <summary>Closes the picker.</summary>
    public void Close()
    {
        ActiveSlot = null;
        ClearFailure();
    }

    /// <summary>
    /// The item being edited changed while the picker is open (spec D7): the tabs follow the new item, and the picker stays on the same kind
    /// of picture when the new item has it.
    /// </summary>
    public async Task FollowItemAsync()
    {
        var slot = ActiveSlot;
        ActiveSlot = null;
        Provider = null;
        Saved = [];
        Message = null;
        ClearFailure();
        await OpenAsync(slot);
    }

    /// <summary>Try again after the saved pictures did not come back: the plain line goes and the read is made again.</summary>
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
                Message = "Saved pictures could not be loaded.";
                SavedLoadFailed = true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "The saved pictures for {Slot} could not be loaded", slot);
            Message = "Saved pictures could not be loaded.";
            SavedLoadFailed = true;
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
                variant.FileName,
                ArtworkPickerRules.IsInOwnFolder(variant.SourceProvider)))
            .ToList();
    }

    /// <summary>
    /// Clicking a version makes it Preferred at once (spec D9, D19) and moves it to the front of the strip. The version that is Preferred
    /// already is left as it is.
    /// </summary>
    public Task<bool> MakePreferredAsync(ArtworkPickerPicture version)
    {
        var slot = ActiveSlot;
        var entityId = _fields.ArtworkEntityId;
        if (slot is null || entityId == Guid.Empty || PreferredAssetId(slot) == version.AssetId)
        {
            return Task.FromResult(false);
        }

        return RunChangeAsync(() => ApplyAsync(entityId, slot, version.AssetId.ToString("D")));
    }

    /// <summary>
    /// "Delete" a picture Library downloaded or the person uploaded, or "Hide" one that lives in the person's own folder (after the person agreed):
    /// both take the picture off this item at once through the Engine's one remove action, which never deletes the shared file itself. The
    /// versions are read again. <paramref name="hide"/> only changes the sentence told when it did not go through.
    /// </summary>
    public async Task<bool> DeleteVersionAsync(ArtworkPickerPicture version, bool hide = false)
    {
        if (ActiveSlot is not { } slot || Busy || version.LinkId == Guid.Empty)
        {
            return false;
        }

        Busy = true;
        Message = null;
        ClearFailure();
        Changed?.Invoke();
        try
        {
            if (!await _api.RemoveArtworkLinkAsync(version.LinkId))
            {
                Message = hide ? "That picture could not be hidden. Try again." : "That picture could not be deleted. Try again.";
                return false;
            }

            Applied?.Invoke();
            await LoadSavedAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "A picture for {Slot} could not be removed", slot);
            Message = hide ? "That picture could not be hidden. Try again." : "That picture could not be deleted. Try again.";
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

    /// <summary>Choosing a provider picture adds it to the item and makes it Preferred at once.</summary>
    public Task<bool> ChooseProviderAsync(ProviderArtworkCandidateDto candidate)
    {
        // The item and role are fixed here, before anything is awaited: a header switch to another item while the picture is being
        // imported must not send the choice to the new item.
        var slot = ActiveSlot;
        var entityId = _fields.ArtworkEntityId;
        if (slot is null || candidate.AlreadyAdded || entityId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return RunChangeAsync(async () =>
        {
            var imported = await ImportProviderAsync(slot, candidate.Id);
            if (imported is null)
            {
                return false;
            }

            // From here the picture is one of the item's versions, so Try again only has to choose it.
            var importedId = imported.Value.ToString("D");
            _retry = () => ApplyAsync(entityId, slot, importedId);
            await LoadSavedAsync();
            return await ApplyAsync(entityId, slot, importedId);
        });
    }

    /// <summary>Stores a picture the person chose from their computer as a picture of the item and makes it Preferred at once.</summary>
    public Task<bool> UploadAsync(IBrowserFile file)
    {
        // The item, kind and role are fixed here, before anything is awaited (see ChooseProviderAsync).
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        var target = ArtworkTarget.From(_fields, _session.ActiveItem?.Title);
        if (slot is null || role is null || Busy)
        {
            return Task.FromResult(false);
        }

        if (file.ContentType is not ("image/jpeg" or "image/png"))
        {
            return Task.FromResult(Reject("Choose a JPEG or PNG image."));
        }

        if (file.Size > MaximumUploadBytes)
        {
            return Task.FromResult(Reject("Artwork must be 10 MB or smaller."));
        }

        return RunChangeAsync(() => StoreAndApplyAsync(target.EntityId, slot, async () =>
        {
            await using var stream = file.OpenReadStream(MaximumUploadBytes);
            return await _api.UploadCanonicalArtworkAsync(
                target.Kind, target.EntityId, role, stream, file.Name,
                target.Title, target.MediaType, null, slot, preferred: false);
        }, "That image could not be uploaded."));
    }

    /// <summary>Stores a picture from a pasted web address as a picture of the item and makes it Preferred at once.</summary>
    public Task<bool> PasteLinkAsync(string? address)
    {
        var slot = ActiveSlot;
        var role = ArtworkPickerRules.RoleFor(slot);
        var target = ArtworkTarget.From(_fields, _session.ActiveItem?.Title);
        var url = address?.Trim();
        if (slot is null || role is null || Busy)
        {
            return Task.FromResult(false);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Task.FromResult(Reject("Paste a web address that starts with http:// or https://."));
        }

        return RunChangeAsync(() => StoreAndApplyAsync(target.EntityId, slot, () => _api.AddArtworkFromUrlAsync(
            target.Kind, target.EntityId,
            new ArtworkFromUrlRequest(url!, role, Preferred: false, EntityLabel: target.Title, MediaType: target.MediaType, SourceAssetType: slot)),
            "That link could not be used."));
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
    /// The Library's picker added these pictures to the item as versions (the same as an upload). The first one is made Preferred at once.
    /// </summary>
    public Task<bool> UseLibraryPicturesAsync(IReadOnlyList<Guid> added)
    {
        var slot = ActiveSlot;
        var entityId = _fields.ArtworkEntityId;
        if (slot is null || added.Count == 0 || entityId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return RunChangeAsync(async () =>
        {
            await LoadSavedAsync();
            return await ApplyAsync(entityId, slot, added[0].ToString("D"));
        });
    }

    /// <summary>Try again after a picture change failed: the same change is made again. False when there is nothing to repeat.</summary>
    public Task<bool> RetryChangeAsync() => _retry is { } retry ? RunChangeAsync(retry) : Task.FromResult(false);

    /// <summary>Where "Fetch all" finds the episodes under the item being edited; set by the editor host from the family tree.</summary>
    public Func<Task<IReadOnlyList<Guid>>>? EpisodeSource { get; set; }

    /// <summary>Whether "Fetch all stills" is offered: a show or season whose episodes the tree can list.</summary>
    public bool CanFetchAllStills => EpisodeSource is not null
        && (string.Equals(_fields.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        && Tabs.Any(tab => string.Equals(tab.Slot, "EpisodeStill", StringComparison.OrdinalIgnoreCase) || tab.Level == ArtworkPickerRules.EpisodesLevel);

    /// <summary>
    /// Fetches the provider's still for every episode under this item and makes each the episode's still at once, in one change to the library.
    /// An episode the provider has no still for is skipped and counted in the sentence in <see cref="Message"/>.
    /// </summary>
    public async Task<int> FetchAllStillsAsync()
    {
        if (Busy || EpisodeSource is null)
        {
            return 0;
        }

        var changed = 0;
        await RunChangeAsync(async () =>
        {
            changed = 0;
            var episodes = await EpisodeSource!();
            var fetched = new List<(Guid Owner, Guid Asset)>();
            var skipped = 0;
            foreach (var episodeId in episodes)
            {
                if (await FetchStillAsync(episodeId) is { } still)
                {
                    fetched.Add(still);
                }
                else
                {
                    skipped++;
                }
            }

            if (fetched.Count > 0)
            {
                var outcome = await _applyChoices(fetched.Select(still => new ArtworkChoice(still.Owner, "EpisodeStill", still.Asset.ToString("D"))).ToList());
                if (!outcome.Saved)
                {
                    return FailChange(FailureText(outcome));
                }

                Applied?.Invoke();
                changed = fetched.Count;
            }

            Message = episodes.Count == 0
                ? "No episodes were found to fetch pictures for."
                : skipped == 0
                    ? $"Added a picture for {changed} episodes."
                    : $"Added a picture for {changed} episodes; {skipped} had none to offer.";
            return true;
        });

        return changed;
    }

    private async Task<(Guid Owner, Guid Asset)?> FetchStillAsync(Guid episodeId)
    {
        var context = await _api.GetMediaEditorContextAsync(episodeId);
        var scope = context?.Scopes.FirstOrDefault(candidate => candidate.ArtworkSlots.Contains("EpisodeStill", StringComparer.OrdinalIgnoreCase));
        if (scope is null)
        {
            return null;
        }

        var owner = scope.ArtworkOwnerEntityId ?? episodeId;
        var found = await _api.DiscoverProviderArtworkAsync(
            owner, scope.ScopeId, "Primary", new ProviderArtworkDiscoveryRequestDto(PageSize: 12, SourceAssetType: "EpisodeStill"));
        var candidate = found?.Items.FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var imported = await _api.ImportProviderArtworkAsync(owner, scope.ScopeId, "Primary", [candidate.Id]);
        return imported?.Items.FirstOrDefault()?.AssetId is { } assetId ? (owner, assetId) : null;
    }

    // -- One picture change --------------------------------------------------------------------------------

    // Runs one change (the busy state on, the earlier failure cleared) and remembers it so Try again can repeat it. A failure is told on
    // one plain line (Failure) and the previous picture stays; an exception from the Engine call is logged and told the same way.
    private async Task<bool> RunChangeAsync(Func<Task<bool>> attempt)
    {
        if (Busy)
        {
            return false;
        }

        _retry = attempt;
        Busy = true;
        Applying = true;
        Failure = null;
        Message = null;
        Changed?.Invoke();
        try
        {
            var done = await attempt();
            if (done)
            {
                _retry = null;
            }

            return done;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger?.LogWarning(ex, "A picture change for {Slot} did not go through", ActiveSlot);
            return FailChange(ChangeFailed);
        }
        finally
        {
            Busy = false;
            Applying = false;
            Changed?.Invoke();
        }
    }

    // The picture becomes the item's Preferred one for the slot through the Engine's edit session (check, then apply): the same permission,
    // refusals and write-back as Save changes. The saved pictures are read again so the preview and the strip show it.
    // The item and role are the ones the person chose for, handed in by the caller: they are not read again here, so a switch to another
    // item or role while a picture was uploading or importing still lands on the item it was chosen for.
    private async Task<bool> ApplyAsync(Guid entityId, string slot, string targetId)
    {
        var outcome = await _applyChoices([new ArtworkChoice(entityId, slot, targetId)]);
        if (!outcome.Saved)
        {
            return FailChange(FailureText(outcome));
        }

        Applied?.Invoke();
        await LoadSavedAsync();
        return true;
    }

    // A stored picture is always chosen for the item and role it was stored for, even when the person has moved to another role or item
    // meanwhile: it was never silently left unchosen. The strip is read again for whatever is open now.
    private async Task<bool> StoreAndApplyAsync(Guid entityId, string slot, Func<Task<ArtworkAssetDto?>> store, string failure)
    {
        var stored = await store();
        if (stored is null)
        {
            return FailChange(_api.LastError ?? failure);
        }

        // From here the picture is one of the item's versions, so Try again only has to choose it.
        var assetId = stored.Id.ToString("D");
        _retry = () => ApplyAsync(entityId, slot, assetId);
        await LoadSavedAsync();
        return await ApplyAsync(entityId, slot, assetId);
    }

    // What an upload or a pasted link is for, read once before any await.
    private readonly record struct ArtworkTarget(string Kind, Guid EntityId, string? MediaType, string? Title)
    {
        public static ArtworkTarget From(ItemFieldEditSession fields, string? title) =>
            new(fields.ArtworkEntityKind, fields.ArtworkEntityId, fields.MediaType, title);
    }

    private async Task<Guid?> ImportProviderAsync(string slot, string candidateId)
    {
        var role = ArtworkPickerRules.RoleFor(slot);
        if (role is null)
        {
            return null;
        }

        var result = await _api.ImportProviderArtworkAsync(_fields.EditorEntityId, _fields.ScopeId, role, [candidateId]);
        if (result?.Items.FirstOrDefault()?.AssetId is { } assetId)
        {
            return assetId;
        }

        FailChange(result?.Items.FirstOrDefault()?.Error ?? "That picture could not be added. Reload the provider pictures and try again.");
        return null;
    }

    private static string FailureText(EditSessionSaveOutcome outcome) =>
        outcome.Failures.FirstOrDefault()?.Reason is { Length: > 0 } reason ? $"{ChangeFailed} {reason}"
        : outcome.Message is { Length: > 0 } message ? $"{ChangeFailed} {message}"
        : ChangeFailed;

    private bool FailChange(string line)
    {
        Failure = line;
        return false;
    }

    // A change that cannot even be tried (the wrong file type, a link that is not a web address) says so; Try again would repeat the same refusal.
    private bool Reject(string line)
    {
        _retry = null;
        Failure = line;
        Changed?.Invoke();
        return false;
    }

    private void ClearFailure()
    {
        Failure = null;
        _retry = null;
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
