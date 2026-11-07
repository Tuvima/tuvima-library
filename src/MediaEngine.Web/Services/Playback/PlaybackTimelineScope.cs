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

    /// <summary>Reads the active profile's choice once per profile; a different profile has its own.</summary>
    public async Task EnsureLoadedAsync()
    {
        var profile = _playback.ActiveProfileId;
        if (_hasLoaded && _loadedFor == profile || _loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey(profile));
            _wholeBook = string.Equals(stored, "book", StringComparison.Ordinal);
            _loadedFor = profile;
            _hasLoaded = true;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException)
        {
            _logger?.LogDebug(ex, "The Chapter or Book choice could not be read; the timeline shows the chapter.");
            _wholeBook = false;
            _loadedFor = profile;
            _hasLoaded = true;
        }
        finally { _loading = false; }
    }

    /// <summary>Switches between the chapter (false) and the whole book (true) for the active profile and remembers the choice.</summary>
    public async Task SetWholeBookAsync(bool wholeBook)
    {
        var profile = _playback.ActiveProfileId;
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
