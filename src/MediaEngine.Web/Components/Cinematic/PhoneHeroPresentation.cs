using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Components.Cinematic;

/// <summary>Which picture the phone hero card shows, best first: a landscape backdrop, the cover, then an honest placeholder tile.</summary>
public enum PhoneHeroArt
{
    Backdrop,
    Cover,
    Placeholder,
}

/// <summary>The picture choice for one phone hero card.</summary>
public sealed record PhoneHeroArtwork(PhoneHeroArt Mode, string? ImageUrl, string? LogoUrl);

/// <summary>The words on the phone hero's primary button: the action, and an optional short suffix such as "23 min left".</summary>
public sealed record PhoneHeroPrimaryLabel(string Text, string? Detail)
{
    /// <summary>The label and suffix as one line, for the accessible name.</summary>
    public string Full => string.IsNullOrWhiteSpace(Detail) ? Text : $"{Text} · {Detail}";
}

/// <summary>
/// Pure presentation rules for the phone Home hero card: which picture to show, what the one meta line says, and the
/// words on the placeholder tile. Kept free of components so the rules are unit tested without rendering.
/// </summary>
public static partial class PhoneHeroPresentation
{
    /// <summary>A cover narrower than this (or of unknown width) is treated as the Engine's stand-in picture, never shown scaled up.</summary>
    public const int MinUsableCoverWidthPx = 240;

    /// <summary>"101" becomes "1h 41m", "58" becomes "58m", "120" becomes "2h". Nothing for zero or less.</summary>
    public static string? FormatRuntime(double minutes)
    {
        if (double.IsNaN(minutes) || double.IsInfinity(minutes))
        {
            return null;
        }

        var total = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        if (total <= 0)
        {
            return null;
        }

        var hours = total / 60;
        var rest = total % 60;
        return hours == 0 ? $"{rest}m" : rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }

    /// <summary>"1 track", "2 tracks"; nothing when the count is zero or less (a "0 tracks" part is hidden).</summary>
    public static string? Count(int count, string singular, string? plural = null)
        => count <= 0 ? null : count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

