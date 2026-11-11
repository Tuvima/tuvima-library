using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

public partial class DetailPage
{
    // Audiobook playback: choosing the chapter and position to start or resume at, the data-* hooks that start playback immediately, and the queue item and chapter mapping.

    private async Task PlayAudiobookAsync(bool preferResume)
    {
        if (Model is null)
        {
            return;
        }

        var chapters = AudiobookTracks();

        if (chapters.Count == 0)
        {
            return;
        }

        var totalDurationSeconds = ResolveAudiobookTotalSeconds(chapters);
        var resumePositionSeconds = preferResume
            ? ResolveAudiobookResumePosition(chapters, Model.Progress?.Percent, Playback.AudiobookNearStartGuardSeconds, totalDurationSeconds)
            : 0d;
        var startItem = preferResume
            ? ResolveAudiobookResumeItem(chapters, resumePositionSeconds)
            : chapters.FirstOrDefault();
        if (startItem is null)
        {
            return;
        }

        var item = ToAudiobookQueueItem(startItem, chapters, resumePositionSeconds);
        if (item.WorkId == Guid.Empty)
        {
            return;
        }

        await Playback.StartAudiobookAsync(
            new AudiobookStartRequest(
                item,
                preferResume ? AudiobookStartKinds.Resume : AudiobookStartKinds.Chapter,
                resumePositionSeconds,
                startItem.ChapterIndex,
                Model.Title));

        // Unified player v1 D6: the player is always on top. The details window is a modal layer, so left open it would cover the bar that
        // just started (issue #705). Closing it returns to the page the person came from; an unsaved edit still gets its own question.
        if (Overlay is { IsOpen: true } overlay)
        {
            await overlay.DismissAsync();
        }
    }

    private IReadOnlyDictionary<string, object>? AudiobookActionAttributes(DetailAction action)
    {
        var model = Model;
        var isListenAction = string.Equals(action.Key, "listen", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Label, "Listen", StringComparison.OrdinalIgnoreCase);
        if (model is null || !IsAudiobook || action.IsDisabled || (!isListenAction && !IsContinueAction(action)))
        {
            return null;
        }

        var chapters = AudiobookTracks();
        if (chapters.Count == 0)
        {
            return null;
        }

        var preferResume = IsContinueAction(action);
        var totalDurationSeconds = ResolveAudiobookTotalSeconds(chapters);
        var resumePositionSeconds = preferResume
            ? ResolveAudiobookResumePosition(chapters, model.Progress?.Percent, Playback.AudiobookNearStartGuardSeconds, totalDurationSeconds)
            : null;
        var startItem = preferResume
            ? ResolveAudiobookResumeItem(chapters, resumePositionSeconds)
            : chapters.FirstOrDefault();
        if (startItem is null || !Guid.TryParse(startItem.AssetId, out var assetId))
        {
            return null;
        }

        var positionSeconds = resumePositionSeconds ?? ResumePositionFor(startItem) ?? startItem.StartSeconds ?? 0;
        if (preferResume && positionSeconds > 0)
        {
            positionSeconds = Math.Max(0, positionSeconds - Playback.ResumeRewindSeconds);
        }

        var playbackRate = Playback.IsAudiobookMode
            ? Playback.PlaybackRate
            : (double)new ListeningSettingsDto().AudiobookDefaultSpeed;

        return new Dictionary<string, object>
        {
            ["data-listen-immediate-start"] = "audiobook",
            ["data-listen-start-kind"] = preferResume ? AudiobookStartKinds.Resume : AudiobookStartKinds.Chapter,
            ["data-listen-start-url"] = $"/engine-stream/{assetId:D}",
            ["data-listen-start-position"] = positionSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["data-listen-start-progress"] = (model.Progress?.Percent ?? 0).ToString("0.###", CultureInfo.InvariantCulture),
            ["data-listen-start-duration"] = (totalDurationSeconds ?? 0).ToString("0.###", CultureInfo.InvariantCulture),
            ["data-listen-start-rewind"] = (preferResume ? Playback.ResumeRewindSeconds : 0).ToString(CultureInfo.InvariantCulture),
            ["data-listen-start-rate"] = playbackRate.ToString("0.###", CultureInfo.InvariantCulture),
            ["data-listen-start-volume"] = Playback.Volume.ToString("0.###", CultureInfo.InvariantCulture),
            ["data-listen-start-muted"] = Playback.IsMuted ? "true" : "false",
        };
    }

    private static bool IsContinueAction(DetailAction action) =>
        string.Equals(action.Key, "continue", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action.Label, "Continue", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action.Label, "Continue Listening", StringComparison.OrdinalIgnoreCase);

