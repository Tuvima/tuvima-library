using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the page shows for artwork while the choice is only a preview (universal editor spec D8, D9). A picture the person has picked
/// in the Artwork tool shows at once; a picture they staged with "Use this artwork" keeps showing until Save changes; nothing here is
/// written to the library. A staged picture only counts while its change is still in the pending list, so removing the change in Review
/// (or Cancel) puts the page back by itself.
/// </summary>
public sealed class ArtworkEditPreview
{
    /// <summary>The slots the page does not normally show: choosing one of these opens the labelled temporary view (spec D9).</summary>
    private static readonly string[] TemporarySlots = ["SeasonPoster", "SeasonThumb", "EpisodeStill"];

    private readonly EditSessionState _session;
    private readonly ItemFieldEditSession _fields;
    private readonly Dictionary<string, Shown> _picked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Shown> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);

    public ArtworkEditPreview(EditSessionState session, ItemFieldEditSession fields)
    {
        _session = session;
        _fields = fields;
    }

    private sealed record Shown(string TargetId, string? Url);

    /// <summary>True for a slot the page does not normally show (season poster, season thumbnail, episode still).</summary>
    public static bool UsesTemporaryView(string? slot) =>
        slot is not null && TemporarySlots.Contains(slot, StringComparer.OrdinalIgnoreCase);

    private string? _temporarySlot;

    /// <summary>The slot the temporary view is showing, or null while the page is in its normal view.</summary>
    public string? TemporarySlot => _session.IsActive && _session.ViewContext == EditViewContext.TemporaryArtwork ? _temporarySlot : null;

    /// <summary>Remembers the picture the item has now for a slot, so the temporary view has something to show before a pick.</summary>
    public void SetCurrent(string slot, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _current.Remove(slot);
        }
        else
        {
            _current[slot] = url;
        }
    }

    /// <summary>Shows a picture the person has picked but not yet used. Replaces an earlier pick for the slot.</summary>
    public void Pick(string slot, string targetId, string? url) => _picked[slot] = new Shown(targetId, url);

    /// <summary>Drops the pick for a slot (another slot was chosen, or the tool was closed).</summary>
    public void ClearPick(string slot) => _picked.Remove(slot);

    public void ClearPicks() => _picked.Clear();

    public bool HasPick(string slot) => _picked.ContainsKey(slot);

    /// <summary>The target id of the picked picture, or null.</summary>
    public string? PickedTarget(string slot) => _picked.TryGetValue(slot, out var picked) ? picked.TargetId : null;

    /// <summary>Remembers the preview of a picture that was staged, so the page keeps showing it until Save changes.</summary>
    public void RememberStaged(string slot, string targetId, string? url)
    {
        _staged[slot] = new Shown(targetId, url);
        _picked.Remove(slot);
    }

    /// <summary>Whether the pending list holds a choice for the slot (including "remove artwork").</summary>
    public bool IsStaged(string slot) => FindStaged(slot) is not null;

    /// <summary>Whether the staged choice for the slot is "remove artwork".</summary>
    public bool IsRemoval(string slot) =>
        string.Equals(FindStaged(slot)?.TargetId, EditSessionArtworkTargets.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of the pending change for the slot, so it can be taken back.</summary>
    public string? StagedChangeId(string slot) => FindStaged(slot)?.Id;

    /// <summary>The picture the page should show for a slot: the pick, else the staged picture, else null (the page keeps its own).</summary>
    public string? Url(string slot)
    {
        if (_picked.TryGetValue(slot, out var picked) && !string.IsNullOrWhiteSpace(picked.Url))
        {
            return picked.Url;
        }

        var change = FindStaged(slot);
        return change is not null
               && _staged.TryGetValue(slot, out var staged)
               && string.Equals(staged.TargetId, change.TargetId, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(staged.Url)
            ? staged.Url
            : null;
    }

    /// <summary>What the temporary view shows: the pick or staged picture, else the picture the item has now.</summary>
    public string? TemporaryUrl(string slot) => Url(slot) ?? (_current.TryGetValue(slot, out var current) ? current : null);

    public void SetTemporarySlot(string? slot) => _temporarySlot = slot;

    /// <summary>The page's artwork with every previewed picture in place of the stored one. Returns <paramref name="artwork"/> itself when nothing is previewed.</summary>
    public ArtworkSet Apply(ArtworkSet artwork)
    {
        var cover = Url("CoverArt");
        var background = Url("Background");
        var logo = Url("Logo");
        if (cover is null && background is null && logo is null)
        {
            return artwork;
        }

        var hero = artwork.HeroArtwork;
        return new ArtworkSet
        {
            BackdropUrl = background ?? artwork.BackdropUrl,
            BannerUrl = artwork.BannerUrl,
            PosterUrl = cover ?? artwork.PosterUrl,
            CoverUrl = cover ?? artwork.CoverUrl,
            LogoUrl = logo ?? artwork.LogoUrl,
            PortraitUrl = artwork.PortraitUrl,
            RelatedArtworkUrls = artwork.RelatedArtworkUrls,
            DominantColors = artwork.DominantColors,
            PrimaryColor = artwork.PrimaryColor,
            SecondaryColor = artwork.SecondaryColor,
            AccentColor = artwork.AccentColor,
            BackdropLeftTopColor = artwork.BackdropLeftTopColor,
            BackdropLeftMiddleColor = artwork.BackdropLeftMiddleColor,
            BackdropLeftBottomColor = artwork.BackdropLeftBottomColor,
            PresentationMode = artwork.PresentationMode,
            Source = artwork.Source,
            HeroArtwork = background is null
                ? hero
                : new HeroArtworkViewModel
                {
                    Url = background,
                    SmallUrl = background,
                    MediumUrl = background,
                    LargeUrl = background,
                    Mode = hero.Mode,
                    HasImage = true,
                    AspectRatio = hero.AspectRatio,
                    BackgroundPosition = hero.BackgroundPosition,
                    MobilePosition = hero.MobilePosition,
                },
        };
    }

    private EditSessionChange? FindStaged(string slot)
    {
        var entityId = _fields.ArtworkEntityId;
        if (!_session.IsActive || entityId == Guid.Empty)
        {
            return null;
        }

        return _session.Pending.Changes.FirstOrDefault(change =>
            change.Kind == EditSessionChangeKinds.Artwork
            && change.EntityId == entityId
            && string.Equals(change.Slot, slot, StringComparison.OrdinalIgnoreCase));
    }
}
