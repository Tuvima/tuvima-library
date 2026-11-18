# Spec: Editor v8, a match badge, a simpler Artwork tab, editable collections, and four fixes (amends v7, v6, v5)

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-11-17 · **Related docs:** [Editor v7](universal-editor-v7-bug-review.md) · [Editor v5](universal-editor-v5-header-navigation-and-supporting-files.md) · mockups in `../../design/mockups/editor-v8/editor-v8-mockups.html` (screens A to F)

Locked by Shy on 2026-11-17 ("build the rest"), after reviewing the mockups and answering the questions below on 2026-11-15. Issues: 1045, 1046, 1047, 1048, 1062, 1064, 1065.

## 1. Goal
Make the editor easier to read and trust: one small match badge instead of links under the close button, an Artwork tab with a big preview and a picture strip, collections whose own artwork and tags can be edited, and four faults fixed (wrong season count, an episode that will not open, matched episodes asking "What is this file?", a Match tab that misses a match).

## 2. Scope
**In scope:** issues 1045, 1046, 1047, 1048, 1062, 1064, 1065 and the item-dropdown change (mock F).
**Out of scope:** episode frames missing from the picker (issue 1044, built separately, except that the new Artwork tab must show whatever it supplies); PDF extras not appearing (issue 1043); the Match flow itself (v6) except the cards named here.

## 3. Decisions made
- **D1 (amends v7 D2 and D14).** The header's match signal is a small badge left of the close button: "Retail" and "Canonical", each with a green check when matched and a red X when not. Nothing overlaps the close button. Retail means a provider match (TheTVDB, Apple Books and so on); Canonical means Wikidata. The badge has a tooltip naming the provider. It also shows on a phone, beside the close button. It is hidden for items that never use a retail provider (personal videos, photos, documents). The links to the provider and Wikidata pages live in the Match tab and at the bottom of the details page (the phone's links on the Match card stay).
- **D2.** The item dropdown lists episodes (number, still, title, mark, an amber dot for unsaved changes) and the supporting files of the level, with a search box. The rows "All supporting files" and "Browse files and folders" go; the folder button is the only way into the supporting-files browser.
- **D3.** Reorder videos is offered only on a video collection whose order Library is still guessing, never for episodes matched to a provider. Save order stages one change. With write-back on, files in Managed folders are renamed to the new order (never Read-only folders); a later match that changes the order renames them again (the match order wins).
- **D4 (replaces v7's small preview and versions strip).** The Artwork tab has tabs for Background, Cover and Logo with counts, Add artwork and Refresh on the right, one large preview with a Current badge and a "Library · 1600 x 2400 · 1 of 8" line, and a bottom carousel of every picture with a check on the current one. Clicking a picture applies it at once (no Undo, no Review line). Each picture has a "..." menu with only Magnify and Delete. Delete asks once and works only for pictures Library downloaded or the person uploaded; pictures sitting in the person's own folders offer Hide instead. Each episode has its own Artwork picker. On a phone the carousel stays at the bottom, the tabs scroll sideways, the menu is a bottom sheet.
- **D5 (amends v5 D7 and D24).** A Series is editable only for what is the owner's own: its artwork (cover, background, logo), tags and sort title. Everything from Wikidata or from its members stays locked and shown as locked. A Universe stays overrides only.
- **D6.** A Series' dropdowns: the first picks the media type (Books, Movies, TV and so on), the second picks the item. Every title of that type in the collection is listed; titles not owned appear only when the profile has Show missing on.
- **D7.** The order line "Books in order" becomes "Reading order" for books, "Watch order" for films and shows, "Listening order" for audiobooks and music, and "In order" for mixed.
- **D8.** The header and Quick Facts count real seasons. A show whose episodes sit directly under it reads "1 season".
- **D9.** Choosing an episode from the dropdown always opens it. "What is this file?" appears only for a file the Engine could not place; matched episodes show as episodes. The supporting-files page gets a purpose line ("These are the files behind this title. Anything Library could not place is listed first so you can say what it is.") and a tidier layout that works on a phone.
- **D10.** The Match tab shows the Wikidata match (name, description, link) when there is one, and the provider card shows its cover or album art and an "Open in <provider>" link, for music, books, films and shows.
- **D11 (rules updated).** CLAUDE.md, `.claude/rules/dashboard-surfaces-editor.md`, `src/MediaEngine.Web/CLAUDE.md` and the header guardrail test say the badge, not links, is the header's match signal.

## 4. UI behaviour
The mockups are the reference; the build matches them 1:1 (labels, order, spacing). Empty: no pictures shows "No artwork yet" with Add artwork. Loading: the app's normal skeleton. Error: a plain sentence, never a generic one. Phone: nothing left out.

## 5. Data / model changes
No new tables. The editor's Series subject becomes editable for artwork, tags and sort title (stored as the Series' own local values). Provider match saves its cover address and page link if it did not. Resets are acceptable pre-release.

## 6. Work packets
- **WP1 Season count (D8).** Count only real seasons in `ItemQuickFactsRepository.ForWorks`, header and Quick Facts. Depends: none. Standard. Done: test with episodes directly under a show reads "1 season".
- **WP2 Open any episode and the files question (D9, issues 1047, 1045).** Reproduce, find why the episode editor context is not found (family ids), fix, show "What is this file?" only for unplaced files; clearer failure message. Depends: none. Standard. Done: tests open a matched episode from a show; matched episodes carry no question.
- **WP3 Match tab (D10, issue 1065).** Wikidata side reads the saved id; provider card shows cover and link. Depends: none. Standard.
- **WP4 Header badge and rules (D1, D11).** Depends: WP3 (links move). Standard. Done: badge shows check/check, check/X, X/X, never overlaps close, phone too; guardrail and docs updated.
- **WP5 Item dropdown and Reorder (D2, D3).** Depends: WP2. Standard.
- **WP6 Artwork tab (D4).** Depends: WP2. Complex.
- **WP7 Supporting-files page touch-up (D9).** Depends: WP2. Standard.
- **WP8 Collection editing (D5, D6, D7).** Engine allows Series artwork, tags and sort title; dropdowns; labels in the string file. Depends: WP6 for the artwork tool. Complex.
- **WP9 Docs and wrap-up.** tuvima-docs editing pages, README index, batch.md rows. Depends: all. Mechanical.

## 7. Local checks
Open the MasterClass show, a matched book (Gods of Risk), an album, a movie and a Series on real media at 1024, 1280, 1440 and phone: badge states, Artwork click-to-apply and Delete rules, episode switching, supporting files, Reorder with write-back on a Managed folder, Series editing. Rows go in `engineering/validation/batch.md`.

## 8. Risks and open questions
Causes of WP2 and WP3 were read from code, not reproduced; a build thread confirms them first and records what it found. If WP2 needs real files to reproduce, the cloud builds the likeliest fix with a generated library and the Local check settles it.

**Plain English summary:** the editor header shows two small match ticks, Artwork becomes a big picture with a strip you click, Series can have their own cover and tags, and four bugs get fixed.
