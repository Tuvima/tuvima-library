---
title: "Tuvima.Wikidata Improvement Proposals"
summary: "Target-state proposal notes for future improvements to the Tuvima.Wikidata library."
audience: "developer"
category: "proposal"
product_area: "providers"
status: "target-state"
---

# Tuvima.Wikidata Library - Improvement Proposals

**Version:** v1.0.0 target
**Date:** 2026-04-02
**Status:** Proposal - awaiting implementation

---

## Overview

Three improvements to the `Tuvima.Wikidata` library that address gaps discovered during real-world usage. Each improvement is designed generically - they benefit any consumer of the library, not just the Tuvima Library product.

---

## Improvement 1: Wikipedia Section Content - Heading Stripping

### Problem

`GetWikipediaSectionContentAsync` returns the raw content of a Wikipedia section, including the section heading itself. When a consumer requests section index 3 (titled "Plot"), the returned content starts with `"Plot\n\n..."` or `"== Plot ==\n\n..."` depending on the wiki markup. Every consumer must strip this heading manually before using the content.

### Current Behaviour

```
Input:  GetWikipediaSectionContentAsync("Q83495", sectionIndex: 3, "en")
Output: "== Plot ==\n\nThe story follows Walter White, a high school chemistry teacher..."
```

### Proposed Behaviour

```
Input:  GetWikipediaSectionContentAsync("Q83495", sectionIndex: 3, "en")
Output: "The story follows Walter White, a high school chemistry teacher..."
```

The method should strip its own section heading before returning. The caller already knows which section they asked for - returning the heading is redundant and creates a cleanup burden.

### Specification

1. After retrieving section content from the MediaWiki API, strip any leading heading markup:
   - MediaWiki headings: `== Title ==`, `=== Title ===`, etc.
   - Plain text headings: A line matching the section title followed by a blank line
   - HTML headings if the API returns parsed content: `<h2>`, `<h3>`, etc.
2. Trim leading whitespace/newlines after stripping.
3. If the content is *only* a heading with no body, return `null` (not an empty string).
4. This should be the default behaviour. No opt-out parameter is needed - there is no use case for wanting the heading included.

### Breaking Change Assessment

**Low risk.** Any consumer that was manually stripping headings will now get double-clean content (the heading is already gone), which is harmless. Consumers that weren't stripping will see improved output.

---

## Improvement 2: Child Entity Discovery

### Problem

The library currently supports one parent-child traversal pattern: `GetEditionsAsync` discovers editions of a literary work via P747 (has edition or translation), filtered by P31 (instance of) classes. This works well for audiobook edition discovery.

However, Wikidata represents many other hierarchical relationships that consumers need to traverse:

- A **TV series** has **seasons** (P527 "has parts"), each season has **episodes** (P527 again)
- A **music album** has **tracks** (P658 "tracklist" or P527 "has parts")
- A **book series** has **installments** (P527 "has parts")
- A **film series** has **films** (P527 "has parts")
- A **symphony** has **movements** (P527 "has parts")
- A **building complex** has **buildings** (P527 "has parts")
- A **podcast series** has **episodes** (P527 "has parts" or seasonal grouping)

Each of these follows the same pattern: given a parent entity, find all child entities linked by a specific relationship property, optionally filtered by type, and return them with selected properties.

### Current API Gap

There is no generic method for this. `GetEditionsAsync` is hardcoded to P747 and edition-specific logic. To discover TV episodes, music tracks, or any other child entity type, consumers must:

1. Call `GetPropertiesAsync` on the parent to get P527 values
2. Extract child QIDs from the results
3. Call `GetPropertiesAsync` again on each child QID
4. Filter by P31 type themselves
5. Handle pagination, ordering, and missing data

This is repetitive, error-prone, and requires multiple API round-trips that the library could batch.

### Proposed API

