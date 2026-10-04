namespace MediaEngine.Web.Services.Playback;

/// <summary>One transient playback tool per circuit. This state is never persisted.</summary>
public sealed class PlaybackTransientToolCoordinator : IDisposable
{
    private readonly PlaybackSessionController? _playback;
    private (Guid? Asset, long Version, Guid? Profile) _identity;
    public string? OpenToolId { get; private set; }
    public bool IsPinned { get; private set; }
    public event Action? Changed;

    public PlaybackTransientToolCoordinator(PlaybackSessionController? playback = null)
    {
        _playback = playback;
        if (playback is null) return;
        _identity = Identity(playback);
        playback.Changed += PlaybackChanged;
    }

    public bool IsOpen(string toolId) => OpenToolId == toolId;
    public void Open(string toolId, bool pinned = false)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        if (OpenToolId == toolId && IsPinned == pinned) return;
        OpenToolId = toolId;
        IsPinned = pinned;
        Changed?.Invoke();
    }

    public void Close(string? toolId = null)
    {
        if (OpenToolId is null || toolId is not null && OpenToolId != toolId) return;
        OpenToolId = null;
        IsPinned = false;
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
