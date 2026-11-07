namespace MediaEngine.Api.Services.EditSession;

/// <summary>One member (an episode, a track) of a family, with its number.</summary>
public sealed record EditorFamilyMember(Guid WorkId, string Title, int? Number);

/// <summary>A season (or, for an album, all the tracks with no group id) and its members.</summary>
public sealed record EditorFamilyGroup(Guid? GroupId, string Title, int? Number, IReadOnlyList<EditorFamilyMember> Members);

/// <summary>The owned members of the family around an item (see <see cref="EditorHeaderReadService.GetFamilyMembersAsync"/>).</summary>
public sealed record EditorFamilyMembers(Guid RootId, string RootTitle, string MediaType, IReadOnlyList<EditorFamilyGroup> Groups);

/// <summary>Where the editor's Bookmarks overview learns the family of a show, season or album (the header read service; a test can stand in).</summary>
public interface IEditorFamilySource
{
    /// <summary>The owned members of the family around an item, grouped by season; null when the item is not in the Library.</summary>
    Task<EditorFamilyMembers?> GetFamilyMembersAsync(Guid workId, CancellationToken ct = default);
}
