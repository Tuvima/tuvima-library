using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// How much of a long TV episode list one detail response carries. A show with a thousand episodes is not sent
/// (or enriched) in one piece: the response lists every season with its counts and carries only one window of
/// episodes, and the Dashboard asks for other windows through the same detail endpoint
/// (<c>sequenceGroup</c>, <c>sequenceOffset</c>, <c>sequenceLimit</c>, <c>sequenceCurrent</c>, <c>sequenceOnly</c>).
/// <c>SelectedSeasonId</c> is not a query parameter: a season opened on its own (D13) sets it so the show page opens
/// on that season instead of the one being watched.
/// </summary>
internal sealed record SequenceWindowRequest(
    string? GroupKey = null,
    int Offset = 0,
    int Limit = SequenceWindowRequest.DefaultLimit,
    Guid? CurrentWorkId = null,
    bool PlacementOnly = false,
    Guid? SelectedSeasonId = null)
{
    /// <summary>Episodes per window. A normal season fits in one window, so only very long seasons ever page.</summary>
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>
    /// Up to this many episodes in total everything is sent in one response, exactly as before, so ordinary
    /// shows switch seasons without another request. Longer shows send one window.
    /// </summary>
    public const int FullEmitThreshold = 100;

    /// <summary>Every episode in one response (playback queues and the episode picker need the whole list).</summary>
    public static SequenceWindowRequest All(Guid? currentWorkId = null, bool placementOnly = false)
        => new(Limit: 0, CurrentWorkId: currentWorkId, PlacementOnly: placementOnly);

    public bool IsAll => Limit <= 0;

    /// <summary>The caller named a season, so it gets exactly that window even for a short show.</summary>
    public bool IsExplicit => !string.IsNullOrWhiteSpace(GroupKey);

    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);

    /// <summary>
    /// Builds a request from the detail endpoint's query string; null when no sequence parameter was supplied
    /// (the caller then gets the whole list).
    /// </summary>
    public static SequenceWindowRequest? FromQuery(
        string? group,
        int? offset,
        int? limit,
        string? current,
        bool? only)
    {
        if (string.IsNullOrWhiteSpace(group) && offset is null && limit is null && string.IsNullOrWhiteSpace(current) && only is not true)
        {
            return null;
        }

        return new SequenceWindowRequest(
            string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            Math.Max(0, offset ?? 0),
            // Windowing is opt-in: without sequenceLimit a request that only names an episode gets the whole list;
            // naming a season or an offset alone gets the default window.
            limit is null ? (string.IsNullOrWhiteSpace(group) && offset is null ? 0 : DefaultLimit) : Math.Max(0, limit.Value),
            Guid.TryParse(current, out var currentWorkId) ? currentWorkId : null,
            only is true);
    }
}

/// <summary>Reads the items a placement actually carries, whether they are listed flat or inside their season groups.</summary>
internal static class SequencePlacementItems
{
    public static IEnumerable<SequenceItemViewModel> EmittedItems(this SequencePlacementViewModel placement)
        => placement.OrderedItems.Count > 0 ? placement.OrderedItems : placement.Groups.SelectMany(group => group.Items);
}
