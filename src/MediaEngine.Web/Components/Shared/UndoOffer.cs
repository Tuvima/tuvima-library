namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// What a surface shows after a change: the sentence ("Status updated.") and what Undo does. A menu that closes after the choice hands this
/// up, and the surface that hosts the menu draws it with <see cref="AppUndoNote"/>. A line with nothing to take back (the sentence after
/// Add to playlist) leaves <paramref name="UndoAsync"/> null and shows no Undo button; <paramref name="Severity"/> sets how loudly it reads.
/// </summary>
public sealed record UndoOffer(string Message, Func<Task>? UndoAsync = null, AppSeverity Severity = AppSeverity.Success);
