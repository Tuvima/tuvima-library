namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Answers one question for background work that should stay out of the way: is anyone
/// actively playing media right now? Paused and stale sessions do not count.
/// </summary>
public interface IPlaybackActivityProbe
{
    Task<bool> IsAnyonePlayingAsync(CancellationToken ct = default);
}
