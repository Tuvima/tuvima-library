namespace MediaEngine.Contracts.Display;

/// <summary>The sort a person last chose on each "View all" grid, keyed by grid (watch, read, listen, my-list, liked). A grid with no entry uses its default order.</summary>
public sealed record DisplayGridPreferencesDto(IReadOnlyDictionary<string, string> Sorts);

/// <summary>Body of the action that remembers the sort for one grid: added, title, year or played.</summary>
public sealed record DisplayGridSortPreferenceRequest(string Sort);

/// <summary>How a person wants the Home hero to behave. These only narrow what the hero rules choose from; they never change the rules' order.</summary>
/// <param name="Enabled">False hides the hero and Home starts at its first row.</param>
/// <param name="SlideCount">The most slides to show, 1 to 5.</param>
/// <param name="AllowWatch">Movies, shows and videos may appear.</param>
/// <param name="AllowRead">Books and comics may appear.</param>
/// <param name="AllowListen">Music and audiobooks may appear.</param>
/// <param name="ContinuedOnly">True shows only things the person has started; false adds new items after them.</param>
public sealed record DisplayHeroSettingsDto(bool Enabled, int SlideCount, bool AllowWatch, bool AllowRead, bool AllowListen, bool ContinuedOnly);
