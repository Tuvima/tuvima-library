using System.Globalization;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one home for the words a folder of Other videos (a course, a saved channel) goes by (details page D15 and item 17, Videos spec
/// wording note). Such a folder is a plain ordered list of videos whose sub-folders are named sections: it never says Show, Season,
/// Episode or "S1 E1", whichever surface draws it (Home, Watch, search, the details page, the editor). A TV show keeps its own wording.
/// The Engine's media type for the folder is <c>Videos</c> (the single videos inside it are <c>Video</c>); <see cref="IsVideoFolderMediaType"/>
/// is the one place that decides it, so the Engine and the Dashboard never guess from names.
/// </summary>
public static class VideoFolderWording
{
    /// <summary>The media type the Engine sends for a folder of videos.</summary>
    public const string FolderMediaType = "Videos";

    /// <summary>The kind line of a folder ("Video folder").</summary>
    public const string KindLabel = "Video folder";

    /// <summary>What the folder's tile and hero button says (a TV show says "Open Show").</summary>
    public const string OpenLabel = "Open folder";

    /// <summary>The play button of a video that has not been started.</summary>
    public const string PlayLabel = "Play";

    /// <summary>The play button of a video that has been started.</summary>
    public const string ResumeLabel = "Resume";

    /// <summary>What the folder is called in a sentence about its videos ("88 videos owned in folder").</summary>
    public const string ContainerLabel = "Folder";

    /// <summary>What one video in the folder is called.</summary>
    public const string ItemLabel = "Video";

    /// <summary>What a named group of videos inside the folder (a sub-folder) is called.</summary>
    public const string SectionLabel = "Section";

    /// <summary>The title of a group of videos that has no name of its own.</summary>
    public const string UnnamedSectionTitle = "Videos";

    /// <summary>The heading over the folder's list in the editor's Contents tab.</summary>
    public const string ContentsHeading = "Videos and sections";

    /// <summary>The label of the sections fact in the editor's Quick Facts.</summary>
    public const string SectionsFactLabel = "Sections";

    /// <summary>The label of the videos fact in the editor's Quick Facts.</summary>
    public const string VideosFactLabel = "Videos owned";

    /// <summary>The sentence a folder's wording check refuses: the TV words a folder of videos must never show.</summary>
    public static readonly string[] ForbiddenWords = ["Season", "Seasons", "Episode", "Episodes", "Show"];

    /// <summary>True when the Engine's media type is the folder-of-videos type (<c>Videos</c>, case ignored).</summary>
    public static bool IsVideoFolderMediaType(string? mediaType)
        => string.Equals(mediaType?.Trim(), FolderMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a video that lives in a Videos or Other videos library (the Engine's <c>Videos</c> media type, or <c>Video</c> for a single one): it is numbered, never given a season or episode code.</summary>
    public static bool IsVideoItemMediaType(string? mediaType)
    {
        var value = mediaType?.Trim();
        return string.Equals(value, FolderMediaType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, ItemLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A video's title line: "3 · Ball handling" when it has a number, otherwise the title alone. Takes the number as the Engine sent it
    /// ("3", "03", "E3" or "Episode 3") and never produces a season or episode code.
    /// </summary>
    public static string NumberedTitle(string? number, string? title)
    {
        var text = title?.Trim() ?? string.Empty;
        var digits = number?.Trim() ?? string.Empty;
        if (digits.StartsWith("Episode ", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits["Episode ".Length..].Trim();
        }
        else if (digits.Length > 1 && (digits[0] is 'E' or 'e') && char.IsDigit(digits[1]))
        {
            digits = digits[1..];
        }

        if (digits.Length == 0 || !digits.All(char.IsDigit))
        {
            return text;
        }

        var shown = digits.TrimStart('0');
        shown = shown.Length == 0 ? "0" : shown;
        return text.Length == 0 ? shown : $"{shown} · {text}";
    }

    /// <summary>True when a Series-style list is a folder of videos, by the labels the Engine gave it (<see cref="ContainerLabel"/> and <see cref="ItemLabel"/>).</summary>
    public static bool IsVideoFolderList(string? containerLabel, string? itemLabel)
        => string.Equals(containerLabel, ContainerLabel, StringComparison.Ordinal) && string.Equals(itemLabel, ItemLabel, StringComparison.Ordinal);

    /// <summary>"1 video" or "88 videos".</summary>
    public static string VideoCount(int count)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "video" : "videos")}");

    /// <summary>"1 section" or "14 sections".</summary>
    public static string SectionCount(int count)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "section" : "sections")}");

    /// <summary>The noun for what the folder holds ("video", "videos").</summary>
    public static string VideoNoun(int count) => count == 1 ? "video" : "videos";

    /// <summary>The sections of a folder as one line, or null when the folder is a single plain list (one section or none is no section at all).</summary>
    public static string? SectionsLine(int sectionCount) => sectionCount > 1 ? SectionCount(sectionCount) : null;

    /// <summary>The small label over a Home hero's title once something has been started (otherwise a folder says <see cref="KindLabel"/> and a video <see cref="ItemLabel"/>).</summary>
    public const string ContinueLabel = "Continue Watching";

    /// <summary>The lead-in of the video a folder's hero will play next ("Next up: Triple Point").</summary>
    public const string NextUpLabel = "Next up";

    /// <summary>"88 videos" or "88 videos · 14 sections"; a folder with one section or none says only its videos.</summary>
    public static string CountsLine(int videoCount, int sectionCount)
        => SectionsLine(sectionCount) is { } sections ? $"{VideoCount(videoCount)} · {sections}" : VideoCount(videoCount);

    /// <summary>"38 minutes left" or "1 minute left"; null when no time is left or it is not known.</summary>
    public static string? MinutesLeft(double? seconds)
    {
        if (seconds is not > 0 || !double.IsFinite(seconds.Value))
        {
            return null;
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(seconds.Value / 60));
        return string.Create(CultureInfo.InvariantCulture, $"{minutes} {(minutes == 1 ? "minute" : "minutes")} left");
    }

    /// <summary>
    /// The one muted line under a folder's Home hero title: its videos and sections, then, when a video in it has been started,
    /// "Next up: &lt;title&gt;" and the time left in it. Never carries a season or episode number.
    /// </summary>
    public static string HeroLine(int videoCount, int sectionCount, string? nextUpTitle, double? secondsLeft)
    {
        var parts = new List<string> { CountsLine(videoCount, sectionCount) };
        if (!string.IsNullOrWhiteSpace(nextUpTitle))
        {
            parts.Add($"{NextUpLabel}: {nextUpTitle.Trim()}");
            if (MinutesLeft(secondsLeft) is { } left)
            {
                parts.Add(left);
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The play button of a folder's video: Play, or Resume once it has been started. Never carries a season or episode number.</summary>
    public static string PlayButton(bool started) => started ? ResumeLabel : PlayLabel;

    /// <summary>
    /// Words a TV-shaped count pill ("14 seasons", "88 episodes") reads as in a folder of videos: sections for seasons, videos for episodes.
    /// Any other text is returned as it came.
    /// </summary>
    public static string FromTvCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0 || !int.TryParse(trimmed.AsSpan(0, space), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return text;
        }

        var noun = trimmed[(space + 1)..].Trim();
        if (noun.StartsWith("season", StringComparison.OrdinalIgnoreCase))
        {
            return SectionCount(count);
        }

        return noun.StartsWith("episode", StringComparison.OrdinalIgnoreCase) ? VideoCount(count) : text;
    }

    /// <summary>True when the text uses a TV word (Season, Episode, Show, or a "S1 E1" position) a folder of videos must never show.</summary>
    public static bool UsesTvWording(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var word in ForbiddenWords)
        {
            if (ContainsWord(text, word))
            {
                return true;
            }
        }

        return ContainsPosition(text);
    }

    private static bool ContainsWord(string text, string word)
    {
        var from = 0;
        while (from < text.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + word.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    // "S1 E1", "S01E02": a season letter and number followed by an episode letter and number.
    private static bool ContainsPosition(string text)
    {
        for (var index = 0; index < text.Length - 2; index++)
        {
            if (!char.IsLetterOrDigit(index == 0 ? ' ' : text[index - 1]) && (text[index] is 'S' or 's') && char.IsDigit(text[index + 1]))
            {
                var cursor = index + 1;
                while (cursor < text.Length && char.IsDigit(text[cursor]))
                {
                    cursor++;
                }

                while (cursor < text.Length && text[cursor] == ' ')
                {
                    cursor++;
                }

                if (cursor + 1 < text.Length && (text[cursor] is 'E' or 'e') && char.IsDigit(text[cursor + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
