using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components.Forms;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private ArtworkLibraryItemDto? CollectionArtworkItem => EditingCollection is null
        ? null
        : new ArtworkLibraryItemDto(
            EditingCollection.Id,
            "Collection",
            EditingCollection.Name,
            EditingCollection.PrimaryArea,
            null,
            EditingCollection.CollectionType,
            EditingCollection.CoverUrl,
            "CoverArt",
            new[] { "CoverArt", "Background", "Logo" },
            new[] { EditingCollection.CoverUrl, EditingCollection.BackgroundUrl, EditingCollection.LogoUrl }.Count(url => !string.IsNullOrWhiteSpace(url)),
            EditingCollection.ItemCount,
            false)
        {
            IsStructural = true,
            GroupKind = EditingCollection.CollectionType,
            ResolutionMode = !string.IsNullOrWhiteSpace(EditingCollection.CoverUrl)
                ? ArtworkResolutionMode.Explicit
                : AutomaticArtworkItems.Count > 0
                    ? ArtworkResolutionMode.AutomaticGroup
                    : ArtworkResolutionMode.None,
            BackgroundImageUrl = EditingCollection.BackgroundUrl,
            LogoImageUrl = EditingCollection.LogoUrl,
            PreviewItems = AutomaticArtworkItems.Select(item => new ArtworkLibraryPreviewItemDto(
                item.WorkId ?? Guid.Empty,
                item.AssetId,
                item.Title,
                item.ImageUrl,
                item.Shape switch { ArtworkShape.Square => "square", ArtworkShape.Wide => "wide", _ => "portrait" },
                item.Position,
                item.MediaType)).ToList(),
        };

    private Task OnArtworkSelected(InputFileChangeEventArgs args) => OnArtworkChosenAsync(args.File);

    private async Task OnArtworkChosenAsync(IBrowserFile? file)
    {
        if (file is null)
        {
            return;
        }

        ClearNotice();

        var slot = IsPlaylistDialog ? "poster" : _activeArtworkSlot;
        if (slot == "logo" && !string.Equals(Path.GetExtension(file.Name), ".png", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice(L["CollectionEditorShell_CollectionLogosMustBeTransparent"].Value, AppUiTone.Warning);
            return;
        }
        _artworkFiles[slot] = file;
        _removedArtworkSlots.Remove(slot);
        MarkDirty();

        try
        {
            await using var stream = file.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            _artworkPreviewUrls[slot] = $"data:{file.ContentType ?? "image/jpeg"};base64,{Convert.ToBase64String(ms.ToArray())}";
        }
        catch
        {
            _artworkFiles.Remove(slot);
            _artworkPreviewUrls.Remove(slot);
            ShowNotice(L["CollectionEditorShell_ArtworkPreviewCouldNotBe"].Value, AppUiTone.Warning);
        }
    }

    // The artwork preview fills the panel (a poster up to 480px tall, a 16:9 background or a logo about 700px wide): the medium copy.
    private const int ArtworkPreviewDrawnPx = 960;

    private string? SelectedArtworkPreviewUrl => _artworkPreviewUrls.GetValueOrDefault(_activeArtworkSlot);
    private string? CurrentArtworkUrl => _removedArtworkSlots.Contains(_activeArtworkSlot) ? null : _activeArtworkSlot switch
    {
        "background" => EditingCollection?.BackgroundUrl,
        "logo" => EditingCollection?.LogoUrl,
        _ => EditingCollection?.CoverUrl,
    };
    private string ArtworkSlotTitle => _activeArtworkSlot switch { "background" => "Background", "logo" => "Logo", _ => "Poster / Cover" };
    private string ArtworkSlotDescription => _activeArtworkSlot switch
    {
        "background" => "Optional custom landscape artwork behind the collection detail presentation.",
        "logo" => "Optional transparent collection branding shown without a boxed background.",
        _ => "Automatic representative artwork or an optional custom portrait image.",
    };
    private string ArtworkUploadDescription => _activeArtworkSlot switch { "background" => "Upload a cinematic landscape image.", "logo" => "Upload transparent collection branding.", _ => "Upload a custom portrait image or keep the automatic stack." };
    private string ArtworkFileRequirements => _activeArtworkSlot == "logo" ? "Transparent PNG, up to 5 MB" : "JPG or PNG, up to 5 MB";
    private string ArtworkAccept => _activeArtworkSlot == "logo" ? ".png" : ".jpg,.jpeg,.png";
    private string ArtworkGuidance => _activeArtworkSlot switch
    {
        "background" => "Use high-contrast 16:9 artwork near 1920 × 1080. Keep important subjects away from text areas.",
        "logo" => "Use a wide transparent canvas near 1600 × 600 with generous clear padding. Do not bake in a background.",
        _ => "Automatic stacked is the default and updates with membership. Custom posters work best at a 2:3 ratio near 1000 × 1500.",
    };
    private string ArtworkEmptyTitle => _activeArtworkSlot switch { "background" => "Neutral collection background", "logo" => "No custom logo", _ => "Automatic stacked artwork" };
    private string ArtworkEmptyDescription => _activeArtworkSlot switch { "background" => "The standard Library collection wash will be used until a custom background is uploaded.", "logo" => "The collection name remains the visual identity until a transparent logo is uploaded.", _ => "Representative item art appears as members become available." };
    private string ArtworkSlotIcon => _activeArtworkSlot switch { "background" => AppMaterialIcons.Outlined.Panorama, "logo" => AppMaterialIcons.Outlined.BrandingWatermark, _ => AppMaterialIcons.Outlined.AutoAwesomeMotion };
    // The automatic artwork is previewed on the real group tile, so what is shown here is what the Collections page draws.
    private MediaTileViewModel AutomaticPreviewTile => new()
    {
        Id = EditingCollection?.Id ?? Guid.Empty,
        Title = EditingCollection?.Name ?? _name,
        ArtworkStackItems = AutomaticArtworkItems,
        PreviewTotalCount = EditingCollection?.ItemCount ?? AutomaticArtworkItems.Count,
        IsCollection = true,
        UseGroupTile = true,
        GroupKind = EditingCollection?.CollectionType switch
        {
            "Series" => MediaTileGroupKind.Series,
            "Universe" => MediaTileGroupKind.Universe,
            "Set" => MediaTileGroupKind.Set,
            _ => MediaTileGroupKind.Collection,
        },
        NavigationUrl = "/",
    };

    private IReadOnlyList<ArtworkStackItem> AutomaticArtworkItems => (SupportsManualItems
            ? _manualItems.Select(item => (item.WorkId, item.Title, item.CoverUrl, item.MediaType))
            : (_previewResult?.Items ?? []).Select(item => (item.EntityId, item.Title, item.CoverUrl, item.MediaType)))
        .Where(item => !string.IsNullOrWhiteSpace(item.Item3))
        .Take(4)
        .Select(item => new ArtworkStackItem
        {
            Id = item.Item1.ToString("N"),
            WorkId = item.Item1,
            Title = item.Item2,
            ImageUrl = item.Item3!,
            MediaType = item.Item4,
            Shape = item.Item4.Equals("Music", StringComparison.OrdinalIgnoreCase) ? ArtworkShape.Square : ArtworkShape.Portrait,
        }).ToList();

    private async Task RemoveArtworkAsync()
    {
        if (EditingCollection is null)
        {
            return;
        }

        ClearNotice();
        if (!await ApiClient.DeleteCollectionArtworkAsync(EditingCollection.Id, _activeArtworkSlot, Request.ActiveProfileId))
        {
            ShowNotice(L["CollectionEditorShell_ArtworkCouldNotBeRemoved"].Value, AppUiTone.Error);
            return;
        }
        _artworkFiles.Remove(_activeArtworkSlot);
        _artworkPreviewUrls.Remove(_activeArtworkSlot);
        _removedArtworkSlots.Add(_activeArtworkSlot);
        // The preview now shows the automatic stack (or the empty slot): that is the confirmation.
    }
}