```csharp
/// <summary>
/// Discovers child entities of a parent by traversing a relationship property,
/// optionally filtered by instance_of (P31) type classes.
/// </summary>
/// <param name="parentQid">The parent entity's Wikidata QID.</param>
/// <param name="relationshipProperty">
///   The Wikidata property that links parent to children.
///   Common values: "P527" (has parts), "P747" (has edition), "P179" (part of series - reverse).
/// </param>
/// <param name="childTypeFilter">
///   Optional P31 class QIDs to filter children by (e.g., ["Q21191270"] for TV episodes).
///   When null or empty, all children are returned regardless of type.
/// </param>
/// <param name="childProperties">
///   Property codes to fetch for each discovered child entity.
///   Example: ["P1476", "P577", "P1545", "P2047"] for title, date, series ordinal, duration.
/// </param>
/// <param name="language">Language for labels and monolingual text values.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>
///   Ordered list of child entities with their requested properties.
///   Ordered by P1545 (series ordinal) if available, then by P577 (publication date),
///   then by label alphabetically.
/// </returns>
Task<IReadOnlyList<ChildEntityInfo>> GetChildEntitiesAsync(
    string parentQid,
    string relationshipProperty,
    IReadOnlyList<string>? childTypeFilter,
    IReadOnlyList<string> childProperties,
    string language,
    CancellationToken cancellationToken = default);
```

**Return type:**

```csharp
public record ChildEntityInfo(
    string EntityId,
    string Label,
    string? Description,
    int? Ordinal,                    // P1545 series ordinal, if present
    IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> Properties);
```

### Specification

1. **Forward traversal (default):** Query the parent entity for the specified relationship property. Each value is a child QID. Fetch those children in batch.

2. **Reverse traversal:** When the relationship property is prefixed with `^` (e.g., `"^P179"`), perform a reverse lookup - find all entities where *their* P179 points to the parent QID. This handles "part of the series" where the child points to the parent, not vice versa. Implementation: use CirrusSearch `haswbstatement:P179=Q{parentId}` with optional P31 type filtering.

3. **Type filtering:** After discovering child QIDs, check each child's P31 values against `childTypeFilter`. Discard non-matching children. If `childTypeFilter` is null/empty, skip filtering.

4. **Property fetching:** For surviving children, batch-fetch the requested `childProperties` via `wbgetentities` (same as `GetPropertiesAsync`).

5. **Ordering:** Sort results by:
   - P1545 (series ordinal) ascending, if present
   - P577 (publication/air date) ascending, if P1545 is missing
   - Label alphabetically, as final tiebreaker

6. **Batching:** Fetch child entities in batches of 50 (Wikidata API limit per request). Parallelise batches where possible.

7. **Depth:** This method traverses one level only. For multi-level hierarchies (series -> season -> episode), the consumer calls it twice - once for seasons, once per season for episodes. Recursive traversal is intentionally excluded to keep the API predictable.

### Example Usage

**TV series episodes:**
```csharp
// Get all seasons of Breaking Bad
var seasons = await reconciler.GetChildEntitiesAsync(
    parentQid: "Q1079",                           // Breaking Bad
    relationshipProperty: "P527",                   // has parts
    childTypeFilter: ["Q3464665"],                  // TV season
    childProperties: ["P1476", "P1545", "P580"],    // title, ordinal, start date
    language: "en");

// Get all episodes of Season 1
var episodes = await reconciler.GetChildEntitiesAsync(
    parentQid: seasons[0].EntityId,                 // Season 1 QID
    relationshipProperty: "P527",                   // has parts
    childTypeFilter: ["Q21191270"],                  // TV episode
    childProperties: ["P1476", "P1545", "P577", "P2047", "P57", "P161"],
    // title, ordinal, air date, duration, director, cast
    language: "en");
```

**Music album tracks:**
```csharp
var tracks = await reconciler.GetChildEntitiesAsync(
    parentQid: "Q193563",                           // OK Computer (album)
    relationshipProperty: "P658",                    // tracklist
    childTypeFilter: null,                           // accept all child types
    childProperties: ["P1476", "P1545", "P2047", "P175", "P577"],
    // title, track number, duration, performer, release date
    language: "en");
```

