# Spec: Universal editor v3.1 — video series, order and rename-to-order

> **Status:** Locked (Shy, 2026-11-03: "Agree on all") · **Version:** 1 · **Last updated:** 2026-11-03 (drag and drop added) · **Related docs:** [Universal editor v3](universal-editor-v3.md) (Locked, built; its body is not rewritten) · [Videos](videos.md) · Mockups: [`video-series-and-order.html`](../design/mockups/universal-editor-v3/video-series-and-order.html)

## 1. Goal

Put back three things that disappeared when the old page editor was retired, now inside the pop-over editor: putting a stand-alone video into a series (or taking it out), putting a series' videos in the order you want, and, only if you choose, renaming the files to match that order. Shy asked for them back on 2026-11-03.

## 2. Scope

**In scope**
- Series chooser for a video (existing series, start a new series by name, not in a series), in the Match tool.
- Order of a series' videos: drag and drop by a grip handle on each row, with move up and move down arrows kept for the keyboard and for screen readers, in the Match tool of the series.
- A "Rename the files to match this order" switch, off by default, shown only when every video is in a Managed folder.
- Sections of a series (order per section; drag a video between sections), a sort button (by file name or date added, then adjust by hand), a "not matched" mark in the tree for videos in no series, and putting several ticked videos in a series at once (multi-edit, "Put in a series").
- All three are staged like every other change: nothing saves until Save changes and Review changes.
- Public docs and the Local checks list.

**Out of scope**
- Auto-order from file names ("Ep 01", "Part 2"): a later add-on, because a wrong guess looks like the person's own choice.
- Undo after saving: the editor has no undo journal by design; History shows what changed.
- Identify by YouTube link and Change thumbnail (already built in the pop-over's Match and Artwork).
- Photos, music, TV and books (they already have their own ordering and parent moves).
- Any change to the Engine's rules for who may edit.

## 3. Decisions made

- **D1.** Series membership and order live in **Match**, not a new tool. The Locked editor says structural parent moves live in Match; this keeps the tools row at Details, Artwork, Match, History.
- **D2.** Both are **staged**, not saved on the spot as the old editor did. They join the change count in the footer and appear in Review changes, marked "Library only". Reason: one consistent save-and-review habit, and Cancel really cancels.
- **D3.** Series and order changes never move files. Only the rename switch does, and it is **off by default**, listed last in Review as "Cannot be undone", and run one file at a time like every file move.
- **D4.** The rename switch is shown only when every video in the list is in a Managed folder. A Read-only folder never shows it and is never touched; files stay in their folder. This keeps the existing Engine rule.
- **D5.** The Engine already has the actions (read and save series, read and save order, rename). They gain "preview" answers so Review can check them before saving; no new product behaviour.
- **D7.** Drag and drop is in scope (Shy, 2026-11-03). Each row has a grip handle on the left; dragging shows a line where the video will land, and dropping stages the new order exactly like the arrows do. Arrows stay because drag is not usable by keyboard or screen reader. The same handle drags a video onto another series in the tree (a drop on a series row stages "join this series"). Editing is desktop only, so touch drag is not needed.
- **D8.** Sections: a series with sections shows one list per section; drag works inside a section and between sections (staged as a section move). The sort button is a one-shot ordering of the list, not a standing rule. Videos in no series carry the existing "not matched" mark in the tree.
- **D9.** Several ticked videos: Edit N items opens the shared multi-edit window with "Put in a series"; Review lists each video and flags any that cannot join, as for songs.
- **D6.** Editing is desktop only, as for the whole editor; the Engine's permission check still decides who may edit.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Video on its own, Match | A Series chooser: existing series with video counts, "Start a new series…" (asks a name), "Not in a series". A "What will happen" line says no file moves. |
| Video in a series | The chooser shows the current series; "Not in a series" takes it out. |
| Series of videos, Match | The videos in order with positions and move up/down arrows; the tree on the left follows the order. |
| Dragging a video | A grip handle on every row; a line shows where it will land; dropping stages the new order and the tree updates. Arrows still work. |
| Rename switch | Off by default; only in a Managed folder; wording "Rename the files to match this order". |
| Review changes | Series and order rows say "Library only"; a rename row is last and says "Cannot be undone". |
| Nothing to order | A series with one video shows no order list. |
| Error | A failed check keeps the change in Review with the reason; nothing is lost. |

Mockups: every screen above is in `video-series-and-order.html` (numbered markers, notes in captions).

## 5. Data / model changes

None to the stored data: series membership, order and the files' names are already stored as today. Contracts gain two change kinds' worth of staged entries (series, order) and, if needed, a rename entry; wire names are added with Contracts tests and refreshed fixtures. Pre-release: no migration.

## 6. Work packets

### WP1 — Staged series and order in the Engine
- **What it does:** Adds series and order as change kinds of an edit session (check then apply, same pattern as sibling match), reusing the existing video-series and video-order logic; rename is a file-move change gated by the Managed-folder rule.
- **Files likely touched:** `src/MediaEngine.Contracts/Editing/EditSession.cs`, `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs`, edit-session change handlers, Contracts tests and fixtures.
- **Depends on:** none · **Size:** Standard
- **Done when:** tests show a series change, an order change and a rename each check and apply through an edit session, a Read-only folder refuses the rename, and nothing is saved on Cancel.

### WP2 — Series and Order in the Match tool
- **What it does:** Series chooser and order list with drag handles (a small drag script, no new library) in the pop-over, rename switch, preview line, Review rows; tree follows the order.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/EditorMatchPanel.razor` and a small series/order panel, `SharedStrings.resx`, component CSS, bUnit tests.
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** bUnit tests cover choosing a series, reordering by drag and by arrows, the switch showing only for Managed folders, and the Review rows; screenshots match the mockups.

### WP3 — Sections, sort, marks and multi-select
- **What it does:** Per-section lists and cross-section drag, the sort button, not-matched marks, and "Put in a series" in the multi-edit window with Review flags.
- **Files likely touched:** the series/order panel, `EditorFamilyTree.razor`, `ListEditLauncher`/multi-edit panel, `SharedStrings.resx`, bUnit tests.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** bUnit tests cover a drag between sections, the sort button, the mark, and a multi-select join with one video flagged.

### WP4 — Docs
- **What it does:** Brings back the "Videos: series, order, names" section of the editing guide (already worded) with the why: Library-only changes never touch files; rename is optional because it cannot be undone.
- **Files likely touched:** tuvima-docs `content/products/library/guides/editing-items.md`.
- **Depends on:** WP3 · **Size:** Mechanical · **Done when:** the docs build passes.

## 7. Local checks

- **L1.** On a small video library, put a stand-alone video into a series, then take it out; the video moves in the tree and nothing changes on disk.
- **L2.** Reorder a series and rename with the switch on in a Managed folder; files get "01 - …" prefixes. A Read-only folder never shows the switch.

## 8. Risks and open questions

- **Risks:** a rename cannot be undone, so it is off by default and always listed last in Review.
- None open. Shy answered: Order lives in Match; series and order are staged; drag and drop, sections, sort button, not matched marks and multi-select are in; auto-order is later.
