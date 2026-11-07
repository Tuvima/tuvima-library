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

/// <param name="MoveFrom">For <see cref="EditorFooterStatus.WillMove"/>: where the file is now, when the staged change recorded it.</param>
/// <param name="MoveTo">For <see cref="EditorFooterStatus.WillMove"/>: where it will go, when the staged change recorded it. With either missing the footer uses its generic line.</param>
public sealed record EditorFooterState(EditorFooterStatus Status, string? MoveFrom = null, string? MoveTo = null);

/// <summary>
/// What the footer says about the files (universal editor v5 D10, section 4). Pure: the write-back setting and the pending list go in, the
/// state comes out. A staged file move (a match or a Belongs to change in a Managed folder) always wins over the write-back sentence.
/// </summary>
public static class EditorFooterRules
{
    public static EditorFooterState For(FileWriteSetting setting, PendingEditList? pending)
    {
        var move = pending?.Changes.FirstOrDefault(change => change.Kind == EditSessionChangeKinds.FileMove);
        if (move is not null)
        {
            var from = pending!.Before(move.Id);
            var to = pending.Label(move.Id);
            return !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to)
                ? new EditorFooterState(EditorFooterStatus.WillMove, from, to)
                : new EditorFooterState(EditorFooterStatus.WillMove);
        }

        return new EditorFooterState(setting.State switch
        {
            FileWriteState.On => EditorFooterStatus.WriteBackOn,
            FileWriteState.Off => EditorFooterStatus.WriteBackOff,
            _ => EditorFooterStatus.Unknown,
        });
    }
}