**Reverse traversal - find all books in a series:**
```csharp
var books = await reconciler.GetChildEntitiesAsync(
    parentQid: "Q8337",                              // Harry Potter series
    relationshipProperty: "^P179",                    // reverse: part of the series
    childTypeFilter: ["Q7725634"],                    // literary work
    childProperties: ["P1476", "P1545", "P577", "P50"],
    // title, ordinal, publication date, author
    language: "en");
```

### Relationship to GetEditionsAsync

`GetEditionsAsync` remains unchanged - it serves the specific audiobook edition discovery use case with P747 and has edition-specific return types. `GetChildEntitiesAsync` is the general-purpose method for all other parent-child traversals. Over time, `GetEditionsAsync` could be reimplemented as a thin wrapper over `GetChildEntitiesAsync` if desired, but that is not required.

---

## Improvement 3: Multi-Value Reconciliation Constraints

### Problem

`ReconciliationRequest.Properties` accepts a list of `PropertyConstraint` objects, each with a single `Value`. When a work has multiple creators (e.g., a book by "Neil Gaiman" and "Terry Pratchett"), the consumer must choose one name to constrain on - or submit multiple reconciliation requests and merge results.

This is a general limitation. Any entity with multiple values for a property faces the same problem:

- Multiple authors of a book
- Multiple directors of a film
- Multiple performers on a track
- Multiple editors of a journal
- Multiple architects of a building

### Current Behaviour

```csharp
// Can only constrain on ONE author - must choose
var request = new ReconciliationRequest
{
    Query = "Good Omens",
    Properties = [new PropertyConstraint("P50", "Neil Gaiman")]
};
// If Wikidata returns a candidate where P50 = "Terry Pratchett",
// the constraint fails even though the book has BOTH authors.
```

### Proposed API Change

Extend `PropertyConstraint` to accept multiple values:

```csharp
public record PropertyConstraint
{
    /// <summary>Single expected value (existing - preserved for backward compatibility).</summary>
    public string? Value { get; init; }

    /// <summary>
    /// Multiple expected values for this property. When provided, a candidate matches
    /// if ANY of its values for this property matches ANY of the constraint values.
    /// Candidates matching MORE values score higher.
    /// </summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>The Wikidata property code (e.g., "P50").</summary>
    public string PropertyId { get; init; }
}
```

### Specification

1. **Backward compatible:** If `Value` is set and `Values` is null, behaviour is identical to today.

2. **Multiple values:** If `Values` is set (non-null, non-empty), the constraint matches if *any* of the candidate's values for the property fuzzy-matches *any* of the constraint values. The existing label-matching logic applies to each pair.

3. **Scoring bonus:** Candidates matching more constraint values score proportionally higher. If a book has P50 = ["Neil Gaiman", "Terry Pratchett"] and the constraint provides `Values = ["Neil Gaiman", "Terry Pratchett"]`, both match - the candidate gets a full score. If only one matches, it gets a partial score (e.g., 50% of the property weight for 1-of-2 matches).

4. **Fuzzy matching:** Each value comparison uses the same label-matching logic already used for single-value constraints (normalised, diacritic-insensitive, token-order-insensitive).

5. **If both `Value` and `Values` are set:** `Values` takes precedence. `Value` is ignored with no error.

### Example Usage

```csharp
// Multi-author book
var request = new ReconciliationRequest
{
    Query = "Good Omens",
    Properties =
    [
        new PropertyConstraint
        {
            PropertyId = "P50",
            Values = ["Neil Gaiman", "Terry Pratchett"]
        }
    ]
};

// Multi-director film
var request = new ReconciliationRequest
{
    Query = "Everything Everywhere All at Once",
    Properties =
    [
        new PropertyConstraint
        {
            PropertyId = "P57",
            Values = ["Daniel Kwan", "Daniel Scheinert"]
        }
    ]
};
```

