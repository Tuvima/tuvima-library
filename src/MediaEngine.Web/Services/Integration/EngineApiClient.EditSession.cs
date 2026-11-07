using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<LibraryBatchEditOutcome<EditSessionResult>> RunEditSessionAsync(
        EditSessionRequest request, CancellationToken ct = default) =>
        PostBatchEditAsync<EditSessionRequest, EditSessionResult>("/library/edit-session", request, "POST /library/edit-session", ct);

    public Task<EditorSubjectDto?> GetEditorSubjectAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<EditorSubjectDto>("GET /library/editor-subjects/{id}", $"/library/editor-subjects/{id:D}", ct: ct);

    public Task<EditorSubjectDto?> GetUniverseEditorSubjectAsync(string qid, CancellationToken ct = default) =>
        GetAsync<EditorSubjectDto>(
            "GET /library/editor-subjects/universes/{qid}",
            $"/library/editor-subjects/universes/{Uri.EscapeDataString(qid)}",
            ct: ct);

    public Task<ItemQuickFactsDto?> GetEditorQuickFactsAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ItemQuickFactsDto>("GET /library/editor-quick-facts/{id}", $"/library/editor-quick-facts/{id:D}", ct: ct);

    public Task<EditorHeaderLevelDto?> GetEditorNavLevelAsync(
        Guid entityId, Guid? levelId, string? query, int offset, Guid? supportingFileId = null, CancellationToken ct = default)
    {
        var parts = new List<string> { $"offset={Math.Max(0, offset)}" };
        if (levelId is { } level)
        {
            parts.Add($"level_id={level:D}");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        if (supportingFileId is { } file)
        {
            parts.Add($"supporting_file_id={file:D}");
        }

        return GetAsync<EditorHeaderLevelDto>(
            "GET /metadata/{id}/editor-nav/level", $"/metadata/{entityId:D}/editor-nav/level?{string.Join('&', parts)}", ct: ct);
    }

    public Task<EditorHeaderConfigDto?> GetEditorKindConfigAsync(string kind, CancellationToken ct = default) =>
        GetAsync<EditorHeaderConfigDto>(
            "GET /metadata/editor-config", $"/metadata/editor-config?kind={Uri.EscapeDataString(kind)}", logAsWarning: false, ct: ct);

    public Task<EditorUniverseThingsDto?> GetEditorUniverseThingsAsync(
        string qid, string? kindKey, string? search, int offset, int limit, CancellationToken ct = default)
    {
        var query = new List<string> { $"offset={Math.Max(0, offset)}", $"limit={Math.Clamp(limit, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(kindKey))
        {
            query.Add($"kind={Uri.EscapeDataString(kindKey)}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        return GetAsync<EditorUniverseThingsDto>(
            "GET /library/editor-subjects/universes/{qid}/things",
            $"/library/editor-subjects/universes/{Uri.EscapeDataString(qid)}/things?{string.Join('&', query)}",
            ct: ct);
    }

    public Task<bool> DeleteEditorUniverseThingAsync(Guid id, CancellationToken ct = default) =>
        DeleteAsync("DELETE /library/editor-subjects/things/{id}", $"/library/editor-subjects/things/{id:D}", ct: ct);

    public async Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetEditorSubjectHistoryAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await GetAsync<List<MediaEngine.Contracts.Items.LibraryItemHistoryDto>>(
            "GET /library/editor-subjects/{id}/history", $"/library/editor-subjects/{id:D}/history", ct: ct);
        return rows is null
            ? ([], "History could not be loaded. Try again later.")
            : (rows.Select(item => new LibraryItemHistoryDto
            {
                Id = item.Id,
                EntityId = item.EntityId,
                OccurredAt = item.OccurredAt,
                EventType = item.EventType,
                Label = item.Label,
                Detail = item.Detail,
                Category = item.Category,
                ActorLabel = item.ActorLabel,
            }).ToList(), null);
    }
}
