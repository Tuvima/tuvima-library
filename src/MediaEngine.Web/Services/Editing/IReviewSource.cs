using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What Review changes needs from whoever holds the pending list (universal editor spec D12): a detail page's editing bar
/// (<see cref="EditSessionFlow"/>) or a list's selection bar (<see cref="BulkEditSession"/>). Every way into the editor saves through the
/// same dialog, so the rows, the checks, the save and the one "Files will be updated" line look and behave the same everywhere.
/// </summary>
public interface IReviewSource
{
    /// <summary>The pending list as the rows Review changes shows.</summary>
    IReadOnlyList<EditReviewRow> ReviewRows();

    /// <summary>Asks the Engine to check the pending list without saving anything.</summary>
    Task<EditSessionSaveOutcome> PreviewAsync(CancellationToken ct = default);

    /// <summary>Takes one Review row's change back out of the pending list.</summary>
    bool RemoveReviewRow(EditReviewRow row);

    /// <summary>Review's Edit on a row: sends the person back to the tool that holds the change. Pending changes are kept.</summary>
    Task JumpToAsync(EditReviewRow row);

    /// <summary>Discard changes: asks "Unsaved changes" first. False means the person stayed.</summary>
    Task<bool> LeaveAsync();

    /// <summary>Checks and saves the pending list. True only when everything was saved.</summary>
    Task<bool> SaveAsync();

    /// <summary>True when the last save wrote the data changes to the library, even if some file moves then failed.</summary>
    bool DataSaved { get; }

    /// <summary>A plain sentence when the last save could not even be tried.</summary>
    string? Message { get; }

    /// <summary>The Engine's answer for every change in the last save.</summary>
    IReadOnlyList<EditSessionChangeResult> Results { get; }

    /// <summary>The editor media types ("Music", "TV"...) being edited, which say which library's file setting applies.</summary>
    IReadOnlyList<string> FileWriteMediaTypes { get; }

    /// <summary>This save's own answer to "Save changes into the files": null follows the library, false skips the files this once.</summary>
    bool? WriteIntoFiles { get; set; }
}