    /// <summary>
    /// The single meta line under the title, by media type: movie year · runtime · rating, TV year · seasons (an episode
    /// reads S2 E6 · year), book author · year · pages, album artist · tracks · time, audiobook author · length.
    /// Raw decimal minutes are never shown, plurals are right, and empty or zero parts are dropped.
    /// </summary>
    public static string MetaLine(DiscoveryHeroViewModel hero)
    {
        var parts = Parse(hero.MetaPills);
        var subtitle = string.IsNullOrWhiteSpace(hero.Subtitle) ? null : hero.Subtitle.Trim();
        var kind = (hero.MediaKind ?? string.Empty).Trim().ToLowerInvariant();
        var line = new List<string?>();
        switch (kind)
        {
            case "movie":
                line.Add(parts.Year);
                line.Add(parts.Runtime);
                line.Add(parts.Rating);
                line.Add(parts.ContentRating);
                break;
            case "tv":
                if (hero.EpisodeContext is { } episode)
                {
                    line.Add(episode.SeasonNumber is > 0 && episode.EpisodeNumber is > 0 ? $"S{episode.SeasonNumber} E{episode.EpisodeNumber}" : null);
                    line.Add(parts.Year);
                }
                else
                {
                    line.Add(parts.Year);
                    line.Add(parts.Seasons);
                }
                break;
            case "music":
                line.Add(subtitle);
                line.Add(parts.Tracks);
                line.Add(parts.Runtime);
                break;
            case "audiobook":
                line.Add(subtitle);
                line.Add(parts.Runtime);
                break;
            default:
                if (IsVideoKind(kind))
                {
                    // A video or a folder of videos: year, length, then "14 sections" and "88 videos"; never seasons or episodes (item 17).
                    line.Add(parts.Year);
                    line.Add(parts.Runtime);
                    line.Add(parts.Sections);
                    line.Add(parts.Videos);
                    break;
                }

                line.Add(subtitle);
                line.Add(parts.Year);
                line.Add(parts.Pages);
                break;
        }

        return string.Join(" · ", line.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The picture ladder: a landscape backdrop (with the title logo over it when there is one; a video's extracted frame counts as one), else the cover at its
    /// natural shape, else a placeholder. The Engine's stand-in cover is a 100 x 150 picture and carries no flag of its own,
    /// so the measured cover width is the signal: a cover of unknown or tiny width is not real artwork and gets the tile.
    /// Phones ask for the "m" rendition, which stays under about 210 KB.
    /// </summary>
    public static PhoneHeroArtwork ResolveArt(DiscoveryHeroViewModel hero)
    {
        var backdrop = StringHelpers.FirstNonBlank(hero.HeroBackgroundImageUrl, hero.BackgroundImageUrl, hero.BannerImageUrl);
        var landscape = hero.SurfaceKind == MediaTileSurfaceKind.BannerLandscape;
        if (landscape && !string.IsNullOrWhiteSpace(backdrop))
        {
            var logo = hero.EpisodeContext is null && !string.IsNullOrWhiteSpace(hero.LogoUrl)
                ? MediaTileArtworkUrl.Sized(hero.LogoUrl, "m") ?? hero.LogoUrl
                : null;
            return new PhoneHeroArtwork(PhoneHeroArt.Backdrop, MediaTileArtworkUrl.Sized(backdrop, "m") ?? backdrop, logo);
        }

        var coverUsable = !string.IsNullOrWhiteSpace(hero.PreviewImageUrl)
            && hero.CoverWidthPx is { } coverWidth
            && coverWidth >= MinUsableCoverWidthPx;
        if (coverUsable && IsVideoKind(hero.MediaKind))
        {
            // An Other video has no portrait cover: its picture is a frame taken from the video, so it fills the card like a film's backdrop.
            return new PhoneHeroArtwork(PhoneHeroArt.Backdrop, MediaTileArtworkUrl.Sized(hero.PreviewImageUrl, "m") ?? hero.PreviewImageUrl, null);
        }

        if (coverUsable)
        {
            return new PhoneHeroArtwork(PhoneHeroArt.Cover, MediaTileArtworkUrl.Sized(hero.PreviewImageUrl, "m") ?? hero.PreviewImageUrl, null);
        }

        return new PhoneHeroArtwork(PhoneHeroArt.Placeholder, null, null);
    }

    /// <summary>
    /// The primary button by media type and whether the person has started the item. Unstarted: Play (movie), Watch S1 E1
    /// (TV), Start Reading (book, comic), Listen (audiobook), Play album (music). Started: Resume S5 E1 with the time left
    /// (movie, TV), Continue Reading with the percent read, Continue Listening with the time left. Continue, Resume and "0%"
    /// never appear on an unstarted item, and a suffix is only shown when the Engine actually supplied the number.
    /// An "Open ..." action (a collection or other non-playable target) keeps the Engine's wording.
    /// </summary>
    public static PhoneHeroPrimaryLabel PrimaryLabel(DiscoveryHeroViewModel hero)
    {
        var engineLabel = string.IsNullOrWhiteSpace(hero.PrimaryActionLabel) ? "Open" : hero.PrimaryActionLabel.Trim();
        if (engineLabel.StartsWith("Open", StringComparison.OrdinalIgnoreCase))
        {
            return new PhoneHeroPrimaryLabel(engineLabel, null);
        }

        var kind = (hero.MediaKind ?? string.Empty).Trim().ToLowerInvariant();
        var started = IsStarted(hero);
        var percent = ProgressPct(hero);
        var left = TimeLeft(hero);
        switch (kind)
        {
            case "movie":
                return started ? new("Resume", left ?? percent) : new("Play", null);
            case "tv":
                var position = EpisodePosition(hero) ?? PositionFrom(engineLabel);
                var watch = started ? "Resume" : "Watch";
                return new(position is null ? watch : $"{watch} {position}", started ? left ?? percent : null);
            case "music":
                // The hero carries no track position, so an album always starts from its first track.
                return new("Play album", null);
            case "audiobook":
                return started ? new("Continue Listening", left ?? percent) : new("Listen", null);
            case "book":
            case "books":
            case "ebook":
            case "comic":
            case "comics":
                return started ? new("Continue Reading", percent is null ? null : $"{percent} read") : new("Start Reading", null);
            default:
                // Videos, Other videos and YouTube play like a movie; anything else keeps the Engine's wording and is
                // never presented as something to read.
                return IsVideoKind(kind)
                    ? new(VideoFolderWording.PlayButton(started), started ? left ?? percent : null)
                    : new(engineLabel, null);
        }
    }

    /// <summary>
    /// The small label over the title of a video hero: "Continue Watching" once something has been started, otherwise "Video folder"
    /// for a folder of videos and "Video" for a single one. Null for every other kind, which keeps its own label.
    /// </summary>
    public static string? VideoLabel(DiscoveryHeroViewModel hero)
    {
        if (!IsVideoKind(hero.MediaKind))
        {
            return null;
        }

        return IsStarted(hero)
            ? VideoFolderWording.ContinueLabel
            : VideoFolderWording.IsVideoFolderMediaType(hero.MediaKind) ? VideoFolderWording.KindLabel : VideoFolderWording.ItemLabel;
    }

    /// <summary>
    /// The one muted line under the title of a video hero. A folder says "N videos · M sections" and, when a video in it has been
    /// started, "Next up: ..." with the time left (the Engine composes it); a single video says "N minutes left" once started,
    /// otherwise its length. Null for every other kind.
    /// </summary>
    public static string? VideoLine(DiscoveryHeroViewModel hero)
    {
        if (!IsVideoKind(hero.MediaKind))
        {
            return null;
        }

        var parts = Parse(hero.MetaPills);
        if (VideoFolderWording.IsVideoFolderMediaType(hero.MediaKind))
        {
            if (!string.IsNullOrWhiteSpace(hero.Subtitle))
            {
                return hero.Subtitle.Trim();
            }

            var counts = string.Join(" · ", new[] { parts.Videos, parts.Sections }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return counts.Length == 0 ? null : counts;
        }

        if (IsStarted(hero) && VideoFolderWording.MinutesLeft(RemainingSeconds(hero)) is { } left)
        {
            return left;
        }

        return parts.RuntimeMinutes is { } minutes ? VideoLength(minutes) : parts.Runtime ?? parts.Year;
    }

    /// <summary>The line to draw under the title on the phone: a video hero's own line, otherwise the usual meta line.</summary>
    public static string HeroLine(DiscoveryHeroViewModel hero) => VideoLine(hero) ?? MetaLine(hero);

    // "21 min", "1h 41m".
    private static string VideoLength(double minutes)
    {
        var total = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        return total is > 0 and < 60 ? $"{total} min" : FormatRuntime(minutes) ?? string.Empty;
    }

    /// <summary>True for the video kinds that are neither a movie nor a TV show: Videos, Other videos and YouTube.</summary>
    public static bool IsVideoKind(string? mediaKind)
    {
        var kind = (mediaKind ?? string.Empty).Trim().ToLowerInvariant();
        return kind.Contains("video", StringComparison.Ordinal) || kind.Contains("youtube", StringComparison.Ordinal);
    }

    private static bool IsStarted(DiscoveryHeroViewModel hero)
        => hero.ContinuationState == DisplayContinuationState.InProgress
            || hero.EpisodeContext?.State == DisplayContinuationState.InProgress
            || hero.ProgressPct is > 0 and < 99.5;

    private static string? ProgressPct(DiscoveryHeroViewModel hero)
        => hero.ProgressPct is > 0 and < 99.5 ? $"{Math.Max(1, (int)Math.Round(hero.ProgressPct.Value))}%" : null;

    private static double? RemainingSeconds(DiscoveryHeroViewModel hero)
    {
        double? seconds = hero.RemainingSeconds;
        if (seconds is not > 0 && hero.EpisodeContext is { PositionSeconds: { } position, DurationSeconds: { } duration })
        {
            seconds = duration - position;
        }

        return seconds;
    }

    private static string? TimeLeft(DiscoveryHeroViewModel hero)
    {
        var seconds = RemainingSeconds(hero);
        if (seconds is not > 0 || !double.IsFinite(seconds.Value))
        {
            return null;
        }

        var minutes = (int)Math.Ceiling(seconds.Value / 60);
        return minutes < 60 ? $"{minutes} min left" : $"{FormatRuntime(minutes)} left";
    }

    private static string? EpisodePosition(DiscoveryHeroViewModel hero)
        => hero.EpisodeContext is { SeasonNumber: > 0, EpisodeNumber: > 0 } episode ? $"S{episode.SeasonNumber} E{episode.EpisodeNumber}" : null;

    private static string? PositionFrom(string label)
        => PositionRegex().Match(label) is { Success: true } match ? $"S{match.Groups[1].Value} E{match.Groups[2].Value}" : null;

    /// <summary>The word on a placeholder tile.</summary>
    public static string PlaceholderLabel(string? mediaKind) => (mediaKind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "movie" => "Movie",
        "tv" => "TV show",
        "music" => "Album",
        "audiobook" => "Audiobook",
        "comic" => "Comic",
        "book" or "books" or "ebook" => "Book",
        var other when IsVideoKind(other) => "Video",
        _ => "Item",
    };

    private sealed record Parts(string? Year, string? Runtime, string? Rating, string? ContentRating, string? Seasons, string? Tracks, string? Pages, string? Sections, string? Videos, double? RuntimeMinutes);

    private static Parts Parse(IReadOnlyList<string> pills)
    {
        string? year = null, runtime = null, rating = null, contentRating = null, seasons = null, tracks = null, pages = null, sections = null, videos = null;
        double? runtimeMinutes = null;
        foreach (var raw in pills.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()))
        {
            if (raw.StartsWith('★'))
            {
                var value = raw.TrimStart('★').Trim();
                if (value.Length > 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stars) && stars > 0)
                {
                    rating ??= $"★ {value}";
                }
            }
            else if (raw.Length == 4 && int.TryParse(raw, out var y) && y is >= 1800 and <= 2200)
            {
                year ??= raw;
            }
            else if (CountRegex().Match(raw) is { Success: true } count && int.TryParse(count.Groups[1].Value, out var n))
            {
                var noun = count.Groups[2].Value.ToLowerInvariant();
                if (noun.StartsWith("season"))
                {
                    seasons ??= Count(n, "season");
                }
                else if (noun.StartsWith("track"))
                {
                    tracks ??= Count(n, "track");
                }
                else if (noun.StartsWith("page"))
                {
                    pages ??= Count(n, "page");
                }
                else if (noun.StartsWith("section"))
                {
                    sections ??= VideoFolderWording.SectionCount(n);
                }
                else if (noun.StartsWith("video"))
                {
                    videos ??= VideoFolderWording.VideoCount(n);
                }
            }
            else if (MinutesRegex().Match(raw) is { Success: true } minutes
                && double.TryParse(minutes.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
            {
                runtime ??= FormatRuntime(m);
                runtimeMinutes ??= m;
            }
            else if (HoursRegex().IsMatch(raw))
            {
                runtime ??= raw;
            }
            else if (raw.StartsWith("TV-", StringComparison.OrdinalIgnoreCase) || raw is "G" or "PG" or "PG-13" or "R" or "NC-17")
            {
                contentRating ??= raw;
            }
        }

        return new Parts(year, runtime, rating, contentRating, seasons, tracks, pages, sections, videos, runtimeMinutes);
    }

    [GeneratedRegex(@"\bS(\d+)\s*E(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PositionRegex();

    [GeneratedRegex(@"^(\d+)\s+(seasons?|tracks?|pages?|sections?|videos?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*min(?:ute)?s?$", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesRegex();

    [GeneratedRegex(@"^\d+\s*h(?:ours?)?(?:\s*\d+\s*m(?:in(?:ute)?s?)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HoursRegex();
}
