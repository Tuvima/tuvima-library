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
    private readonly HashSet<string> _dockTools = [];
    private string? _pinnedDockTool;
    private string? _experience;
    public bool IsPinnedDock(string tool) => _pinnedDockTool == tool;
    public bool IsPinnedFor(string tool) => IsPinnedDock(tool) || IsPinned && IsOpen(tool);
    public void RegisterPanel(string panel, string tool, bool dockPreview = false)
    {
        _panels[panel] = tool;
        if (dockPreview) _dockTools.Add(tool);
    }
    public void UnregisterPanel(string panel)
    {
        if (_panels.Remove(panel, out var tool)) _dockTools.Remove(tool);
    }
    public bool HasPanel(string panel) => _panels.ContainsKey(panel);

    public PlaybackTransientToolCoordinator(PlaybackSessionController? playback = null)
    {
        _playback = playback;
        if (playback is null) return;
        _identity = Identity(playback);
        _experience = playback.Experience;
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
        if (_dockTools.Contains(toolId))
        {
            // A hover preview cannot replace a card the user explicitly pinned.
            if (!pinned && _pinnedDockTool is not null) return;
            if (pinned) _pinnedDockTool = toolId;
        }
        if (OpenToolId == toolId && IsPinned == pinned) return;
        if (parentPanel is not null && _panels.TryGetValue(parentPanel, out var parent) && parent != toolId)
            _parents[toolId] = parent;
        else if (_pinnedDockTool is not null && _pinnedDockTool != toolId && !_dockTools.Contains(toolId))
            _parents[toolId] = _pinnedDockTool;
        else _parents.Remove(toolId);
        OpenToolId = toolId;
        IsPinned = pinned;
        Changed?.Invoke();
    }

    public void Close(string? toolId = null)
    {
        if (OpenToolId is null || toolId is not null && !IsOpen(toolId)) return;
        if (toolId is null || toolId == _pinnedDockTool) _pinnedDockTool = null;
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
        var retainDock = _pinnedDockTool is not null && _identity.Profile == identity.Profile
            && _experience == _playback.Experience && _playback.HasQueue && !_playback.IsVideoMode;
        _identity = identity;
        _experience = _playback.Experience;
        if (retainDock)
        {
            OpenToolId = _pinnedDockTool;
            IsPinned = true;
            Changed?.Invoke();
        }
        else Close();
    }

    public void Dispose()
    {
        if (_playback is not null) _playback.Changed -= PlaybackChanged;
    }
}
