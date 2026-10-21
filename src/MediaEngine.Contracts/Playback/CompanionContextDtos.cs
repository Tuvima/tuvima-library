namespace MediaEngine.Contracts.Playback;

/// <summary>
/// What the phone companion shows for one Work at one position: where you are, who is here (spoiler-safe), where else you
/// have met them, what came before and a way back to the Universe. Read-only; built on the caller's own profile, so a Work or
/// character the profile may not see is never named. Nothing here is invented: a part the Library has no data for is empty
/// (or absent) and the screen says so.
/// </summary>
public sealed record CompanionContextDto
{
    public Guid WorkId { get; init; }

    /// <summary>One of <see cref="CompanionKinds"/>.</summary>
    public string Kind { get; init; } = CompanionKinds.Other;

    /// <summary>The Work's title; for a TV episode, the episode's title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The show's name for a TV episode; null otherwise.</summary>
    public string? ShowTitle { get; init; }

    /// <summary>The position in words, for example "Season 1 · Episode 3", "Chapter 12: The Gate" or "Film".</summary>
    public string PositionLabel { get; init; } = string.Empty;

    public int? ChapterIndex { get; init; }

    public string? ChapterTitle { get; init; }

    public string? CoverUrl { get; init; }

    /// <summary>The episode's or Work's own synopsis; null when the Library holds none.</summary>
    public string? Synopsis { get; init; }

    /// <summary>The Dashboard path of the Work's item page; null when it cannot be built.</summary>
    public string? DetailsRoute { get; init; }

    /// <summary>The profile's spoiler level, <c>up_to_progress</c> or <c>everything</c> (the same preference as Settings).</summary>
    public string SpoilerLevel { get; init; } = "up_to_progress";

    /// <summary>The Universe this Work belongs to, when the profile can open it.</summary>
    public CompanionUniverseLinkDto? Universe { get; init; }

    public CompanionWhosHereDto WhosHere { get; init; } = new();

    /// <summary>The previous owned episode of a show; null for anything else, or the first owned episode.</summary>
    public CompanionPreviouslyDto? Previously { get; init; }

    /// <summary>The actors the Library lists for this Work (names only, never character names); empty when none.</summary>
    public IReadOnlyList<string> Cast { get; init; } = [];

    /// <summary>What can be picked by hand for this Work (episodes of a show, chapters of a book); null when nothing can.</summary>
    public CompanionPickerDto? Picker { get; init; }
}

/// <summary>The values of <see cref="CompanionContextDto.Kind"/>.</summary>
public static class CompanionKinds
{
    public const string Film = "film";
    public const string Episode = "episode";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Comic = "comic";
    public const string Other = "other";
}

public sealed record CompanionUniverseLinkDto
{
    public string Qid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>The characters in this Work as far as the viewer has got, judged by the one spoiler rule.</summary>
public sealed record CompanionWhosHereDto
{
    /// <summary>Characters the profile may be shown, with "No plot details" ones last. Characters held back are never listed.</summary>
    public IReadOnlyList<CompanionCharacterDto> Characters { get; init; } = [];

    /// <summary>How many characters are held back to avoid spoilers (always 0 at the level "Everything").</summary>
    public int HeldBackCount { get; init; }

    /// <summary>How many more characters could be listed but were cut to keep the list short (the screen says "and N more").</summary>
    public int MoreCount { get; init; }

    /// <summary>False when the Library holds no character details for this Work at all (so the screen says so).</summary>
    public bool HasCharacterDetails { get; init; }
}

public sealed record CompanionCharacterDto
{
    /// <summary>The character's Wikidata item; "Also appears in" is matched on it, never on the name.</summary>
    public string Qid { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The Universe the character belongs to (the "Also appears in" panel is opened with it); empty when unknown.</summary>
    public string UniverseQid { get; init; } = string.Empty;

    /// <summary>Where the Library learned the character, for example "Wikidata"; never empty.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>One of <see cref="CompanionCharacterVisibility"/>.</summary>
    public string Visibility { get; init; } = CompanionCharacterVisibility.Shown;

    /// <summary>The performer's name when known and the character is shown; null otherwise.</summary>
    public string? PerformedBy { get; init; }

    /// <summary>How many other Works the profile owns where this character appears; 0 unless the character is shown.</summary>
    public int OtherWorkCount { get; init; }
}

/// <summary>The values of <see cref="CompanionCharacterDto.Visibility"/>; a hidden character is never sent.</summary>
public static class CompanionCharacterVisibility
{
    public const string Shown = "shown";
    public const string NoPlotDetails = "no_plot_details";
}

public sealed record CompanionPreviouslyDto
{
    public Guid WorkId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Synopsis { get; init; }
}

/// <summary>The choices for a manual position.</summary>
public sealed record CompanionPickerDto
{
    /// <summary>"episode" (each choice is another Work) or "chapter" (each choice is a chapter index of this Work).</summary>
    public string Kind { get; init; } = CompanionPickerKinds.Chapter;

    public IReadOnlyList<CompanionPickerChoiceDto> Choices { get; init; } = [];
}

public static class CompanionPickerKinds
{
    public const string Episode = "episode";
    public const string Chapter = "chapter";
}

public sealed record CompanionPickerChoiceDto
{
    /// <summary>The Work to hold: another episode's Work for an episode choice, this Work for a chapter choice.</summary>
    public Guid WorkId { get; init; }

    /// <summary>The chapter index for a chapter choice; null for an episode choice.</summary>
    public int? ChapterIndex { get; init; }

    public string Label { get; init; } = string.Empty;

    public string? Title { get; init; }
}
