namespace MediaEngine.Contracts.Characters;

/// <summary>
/// Wire responses for <c>CharacterEndpoints</c> routes that previously returned anonymous
/// types (<c>Results.Ok(new { ... })</c>). Property names are deliberately left exactly as the
/// anonymous types declared them — snake_case, not PascalCase — and carry no
/// <c>[JsonPropertyName]</c> overrides, so the JSON payload these records produce is
/// byte-identical to what the replaced anonymous types produced.
///
/// <para>
/// Named with a <c>Dto</c>/<c>Response</c> suffix distinct from
/// <see cref="MediaEngine.Web.Models.ViewDTOs.CharacterDtos"/> — that project is not
/// referenced from <c>MediaEngine.Api</c>/<c>MediaEngine.Contracts</c>, so there is no
/// compile-time collision, only a naming echo worth flagging.
/// </para>
/// </summary>
public sealed record EntityAssetSummaryDto(
    Guid id,
    string entity_id,
    string asset_type,
    string? image_url,
    bool is_preferred,
    string? source_provider);

public sealed record UniverseEnrichmentTriggerResponse(bool triggered, string message);
