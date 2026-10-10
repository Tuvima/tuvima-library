namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Which slice of a long TV episode list a detail request asks for. Without one the Engine sends the default
/// window (the whole list for an ordinary show, one window of a very long one). Maps to the detail endpoint's
/// <c>sequenceGroup</c>, <c>sequenceOffset</c>, <c>sequenceLimit</c>, <c>sequenceCurrent</c> and <c>sequenceOnly</c>.
/// </summary>
/// <param name="Group">Season (group) key to read, for example <c>season-2</c>.</param>
/// <param name="Offset">Index of the first episode wanted within that season.</param>
/// <param name="Limit">Episodes wanted; 0 asks for every episode.</param>
/// <param name="Current">Episode to mark as the current one in the answer.</param>
/// <param name="Only">Return just the episode list, not the rest of the page.</param>
public sealed record SequenceWindowQuery(
    string? Group = null,
    int Offset = 0,
    int Limit = SequenceWindowQuery.PageSize,
    Guid? Current = null,
    bool Only = false)
{
    /// <summary>Episodes per window; matches the Engine's default.</summary>
    public const int PageSize = 50;

    /// <summary>Every owned and missing episode, for playback queues and the player's episode picker.</summary>
    public static SequenceWindowQuery WholeList { get; } = new(Limit: 0, Only: true);

    /// <summary>One window of one season, without the rest of the page.</summary>
    public static SequenceWindowQuery Page(string group, int offset, Guid? current)
        => new(group, Math.Max(0, offset), PageSize, current, Only: true);
}