    private static MediaGroupingItemViewModel? ResolveAudiobookResumeItem(
        IReadOnlyList<MediaGroupingItemViewModel> chapters,
        double? resumePositionSeconds)
    {
        if (resumePositionSeconds is > 0)
        {
            return ChapterAtPosition(chapters, resumePositionSeconds.Value)
                ?? chapters.FirstOrDefault(item => item.ResumePositionSeconds is > 0)
                ?? chapters.FirstOrDefault(item => item.ProgressPercent is > 0 and < 99.5)
                ?? chapters.FirstOrDefault(item => !IsCompletedChapter(item))
                ?? chapters.FirstOrDefault();
        }

        return chapters.FirstOrDefault(item => item.ResumePositionSeconds is > 0)
            ?? chapters.FirstOrDefault(item => item.ProgressPercent is > 0 and < 99.5)
            ?? chapters.FirstOrDefault(item => !IsCompletedChapter(item))
            ?? chapters.FirstOrDefault();
    }

    private static double? ResolveAudiobookResumePosition(
        IReadOnlyList<MediaGroupingItemViewModel> chapters,
        double? pageProgressPercent,
        int nearStartGuardSeconds,
        double? knownTotalSeconds = null)
    {
        double? inferredPagePosition = null;
        if (pageProgressPercent is > 0 and < 99.5)
        {
            var totalSeconds = knownTotalSeconds is > 0
                ? knownTotalSeconds
                : TotalAudiobookSeconds(chapters);
            if (totalSeconds is > 0)
            {
                inferredPagePosition = totalSeconds.Value * Math.Clamp(pageProgressPercent.Value, 0, 100) / 100d;
            }
        }

        var explicitPosition = chapters
            .Where(item => item.ResumePositionSeconds is > 0)
            .Select(item => item.ResumePositionSeconds!.Value)
            .OrderDescending()
            .FirstOrDefault();
        if (explicitPosition > 0)
        {
            if (explicitPosition <= Math.Max(0, nearStartGuardSeconds)
                && inferredPagePosition > explicitPosition + Math.Max(0, nearStartGuardSeconds))
            {
                return inferredPagePosition;
            }

            return explicitPosition;
        }

        var chapterProgressPosition = chapters
            .Where(item => item.ProgressPercent is > 0 and < 99.5)
            .Select(ResumePositionFor)
            .Where(position => position is > 0)
            .Select(position => position!.Value)
            .OrderDescending()
            .FirstOrDefault();
        if (chapterProgressPosition > 0)
        {
            return chapterProgressPosition;
        }

        if (inferredPagePosition is > 0)
        {
            return inferredPagePosition;
        }

        return null;
    }

    private double? ResolveAudiobookTotalSeconds(IReadOnlyList<MediaGroupingItemViewModel> chapters)
    {
        var total = TotalAudiobookSeconds(chapters);
        if (total is > 0)
        {
            return total;
        }

        foreach (var label in Model?.Metadata
                     .Where(item => string.Equals(item.Kind, "duration", StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Label) ?? [])
        {
            if (TryParseDurationSeconds(label) is { } seconds)
            {
                return seconds;
            }
        }

        foreach (var runtime in Model?.OwnedFormats
                     .Where(item => item.FormatType == MediaFormatType.Audiobook)
                     .Select(item => item.Runtime) ?? [])
        {
            if (TryParseDurationSeconds(runtime) is { } seconds)
            {
                return seconds;
            }
        }

        return null;
    }

    private static MediaGroupingItemViewModel? ChapterAtPosition(
        IReadOnlyList<MediaGroupingItemViewModel> chapters,
        double positionSeconds) =>
        chapters.LastOrDefault(item =>
        {
            var start = Math.Max(0, item.StartSeconds ?? 0);
            var end = item.EndSeconds ?? (item.DurationSeconds is > 0 ? start + item.DurationSeconds.Value : (double?)null);
            return positionSeconds >= start - 0.5d
                && (!end.HasValue || positionSeconds < end.Value - 0.5d);
        });

    private static double? TotalAudiobookSeconds(IReadOnlyList<MediaGroupingItemViewModel> chapters)
    {
        var maxEnd = chapters
            .Select(item => item.EndSeconds ?? (item.StartSeconds.HasValue && item.DurationSeconds.HasValue
                ? item.StartSeconds.Value + item.DurationSeconds.Value
                : item.DurationSeconds))
            .Where(seconds => seconds is > 0)
            .Select(seconds => seconds!.Value)
            .DefaultIfEmpty()
            .Max();

        if (maxEnd > 0)
        {
            return maxEnd;
        }

        var durations = chapters
            .Select(item => item.DurationSeconds)
            .Where(seconds => seconds is > 0)
            .Select(seconds => seconds!.Value)
            .ToList();
        return durations.Count == chapters.Count && durations.Count > 0
            ? durations.Sum()
            : null;
    }

    private static double? TryParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        var trimmed = duration.Trim();
        var clockParts = trimmed.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (clockParts.Length is 2 or 3 && clockParts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var total = 0d;
            foreach (var part in clockParts)
            {
                total = total * 60d + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            return total > 0 ? total : null;
        }

        var matches = Regex.Matches(
            trimmed,
            @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            return null;
        }

        var seconds = 0d;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            seconds += unit[0] switch
            {
                'h' => value * 3600d,
                'm' => value * 60d,
                's' => value,
                _ => 0d,
            };
        }

        return seconds > 0 ? seconds : null;
    }

