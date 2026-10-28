using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// What a detail page hands to the page that replaces it when it follows an item to another address (a move merged the edited title into
/// an entry's own title, which removed it): the page that loads for <paramref name="EntityId"/> turns Editing Mode back on, reopens
/// <paramref name="Section"/> and shows <paramref name="ResultLine"/>, so following the surviving item does not end the edit.
/// </summary>
/// <param name="EntityId">The surviving item the new page shows.</param>
/// <param name="Section">The Editing Mode section to reopen (Match and Identity for a move).</param>
/// <param name="ResultLine">What the move did, in plain words, for the section to show; null when there is nothing to say.</param>
/// <param name="SelectRow">
/// A row of the page's list (a track on an album) to tick, with the Edit panel open for it, instead of opening <paramref name="Section"/>.
/// It is how a song's Edit button, the lyrics button in the player and Needs Review reach the editor on the page behind them (spec D18, D19).
/// </param>
public sealed record DetailsEditResume(Guid EntityId, EditingSection Section, string? ResultLine, BulkEditRow? SelectRow = null);
