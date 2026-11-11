using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    private void ApplyAudiobookQueryState()
    {
        if (!IsAudiobooksView)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BrowseQuery))
        {
            _audiobookView = "all";
            _audiobookSeriesFocus = null;
            return;
        }

        var requestedView = BrowseQuery.Trim().ToLowerInvariant();
        if (requestedView is "all" or "series" or "authors" or "narrators" or "timeline")
        {
            _audiobookView = requestedView;
            _audiobookSeriesFocus = null;
        }
    }

    private IReadOnlyList<AudiobookListenItem> BuildAudiobookContinueCards()
    {
        var continueItems = _audiobookJourney
            .Where(item => item.ProgressPct is > 0 and < 99.5)
            .OrderByDescending(item => item.LastAccessed)
            .Select(item => new AudiobookListenItem(ResolveAudiobookItem(item.WorkId, item), item))
            .Take(3)
            .ToList();

        if (continueItems.Count > 0)
        {
            return continueItems;
        }

        return AudiobookDisplayItems
            .OrderByDescending(AudiobookSortTimestamp)
            .Take(3)
            .Select(item => new AudiobookListenItem(item, null))
            .ToList();
    }

    private IReadOnlyList<AudiobookDisplayItem> BuildAudiobookDisplayItems()
    {
        if (_audiobookCards.Count > 0)
        {
            return _audiobookCards
                .Select(card => new AudiobookDisplayItem(card, ResolveAudiobookWork(card.WorkId)))
                .ToList();
        }

        return _audiobookWorks
            .Select(work => new AudiobookDisplayItem(null, work))
            .ToList();
    }

    private AudiobookDisplayItem ResolveAudiobookItem(Guid workId, JourneyItemViewModel? journey)
    {
        var card = _audiobookCards.FirstOrDefault(candidate => candidate.WorkId == workId);
        var work = ResolveAudiobookWork(workId);
        if (card is not null || work is not null)
        {
            return new AudiobookDisplayItem(card, work);
        }

        var fallback = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Audiobook",
            GroupingType: "work",
            Title: journey?.Title ?? "Audiobook",
            Subtitle: journey?.Author,
            Facts: [],
            Artwork: new DisplayArtworkDto(
                CoverUrl: journey?.CoverUrl,
                CoverSmallUrl: null,
                CoverMediumUrl: null,
                CoverLargeUrl: null,
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: null,
                CoverHeightPx: null,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: null),
            PreferredShape: "cover",
            Presentation: "work",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: journey is null ? null : new DisplayProgressDto(journey.ProgressPct, journey.ProgressDisplay, journey.LastAccessed, null),
            Actions: [new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: $"/details/work/{workId:D}?context=listen")],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: journey?.LastAccessed ?? DateTimeOffset.MinValue);

        return new AudiobookDisplayItem(fallback, null);
    }

    private WorkViewModel? ResolveAudiobookWork(Guid? workId)
        => workId.HasValue && _workLookup.TryGetValue(workId.Value, out var work) && IsAudiobookWork(work)
            ? work
            : null;

    private IReadOnlyList<AudiobookDisplayItem> BuildAudiobookBrowseItems()
        => FilteredAudiobookItems;

    private IReadOnlyList<AudiobookDisplayItem> ApplyAudiobookFilters()
    {
        IEnumerable<AudiobookDisplayItem> items = AudiobookDisplayItems;

        if (!string.IsNullOrWhiteSpace(_audiobookSeriesFocus))
        {
            items = items.Where(item => AudiobookMatchesSeries(item, _audiobookSeriesFocus));
        }

        var search = _audiobookSearch.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item => AudiobookMatchesSearch(item, search));
        }

        if (_audiobookInProgressOnly)
        {
            items = items.Where(item => AudiobookProgressPercent(item) is > 0 and < 99.5);
        }

        if (_audiobookUnreadOnly)
        {
            items = items.Where(item => AudiobookProgressPercent(item) <= 0);
        }

        items = _audiobookLengthFilter switch
        {
            "short" => items.Where(item => AudiobookDurationSeconds(item) is > 0 and <= 21600),
            "medium" => items.Where(item => AudiobookDurationSeconds(item) is > 21600 and <= 50400),
            "long" => items.Where(item => AudiobookDurationSeconds(item) > 50400),
            _ => items,
        };

        return _audiobookSort switch
        {
            "title" => items.OrderBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase).ToList(),
            "author" => items.OrderBy(AudiobookCreatorLine, StringComparer.OrdinalIgnoreCase)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "length" => items.OrderByDescending(AudiobookDurationSeconds)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => items.OrderByDescending(AudiobookSortTimestamp)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyAudiobookSeriesFilters()
    {
        IEnumerable<ContentGroupViewModel> groups = _audiobookSeriesGroups;
        var search = _audiobookSearch.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            groups = groups.Where(group =>
                ContainsSearch(group.DisplayName, search)
                || ContainsSearch(group.Creator, search)
                || ContainsSearch(group.Description, search)
                || ContainsSearch(group.Tagline, search));
        }

        return _audiobookSort switch
        {
            "title" or "author" => groups.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => groups.OrderByDescending(group => group.WorkCount).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static bool AudiobookMatchesSearch(AudiobookDisplayItem item, string search)
    {
        var card = item.Card;
        var work = item.Work;
        return ContainsSearch(card?.Title, search)
               || ContainsSearch(card?.Subtitle, search)
               || (card?.Facts.Any(fact => ContainsSearch(fact, search)) ?? false)
               || ContainsSearch(work?.Title, search)
               || ContainsSearch(work?.Author, search)
               || ContainsSearch(work?.Artist, search)
               || ContainsSearch(work?.Narrator, search)
               || ContainsSearch(work?.Series, search)
               || (work?.Genres.Any(genre => ContainsSearch(genre, search)) ?? false);
    }

    private static bool AudiobookMatchesSeries(AudiobookDisplayItem item, string series)
    {
        return ContainsSearch(item.Work?.Series, series)
               || (item.Card?.Facts.Any(fact => string.Equals(fact, series, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool ContainsSearch(string? value, string search)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AudiobookAuthorGroup> BuildAudiobookAuthorGroups(IEnumerable<AudiobookDisplayItem> items)
        => items
            .SelectMany(item =>
            {
                var authors = AudiobookAuthorNames(item);
                return authors.Select(author => new { Author = author, Item = item });
            })
            .GroupBy(item => item.Author, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudiobookAuthorGroup(
                group.Key,
                group.Select(item => item.Item)
                    .OrderByDescending(AudiobookSortTimestamp)
                    .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> AudiobookAuthorNames(AudiobookDisplayItem item)
    {
        if (item.Work?.Authors.Count > 0)
        {
            return item.Work.Authors;
        }

        var creator = AudiobookCreatorLine(item);
        return string.IsNullOrWhiteSpace(creator) || string.Equals(creator, "Unknown Author", StringComparison.OrdinalIgnoreCase)
            ? ["Unknown Author"]
            : [creator];
    }

    private static IReadOnlyList<AudiobookAuthorGroup> BuildAudiobookNarratorGroups(IEnumerable<AudiobookDisplayItem> items)
        => items
            .Where(item => !string.IsNullOrWhiteSpace(item.Work?.Narrator))
            .GroupBy(item => item.Work!.Narrator!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudiobookAuthorGroup(
                group.Key,
                group.OrderByDescending(AudiobookSortTimestamp)
                    .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private JourneyItemViewModel? AudiobookJourneyFor(AudiobookDisplayItem item)
    {
        var workId = AudiobookWorkId(item);
        return workId.HasValue
            ? _audiobookJourney
                .Where(journey => journey.WorkId == workId.Value)
                .OrderByDescending(journey => journey.LastAccessed)
                .FirstOrDefault()
            : null;
    }

    private static Guid? AudiobookWorkId(AudiobookDisplayItem item)
        => item.Card?.WorkId ?? item.Work?.Id;

    private static string AudiobookTitle(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(item.Card?.Title, item.Work?.Title, "Audiobook") ?? "Audiobook";

    private static string? AudiobookCoverUrl(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(
            item.Card?.Artwork.CoverUrl,
            item.Card?.Artwork.CoverMediumUrl,
            item.Card?.Artwork.CoverSmallUrl,
            item.Card?.Artwork.SquareUrl,
            item.Card?.Artwork.BackgroundUrl,
            item.Work?.CoverUrl,
            item.Work?.CoverUrlMedium,
            item.Work?.CoverUrlSmall,
            item.Work?.SquareUrl);

    private static DateTimeOffset AudiobookSortTimestamp(AudiobookDisplayItem item)
        => item.Card?.SortTimestamp ?? item.Work?.CreatedAt ?? DateTimeOffset.MinValue;

    private double AudiobookProgressPercent(AudiobookDisplayItem item)
        => item.Card?.Progress?.Percent ?? AudiobookJourneyFor(item)?.ProgressPct ?? 0;

    private double AudiobookProgressPercent(AudiobookListenItem item)
        => item.Journey?.ProgressPct ?? AudiobookProgressPercent(item.Item);

    private string AudiobookProgressLabel(AudiobookDisplayItem item)
    {
        var progress = AudiobookProgressPercent(item);
        return progress switch
        {
            >= 99.5 => "Complete",
            > 0 => $"{Math.Max(1, progress):F0}%",
            _ => "Unread",
        };
    }

    private string AudiobookProgressLabel(AudiobookListenItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Journey?.ProgressDisplay))
        {
            return item.Journey.ProgressDisplay;
        }

        var progress = AudiobookProgressPercent(item);
        return progress switch
        {
            >= 99.5 => "Complete",
            > 0 => $"{Math.Max(1, progress):F0}%",
            _ => "Ready to start",
        };
    }

    private string AudiobookRemainingLabel(AudiobookListenItem item)
    {
        var durationSeconds = AudiobookDurationSeconds(item.Item);
        if (durationSeconds <= 0)
        {
            return item.Journey is null ? "Length unknown" : item.Journey.ProgressDisplay;
        }

        var progress = Math.Clamp(AudiobookProgressPercent(item), 0, 100);
        if (progress <= 0)
        {
            return FormatAudiobookDuration(durationSeconds);
        }

        if (progress >= 99.5)
        {
            return "Complete";
        }

        var remainingSeconds = (long)Math.Round(durationSeconds * ((100 - progress) / 100d));
        return $"{FormatAudiobookDuration(remainingSeconds)} left";
    }

    private static long AudiobookDurationSeconds(AudiobookDisplayItem item)
        => item.Work is null ? 0 : GetTrackDurationSeconds(item.Work);

    private static string AudiobookDurationLabel(AudiobookDisplayItem item)
    {
        var duration = FormatAudiobookDuration(AudiobookDurationSeconds(item));
        if (!string.Equals(duration, "Length unknown", StringComparison.OrdinalIgnoreCase))
        {
            return duration;
        }

        return item.Card?.Facts.FirstOrDefault(IsDurationFact) ?? duration;
    }

    private static bool IsDurationFact(string value)
        => value.Contains('h', StringComparison.OrdinalIgnoreCase)
           || value.Contains("min", StringComparison.OrdinalIgnoreCase)
           || value.Contains("hour", StringComparison.OrdinalIgnoreCase);

    private static string FormatAudiobookDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "Length unknown";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        var hours = (int)Math.Floor(duration.TotalHours);
        var minutes = duration.Minutes;

        if (hours <= 0)
        {
            return $"{Math.Max(1, minutes)}m";
        }

        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    private static string AudiobookCreatorLine(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(item.Card?.Subtitle, item.Work?.Author, item.Work?.Artist, "Unknown Author") ?? "Unknown Author";

    private static string AudiobookSecondaryLine(AudiobookDisplayItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Work?.Narrator))
        {
            return $"Narrated by {item.Work.Narrator}";
        }

        var creator = AudiobookCreatorLine(item);
        var fact = item.Card?.Facts.FirstOrDefault(candidate =>
            !string.Equals(candidate, creator, StringComparison.OrdinalIgnoreCase)
            && !IsDurationFact(candidate));
        return string.IsNullOrWhiteSpace(fact) ? "Audiobook" : fact;
    }

    private static string AudiobookBadge(AudiobookDisplayItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Work?.SeriesPosition))
        {
            return $"Book {item.Work.SeriesPosition}";
        }

        if (!string.IsNullOrWhiteSpace(item.Work?.Series)
            || string.Equals(item.Card?.Presentation, "audiobookSeries", StringComparison.OrdinalIgnoreCase))
        {
            return "Series";
        }

        return item.Card?.Badges.FirstOrDefault()?.Label ?? "Audiobook";
    }

    private static string AudiobookSeriesMeta(ContentGroupViewModel series)
    {
        var count = series.WorkCount == 1 ? "1 book" : $"{series.WorkCount} books";
        return string.IsNullOrWhiteSpace(series.Creator) ? count : $"{count} - {series.Creator}";
    }

    private static string AudiobookSeriesDescription(ContentGroupViewModel series)
        => StringHelpers.FirstNonBlank(series.Description, series.Tagline)
           ?? $"{series.DisplayName} includes {Pluralize(series.WorkCount, "audiobook")} in your library.";

    private static string AudiobookAuthorMeta(AudiobookAuthorGroup author)
        => author.Items.Count == 1 ? "1 audiobook" : $"{author.Items.Count} audiobooks";

    private static string AudiobookRoute(AudiobookDisplayItem item)
        => item.Card?.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.WebUrl))?.WebUrl
           ?? (item.Work is null ? "/" : MediaNavigation.ForWork(item.Work));

    private static string AudiobookSeriesRoute(ContentGroupViewModel series)
        => $"/details/collection/{series.CollectionId:D}?context=listen";

    private static string AudiobookTabClass(string view, string activeView)
        => string.Equals(view, activeView, StringComparison.OrdinalIgnoreCase)
            ? "audiobooks-tab is-active"
            : "audiobooks-tab";

    private string AudiobookLayoutToggleClass(LibraryLayoutMode mode)
        => _audiobookLayout == mode
            ? "audiobooks-layout-toggle__button is-active"
            : "audiobooks-layout-toggle__button";

    private void SetAudiobookView(string view)
    {
        _audiobookView = view;
        _audiobookSeriesFocus = null;
        Nav.NavigateTo(AudiobookBrowseRoute(view));
    }

    private void UpdateAudiobookSearch(ChangeEventArgs args)
    {
        _audiobookSearch = args.Value?.ToString() ?? string.Empty;
    }

    private void SetAudiobookInProgress(bool selected)
    {
        _audiobookInProgressOnly = selected;
        if (_audiobookInProgressOnly)
        {
            _audiobookUnreadOnly = false;
        }
    }

    private void SetAudiobookUnread(bool selected)
    {
        _audiobookUnreadOnly = selected;
        if (_audiobookUnreadOnly)
        {
            _audiobookInProgressOnly = false;
        }
    }

    private void ViewAudiobookSeries(ContentGroupViewModel series)
        => Nav.NavigateTo(AudiobookSeriesRoute(series));

    private void ViewAudiobookAuthor(AudiobookAuthorGroup author)
    {
        _audiobookView = "all";
        _audiobookSearch = author.Name;
        _audiobookSeriesFocus = null;
    }

    private void ViewAudiobookNarrator(AudiobookAuthorGroup narrator)
    {
        _audiobookView = "all";
        _audiobookSearch = narrator.Name;
        _audiobookSeriesFocus = null;
        Nav.NavigateTo(AudiobooksRoute);
    }

    private void ClearAudiobookSeriesFocus()
    {
        _audiobookSeriesFocus = null;
    }

    private Task PlayAudiobookItemAsync(AudiobookDisplayItem item)
    {
        if (item.Work is not null)
        {
            return PlaySingleWorkAsync(item.Work, "Audiobooks");
        }

        Nav.NavigateTo(AudiobookRoute(item));
        return Task.CompletedTask;
    }
}
