using MediaEngine.Application.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Knows which library items are a Series, so the edit session can refuse to write to them (universal editor v5 D7, D24). A Series of
/// books, audiobooks, comics or films is the root of a family whose members are the real items: it has no fields or picture of its own
/// to change. This reads the family's structure the same way the editor header does (an item of such a library with members under it is
/// a Series); it writes nothing.
/// </summary>
public sealed class EditorSeriesGuard(IMediaEditorNavigationReadService navigation)
{
    private static readonly HashSet<string> SeriesMediaTypes = new(StringComparer.OrdinalIgnoreCase) { "Movies", "Books", "Audiobooks", "Comics" };

    /// <summary>True when the library item with this id is a Series (a container of films, books, audiobooks or comics, not one of them).</summary>
    public async Task<bool> IsSeriesAsync(Guid workId, CancellationToken ct = default)
    {
        var navigator = await navigation.GetNavigatorAsync(workId, ct, structureOnly: true).ConfigureAwait(false);
        if (navigator is not { Enabled: true } || !SeriesMediaTypes.Contains(navigator.MediaType))
        {
            return false;
        }

        var node = navigator.Nodes.FirstOrDefault(entry => entry.EntityId == workId);
        return node is { IsLeaf: false };
    }
}
