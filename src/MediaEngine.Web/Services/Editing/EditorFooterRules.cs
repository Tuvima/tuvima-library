using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Which sentence the editor's footer shows next to Delete (universal editor v5, screen J).</summary>
public enum EditorFooterStatus
{
    /// <summary>The write-back setting could not be read, so nothing is claimed about the files.</summary>
    Unknown,

    /// <summary>"File write-back off": edits save in Tuvima and the original files stay unchanged.</summary>
    WriteBackOff,

    /// <summary>"File write-back on": edits save in Tuvima and are also written into the file's own tags.</summary>
    WriteBackOn,

    /// <summary>A staged change will move a file in a Managed folder; the amber warning replaces the write-back sentence until Save or Discard.</summary>
    WillMove,
}

/// <param name="MoveDetail">For <see cref="EditorFooterStatus.WillMove"/>: the line under the warning ("Managed folder · from → to").</param>
public sealed record EditorFooterState(EditorFooterStatus Status, string? MoveDetail = null);

/// <summary>
/// What the footer says about the files (universal editor v5 D10, section 4). Pure: the write-back setting and the pending list go in, the
/// state comes out. A staged file move (a match or a Belongs to change in a Managed folder) always wins over the write-back sentence.
/// </summary>
public static class EditorFooterRules
{
    /// <summary>The line under the move warning when the staged change does not name the paths.</summary>
    public const string MovedAndRenamed = "Moved and renamed to match the new details";

    public static EditorFooterState For(FileWriteSetting setting, PendingEditList? pending)
    {
        var move = pending?.Changes.FirstOrDefault(change => change.Kind == EditSessionChangeKinds.FileMove);
        if (move is not null)
        {
            var from = pending!.Before(move.Id);
            var to = pending.Label(move.Id);
            var detail = !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to) ? $"{from} → {to}" : MovedAndRenamed;
            return new EditorFooterState(EditorFooterStatus.WillMove, $"Managed folder · {detail}");
        }

        return new EditorFooterState(setting.State switch
        {
            FileWriteState.On => EditorFooterStatus.WriteBackOn,
            FileWriteState.Off => EditorFooterStatus.WriteBackOff,
            _ => EditorFooterStatus.Unknown,
        });
    }
}
