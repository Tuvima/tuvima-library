using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The video player's Find subtitles view (video player follow-ups D9 to D12): one live search when the view opens, in the profile's
/// subtitle language; a language choice that searches again; "Use this" and "This one is wrong". The Engine decides everything about
/// sources and access; this holds the state and the calls, and the view holds the wording. It stays out of the host's way: the host only
/// gives it the item, a way to find the track in use, and what to do once the subtitle tracks changed.
/// </summary>
public sealed class SubtitleFindPresenter(IEngineApiClient api, TimeSpan? stillSearchingAfter = null) : IDisposable
{
    /// <summary>How long a search runs before the view says it is still searching (follow-ups D11).</summary>
    public static readonly TimeSpan DefaultStillSearchingAfter = TimeSpan.FromSeconds(20);

    private const string SubtitlesKind = "subtitles";
    private readonly TimeSpan _stillSearchingAfter = stillSearchingAfter ?? DefaultStillSearchingAfter;
    private CancellationTokenSource _search = new();
    private Guid? _assetId;
    private long _generation;
    private bool _disposed;

    public event Action? Changed;

    /// <summary>What to do once the title's subtitle tracks changed: reload them and show the given track (null when none is in use now).</summary>
    public Func<Guid?, Task>? TracksChanged { get; set; }

    /// <summary>The managed (downloaded) track in use now that came from the given source, or null.</summary>
    public Func<string, Guid?>? CurrentTrackFor { get; set; }

    public bool Loading { get; private set; }

    /// <summary>True once a search has run longer than the wait; the view keeps waiting and says so.</summary>
    public bool StillSearching { get; private set; }

    /// <summary>The Engine's answer, or null while searching or when it could not be reached (see <see cref="Failed"/>).</summary>
    public TextTrackAlternativesResponse? Response { get; private set; }

    /// <summary>True when the Engine could not be reached, so the view says the search could not be completed.</summary>
    public bool Failed { get; private set; }

    /// <summary>The name of the source the last answer came from, kept so a new search can say "Searching SubDL…".</summary>
    public string? SourceName { get; private set; }

    /// <summary>The language searched, as a language name ("English").</summary>
    public string Language { get; private set; } = DefaultLanguageName;

    /// <summary>The language choice: the profile's preferred and fallback languages first, then the app's fixed list.</summary>
    public IReadOnlyList<string> Languages { get; private set; } = PlaybackLanguageOptions.Common;

    /// <summary>The result a download was asked for and is running (its source id), or null.</summary>
    public string? ChoosingSourceId { get; private set; }

    /// <summary>The result whose download or removal just failed, so its row says so; the list stays.</summary>
    public string? FailedSourceId { get; private set; }

    /// <summary>What went wrong on <see cref="FailedSourceId"/>: <c>download</c> or <c>remove</c>.</summary>
    public string? FailedAction { get; private set; }

    private static string DefaultLanguageName
    {
        get
        {
            var culture = CultureInfo.CurrentUICulture;
            var two = culture.TwoLetterISOLanguageName;
            try
            {
                // The invariant culture ("iv") is no language: it falls through to English.
                var name = culture.Name.Length > 0 && two.Length == 2 ? CultureInfo.GetCultureInfo(two).EnglishName : null;
                return string.IsNullOrWhiteSpace(name) ? "English" : name;
            }
            catch (CultureNotFoundException)
            {
                return "English";
            }
        }
    }

    /// <summary>The item the view is about. A different item clears everything, so a late answer for the old one is never shown.</summary>
    public void Bind(Guid? assetId)
    {
        if (_assetId == assetId)
        {
            return;
        }
        _assetId = assetId;
        Reset();
    }

    /// <summary>Forgets the last search and cancels one that is running.</summary>
    public void Reset()
    {
        _generation++;
        CancelSearch();
        Loading = false; StillSearching = false; Response = null; Failed = false; SourceName = null;
        ChoosingSourceId = null; FailedSourceId = null; FailedAction = null;
        Raise();
    }

    /// <summary>The view opened: works out the language from the profile and searches once.</summary>
    public Task OpenAsync(SubtitleLanguageSettingsDto? profile)
    {
        var preferred = profile is null ? [] : VideoPresentationResolver.SubtitleLanguagesInOrder(profile);
        Language = preferred.Count > 0 ? preferred[0] : DefaultLanguageName;
        Languages = preferred
            .Concat(PlaybackLanguageOptions.Common)
            .Append(Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return SearchAsync();
    }

    public Task ChangeLanguageAsync(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, Language, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }
        Language = language.Trim();
        return SearchAsync();
    }

