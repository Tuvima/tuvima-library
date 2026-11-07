using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Whether an audiobook's timeline shows the current chapter or the whole book (unified player v2 D9, D20). One profile preference, remembered in this
/// browser, shared by the bar's label ("Chapter 8" or "Book · 32%") and the Chapters panel's footer control (Chapter | Whole book), so a change in
/// either place shows in the other at once.
/// </summary>
public sealed class PlaybackTimelineScope
{
    private const string KeyPrefix = "tuvima.player.bookView.";

    private readonly IJSRuntime _js;
    private readonly PlaybackSessionController _playback;
    private readonly ILogger? _logger;
    private Guid? _loadedFor;
    private bool _hasLoaded;
    private bool _wholeBook;
    private bool _loading;
    private int _choiceVersion;

    public PlaybackTimelineScope(IJSRuntime js, PlaybackSessionController playback, ILogger<PlaybackTimelineScope>? logger = null)
    {
        _js = js;
        _playback = playback;
        _logger = logger;
    }

    /// <summary>Raised after the choice changed (or was read for another profile), so the bar and the panel redraw.</summary>
    public event Action? Changed;

    /// <summary>The browser storage key for a profile.</summary>
    public static string StorageKey(Guid? profileId) => $"{KeyPrefix}{profileId:D}";

    /// <summary>True when the active profile chose the whole book. Reads as the chapter until <see cref="EnsureLoadedAsync"/> has read the profile's choice.</summary>
    public bool WholeBook => _hasLoaded && _loadedFor == _playback.ActiveProfileId && _wholeBook;

    /// <summary>
    /// Reads the active profile's choice once per profile; a different profile has its own. A choice made while the read is in flight wins over the stored
    /// value, and when the profile changes during the read it is read again for the new one.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            while (true)
            {
                var profile = _playback.ActiveProfileId;
                if (_hasLoaded && _loadedFor == profile)
                {
                    return;
                }

                var version = _choiceVersion;
                var wholeBook = false;
                try
                {
                    var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey(profile));
                    wholeBook = string.Equals(stored, "book", StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException)
                {
                    _logger?.LogDebug(ex, "The Chapter or Book choice could not be read; the timeline shows the chapter.");
                }

                if (version != _choiceVersion)
                {
                    // Someone chose Chapter or Whole book while the stored value was being read; that choice is newer and already applied.
                    continue;
                }

                _wholeBook = wholeBook;
                _loadedFor = profile;
                _hasLoaded = true;
                Changed?.Invoke();
            }
        }
        finally { _loading = false; }
    }

    /// <summary>Switches between the chapter (false) and the whole book (true) for the active profile and remembers the choice.</summary>
    public async Task SetWholeBookAsync(bool wholeBook)
    {
        var profile = _playback.ActiveProfileId;
        _choiceVersion++;
        _wholeBook = wholeBook;
        _loadedFor = profile;
        _hasLoaded = true;
        Changed?.Invoke();
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey(profile), wholeBook ? "book" : "chapter"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException)
        {
            _logger?.LogDebug(ex, "The Chapter or Book choice could not be remembered.");
        }
    }
}
