namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Move area tells its host after a committed move: the result line, the Work the item now lives in or as (the Engine's
/// <c>result_entity_id</c>; it differs from the edited item when the move merged it into an entry's own title, which removed the edited
/// title, or when a part joined another audiobook), and whether the item is now shown as Not matched.
/// </summary>
/// <param name="ResultLine">What happened, in plain words.</param>
/// <param name="ResultEntityId">The surviving Work the editor should show, or null when the Engine did not name one.</param>
/// <param name="IsUnmatched">True when the item was added anyway (no number, tagged Not matched).</param>
public sealed record ItemMovedArgs(string ResultLine, Guid? ResultEntityId, bool IsUnmatched);
