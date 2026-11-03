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

/// <summary>A picture already saved for the item and slot, as the picker lists it.</summary>
public sealed record ArtworkPickerPicture(Guid AssetId, string ThumbnailUrl, string ContentUrl, string Caption, bool IsCurrent);

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
        "headshot" or "characterportrait" => "Portrait",
        "logo" => "Logo",
        _ => null,
    };

    public static string LabelFor(string slot, string coverLabel = "Poster") => slot.Trim().ToLowerInvariant() switch
    {
        "coverart" => coverLabel,
        "headshot" => "Photo",
        "characterportrait" => "Portrait",
        "background" => "Background",
        "logo" => "Logo",
        "seasonposter" => "Season poster",
        "seasonthumb" => "Season thumbnail",
        "episodestill" => "Episode still",
        _ => slot,
    };

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

    public IReadOnlyList<ArtworkPickerPicture> Saved { get; private set; } = [];

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
        Changed?.Invoke();
        try
        {
            var workspace = await _api.GetEntityArtworkAsync(
                _fields.ArtworkEntityKind, _fields.ArtworkEntityId, _fields.MediaType, null, _fields.ArtworkSlots);
            if (!string.Equals(slot, ActiveSlot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Saved = (workspace?.Variants ?? [])
                .Where(variant => string.Equals(variant.Role, role, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(variant.SourceAssetType)
                        || string.Equals(variant.SourceAssetType, slot, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(variant => variant.ArtworkAssetId)
                .Select(variant => new ArtworkPickerPicture(
                    variant.ArtworkAssetId,
                    variant.ThumbnailUrl,
                    variant.ContentUrl,
                    Caption(variant),
                    variant.IsPreferred))
                .ToList();
            _preview.SetCurrent(slot, Saved.FirstOrDefault(picture => picture.IsCurrent)?.ContentUrl);
            if (workspace is null)
            {
                Message = "Saved pictures could not be loaded.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex, "The saved pictures for {Slot} could not be loaded", slot);
            Message = "Saved pictures could not be loaded.";
        }
        finally
        {
            SavedLoading = false;
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

    /// <summary>Picks a saved picture: the page previews it, nothing is written.</summary>
    public void PickSaved(ArtworkPickerPicture picture)
    {
        if (ActiveSlot is null)
        {
            return;
        }

        _pickedProviderId = null;
        _preview.Pick(ActiveSlot, picture.AssetId.ToString("D"), picture.ContentUrl);
        Changed?.Invoke();
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

    /// <summary>"Use this artwork": stages the picked picture. Nothing is saved until Save changes.</summary>
    public async Task<bool> UseAsync()
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

            _session.Pending.SetArtwork(_fields.ArtworkEntityId, slot, assetId, before: BeforeText(slot));
            _preview.RememberStaged(slot, assetId, url);
            _pickedProviderId = null;
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

    private static string Caption(ArtworkEntityVariantDto variant)
    {
        var source = string.Equals(variant.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
            ? "Uploaded"
            : string.IsNullOrWhiteSpace(variant.SourceProvider) ? "Saved" : variant.SourceProvider!;
        return variant.Width is > 0 && variant.Height is > 0 ? $"{source} · {variant.Width} × {variant.Height}" : source;
    }
}
