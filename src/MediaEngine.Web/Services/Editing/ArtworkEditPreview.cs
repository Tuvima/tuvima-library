namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Artwork tool shows for each picture slot: the picture the item has now (universal editor v7 D9). A picture applies the moment
/// it is chosen, so nothing here is a preview of a choice that waits for Save changes; this only remembers the current picture of each slot
/// so the tool has something to draw before and between reads of the saved pictures.
/// </summary>
public sealed class ArtworkEditPreview
{
    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers the picture the item has now for a slot, so the tool has something to show.</summary>
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

    /// <summary>Whether the item has a picture saved for the slot now.</summary>
    public bool HasCurrent(string slot) => _current.ContainsKey(slot);

    /// <summary>What the Artwork tool shows for a slot: the picture the item has now, or null.</summary>
    public string? ShownUrl(string slot) => _current.TryGetValue(slot, out var current) ? current : null;
}
