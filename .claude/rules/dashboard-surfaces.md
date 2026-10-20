---
paths:
  - "src/MediaEngine.Web/**"
  - "src/MediaEngine.Api/Services/Details/**"
  - "tests/MediaEngine.Web.Tests/**"
---

# Dashboard product-surface rules

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings) and split by area on 2026-10-20 so only the relevant file loads. Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

Area files (each loads only for its own folders):

- `dashboard-surfaces-collections-tv.md` — Collections, linked identity, TV episode context
- `dashboard-surfaces-settings-setup.md` — Libraries settings, Setup, Operations, View storage
- `dashboard-surfaces-editor.md` — Editor relationship and identity model, shelf editing
- `dashboard-surfaces-home-player.md` — Home, Continue cards, player panels, Watch/View video

Rule that applies everywhere:

## UI action alignment completion rule

Before finishing any UI work, verify action alignment at desktop, tablet, and mobile breakpoints. Buttons beside labeled form fields must align with the input control rather than the label or helper text. Buttons in list or detail rows must remain in the row's intended action column and must not auto-flow onto a separate row that creates avoidable empty space. A separate full-width action row is acceptable only when it is an intentional mobile layout. Do not report UI work complete until these alignments have been checked in the rendered layout or enforced with explicit grid/flex placement and regression coverage.
