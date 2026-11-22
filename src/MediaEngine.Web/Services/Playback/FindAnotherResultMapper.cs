using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Turns what the Engine says about one "Find another" result into the shared pick window's row (Find another results v2): the source's own
/// name, two plain checks, and the real words. Lyrics and subtitles use the same row; no picture and no source name appear in it.
/// </summary>
public static class FindAnotherResultMapper
{
    /// <summary>A length off by up to this many seconds reads amber; beyond it, red (D11).</summary>
    public const double AmberUpToSeconds = 15;

    /// <summary>A subtitle file off by up to this many minutes reads amber; beyond it, red.</summary>
    public const double SubtitleAmberUpToMinutes = 15;

    public static AppPickResult ForLyrics(TextTrackAlternativeDto offer, IStringLocalizer<SharedStrings> l)
    {
        var title = !string.IsNullOrWhiteSpace(offer.title) ? offer.title!
            : !string.IsNullOrWhiteSpace(offer.release_name) ? offer.release_name!
            : l["PickResult_UntitledResult"].Value;
        // The source's own name for the song, then its artist, album and length in muted text (D9).
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(offer.artist))
        {
            meta.Add(offer.artist!);
        }

        if (!string.IsNullOrWhiteSpace(offer.album))
        {
            meta.Add(offer.album!);
        }

        if (offer.length_seconds is > 0)
        {
            meta.Add(Clock(offer.length_seconds.Value));
        }

        var checks = new List<AppPickCheck>
        {
            offer.is_timed
                ? new(l["PickResult_Synced"].Value, AppPickTone.Ok)
                : new(l["PickResult_WordsOnlyNotTimed"].Value),
        };
        if (offer.length_check is { } check && offer.length_difference_seconds is { } difference)
        {
            checks.Add(check == "Fits"
                ? new(l["PickResult_FitsTheSong"].Value, AppPickTone.Ok)
                : new(
                    (difference > 0 ? l["PickResult_SecondsLongerThanSong", Whole(Math.Abs(difference))] : l["PickResult_SecondsShorterThanSong", Whole(Math.Abs(difference))]).Value,
                    Math.Abs(difference) <= AmberUpToSeconds ? AppPickTone.Warn : AppPickTone.Bad));
        }

        return new AppPickResult(
            Key(offer), title, meta.Count == 0 ? null : string.Join(" · ", meta), null,
            offer.is_current ? l["PlaybackLyrics_InUse"].Value : null, offer.is_current,
            checks, LinesOf(offer, timed: false), AppPickPreview.None);
    }

    public static AppPickResult ForSubtitle(
        TextTrackAlternativeDto offer, IStringLocalizer<SharedStrings> l, bool showLanguage, string languageName, double sameReleaseConfidence, AppPickPreview preview)
    {
        var meta = new List<string>();
        if (showLanguage)
        {
            meta.Add(languageName);
        }

        if (offer.is_hearing_impaired)
        {
            meta.Add(l["PickResult_HearingImpaired"].Value);
        }

        var checks = new List<AppPickCheck>
        {
            offer.confidence >= sameReleaseConfidence
                ? new(l["PickResult_MadeForYourFile"].Value, AppPickTone.Ok)
                : new(l["PickResult_DifferentReleaseMayBeOutOfStep"].Value, AppPickTone.Warn),
        };
        // The timing check appears only once the file has been read (it needs the file's last line).
        if (offer.length_check is { } check && offer.length_difference_seconds is { } difference)
        {
            var minutes = Math.Abs(difference) / 60d;
            checks.Add(check == "Fits"
                ? new(l["PickResult_FitsTheFilm"].Value, AppPickTone.Ok)
                : new(
                    (difference < 0 ? l["PickResult_EndsMinutesShortOfFilm", Whole(minutes)] : l["PickResult_EndsMinutesPastFilm", Whole(minutes)]).Value,
                    minutes <= SubtitleAmberUpToMinutes ? AppPickTone.Warn : AppPickTone.Bad));
        }

        var lines = LinesOf(offer, timed: true);
        return new AppPickResult(
            Key(offer), string.IsNullOrWhiteSpace(offer.release_name) ? l["PickResult_UntitledResult"].Value : offer.release_name!,
            meta.Count == 0 ? null : string.Join(" · ", meta), null,
            offer.is_current ? l["SubtitleFind_InUse"].Value : null, offer.is_current,
            checks, lines, lines is null ? preview : AppPickPreview.None);
    }

    public static string Key(TextTrackAlternativeDto offer) => $"{offer.provider}:{offer.source_id}";

    private static IReadOnlyList<AppPickLine>? LinesOf(TextTrackAlternativeDto offer, bool timed)
    {
        if (offer.preview_lines.Count == 0)
        {
            return null;
        }

        return offer.preview_lines
            .Select(line => new AppPickLine(timed && line.at_seconds is { } at ? Clock(at) : null, line.text))
            .ToList();
    }

    private static string Whole(double value) => Math.Max(1, Math.Round(value)).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>Minutes and seconds ("3:42"), or hours when long ("1:05:09").</summary>
    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
