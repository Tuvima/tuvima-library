# Spec: Universes v6 — one Series across books and audiobooks, films shown from two, a show joins its books' Universe

> **Status:** Draft (Shy's decisions 227A and 228A, 2026-11-24, are recorded below; only Shy locks it) · **Version:** 6 (amends [universe.md](universe.md) v5; the Locked body of v5 is not rewritten) · **Last updated:** 2026-11-24 · **Related docs:** [Universes](universe.md) · [Universe entry points](universe-entry-points.md) · issues #1361, #1366

## 1. Goal
The synthetic library test showed the same Series twice (19 duplicate names, Dune six times) and Universes missing (The Expanse, Harry Potter, Middle-earth). A person should see each Series once, and a Universe should appear when its books and its show are both owned.

## 2. Scope
**In:** a book and an audiobook of one Series are one Series; a film Series is hidden until two of its films are owned; a TV show based on a book Series joins the Universe the show names; a name-only Series is folded into the same-named Wikidata Series.
**Out:** TV, film, comic and music Series stay separate per media type (the three Expanse Series of v5 D3 become two: books plus audiobooks, and TV). No change to Series being read only. No old-data migration (pre-release: reset and re-ingest).

## 3. Decisions made
- **D1 (227A, Shy 2026-11-24):** a Books Series and an Audiobooks Series with the same Wikidata Series are one Series. Replaces the book-versus-audiobook part of v5 D3 ("Novels, TV and film lines are separate Series"); TV and film lines stay separate.
- **D2 (228A, Shy 2026-11-24):** a film Series is not listed until two of its films are owned. The single film shows on its own, even when the Series says it has more films.
- **D3:** a Series based on another Series (a TV show based on a book Series) is decided together with it: both take the strongest Universe either one names (fictional universe, then franchise, then broader Series, then based-on, then own Series; lowest QID on a tie), whichever was ingested first.
- **D4:** a Series made earlier from a name alone is given the Wikidata id when a Wikidata Series with the same name and media family arrives, instead of a second Series being created.

## 4. UI behaviour
Series and Universe pages look as before; there are fewer duplicates. A Series holding a book and its audiobook lists both formats as one entry per title where the title already groups formats. A one-film Series has no tile; the film appears alone. Empty, loading and error states are unchanged.

## 5. Data / model changes
No schema change. A Series' media type is stored as "Books" for both formats (`SeriesMediaFamily`); lookups by Series match that family.

## 6. Work packets
- **WP1 (Standard, built):** `SeriesMediaFamily`; family-aware shelf lookup, placement rule, manifest queries, placement choices. Done when a book and audiobook of one Series sit on one shelf with one manifest.
- **WP2 (Standard, built):** film Series visibility in the catalog read (D2).
- **WP3 (Standard, built):** Universe decided across "based on" Series (D3).
- **WP4 (Mechanical, built):** adopt a same-named name-only shelf (D4).

## 7. Local checks
Rerun the synthetic library (needs `TUVIMA_SECRETS_KEY_FILE`): no Series name appears twice in Collections; Dune is one Series; The Expanse, Harry Potter and Middle-earth have a Universe; a one-film Series has no tile; a book and its audiobook share one Series page. Listed in `engineering/validation/batch.md`.

## 8. Risks and open questions
- A person who deliberately owns a book Series and an audiobook Series as separate shelves now sees one (intended by D1).
- Existing libraries keep old duplicate shelves until reset (pre-release rule).
- Universes missing because Wikidata names no link between a show and its books stay missing; that needs data, not code.
