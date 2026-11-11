namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// What a surface shows after a change that can be taken back: the sentence ("Status updated.") and what Undo does.
/// A menu that closes after the choice hands this up, and the surface that hosts the menu draws it with <see cref="AppUndoNote"/>.
/// </summary>
public sealed record UndoOffer(string Message, Func<Task> UndoAsync);
