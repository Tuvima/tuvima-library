using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// A list that lets its covers be ticked offers it to their "..." menus (Listen Albums list, spec D15): on a phone the menu then holds
/// "Select", which ticks that cover and puts the list into select mode. A list that offers nothing leaves every menu as it was.
/// </summary>
/// <param name="Applies">Whether this cover can be ticked in this list.</param>
/// <param name="BeginAsync">Ticks the cover and starts select mode.</param>
public sealed record MediaTileSelectionOffer(Func<MediaTileViewModel, bool> Applies, Func<MediaTileViewModel, Task> BeginAsync);
