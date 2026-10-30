namespace MediaEngine.Web.Services.Navigation;

/// <summary>What a tile already knows about the item it opens, so the details page can draw its hero straight away.</summary>
public sealed record DetailPreview(
    IReadOnlyList<Guid> Ids,
    string Title,
    string? CoverUrl,
    string? LogoUrl,
    string? BackdropUrl);

/// <summary>
/// Hand-over from the tile (or hero) the person clicked to the details page that opens next. A preview is used once,
/// only for the item it names, and only if it is still fresh.
/// </summary>
public sealed class DetailPreviewState
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    private DetailPreview? _preview;
    private DateTimeOffset _setAt;

    public void Set(DetailPreview preview)
    {
        _preview = preview;
        _setAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Takes the preview if it names this item; a preview for any other item is dropped.</summary>
    public DetailPreview? TryTake(Guid entityId)
    {
        var preview = _preview;
        _preview = null;
        if (preview is null || DateTimeOffset.UtcNow - _setAt > MaxAge)
        {
            return null;
        }

        return preview.Ids.Contains(entityId) && !string.IsNullOrWhiteSpace(preview.Title) ? preview : null;
    }
}
