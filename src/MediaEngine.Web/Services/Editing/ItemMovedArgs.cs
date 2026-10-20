namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Move area tells its host after a committed move: the result line, the Work the item now lives in or as (the Engine's
/// <c>result_entity_id</c>; it differs from the edited item when the move merged it into an entry's own title, which removed the edited
/// title, or when a part joined another audiobook), and whether the item is now shown as Not matched. The moved item and the names of the
/// places the move left empty and removed tell the host whether the Work it was editing is gone (<see cref="LibraryMoveRules.RemovedEditedWork"/>).
/// </summary>
/// <param name="ResultLine">What happened, in plain words.</param>
/// <param name="ResultEntityId">The surviving Work the editor should show, or null when the Engine did not name one.</param>
/// <param name="IsUnmatched">True when the item was added anyway (no number, tagged Not matched).</param>
/// <param name="ItemId">The item that was moved: the edited Work itself, or for an audiobook one of its parts (an Edition).</param>
/// <param name="RemovedContainers">Names of the places the move left empty and removed ("Season 2", an emptied audiobook).</param>
public sealed record ItemMovedArgs(
    string ResultLine,
    Guid? ResultEntityId,
    bool IsUnmatched,
    Guid ItemId = default,
    IReadOnlyList<string>? RemovedContainers = null);
