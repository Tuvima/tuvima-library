namespace MediaEngine.Web.Services.Playback;

/// <summary>One transient playback tool per circuit. This state is never persisted.</summary>
public sealed class PlaybackTransientToolCoordinator : IDisposable
{
    private readonly PlaybackSessionController? _playback;
    private (Guid? Asset, long Version, Guid? Profile) _identity;
    public string? OpenToolId { get; private set; }
    public bool IsPinned { get; private set; }
    public event Action? Changed;
    private readonly Dictionary<string, string> _panels = [];
    private readonly Dictionary<string, string> _parents = [];
    public void RegisterPanel(string panel, string tool) => _panels[panel] = tool;
    public void UnregisterPanel(string panel) => _panels.Remove(panel);
    public bool HasPanel(string panel) => _panels.ContainsKey(panel);

    public PlaybackTransientToolCoordinator(PlaybackSessionController? playback = null)
    {
        _playback = playback;
        if (playback is null) return;
        _identity = Identity(playback);
        playback.Changed += PlaybackChanged;
    }

    public bool IsOpen(string toolId)
    {
        var candidate = OpenToolId;
        for (var depth = 0; candidate is not null && depth < 8; depth++)
        {
            if (candidate == toolId) return true;
            candidate = _parents.GetValueOrDefault(candidate);
        }
        return false;
    }
    public void Open(string toolId, bool pinned = false, string? parentPanel = null)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        if (OpenToolId == toolId && IsPinned == pinned) return;
        if (parentPanel is not null && _panels.TryGetValue(parentPanel, out var parent) && parent != toolId)
            _parents[toolId] = parent;
        else _parents.Remove(toolId);
        OpenToolId = toolId;
        IsPinned = pinned;
        Changed?.Invoke();
    }

    public void Close(string? toolId = null)
    {
        if (OpenToolId is null || toolId is not null && !IsOpen(toolId)) return;
        var parent = toolId is not null ? _parents.GetValueOrDefault(toolId) : null;
        OpenToolId = parent;
        IsPinned = parent is not null;
        Changed?.Invoke();
    }

    private static (Guid? Asset, long Version, Guid? Profile) Identity(PlaybackSessionController playback) =>
        (playback.CurrentItem?.AssetId, playback.PlaybackRequestVersion, playback.ActiveProfileId);

    private void PlaybackChanged(PlaybackChangeKind _)
    {
        if (_playback is null) return;
        var identity = Identity(_playback);
        if (_identity == identity) return;
        _identity = identity;
        Close();
    }

    public void Dispose()
    {
        if (_playback is not null) _playback.Changed -= PlaybackChanged;
    }
}
