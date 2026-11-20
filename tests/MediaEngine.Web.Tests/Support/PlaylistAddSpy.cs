using MediaEngine.Contracts.Collections;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// A hand-written stand-in for the Engine's batch add (<c>POST /collections/{id}/items</c> with <c>work_ids</c>): the songs the playlist
/// already holds are counted as "already there", the rest are recorded as added. Tests hand it to <c>EngineApiClientStub.SetHandler</c>.
/// </summary>
internal static class PlaylistAddSpy
{
    /// <summary>Answers one batch add. <paramref name="args"/> are the client call's (playlist id, work ids, profile id, cancellation token).</summary>
    public static Task<CollectionItemsAddedResponse?> Answer(object?[]? args, IReadOnlyCollection<Guid> alreadyThere, Action<Guid, Guid> record)
    {
        var playlist = (Guid)args![0]!;
        var sent = (IReadOnlyList<Guid>)args[1]!;
        var fresh = sent.Where(id => !alreadyThere.Contains(id)).ToList();
        foreach (var work in fresh)
        {
            record(playlist, work);
        }

        return Task.FromResult<CollectionItemsAddedResponse?>(new CollectionItemsAddedResponse(fresh.Count, sent.Count - fresh.Count));
    }
}
