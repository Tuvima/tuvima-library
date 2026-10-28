using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The lyrics panel's "Find another" list, "This one is wrong", and the one-time check of whether any lyrics
/// source is switched on. The Engine decides who may do any of it; the panel only shows what comes back.
/// </summary>
public sealed partial class PlaybackLyricsPresenter
{
    private const string LyricsKind = "lyrics";
    private bool _probed;
    private long _lookupGeneration;

    /// <summary>The list under "Not right? Find another", or null while it is closed.</summary>
    public TextTrackAlternativesResponse? Alternatives { get; private set; }
    public bool AlternativesOpen { get; private set; }
    public bool AlternativesLoading { get; private set; }
    public bool Choosing { get; private set; }

    /// <summary>What switched-on plugins and sources offer on the player for this item to the person asking.</summary>
    public IReadOnlyList<PlayerPluginActionDto> PluginActions { get; private set; } = [];
    private bool _actionsLoaded;

    /// <summary>True when the Engine says no lyrics source is switched on (only known once checked).</summary>
    public bool NoSourceOn { get; private set; }

    private void ResetLookup()
    {
        _lookupGeneration++;
        _probed = false; NoSourceOn = false; _actionsLoaded = false; PluginActions = [];
        Alternatives = null; AlternativesOpen = false; AlternativesLoading = false; Choosing = false;
    }

    /// <summary>When there are no lyrics, asks once whether any source is on, so the panel can say so instead of "none found".</summary>
    public async Task ProbeSourcesAsync()
    {
        if (_probed || _identity is not { } identity || Loading || Lines.Count > 0)
        {
            return;
        }
        _probed = true;
        var generation = _lookupGeneration;
        var result = await api.GetTextTrackAlternativesAsync(identity.AssetId, LyricsKind, _reads.Token);
        if (_disposed || generation != _lookupGeneration)
        {
            return;
        }
        NoSourceOn = string.Equals(result?.status, "NoSourceOn", StringComparison.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    public async Task EnsureActionsAsync()
    {
        if (_actionsLoaded || _identity is not { } identity)
        {
            return;
        }
        _actionsLoaded = true;
        var generation = _lookupGeneration;
        var actions = await api.GetPlayerPluginActionsAsync(identity.AssetId, _reads.Token);
        if (_disposed || generation != _lookupGeneration)
        {
            return;
        }
        PluginActions = actions;
        Changed?.Invoke();
    }

    /// <summary>Where the lyrics showing now came from, in plain words; null when none are showing.</summary>
    public string? SourceLine
    {
        get
        {
            if (Lines.Count == 0 || Tracks.FirstOrDefault(track => track.Id == SelectedTrackId) is not { } track)
            {
                return null;
            }
            if (track.IsUserOwned)
            {
                return "Lyrics from a file next to the song";
            }
            return string.Equals(track.Provider, "lrclib", StringComparison.OrdinalIgnoreCase)
                ? "Lyrics from LRCLIB"
                : string.IsNullOrWhiteSpace(track.Provider) ? null : $"Lyrics from {track.Provider}";
        }
    }

    /// <summary>Adds a lyrics file (.lrc or plain text) the person picked, then shows it. Open to any profile.</summary>
    public async Task ImportFileAsync(string fileName, string contentType, Stream content)
    {
        if (Refreshing || _identity is not { } identity || _commands is null)
        {
            return;
        }
        var generation = _generation; var selection = _selectionGeneration;
        Refreshing = true; Status = null; Changed?.Invoke();
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration;
        try
        {
            var result = await api.ImportTextTrackAsync(identity.AssetId, LyricsKind, "und", fileName, contentType, content, _reads.Token);
            if (!Fresh())
            {
                return;
            }
            var message = result?.message ?? "That lyrics file could not be added.";
            _tracksLoaded = false; _contentLoaded = false; _probed = false;
            await EnsureAsync(_snapshot, _commands);
            if (Current(identity, generation))
            {
                Status = message;
            }
        }
        catch (Exception)
        {
            if (Fresh())
            {
                Status = "That lyrics file could not be added.";
            }
        }
        finally { if (Current(identity, generation)) { Refreshing = false; Changed?.Invoke(); } }
    }

    public bool Offers(string kind) => PluginActions.Any(action => string.Equals(action.kind, kind, StringComparison.OrdinalIgnoreCase));

    public async Task OpenAlternativesAsync()
    {
        if (_identity is not { } identity || AlternativesLoading)
        {
            return;
        }
        var generation = _lookupGeneration;
        AlternativesOpen = true; AlternativesLoading = true; Alternatives = null; Status = null; Changed?.Invoke();
        var result = await api.GetTextTrackAlternativesAsync(identity.AssetId, LyricsKind, _reads.Token);
        if (_disposed || generation != _lookupGeneration)
        {
            return;
        }
        Alternatives = result ?? new TextTrackAlternativesResponse { status = "Unavailable", message = "Other lyrics could not be looked up right now." };
        AlternativesLoading = false; Changed?.Invoke();
    }

    public void CloseAlternatives()
    {
        AlternativesOpen = false; AlternativesLoading = false; Alternatives = null; Changed?.Invoke();
    }

    public async Task ChooseAsync(TextTrackAlternativeDto alternative)
    {
        if (_identity is not { } identity || Choosing || alternative.is_current)
        {
            return;
        }
        var generation = _lookupGeneration;
        Choosing = true; Changed?.Invoke();
        var result = await api.ChooseTextTrackAlternativeAsync(identity.AssetId,
            new ChooseTextTrackAlternativeRequest { kind = LyricsKind, provider = alternative.provider, source_id = alternative.source_id }, _reads.Token);
        await AfterChoiceAsync(identity, generation, result, "Those lyrics could not be saved.");
    }

    /// <summary>Marks the lyrics now showing as wrong. The Engine keeps the previous version when there is one.</summary>
    public async Task MarkWrongAsync()
    {
        if (_identity is not { } identity || SelectedTrackId is not { } trackId || Choosing)
        {
            return;
        }
        var generation = _lookupGeneration;
        Choosing = true; Changed?.Invoke();
        var result = await api.MarkTextTrackWrongAsync(identity.AssetId, trackId, _reads.Token);
        await AfterChoiceAsync(identity, generation, result, "Could not mark those lyrics as wrong.");
    }

    private async Task AfterChoiceAsync(PlaybackLyricsIdentity identity, long generation, TextTrackChoiceResponse? result, string failure)
    {
        if (_disposed || generation != _lookupGeneration)
        {
            return;
        }
        Choosing = false;
        AlternativesOpen = false; Alternatives = null;
        if (result is null)
        {
            Status = failure; Changed?.Invoke();
            return;
        }
        _tracksLoaded = false; _contentLoaded = false; _probed = false;
        if (_commands is not null)
        {
            await EnsureAsync(_snapshot, _commands);
        }
        if (!_disposed && generation == _lookupGeneration && _identity == identity)
        {
            Status = string.IsNullOrWhiteSpace(result.message) ? null : result.message;
            Changed?.Invoke();
        }
    }
}
