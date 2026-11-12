using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Components.Settings.NeedsReview;

/// <summary>The kind word a row's meta line starts with ("Movie", "Episode", ...), and how its cover is shaped.</summary>
public enum NeedsReviewKindWord
{
    Item,
    Movie,
    Episode,
    Book,
    Audiobook,
    Song,
    Comic,
}

/// <summary>How a cover is shaped beside a row.</summary>
public enum NeedsReviewCoverShape
{
    Poster,
    Square,
    Wide,
}

/// <summary>Pure facts about one Needs Review row: its title, kind, where its file sits, and which actions can apply to it.</summary>
public static class NeedsReviewRowInfo
{
    /// <summary>The row's name: the title the Engine knows, else the file's name, else <paramref name="fallback"/>.</summary>
    public static string TitleOf(NeedsReviewRowDto row, string fallback)
    {
        if (row.Unmatched is { } file)
        {
            return StringHelpers.FirstNonBlank(file.Title, file.FileName) ?? fallback;
        }

        string? fileName = null;
        row.Review?.DetectedFacts?.TryGetValue("file_name", out fileName);
        return StringHelpers.FirstNonBlank(row.Review?.EntityTitle, fileName, row.Review?.Detail) ?? fallback;
    }

    /// <summary>The file's own name (a file row inside a family is named by it); falls back to the row's title.</summary>
    public static string FileNameOf(NeedsReviewRowDto row, string fallback)
    {
        string? fileName = null;
        row.Review?.DetectedFacts?.TryGetValue("file_name", out fileName);
        return StringHelpers.FirstNonBlank(row.Unmatched?.FileName, fileName) ?? TitleOf(row, fallback);
    }

    /// <summary>The text the search box looks in.</summary>
    public static string SearchText(NeedsReviewRowDto row) =>
        $"{TitleOf(row, string.Empty)} {row.Review?.Detail} {row.Review?.MediaType} {row.Unmatched?.FileName} {row.Unmatched?.Folder} {row.LibraryName} {row.FamilyTitle}";

    /// <summary>The media type text of a row as the Engine gave it (a review item's own; a file's comes from its library).</summary>
    public static string? MediaTypeOf(NeedsReviewRowDto row, string? libraryMediaType) =>
        row.Review is { } review
            ? StringHelpers.FirstNonBlank(review.MediaType) ?? libraryMediaType
            : libraryMediaType;

    public static NeedsReviewKindWord KindOf(string? mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return NeedsReviewKindWord.Item;
        }

        if (value.Contains("audio", StringComparison.Ordinal))
        {
            return NeedsReviewKindWord.Audiobook;
        }

        if (value.Contains("movie", StringComparison.Ordinal))
        {
            return NeedsReviewKindWord.Movie;
        }

        if (value == "tv" || value.Contains("tv ", StringComparison.Ordinal) || value.Contains("episode", StringComparison.Ordinal) || value.Contains("show", StringComparison.Ordinal))
        {
            return NeedsReviewKindWord.Episode;
        }

        if (value.Contains("music", StringComparison.Ordinal) || value.Contains("song", StringComparison.Ordinal))
        {
            return NeedsReviewKindWord.Song;
        }

        if (value.Contains("comic", StringComparison.Ordinal))
        {
            return NeedsReviewKindWord.Comic;
        }

        return value.Contains("book", StringComparison.Ordinal) ? NeedsReviewKindWord.Book : NeedsReviewKindWord.Item;
    }

    /// <summary>Music and audiobook covers are square; every other cover is a 2:3 poster.</summary>
    public static bool IsSquare(NeedsReviewKindWord kind) => kind is NeedsReviewKindWord.Song or NeedsReviewKindWord.Audiobook;

    /// <summary>A folder on one line: an ellipsis, then the last two folder names (the whole path stays in the tooltip).</summary>
    public static string ShortFolder(string folder)
    {
        var separator = folder.Contains('\\') ? '\\' : '/';
        var names = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return names.Length <= 2 ? folder : $"…{separator}{string.Join(separator, names[^2..])}";
    }

    /// <summary>The deepest folder every given folder sits in; null when they share none.</summary>
    public static string? CommonFolder(IEnumerable<string?> folders)
    {
        var lists = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => folder!.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (lists.Count == 0)
        {
            return null;
        }

        var first = lists[0];
        var shared = 0;
        while (shared < first.Length && lists.All(list => list.Length > shared && string.Equals(list[shared], first[shared], StringComparison.OrdinalIgnoreCase)))
        {
            shared++;
        }

        if (shared == 0)
        {
            return null;
        }

        var original = folders.First(folder => !string.IsNullOrWhiteSpace(folder))!;
        var separator = original.Contains('\\') ? '\\' : '/';
        var prefix = string.Join(separator, first.Take(shared));
        return original.StartsWith(separator) ? separator + prefix : prefix;
    }

    /// <summary>
    /// Whether an Engine action can try the row's failed step again: a file that can be looked up again, or a file whose tags could not be
    /// written (the re-tag sweep is asked to pick it up). Other file problems have no such action; they are fixed in the editor.
    /// </summary>
    public static bool CanTryAgain(NeedsReviewRowDto row) =>
        row.Unmatched is { } file
            ? !string.Equals(file.Reason, UnmatchedReasons.NoYoutubeId, StringComparison.Ordinal)
            : IsWritebackRetry(row);

    /// <summary>True for a failed write-back of a file's tags: the Engine's re-tag sweep can be told to retry that file.</summary>
    public static bool IsWritebackRetry(NeedsReviewRowDto row) =>
        row.Review is { EntityType: "MediaAsset" } review
        && string.Equals(review.Trigger, ReviewTrigger.WritebackFailed, StringComparison.Ordinal);

    /// <summary>True when the row's main button is Try again instead of Fix: a file problem with an Engine action that retries the failed step.</summary>
    public static bool RetryIsMainAction(NeedsReviewRowDto row) =>
        row.Group == NeedsReviewGroups.FileProblem && CanTryAgain(row);

    /// <summary>
    /// True when the row shows exactly one suggestion with a Use this button: the Engine offered a best guess on a review item, and the row is
    /// not a film that may be a TV show (that one shows two choices instead).
    /// </summary>
    public static bool HasSingleSuggestion(NeedsReviewRowDto row) =>
        row is { Review: { } review, BestGuess: { } guess }
        && IsKnownAction(guess)
        && !(review.Trigger == MovieTvSuggestionReader.Trigger && MovieTvSuggestionReader.Read(review.CandidatesJson) is not null);

    /// <summary>
    /// True when the page knows how to run the Engine's best guess: move to TV, keep the provider's match, confirm the linked match, or use a
    /// retail match that names its choice. Any other action is never offered (neither Use this nor Use suggestions), so it cannot run a wrong write.
    /// </summary>
    public static bool IsKnownAction(NeedsReviewBestGuessDto guess) => guess.Action switch
    {
        "move_to_tv" or "keep_provider_match" or "confirm_match" => true,
        "use_retail_match" => !string.IsNullOrWhiteSpace(guess.ChoiceId),
        _ => false,
    };

    /// <summary>The thing Fix opens in the editor for one row: the review item's entity, or the file's Work.</summary>
    public static Guid? FixTargetOf(NeedsReviewRowDto row) => row.Review is { } review ? review.EntityId : row.Unmatched?.WorkId;
}
