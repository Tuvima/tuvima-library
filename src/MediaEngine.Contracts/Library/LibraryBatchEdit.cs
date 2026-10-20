using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>
/// The operations a batch field change can carry. Scalar fields take <see cref="Set"/> or <see cref="Clear"/>;
/// list fields (genres, tags) take <see cref="Add"/>, <see cref="Replace"/> or <see cref="Remove"/>.
/// A field that is not sent means "No change".
/// </summary>
public static class LibraryBatchEditOps
{
    public const string Set = "set";
    public const string Clear = "clear";
    public const string Add = "add";
    public const string Replace = "replace";
    public const string Remove = "remove";
}

/// <summary>The kind of a batch-editable field: one value, or a list of values.</summary>
public static class LibraryBatchFieldKinds
{
    public const string Scalar = "scalar";
    public const string List = "list";
}

/// <summary>The outcome of one selected item in a batch apply.</summary>
public static class LibraryBatchItemOutcomes
{
    /// <summary>Every requested change was applied to the item.</summary>
    public const string Updated = "updated";

    /// <summary>Nothing was changed on the item; a Needs Review entry names the fields that did not apply and why.</summary>
    public const string SentToReview = "sent_to_review";

    /// <summary>The caller may not edit the item, or it is gone or still being updated. Nothing was changed and no review entry exists.</summary>
    public const string Refused = "refused";
}

/// <summary>Batch metadata edit: the selected items and the field operations to apply to each of them.</summary>
public sealed class LibraryBatchEditRequest
{
    /// <summary>The most items one batch request may name.</summary>
    public const int MaxEntityIds = 500;

    [JsonPropertyName("entity_ids")]
    public List<Guid> EntityIds { get; init; } = [];

    [JsonPropertyName("field_changes")]
    public List<LibraryFieldChange> FieldChanges { get; init; } = [];
}

/// <summary>
/// One field operation. <c>set</c> needs <see cref="Value"/>; <c>add</c>, <c>replace</c> and <c>remove</c> carry
/// <see cref="Values"/> (<c>replace</c> may carry none, which empties the list); <c>clear</c> carries neither.
/// </summary>
public sealed class LibraryFieldChange
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("op")]
    public required string Op { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }
}

/// <summary>What one selected item came to in a batch apply.</summary>
public sealed class LibraryBatchItemResult
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>One of <see cref="LibraryBatchItemOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>Plain-English reason; empty when the item was updated.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// The field keys that did not apply (sent to review only). An item is applied together or not at all, so this is every
    /// field the batch asked for; <see cref="Reason"/> names the ones that blocked it.
    /// </summary>
    [JsonPropertyName("fields_not_applied")]
    public List<string> FieldsNotApplied { get; init; } = [];

    /// <summary>The Needs Review entry created for the item (sent to review only).</summary>
    [JsonPropertyName("review_id")]
    public Guid? ReviewId { get; init; }
}

public sealed class LibraryBatchEditResult
{
    [JsonPropertyName("updated_count")]
    public int UpdatedCount { get; init; }

    [JsonPropertyName("sent_to_review_count")]
    public int SentToReviewCount { get; init; }

    [JsonPropertyName("refused_count")]
    public int RefusedCount { get; init; }

    [JsonPropertyName("items")]
    public List<LibraryBatchItemResult> Items { get; init; } = [];
}

/// <summary>Asks how the selected items stand on the batch-editable fields.</summary>
public sealed class LibraryBatchEditPreviewRequest
{
    [JsonPropertyName("entity_ids")]
    public List<Guid> EntityIds { get; init; } = [];

    /// <summary>Limit the answer to these field keys; empty or omitted means every batch field.</summary>
    [JsonPropertyName("field_keys")]
    public List<string>? FieldKeys { get; init; }
}

public sealed class LibraryBatchEditPreview
{
    /// <summary>How many of the selected items the caller can see and edit.</summary>
    [JsonPropertyName("affected_count")]
    public int AffectedCount { get; init; }

    [JsonPropertyName("fields")]
    public List<LibraryBatchFieldState> Fields { get; init; } = [];

    /// <summary>Selected items left out of the answer (not allowed, gone, or still being updated).</summary>
    [JsonPropertyName("unavailable_items")]
    public List<LibraryBatchLockedItem> UnavailableItems { get; init; } = [];
}

/// <summary>How one field stands across the selected items.</summary>
public sealed class LibraryBatchFieldState
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>One of <see cref="LibraryBatchFieldKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The operations this field accepts (see <see cref="LibraryBatchEditOps"/>); empty when the field is a provider fact.</summary>
    [JsonPropertyName("operations")]
    public List<string> Operations { get; init; } = [];

    /// <summary>True when every selected item can take the change. When false, <see cref="LockedItems"/> says which cannot and why.</summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; init; }

    [JsonPropertyName("locked_items")]
    public List<LibraryBatchLockedItem> LockedItems { get; init; } = [];

    /// <summary>False means every item agrees (see <see cref="Value"/> / <see cref="Values"/>); true means the page should say "Mixed".</summary>
    [JsonPropertyName("is_mixed")]
    public bool IsMixed { get; init; }

    /// <summary>The shared value of a scalar field when every item agrees and it is not empty.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>The shared list of a list field when every item agrees.</summary>
    [JsonPropertyName("values")]
    public List<string> Values { get; init; } = [];

    /// <summary>Up to five distinct values with how many items carry each, most common first. Filled when mixed.</summary>
    [JsonPropertyName("distinct_values")]
    public List<LibraryBatchDistinctValue> DistinctValues { get; init; } = [];

    /// <summary>How many distinct values there are in all (may be more than the five listed).</summary>
    [JsonPropertyName("distinct_count")]
    public int DistinctCount { get; init; }

    /// <summary>How many selected items have no value for this field.</summary>
    [JsonPropertyName("empty_count")]
    public int EmptyCount { get; init; }
}

public sealed class LibraryBatchDistinctValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>An item that cannot take a change (or be shown), with the reason in plain English.</summary>
public sealed class LibraryBatchLockedItem
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
