using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What Delete means inside the universal editor (v4 D13). Pure: which item the header's "..." can delete and what it is called, and which
/// items the Engine's answer took out of the Library, so the editor can drop them from its lists and move off one that is gone. Nothing is
/// read or changed here.
/// </summary>
public static class EditorDeleteRules
{
    /// <summary>
    /// The whole show, season, album or Series the editor is on, or null for a single title (an episode, a film, a song, a book).
    /// A TV "series" is a show; any other "series" is a book or comic Series.
    /// </summary>
    public static LibraryGroupTarget? GroupFor(Guid entityId, string title, string? scopeId, string? mediaType)
    {
        var tv = string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase);
        var videoFolder = VideoFolderWording.IsVideoFolderMediaType(mediaType);
        var kind = (scopeId ?? string.Empty).ToLowerInvariant() switch
        {
            "series" when tv => LibraryGroupKinds.Show,
            "series" => LibraryGroupKinds.Series,
            "season" => LibraryGroupKinds.Season,
            "album" => LibraryGroupKinds.Album,
            _ => null,
        };
        return kind is null || entityId == Guid.Empty ? null : new LibraryGroupTarget(entityId, kind, title, [], videoFolder && kind == LibraryGroupKinds.Series);
    }

    /// <summary>
    /// How many supporting files go with the group, as the header's read counted them (universal editor v5 D29): a season's own, or the whole
    /// title's for a show, album or Series. 0 when the header did not say, so the confirmation leaves the clause out rather than invent a number.
    /// </summary>
    public static int SupportingFilesIn(LibraryGroupTarget group, MediaEngine.Contracts.Editing.EditorHeaderDto? header)
    {
        ArgumentNullException.ThrowIfNull(group);
        var files = header?.Level.SupportingFiles;
        if (files is null)
        {
            return 0;
        }

        return group.Kind == LibraryGroupKinds.Season ? files.Count : files.TotalInRoot;
    }

    /// <summary>The "..." entry for the item being edited: "Delete episode…", "Delete season…", "Delete show…".</summary>
    public static string MenuLabel(LibraryGroupTarget? group, string? scopeId) => group is not null
        ? group.Kind switch
        {
            LibraryGroupKinds.Show => "Delete show…",
            LibraryGroupKinds.Season => "Delete season…",
            LibraryGroupKinds.Album => "Delete album…",
            _ when group.IsVideoFolder => "Delete folder…",
            _ => "Delete Series…",
        }
        : (scopeId ?? string.Empty).ToLowerInvariant() switch
        {
            "episode" => "Delete episode…",
            "track" => "Delete song…",
            _ => "Delete from library…",
        };

    /// <summary>
    /// Every item the Engine's answer took out of the Library: the titles it deleted, the group itself, and the show a season took with it
    /// when it was the last one. An answer that says the items were already gone names what was asked about.
    /// </summary>
    public static IReadOnlyCollection<Guid> Removed(
        LibraryItemDeletionDialogResult answer,
        IEnumerable<Guid> asked,
        LibraryGroupTarget? group,
        Func<Guid, Guid?> parentOf)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(parentOf);
        var removed = new HashSet<Guid>();
        if (answer.Deleted is { } deleted)
        {
            removed.UnionWith(deleted.DeletedWorkIds);
        }

        // The titles asked about went too: a delete of titles is all or nothing, and "already gone" means they are not there either.
        if (group is null && (answer.Deleted is not null || answer.NoLongerInLibrary))
        {
            removed.UnionWith(asked);
        }

        if (group is not null)
        {
            removed.Add(group.GroupId);
            if (answer.RemovedParent && parentOf(group.GroupId) is { } parent)
            {
                removed.Add(parent);
            }
        }

        return removed;
    }

    /// <summary>The ticked rows' Delete button: "Delete 3 episodes", "Delete 1 song".</summary>
    public static string DeleteRowsLabel(int count, string noun) => $"Delete {count} {(count == 1 && noun.EndsWith('s') ? noun[..^1] : noun)}";
}
