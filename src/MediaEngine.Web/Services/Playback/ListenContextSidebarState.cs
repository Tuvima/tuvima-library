using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Profile preferences for the currently applicable single Listen context panel.</summary>
public sealed class ListenContextSidebarState : IDisposable
{
    private readonly IContextSidebarPreferences preferences;
    private readonly UIOrchestratorService? _orchestrator;
    private readonly Dictionary<string, ContextSidebarLayoutDto> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _contextVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private long _profileGeneration;
    public event Action? Changed;
    public string? PersistenceMessage { get; private set; }
    public long InteractionGeneration => Volatile.Read(ref _interactionGeneration);
    private long _interactionGeneration;

    public ListenContextSidebarState(IContextSidebarPreferences preferences, UIOrchestratorService? orchestrator = null)
    {
        this.preferences = preferences;
        _orchestrator = orchestrator;
        if (_orchestrator is not null) _orchestrator.OnProfileChanged += HandleProfileChanged;
    }

    public ContextSidebarLayoutDto For(PlaybackSessionController playback) => Layout(Context(playback));

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _profileGeneration);
        var loaded = new Dictionary<string, ContextSidebarLayoutDto>(StringComparer.OrdinalIgnoreCase);
        var versions = _contextVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var context in new[] { "desktop:music", "desktop:audiobook", "desktop:video" })
        {
            var saved = ContextSidebarPreferences.Copy(await preferences.GetAsync(context, ct).ConfigureAwait(false));
            // Persist the chosen panel, never restore a temporary over-page tool.
            saved.Open = false;
            loaded[context] = saved;
        }
        if (generation != Volatile.Read(ref _profileGeneration)) return;
        foreach (var pair in loaded)
        {
            if (_contextVersions.GetValueOrDefault(pair.Key) == versions.GetValueOrDefault(pair.Key))
                _layouts[pair.Key] = pair.Value;
        }
        _loaded = true;
        PersistenceMessage = null;
        Changed?.Invoke();
    }

    public async Task TogglePanelAsync(PlaybackSessionController playback, string key)
    {
        var context = Context(playback);
        if (!Allowed(context, key)) return;
        var layout = ContextSidebarPreferences.Copy(Layout(context));
        if (layout.Open && string.Equals(layout.ActivePanelKey, key, StringComparison.OrdinalIgnoreCase))
            layout.Open = false;
        else
        {
            layout.Open = true;
            layout.ActivePanelKey = key;
        }
        await CommitAsync(context, layout).ConfigureAwait(false);
    }

    public async Task OpenPanelAsync(string context, string key)
    {
        if (!Allowed(context, key)) return;
        var layout = ContextSidebarPreferences.Copy(Layout(context));
        layout.Open = true;
        layout.ActivePanelKey = key;
        await CommitAsync(context, layout).ConfigureAwait(false);
    }

    public async Task CloseAsync(PlaybackSessionController playback)
    {
        var context = Context(playback);
        var layout = ContextSidebarPreferences.Copy(Layout(context));
        layout.Open = false;
        await CommitAsync(context, layout).ConfigureAwait(false);
    }

    public async Task<bool> SetOpenAsync(string context, bool isOpen, CancellationToken ct = default)
    {
        var layout = ContextSidebarPreferences.Copy(Layout(context));
        layout.Open = isOpen;
        return await CommitAsync(context, layout, ct).ConfigureAwait(false);
    }

    private ContextSidebarLayoutDto Layout(string context)
    {
        if (_layouts.TryGetValue(context, out var layout)) return layout;
        layout = ContextSidebarPreferences.Default(context);
        layout.Open = false;
        _layouts[context] = layout;
        return layout;
    }

    private async Task<bool> CommitAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default)
    {
        var generation = Volatile.Read(ref _profileGeneration);
        Interlocked.Increment(ref _interactionGeneration);
        var operation = _contextVersions.GetValueOrDefault(context) + 1;
        _contextVersions[context] = operation;
        _layouts[context] = ContextSidebarPreferences.Copy(layout);
        PersistenceMessage = null;
        Changed?.Invoke();
        if (!_loaded) return true;
        var saved = await preferences.SaveAsync(context, layout, ct).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _profileGeneration)
            || _contextVersions.GetValueOrDefault(context) != operation) return false;
        if (!saved) PersistenceMessage = "The panel changed here but could not be saved to this profile.";
        Changed?.Invoke();
        return saved;
    }

    private static string Context(PlaybackSessionController playback) => playback.IsVideoMode
        ? "desktop:video"
        : playback.IsAudiobookMode ? "desktop:audiobook" : "desktop:music";

    private static bool Allowed(string context, string key) => context switch
    {
        "desktop:music" => key is "queue" or "lyrics" or "history",
        "desktop:audiobook" => key is "chapters" or "history",
        "desktop:video" => key is "next-up" or "details",
        "desktop:ingestion" => key is "run",
        _ => false,
    };

    private void HandleProfileChanged()
    {
        Interlocked.Increment(ref _profileGeneration);
        _layouts.Clear();
        _contextVersions.Clear();
        _loaded = false;
        PersistenceMessage = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_orchestrator is not null) _orchestrator.OnProfileChanged -= HandleProfileChanged;
    }
}
