namespace MediaEngine.Contracts.Library;

/// <summary>
/// Wire responses for <c>LibraryEndpoints</c> universe-curation routes that previously
/// returned anonymous types (<c>Results.Ok(new { ... })</c>). Property names are deliberately
/// left exactly as the anonymous types declared them — snake_case, not PascalCase — and carry
/// no <c>[JsonPropertyName]</c> overrides, so the JSON payload these records produce is
/// byte-identical to what the replaced anonymous types produced.
/// </summary>
public sealed record UniverseManualAssignResponse(bool assigned);
