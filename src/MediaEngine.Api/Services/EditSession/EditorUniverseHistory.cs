using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The History tab of a Universe and of its characters, places and factions (universal editor v4, D10): what changed and from where.
/// A saved edit adds one row to the thing's timeline (<see cref="Record"/>), and the editor reads the rows back as the same history
/// rows a library title shows (<see cref="Read"/>), so one History tool draws both.
/// </summary>
public static class EditorUniverseHistory
{
    public const string FieldEvent = "user_field_edit";
    public const string ArtworkEvent = "user_artwork_edit";
    public const string AddedEvent = "user_added";
    public const string Trigger = "edit_session";

    /// <summary>True for the subjects whose edits are listed in History (a Universe and the things in it).</summary>
    public static bool Keeps(string? subjectKind) =>
        subjectKind is EditorSubjectKinds.Universe or EditorSubjectKinds.FictionalEntity;

    public static Task Record(
        IEntityTimelineRepository timeline, Guid entityId, string subjectKind, string eventType, string detail, CancellationToken ct) =>
        timeline.InsertEventAsync(
            new EntityEvent
            {
                EntityId = entityId,
                EntityType = subjectKind == EditorSubjectKinds.Universe ? "Universe" : "FictionalEntity",
                EventType = eventType,
                Trigger = Trigger,
                Detail = detail,
            },
            ct);

    public static async Task<IReadOnlyList<LibraryItemHistoryDto>> Read(IEntityTimelineRepository timeline, Guid entityId, CancellationToken ct)
    {
        var events = await timeline.GetEventsByEntityAsync(entityId, ct).ConfigureAwait(false);
        return events
            .Where(evt => evt.EventType is FieldEvent or ArtworkEvent or AddedEvent)
            .Select(evt => new LibraryItemHistoryDto
            {
                Id = evt.Id.ToString("D"),
                EntityId = entityId,
                OccurredAt = evt.OccurredAt,
                EventType = evt.EventType,
                Label = evt.EventType switch
                {
                    ArtworkEvent => "Picture changed",
                    AddedEvent => "Added by you",
                    _ => "Details changed",
                },
                Detail = evt.Detail,
                Category = evt.EventType == ArtworkEvent ? "artwork" : "manual",
                ActorLabel = "You",
            })
            .OrderByDescending(item => item.OccurredAt)
            .ToList();
    }
}