### Impact on Reconciliation Scoring

When `Values` is provided, the reconciliation engine should adjust the property's contribution to the overall candidate score:

```
propertyScore = matchedCount / totalConstraintValues
```

For "Good Omens" with `Values = ["Neil Gaiman", "Terry Pratchett"]`:
- Candidate has P50 = ["Neil Gaiman", "Terry Pratchett"] -> propertyScore = 2/2 = 1.0
- Candidate has P50 = ["Neil Gaiman"] -> propertyScore = 1/2 = 0.5
- Candidate has P50 = ["Terry Pratchett"] -> propertyScore = 1/2 = 0.5
- Candidate has P50 = ["Stephen King"] -> propertyScore = 0/2 = 0.0

This proportional scoring ensures that candidates matching *all* provided values rank higher than those matching only one - without completely rejecting partial matches.

---

## Improvement 4: Wikipedia Section Content - Subsection Handling

### Problem (Related to Improvement 1)

Some Wikipedia articles nest their plot content under subsections rather than a single "Plot" section. For example, a TV series article might have:

```
== Plot ==
=== Season 1 ===
...
=== Season 2 ===
...
```

When `GetWikipediaSectionContentAsync` is called with the "Plot" section index, it may return only the top-level content (which could be empty or a single introductory sentence), missing the subsection content entirely.

### Proposed Behaviour

When fetching a section's content, include all nested subsections up to the next sibling section. This matches how a human reads a Wikipedia article - the "Plot" section means everything under that heading until the next `==`-level heading.

### Specification

1. When `GetWikipediaSectionContentAsync` is called for section index N:
   - Fetch section N's content
   - Also fetch content from all subsections (N+1, N+2, ...) until reaching a section at the same or higher heading level
   - Concatenate with double newlines between subsections
2. Strip all heading markup from the concatenated content (per Improvement 1).
3. This should be the default behaviour - most consumers want the full section content, not just the top-level fragment.

---

## Summary

| # | Improvement | Breaking Change | Complexity |
|---|------------|-----------------|------------|
| 1 | Section heading stripping | Low risk (cleaner output) | Low |
| 2 | Child entity discovery | None (new method) | Medium |
| 3 | Multi-value reconciliation constraints | None (additive to PropertyConstraint) | Medium |
| 4 | Subsection content inclusion | Low risk (more complete output) | Low |

### Recommended Implementation Order

1. **Improvement 1** (heading stripping) - simplest, immediate quality-of-life fix
2. **Improvement 4** (subsection handling) - closely related to Improvement 1, natural to implement together
3. **Improvement 3** (multi-value constraints) - unlocks better matching for multi-creator works
4. **Improvement 2** (child entity discovery) - largest feature, depends on understanding real-world usage patterns from 1-3

---

## Wikidata Property Reference

Properties referenced in this document:

| Property | Label | Used For |
|----------|-------|----------|
| P31 | instance of | Type filtering (Q21191270 = TV episode, Q7725634 = literary work, etc.) |
| P50 | author | Book/article creators |
| P57 | director | Film/TV directors |
| P161 | cast member | Actors in a production |
| P175 | performer | Musicians/singers |
| P179 | part of the series | Child -> parent series link |
| P527 | has parts | Parent -> child parts link |
| P577 | publication date | Release/air dates |
| P658 | tracklist | Album -> track link |
| P747 | has edition or translation | Work -> edition link (existing in GetEditionsAsync) |
| P1476 | title | Monolingual title |
| P1545 | series ordinal | Position within a series (episode #, track #, volume #) |
| P2047 | duration | Runtime/length |

### Type Classes Referenced

| QID | Label | Used For |
|-----|-------|----------|
| Q3464665 | TV season | Filtering seasons within a series |
| Q21191270 | TV series episode | Filtering episodes within a season |
| Q7725634 | literary work | Filtering books within a series |
| Q482994 | album | Filtering albums |
| Q105543609 | musical work/composition | Filtering tracks |
