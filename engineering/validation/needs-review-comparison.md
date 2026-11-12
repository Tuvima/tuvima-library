# Needs Review: mock beside the built page

> **Status:** Current (2026-11-12) · **Related docs:** [Spec](../specs/archive/needs-review-redesign.md) · [Mockups](../design/mockups/needs-review-redesign.html) · [Batch list](batch.md)

This is the guide for Local check L6 of the Needs Review redesign: the approved mock beside the live page at 1024, 1280, 1440 and phone width. Cloud sessions cannot run the Engine on real media or take screenshots of it, so the side by side is done once, in the batched visual review.

## What was already compared in the cloud

The page was drawn from its real parts with the app's own styles on sample rows (no Engine) and compared by eye with the mock at 1440, 1024 and 390 wide. Layout, group order, row anatomy, the suggestion card, the choices strip, the folded row and the kept line matched. Two phone differences found then were fixed in wave 3 (a lone Try again button now takes its own line; "Show them" is in the accent color). Tick boxes and the action bar were built after that render and have not been compared.

## Getting rows in every group

A small test library is enough; never use the real library for this check.

| Group | How to get a row |
|---|---|
| No match found | The development seed's synthetic titles (`POST /dev/seed-library`), or any file named with nonsense. |
| Ready to confirm | A film file whose name lacks a year but has one obvious match, or any item Library matched with low confidence. |
| Choose a match | A film file named after a title that several films share, with no year (for example a remake and its original). |
| File problems | In a Managed folder with file write-back on, make one matched file read-only, then change its title in the editor. |
| One row for a show | Three or more episode files of one show that Library cannot match, in one show folder. |
| Kept items | Use Keep as it is on any row. |

## What to compare at each width

1. Header line, library filter and search: same order and spacing as mock markers 1 to 3.
2. Group headings with counts and their tick boxes.
3. A row: cover, title, the folder line, the one-sentence reason, the suggestion card with Use this, the "..." menu.
4. A row with choices: pictures, names, one line each, and a Use this button per choice.
5. A folded row for a show, closed and open.
6. A finished line and a failed action's reason under its row.
7. The action bar with two rows ticked.
8. The kept line, opened.
9. Phone width: cards, the bottom sheet from "...", Select, the bar above the dock.

## Known differences from the mock

- The tick column is wider than drawn: the shared check box is 42 px, the mock drew 18 px.
- A group heading's partly ticked state is drawn as an overlay; the shared check box has no such state yet.
- On a phone, Fix, Edit and Delete are absent until phone editing is built (decision D12).
- Television rows never show choices in the row: a show is matched in the editor against TheTVDB.
- Choices for books have no picture: book providers' picture hosts are not on the Engine's allow-list.

## Record the result

Save each pair as `needs-review-<width>-mock.png` and `needs-review-<width>-live.png` in the ignored `.tmp/` folder (test media or sample covers only), and mark rows L1 to L7 in the batch list Validated or Failed with what is off.
