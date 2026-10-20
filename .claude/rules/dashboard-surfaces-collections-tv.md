---
paths:
  - "src/MediaEngine.Web/Components/Collections/**"
  - "src/MediaEngine.Web/Components/Universe/**"
  - "src/MediaEngine.Web/Components/Details/**"
  - "src/MediaEngine.Api/Services/Details/**"
---

# Dashboard rules: Collections, People and TV

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings) and split by area on 2026-10-20 so only the relevant file loads. Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

## Collections and linked identity

- Collections uses one viewport-bounded, padded content scroller beside its anchored rail; never allow a second document scrollbar.
- Automatic rollups prioritize trusted fictional-universe, franchise, and based-on/adaptation QIDs over lane-local parent/series structure. A cross-media identity such as Batman can therefore combine qualifying comic and film groups.
- People presence counts distinct top-level works: tracks collapse to albums and episodes collapse to shows.
- Person names use the exact shared media-detail title family, scale, weight, responsive density, and lower-left identity anchor through the rendered person copy hook. Person dates, locations, and owned-title facts reuse the standard detail metadata row/item typography used by year, runtime, and genre. The portrait may remain centered independently; it must not vertically center the identity column.
- Musical groups resolve all canonical Wikidata `has parts` identities into `person_group_members` and queue incomplete people for full hydration. Collective pen names continue to use aliases; band members must never be rendered as pseudonym aliases.

## TV episode context and personal status

TV detail uses one profile-aware continuation policy: unstarted/reset shows retain series artwork, active shows use the current or next owned episode still, and explicit episode details stay episode-scoped. Completed owned runs offer rewatch with series artwork. Episodes use short episode synopsis text, never Wikipedia extracts. Missing catalogue entries have no synopsis or artwork and retain the Not in library placeholder. The episode rail scrolls without a four-card cap.

TMDB episode credits are stored separately from show aggregates; full credits support episode, season, and role filtering. Season coverage currently reflects owned episode evidence. More exposes media-appropriate personal completion/reset, history, Undo, Continue visibility, and applicable queue/playlist/collection utilities independently of metadata-edit permission. Revision checks prevent stale progress writes from reversing a reset; bookmarks, genuine consumption history, and music play counts are preserved.

These changes require fresh pre-beta ingestion. Runtime and responsive visual acceptance are pending; see the TV episode consistency proposal for validation status and remaining scope.