    /// <summary>Asks the sources once, in <see cref="Language"/>. Also the "Try again" action.</summary>
    public async Task SearchAsync()
    {
        if (_assetId is not Guid assetId || _disposed)
        {
            return;
        }
        var generation = ++_generation;
        CancelSearch();
        var token = _search.Token;
        Loading = true; StillSearching = false; Response = null; Failed = false;
        ChoosingSourceId = null; FailedSourceId = null; FailedAction = null;
        Raise();
        _ = WatchAsync(generation, token);
        TextTrackAlternativesResponse? answer;
        try
        {
            answer = await api.GetTextTrackAlternativesAsync(assetId, SubtitlesKind, token, Language);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || generation != _generation)
        {
            return;
        }
        Loading = false; StillSearching = false;
        Response = answer;
        Failed = answer is null;
        if (!string.IsNullOrWhiteSpace(answer?.source_name))
        {
            SourceName = answer.source_name;
        }
        Raise();
    }

    /// <summary>"Use this": downloads that one result and shows it at once. On failure the list stays and that row says so.</summary>
    public async Task ChooseAsync(TextTrackAlternativeDto item)
    {
        if (_assetId is not Guid assetId || ChoosingSourceId is not null || Response is null)
        {
            return;
        }
        var generation = _generation;
        ChoosingSourceId = item.source_id; FailedSourceId = null; FailedAction = null;
        Raise();
        TextTrackChoiceResponse? choice = null;
        try
        {
            choice = await api.ChooseTextTrackAlternativeAsync(
                assetId, new ChooseTextTrackAlternativeRequest { kind = SubtitlesKind, provider = item.provider, source_id = item.source_id, language = Language });
        }
        catch (OperationCanceledException)
        {
            // The view was left while the download ran; nothing to show.
        }
        if (_disposed || generation != _generation)
        {
            return;
        }
        ChoosingSourceId = null;
        if (choice is not { status: "Updated" })
        {
            FailedSourceId = item.source_id; FailedAction = "download";
            Raise();
            return;
        }
        if (TracksChanged is { } changed)
        {
            await changed(choice.track_id);
        }
        if (_disposed || generation != _generation)
        {
            return;
        }
        Response = Response with
        {
            items = Response.items
                .Select(other => other with
                {
                    is_current = string.Equals(other.provider, item.provider, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(other.source_id, item.source_id, StringComparison.OrdinalIgnoreCase),
                })
                .ToList(),
        };
        Raise();
    }

    /// <summary>"This one is wrong": the Engine removes the track in use and never offers it again; then the list is asked for again.</summary>
    public async Task MarkWrongAsync(TextTrackAlternativeDto item)
    {
        if (_assetId is not Guid assetId || ChoosingSourceId is not null)
        {
            return;
        }
        var generation = _generation;
        var trackId = CurrentTrackFor?.Invoke(item.provider);
        ChoosingSourceId = item.source_id; FailedSourceId = null; FailedAction = null;
        Raise();
        TextTrackChoiceResponse? result = null;
        if (trackId is Guid id)
        {
            try { result = await api.MarkTextTrackWrongAsync(assetId, id); }
            catch (OperationCanceledException)
            {
                // The view was left while the call ran; nothing to show.
            }
        }
        if (_disposed || generation != _generation)
        {
            return;
        }
        ChoosingSourceId = null;
        if (result is not { status: "Updated" })
        {
            FailedSourceId = item.source_id; FailedAction = "remove";
            Raise();
            return;
        }
        if (TracksChanged is { } changed)
        {
            await changed(result.track_id);
        }
        if (_disposed || generation != _generation)
        {
            return;
        }
        await SearchAsync();
    }

    private async Task WatchAsync(long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(_stillSearchingAfter, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!_disposed && generation == _generation && Loading)
        {
            StillSearching = true;
            Raise();
        }
    }

    private void CancelSearch()
    {
        _search.Cancel();
        _search.Dispose();
        _search = new CancellationTokenSource();
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _disposed = true;
        _search.Cancel();
        _search.Dispose();
    }
}
