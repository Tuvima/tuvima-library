namespace MediaEngine.Contracts.Review;

/// <summary>
/// Wire responses for <c>ReviewEndpoints</c> routes that previously returned anonymous
/// types (<c>Results.Ok(new { ... })</c>). Property names are deliberately left exactly as the
/// anonymous types declared them — snake_case, not PascalCase — and carry no
/// <c>[JsonPropertyName]</c> overrides, so the JSON payload these records produce is
/// byte-identical to what the replaced anonymous types produced.
/// </summary>
public sealed record ReviewResolveResponse(bool resolved, Guid review_item_id);

public sealed record ReviewDismissResponse(bool dismissed, Guid review_item_id);

public sealed record ReviewSkipUniverseResponse(bool skipped, Guid review_item_id);

public sealed record ReviewMoveToTvResponse(bool moved, Guid review_item_id, Guid entity_id, string show_name, string? tv_library_id);
