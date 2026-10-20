namespace MediaEngine.Contracts.Display;

/// <summary>The sort a person last chose on each "View all" grid, keyed by grid (watch, read, listen, my-list, liked). A grid with no entry uses its default order.</summary>
public sealed record DisplayGridPreferencesDto(IReadOnlyDictionary<string, string> Sorts);

/// <summary>Body of the action that remembers the sort for one grid: added, title, year or played.</summary>
public sealed record DisplayGridSortPreferenceRequest(string Sort);
