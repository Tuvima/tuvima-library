namespace MediaEngine.Domain.Entities;

/// <summary>One book bookmark from a bookmark set, ready to be saved: its reader location, chapter and name.</summary>
public sealed record ReaderSetBookmark(string Locator, int Chapter, string? Label);

/// <summary>What happened when a bookmark set was applied to a book.</summary>
public sealed record ReaderSetOutcome(int Added, int Replaced);
