using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Artwork tool shows while the choice is only a preview (universal editor spec D8). A picture the person has picked shows at
/// once; a picture they staged with "Use this artwork" keeps showing until Save changes; nothing here is written to the library. A
/// staged picture only counts while its change is still in the pending list, so removing the change in Review (or Cancel) puts the tool
/// back by itself. The page behind the pop-over never shows any of it (spec D1).
/// </summary>
public sealed class ArtworkEditPreview
{
    /// <summary>The slots the page does not normally show: choosing one of these opens the labelled temporary view (spec D9).</summary>
    private static readonly string[] TemporarySlots = ["SeasonPoster", "SeasonThumb", "EpisodeStill"];

    private readonly EditSessionState _session;
    private readonly ItemFieldEditSession _fields;
    private readonly Dictionary<string, Shown> _picked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid Entity, string Slot), Shown> _staged = new();
    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);

    public ArtworkEditPreview(EditSessionState session, ItemFieldEditSession fields)
    {
        _session = session;
        _fields = fields;
    }

    private sealed record Shown(string TargetId, string? Url);

    /// <summary>Remembers the picture the item has now for a slot, so the tool has something to show before a pick.</summary>
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

    /// <summary>Whether the item has a picture saved for the slot now (Review's Before for a changed picture).</summary>
    public bool HasCurrent(string slot) => _current.ContainsKey(slot);

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
        _staged[(_fields.ArtworkEntityId, slot.Trim().ToLowerInvariant())] = new Shown(targetId, url);
        _picked.Remove(slot);
    }

    /// <summary>Whether the pending list holds a choice for the slot (including "remove artwork").</summary>
    public bool IsStaged(string slot) => FindStaged(slot) is not null;

    /// <summary>Whether the staged choice for the slot is "remove artwork".</summary>
    public bool IsRemoval(string slot) =>
        string.Equals(FindStaged(slot)?.TargetId, EditSessionArtworkTargets.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of the pending change for the slot, so it can be taken back.</summary>
    public string? StagedChangeId(string slot) => FindStaged(slot)?.Id;

    /// <summary>The picture to show for a slot: the pick, else the staged picture, else null.</summary>
    public string? Url(string slot) => UrlFor(slot, _fields.ArtworkEntityId);

    // A pick belongs to the item being edited; a staged picture belongs to the item it was staged for.
    private string? UrlFor(string slot, Guid entityId)
    {
        if (entityId == _fields.ArtworkEntityId && _picked.TryGetValue(slot, out var picked) && !string.IsNullOrWhiteSpace(picked.Url))
        {
            return picked.Url;
        }

        var change = FindStaged(slot, entityId);
        return change is not null
               && _staged.TryGetValue((entityId, slot.Trim().ToLowerInvariant()), out var staged)
               && string.Equals(staged.TargetId, change.TargetId, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(staged.Url)
            ? staged.Url
            : null;
    }

    /// <summary>What the Artwork tool shows for a slot: the pick or staged picture, else the picture the item has now.</summary>
    public string? ShownUrl(string slot) => Url(slot) ?? (_current.TryGetValue(slot, out var current) ? current : null);

    private EditSessionChange? FindStaged(string slot) => FindStaged(slot, _fields.ArtworkEntityId);

    private EditSessionChange? FindStaged(string slot, Guid entityId)
    {
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
