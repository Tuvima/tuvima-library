using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests.Support;

/// <summary>A hand-written command sink that records what a player panel sends, and answers with a chosen reply.</summary>
public sealed class RecordingPlaybackSink : IPlaybackCommandSink, IPlaybackIdentityNavigationSink
{
    public ListenPlaybackCommandReplyDto? Reply { get; set; } = new();

    public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];

    public List<(string Kind, Guid Id)> Navigations { get; } = [];

    public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;

    public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
    {
        Commands.Add((snapshot, command));
        return Task.FromResult(Reply);
    }

    public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id, CancellationToken ct = default)
    {
        Navigations.Add((kind, id));
        return Task.FromResult<ListenPlaybackCommandReplyDto?>(new() { BooleanResult = true });
    }
}
