namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Engine's read of an item's current match says (universal editor v6 D10): whether the item is matched and, when it is, to which
/// provider. The Match tab raises it once the read has loaded, so the one-line header on the Match tab says exactly what the tab says.
/// </summary>
/// <param name="EntityId">The item the read is about.</param>
/// <param name="Matched">True when the Engine's read found a match that has not had its removal staged.</param>
/// <param name="ProviderName">The provider's name as shown ("TMDB", "Apple Books"); null when the item is not matched.</param>
public sealed record EditorMatchReadState(Guid EntityId, bool Matched, string? ProviderName);
