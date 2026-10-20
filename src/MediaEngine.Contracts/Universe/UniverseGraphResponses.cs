namespace MediaEngine.Contracts.Universe;

/// <summary>
/// Wire responses for <c>UniverseGraphEndpoints</c> routes that previously returned anonymous
/// types (<c>Results.Ok(new { ... })</c>). Property names are deliberately left exactly as the
/// anonymous types declared them — camelCase or snake_case, not PascalCase — and carry no
/// <c>[JsonPropertyName]</c> overrides, so the JSON payload these records produce is
/// byte-identical to what the replaced anonymous types produced.
///
/// <para>
/// Named <c>UniverseListItemDto</c> rather than <c>UniverseSummaryDto</c> to avoid a
/// same-name collision with <see cref="MediaEngine.Domain.Models.UniverseSummaryDto"/>, which
/// <c>UniverseGraphEndpoints.cs</c> already imports unqualified via
/// <c>using MediaEngine.Domain.Models;</c> — reusing that name here would make every
/// unqualified reference in that file ambiguous (CS0104).
/// </para>
/// </summary>
public sealed record UniverseListItemDto(
    string qid,
    string label,
    string level,
    string? parent_qid,
    int entity_count,
    int character_count,
    int location_count,
    int organization_count,
    int event_count,
    int relationship_count,
    bool has_graph,
    string enrichment_status);

public sealed record UniverseDetailResponse(
    UniverseDetailRef universe,
    int entity_count,
    int character_count,
    int location_count,
    int organization_count,
    int relationship_count);

public sealed record UniverseDetailRef(string qid, string label, string level);

public sealed record UniverseGraphResponse(
    UniverseGraphRef universe,
    IReadOnlyList<UniverseGraphNodeDto> nodes,
    IReadOnlyList<UniverseGraphEdgeDto> edges);

public sealed record UniverseGraphRef(string qid, string label);

public sealed record UniverseGraphNodeDto(
    string id,
    string label,
    string type,
    string? description,
    string? image,
    IEnumerable<UniverseGraphWorkLinkDto> works,
    bool supplemental,
    string provenance,
    string? source_plugin,
    string? source_url);

/// <summary>
/// A graph-node appearance in an owned work. Optional trailing fields preserve the
/// original compact payload for clients that only understand QID and label.
/// </summary>
public sealed record UniverseGraphWorkLinkDto(
    string qid,
    string? label,
    string? link_type = null,
    string? appearance_role = null,
    string? work_context = null,
    string? anchor_kind = null,
    string? anchor_value = null,
    string? narrative_time_index = null,
    string? start_time = null,
    string? end_time = null,
    string? spoiler_for_work_qid = null,
    string? source_provider = null,
    string? provenance = null,
    bool? supplemental = null,
    double? confidence = null,
    string? appearance_key = null);

public sealed record UniverseGraphEdgeDto(
    string source,
    string target,
    string type,
    string label,
    double confidence,
    string? context_work,
    string? start_time,
    string? end_time,
    bool supplemental,
    string provenance,
    string? source_plugin,
    string? source_url,
    string? statement_key = null,
    IReadOnlyList<UniverseGraphQualifierDto>? qualifiers = null,
    string? source_provider = null);

/// <summary>Wire representation of a queryable qualifier retained on a graph fact.</summary>
public sealed record UniverseGraphQualifierDto(
    string type,
    string value,
    string value_kind,
    string provenance,
    bool supplemental,
    string? source_provider,
    double? confidence);

public sealed record UniversePathsResponse(
    string universe_qid,
    string from_qid,
    string to_qid,
    IReadOnlyList<IReadOnlyList<string>> paths);

public sealed record UniverseFamilyTreeResponse(
    string universe_qid,
    string character_qid,
    Dictionary<string, IReadOnlyList<string>> generations);

public sealed record UniverseCastResponse(
    string universe_qid,
    IReadOnlyList<UniverseCastCharacterDto> characters);

public sealed record UniverseCastCharacterDto(
    string qid,
    string label,
    string? image,
    string? description,
    IEnumerable<UniverseCastPerformerDto> performers);

public sealed record UniverseCastPerformerDto(
    Guid person_id,
    string? name,
    string? headshot_url,
    string? work_qid,
    int? year);

public sealed record UniverseAdaptationTreeResponse(
    string universe_qid,
    IReadOnlyList<UniverseAdaptationNodeDto> works);

/// <summary>One work in the explorer's adaptation tree.</summary>
/// <param name="source">Where the relationship to the parent was learned ("Wikidata"); null for a root.</param>
public sealed record UniverseAdaptationNodeDto(
    string qid,
    string label,
    int? year,
    string media_type,
    string? cover_image,
    string? relationship_to_parent,
    IReadOnlyList<UniverseAdaptationNodeDto> children,
    string? source = null);