    private static bool IsCompletedChapter(MediaGroupingItemViewModel item) =>
        item.ProgressPercent is >= 99.5;

    private IReadOnlyList<MediaGroupingItemViewModel> AudiobookTracks() =>
        Model?.MediaGroups
            .Where(group => string.Equals(group.Key, "tracks", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.Items)
            .Where(item => item.EntityType is DetailEntityType.Audiobook or DetailEntityType.Work)
            .OrderBy(TrackSortKey)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    private ListenQueueItem ToAudiobookQueueItem(MediaGroupingItemViewModel item, IReadOnlyList<MediaGroupingItemViewModel> chapters, double? initialPositionSeconds = null)
        => new()
        {
            WorkId = Guid.TryParse(item.Id, out var workId) ? workId : Guid.Empty,
            AssetId = Guid.TryParse(item.AssetId, out var assetId) ? assetId : null,
            MediaType = "Audiobooks",
            Title = Model?.Title ?? item.Title,
            Subtitle = item.Title,
            Album = Model?.Title,
            AudiobookWorkId = Guid.TryParse(Model?.Id, out var audiobookId) ? audiobookId : null,
            AuthorName = string.Join("; ", Model?.Facts?.Authors ?? []),
            NarratorName = string.Join("; ", Model?.Facts?.Narrators ?? []),
            Synopsis = Model?.Description,
            Authors = CanonicalAudiobookContributors(CreditGroupType.Authors),
            Narrators = CanonicalAudiobookContributors(CreditGroupType.Narrators),
            CoverUrl = item.ArtworkUrl ?? Model?.Artwork.CoverUrl ?? Model?.Artwork.PosterUrl,
            Duration = item.Duration,
            InitialPositionSeconds = initialPositionSeconds ?? ResumePositionFor(item),
            Chapters = chapters.Select(ToPlaybackChapter).ToList(),
            ChapterIndex = item.ChapterIndex,
        };

    private IReadOnlyList<PlaybackContributorIdentity> CanonicalAudiobookContributors(CreditGroupType groupType) =>
        Model?.ContributorGroups
            .Where(group => group.GroupType == groupType)
            .SelectMany(group => group.Credits)
            .Where(credit => !string.IsNullOrWhiteSpace(credit.DisplayName))
            .Select(credit => new PlaybackContributorIdentity(
                credit.DisplayName.Trim(),
                credit.EntityType == RelatedEntityType.Person && Guid.TryParse(credit.EntityId, out var personId) && personId != Guid.Empty
                    ? personId
                    : null))
            .ToList()
        ?? [];

    private static PlaybackChapterDto ToPlaybackChapter(MediaGroupingItemViewModel item)
        => new()
        {
            AssetId = Guid.TryParse(item.AssetId, out var assetId) ? assetId : null,
            Index = Math.Max(0, item.ChapterIndex ?? 0),
            Title = string.IsNullOrWhiteSpace(item.Title)
                ? $"Track {Math.Max(1, (item.ChapterIndex ?? 0) + 1)}"
                : item.Title.Trim(),
            StartSeconds = Math.Max(0, item.StartSeconds ?? 0),
            EndSeconds = item.EndSeconds,
        };

    private static double? ResumePositionFor(MediaGroupingItemViewModel item)
    {
        if (item.ResumePositionSeconds is > 0)
        {
            return item.ResumePositionSeconds;
        }

        if (item.ProgressPercent is > 0 and < 100)
        {
            var start = Math.Max(0, item.StartSeconds ?? 0);
            var end = item.EndSeconds;
            if (end is > 0 && end.Value > start)
            {
                return start + (end.Value - start) * Math.Clamp(item.ProgressPercent.Value, 0, 100) / 100d;
            }
        }

        return item.StartSeconds;
    }

    private static int TrackSortKey(MediaGroupingItemViewModel item)
        => int.TryParse(item.TrackNumber, out var parsed) ? parsed : int.MaxValue;
}
