# Spec: Needs Review shows books missing canonical information, with a filter

> **Status:** In progress · **Version:** 1 · **Last updated:** 2026-11-24 · **Related docs:** [Needs Review redesign](archive/needs-review-redesign.md) (this spec replaces its D10 for books and audiobooks only)

## 1. Goal

A book or audiobook that Library found in a book listing but could not link to Wikidata is no longer hidden. It appears in Needs Review, and a filter separates books that were "not found at all" from books that were "found, but canonical information is missing".

## 2. Scope

**In scope**
- Books and audiobooks whose only problem is the missing Wikidata link now show in the Needs Review list and its counts.
- A new group on the page, "Canonical information missing", holding those items.
- A filter on the page: Everything, Not found at all, Canonical information missing.

**Out of scope**
- Films, TV, music, comics and the other types stay as in the redesign's D10: matched items that only lack Wikidata stay hidden while the Engine retries quietly. (Widening this later is one list of media types.)
- No change to how the Engine retries the Wikidata link, or to which items get a Wikidata match.

## 3. Decisions made

- **D1. (Shy, 2026-11-24)** Books with no Wikidata match go to Needs Review, with a filter for "completely not found" versus "canonical information is missing".
- **D2.** Scope is books and audiobooks only, because that is what Shy asked for and showing every type would add a very large number of rows at once. Recommended; easy to widen.
- **D3.** "Not found at all" means the existing "No match found" group (no listing matched, or the file says too little). "Canonical information missing" is a new group of items that did match a listing and only lack the Wikidata link.
- **D4.** These items count in every Needs Review number (page summary, library badges, the notifier), so no number says less than the list shows.
- **D5.** The filter shows only when the list holds at least one "canonical information missing" item, so libraries without such items look exactly as before. It shows the number in each choice.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| A book found in a listing but without a Wikidata entry | A row in the group "Canonical information missing": "Found in the book listing, but there is no matching Wikidata entry yet. Library keeps trying." |
| Filter: Everything | All groups, as today plus the new group |
| Filter: Not found at all | Only "No match found" |
| Filter: Canonical information missing | Only the new group |
| No such items | No filter row; the page is unchanged |

Phone and desktop use the same control (a segmented control above the list).

## 5. Local checks

Open Needs Review on a library with books whose Wikidata link failed; try the three filter choices at 1024, 1280, 1440 and phone width.
