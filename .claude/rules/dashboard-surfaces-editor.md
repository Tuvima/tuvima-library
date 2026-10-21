---
paths:
  - "src/MediaEngine.Web/Components/MediaEditor/**"
  - "src/MediaEngine.Web/Components/Artwork/**"
  - "src/MediaEngine.Web/Services/Editing/**"
  - "src/MediaEngine.Web/Components/Collections/**"
  - "src/MediaEngine.Web/Components/Details/**"
---

# Dashboard rules: Editor, identity and shelf editing

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings) and split by area on 2026-10-20 so only the relevant file loads. Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

## Editor relationship and identity model

- Keep three models distinct: structural media placement (Series/parent/position), authored-container canonical title and order, and the knowledge graph (Universe, fictional entities, and sourced/qualified links). Stage 2 establishes canonical Work identity; bounded Stage 3 enriches grounded graph facts and artwork without silently rewriting authored containers or structural placement.
- The normal media editor uses one stable parent header with adjacent child selectors and a single highlighted editing target. Keep audiobook chapter-title overrides under Chapters. Match information belongs at the top of Details, never in header/selector badges; inherited canonical identity needs no status label. Retail Match owns provider identity plus proposed structural placement; preview previous/target paths before Apply. Do not expose Change Type in the normal editor or Review Queue.
- Universe/entity editing remains content within `SharedMediaEditorShell`, opened from the authorized Edit actions on the Universe Explore surface or its selected entity. Media detail pages link actual accessible Universe relationships; media editors contain no Universe shortcut or mode switch. Keep the Universe header stable and preserve the existing dirty-state guard for category/entity navigation.
- Render graph sections from server readable capabilities, including friendly Organization Members and Event Participants projections; keep full Relationships separate. Category/sibling selectors are anchored, viewport-bounded, searchable popovers with keyboard/focus behavior, while the category rail remains one horizontally scrollable row with explicit overflow controls.
- Managed EntityAsset artwork belongs to its Universe/entity owner and must not be inferred from a work or parent container. Appearance and relationship facts remain work-scoped and preserve role, context, anchor, temporal, provenance, and spoiler qualifiers. Real-world dates, in-universe narrative time, structural sequence order, and editor History are different data and presentation concepts.

## Shelf editing and owner artwork

Structural shelves edit their own parent Work through the shared editor. The Engine declares supported artwork slots and automatic-group presentation; do not duplicate media/scope capability switches in the Dashboard. Shelves preview up to four ordered owned covers, accept a shelf-owned custom cover, and can restore the automatic stack. Title and description overrides remain local presentation choices and must agree across editor, detail, and browse. Group routes and membership resolve by structural owner identity rather than a customized display title. Season selectors use managed SeasonPoster artwork and episodes use EpisodeStill; absent owner artwork remains a placeholder.

Editor Details names the active editing target independently of the stable parent header and shows compact Retail/Canonical match checks. TV matching is exposed only on episode targets, never series or seasons; the inherited canonical record remains visible for context without an editor jump to the series. Match searches retain the current identity until the user explicitly applies or overrides a result. Logo previews preserve the complete image at its natural ratio.
