using MediaEngine.Contracts.Review;

namespace MediaEngine.Web.Components.Settings.NeedsReview;

/// <summary>
/// One picture, title, line of text and button in a row's choices strip (mock marker 9). The strip knows nothing about where choices come
/// from, so the Engine's own candidates (two films that fit a file, the Series that claim a book) can feed it later without a new layout.
/// </summary>
/// <param name="ImageUrl">The choice's picture; null draws no picture.</param>
/// <param name="Title">The choice's name, with its year.</param>
/// <param name="Subtitle">One line under the name.</param>
/// <param name="ButtonLabel">The button's words ("Use this", "Move to TV", "Keep").</param>
/// <param name="Choose">Runs when the person presses the button.</param>
public sealed record NeedsReviewChoice(string? ImageUrl, string Title, string? Subtitle, string ButtonLabel, Func<Task> Choose);

/// <summary>
/// What the Needs Review parts ask the page to do, and the page rules they draw by. The page builds one of these and passes it down, so a row,
/// a family row and the kept section never inject services, hold SQL or know how an action reaches the Engine.
/// </summary>
public sealed class NeedsReviewActions
{
    /// <summary>The page's rule for showing Fix: a desktop that may edit, and a row whose Work the editor can open.</summary>
    public required Func<NeedsReviewEntry, bool> CanFix { get; init; }

    /// <summary>The page's rule for showing Delete: an administrator on a desktop, and a file that already has a Work.</summary>
    public required Func<NeedsReviewRowDto, bool> CanDelete { get; init; }

    /// <summary>True on a phone: Fix leads the "..." sheet there.</summary>
    public required Func<bool> IsPhone { get; init; }

    /// <summary>The media type text for a file row (its library's), since a file the Engine could not match has none of its own.</summary>
    public required Func<NeedsReviewRowDto, string?> LibraryMediaTypeOf { get; init; }

    /// <summary>Fix on a row or a family: opens the editor on Match over this page.</summary>
    public required Func<NeedsReviewEntry, Task> Fix { get; init; }

    /// <summary>Use this: runs the one action the Engine offered as the row's best guess.</summary>
    public required Func<NeedsReviewRowDto, Task> UseThis { get; init; }

    /// <summary>Move to TV: the film is really this TV show.</summary>
    public required Func<NeedsReviewRowDto, Task> MoveToTv { get; init; }

    /// <summary>Keep as a film: the film stays in Movies.</summary>
    public required Func<NeedsReviewRowDto, Task> KeepAsFilm { get; init; }

    /// <summary>Try again for each of these rows.</summary>
    public required Func<IReadOnlyList<NeedsReviewRowDto>, Task> TryAgain { get; init; }

    /// <summary>Keep as it is for each of these rows.</summary>
    public required Func<IReadOnlyList<NeedsReviewRowDto>, Task> Keep { get; init; }

    /// <summary>Show in Activity: opens the Activity list searched for this title.</summary>
    public required Func<string, Task> ShowInActivity { get; init; }

    public required Func<NeedsReviewRowDto, Task> Delete { get; init; }

    /// <summary>Put back a kept item so it waits in Needs Review again.</summary>
    public required Func<NeedsReviewRowDto, Task> PutBack { get; init; }
}
