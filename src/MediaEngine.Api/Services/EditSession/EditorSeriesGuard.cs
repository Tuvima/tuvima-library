using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Knows which library items are a Series, so the edit session and the write actions can refuse to change them (universal editor v5 D7,
/// D24). A Series of books, audiobooks, comics or films is the root of a family whose members are the real items: it has no fields,
/// picture or match of its own to change. The item's media type is looked up first, so a song, an episode or a photo never builds the
/// family; only a root of one of the four libraries that can hold a Series reads its structure, once. It writes nothing.
/// </summary>
public sealed class EditorSeriesGuard(IDatabaseConnection db, IMediaEditorNavigationReadService navigation)
{
    private static readonly HashSet<string> SeriesMediaTypes = new(StringComparer.OrdinalIgnoreCase) { "Movies", "Books", "Audiobooks", "Comics" };

    /// <summary>True when the library item with this id is a Series (a container of films, books, audiobooks or comics, not one of them).</summary>
    public async Task<bool> IsSeriesAsync(Guid workId, CancellationToken ct = default)
    {
        if (workId == Guid.Empty || !CanBeASeries(workId))
        {
            return false;
        }

        var navigator = await navigation.GetNavigatorAsync(workId, ct, structureOnly: true).ConfigureAwait(false);
        if (navigator is not { Enabled: true } || !SeriesMediaTypes.Contains(navigator.MediaType))
        {
            return false;
        }

        var node = navigator.Nodes.FirstOrDefault(entry => entry.EntityId == workId);
        return node is { IsLeaf: false };
    }

    // Only a work of a library that can hold a Series is worth reading the family of.
    private bool CanBeASeries(Guid workId)
    {
        using var connection = db.CreateConnection();
        var mediaType = connection.ExecuteScalar<string?>("SELECT media_type FROM works WHERE id = @id;", new { id = GuidSql.ToBlob(workId) });
        return mediaType is not null && SeriesMediaTypes.Contains(mediaType);
    }
}
