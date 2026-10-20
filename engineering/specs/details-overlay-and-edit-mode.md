# Spec: Details overlay and page-based Editing Mode

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../product/overview.md) · [Item Detail (archived, D12)](archive/item-detail.md) · [Editing Mode (archived, as-built)](archive/editing-mode.md) · [Delete from library](delete-from-library.md) · [Universe](universe.md) · Bug list items 16, 17, 18, 19

## 1. Goal
Titles open as an overlay you can dismiss and come back from. Editing becomes a clear mode of the page itself: choose Edit in the "..." menu, a banner makes it obvious you are editing, and everything (single fields, artwork, matching, batch edits) happens on the page. No side drawer, so nothing is squished and nothing is hidden.

## 2. Scope
**In scope**
- Details overlay (open, dismiss, reload keeps place).
- "..." menu redesign and Edit entry.
- Edit-mode banner, single-item editing, artwork, Match and Identity, History, and batch editing, all in the page.
- Retiring the right-hand drawer (`EditingDrawer`) for single items and batch edits.

**Out of scope**
- Needs Review, browse shells, Recently Added and people/series/curated-collection editors (they keep the modal editor).
- Phones, tablets, TV (consumption only, unchanged).
- Details tabs content (Universe spec owns it).

## 3. Decisions made
- **D1.** Titles open as an overlay over the page you came from. Back, the close button and Esc dismiss it. Reload reopens the same overlay over Home, in read mode. Large centred window on desktop (about 90% of the screen, page dimmed behind), bottom sheet on phone. (Shy, 2026-10-20)
- **D2.** Edit is an entry in the "..." menu ("Edit this show / book / album"). There is no standing Edit button. Reverses Editing Mode D8's button placement; the permission and device rules stay.
- **D3.** No drawer anywhere. Editing is a change in how the page renders. (Shy, 2026-10-20)
- **D4.** One banner design for every item type: a full-width bar under the top navigation (inside the overlay: the overlay's own top), always visible while scrolling. Left: purple dot, "Editing", "Changes stay in your library". Middle: Manage Artwork, Match and Identity, History. Right: one **Done** button. No "Editing Mode" or "Exit Edit Mode" labels, no split Done menu.
- **D5.** The "..." menu has left-aligned text, an icon column, opens beside its button and flips up or left near a screen edge. Delete is its last entry, in red (keeps Delete D17's intent; the edit banner holds no Delete).
- **D6.** Pencils save straight away, one field open at a time (Editing Mode D4, D9 stand). Locks stay on provider facts.
- **D7.** Manage Artwork, Match and Identity and History open as one section directly below the hero, one at a time. Artwork picks apply immediately (Remove asks first). Clicking the banner button again or Done closes the section.
- **D8.** Batch editing: ticking rows (edit mode only) shows a selection bar; its actions open a section above the rows, inside the right column only. Fields say "No change" by default, mixed values say "Mixed". List fields have Add (default), Replace, Remove. One "Apply to 3 episodes" commits the whole batch. Metadata only: renaming, moving files and moving between albums or seasons are structural and live in Match and Identity.
- **D9.** Esc, Done or Back while a field or batch section has unsaved input asks "Save or discard?" (two buttons) and stays in edit mode. Back or Esc on the overlay also leaves edit mode, after that check.
- **D10.** Editing is desktop-only (Editing Mode D2). On other devices the "..." menu has no Edit entry; on a tablet or phone it shows a quiet "Edit on a computer" line.

- **D11.** Shy's answers to Q1 to Q8: all go with the recommendation (★). Done with open input asks Save or discard; no "apply to all in Season" in v1; Refresh Metadata lives in Match and Identity; batch applies as one unit with failures to Needs Review; Delete only in menus; renumbering is a Match move; Move shortcuts live in Match and Identity; reload reopens in read mode. (Shy, 2026-10-20)
- **D12. Match first, then move.** Moving anything into a place that has a known list (an album's tracks, a season's episodes, a book series' positions, an audiobook's parts) always goes through a Match step. The person says which entry it is ("this is Track 5 of OK Computer"), and the move takes that entry's number and identity. We never allow a plain drop that would leave a song in an album that doesn't have it. (Shy, 2026-10-20)
- **D13. Add anyway.** If nothing in the list fits, the person can choose **Add anyway** (a smaller, secondary action with a plain warning). The item joins the destination with no number, in an "Unnumbered" group at the end, tagged "Not matched". It never gets a track, episode or position number, and provider details are not borrowed from a different entry.
- **D14. No list, no gate.** Where nothing known to match against exists (Videos series, personal and custom series, hand-picked collections), moves are free and order is the person's own order (Videos spec D10).
- **D15. Wrong kind is blocked.** A song cannot go into a TV show, an episode into an album, and so on. The destination picker only offers places that accept that kind. Formats of the same title (ebook, audiobook, print) are Editions of one Work and are joined through Match, not moved.
- **D16. Taken slot.** If the chosen entry already has a file, the dialog says so and offers **Add as another version** (an Edition) or **Pick another entry**. It never silently replaces.
- **D17. Every destructive or hard-to-reverse step is confirmed with a preview of what happens**, shown in one shared dialog style: Delete (existing Delete spec dialog), Move (what leaves, what it becomes), Add anyway, Remove artwork, Discard. The Engine works out the preview; the confirm checks it is still true (Delete D6 pattern). No browser pop-ups.
- **D18. Delete in edit mode.** Delete (Delete spec D2, D17) shows only in edit mode, last and red in a "..." menu at the right of the banner, beside Done. The normal page "..." menu also lists it only for people who may edit, as the last red entry, and choosing it opens the same dialog. Resolves the collision with Delete D2 ("only in Editing Mode"): the banner's menu is the one that satisfies it. See Q9.

- **D19.** Shy's answers to Q9 to Q12 (2026-10-20): Delete in the banner "..." in edit mode (and as the last red entry of the page "..." for people who may edit); Add anyway carries only a quiet "Not matched" tag, not Needs Review; in a Managed folder a Move **always** renames and moves the file to fit the destination, so there is no option. The dialog simply says so ("The file will be renamed to 02 - Paranoid Android.flac and moved to OK Computer"); As-is and protected files are never touched; deleting a whole show or series above a size the Engine sets asks the person to type its name.

## 4. UI behaviour
| Situation | What the person sees |
|---|---|
| Normal | Overlay over the previous page. "..." → Edit shows the banner, pencils, locks, hero Edit Background / Logo / Cover chips. |
| Not allowed | No Edit entry. While the item is still processing: disabled with the reason. |
| Loading | Overlay opens at once with the hero skeleton. |
| Error | A failed field save shows inline on that field and stays open. |
| Partly ready | Fields not yet filled show "Still gathering" and cannot be edited. |
| Batch | Selection bar, then batch section above the rows; selected rows stay visible; a result line after Apply ("3 updated" or "2 updated, 1 sent to Needs Review"). |

Open banner with History: read-only timeline in the same section slot. Mark as Watched is hidden in edit mode.

### Moving, matching and deleting, by media type
The **Match step** is one shared screen: left, what you are moving; right, the destination's own list with taken entries marked; the Engine pre-selects its best guess (title, then length) and says why. For several items at once it is a mapping table (current → new entry), as in the music mockup, with unmatched rows highlighted. Apply stays off until every row is matched, or the person chooses **Add unmatched without numbers** (D13).

| Kind | Move target | Match against | If nothing fits | Delete asks |
|---|---|---|---|---|
| Song | another album or compilation | album's track list | Add anyway: unnumbered, end of album | Delete spec dialog (files, space freed, collections affected) |
| TV episode | another season or show | season's episode list (specials = Season 0) | Add anyway: unnumbered "Extras" row | Same dialog; names the season it leaves |
| Audiobook chapter / part | another audiobook | its parts list | Add anyway: unnumbered | Same dialog |
| Book, comic issue | a series | series positions (decimals allowed, 1.5) | Add anyway: unnumbered at the end of the series | Same dialog |
| Movie | a franchise Series | series positions | Add anyway | Same dialog |
| Video (course, YouTube) | a Videos series, or out to standalone | nothing (D14) | not needed: free order | Same dialog |
| Edition (another format of a title) | another Work | the Work's identity (Match) | n/a: joined only by Match | Deleting one Edition says the Work stays |
| Whole show, album, series | not moved | n/a | n/a | Cascade count ("6 seasons, 62 episodes, 41 GB"); type the item's name only above a size or count the Engine sets |
| Photos | outside the Work graph; View keeps its own recycle bin rules | n/a | n/a | Delete spec D23 (View spec) |

**Confirmations (all one dialog style, D17)**
- *Move:* "Move Paranoid Android to OK Computer as Track 2? It leaves The Best Of. Files stay where they are." In a Managed folder the dialog states the new file name and place, because the file is always renamed and moved to fit.
- *Add anyway:* "This won't get a track number or details from OK Computer. You can match it later." Buttons: Add anyway / Match instead.
- *Taken slot:* "Track 2 already has a file. Add as another version, or pick another track."
- *Delete:* Delete spec dialog unchanged. Deleting from a batch shows one dialog listing every file.
- *Remove artwork, Discard, Done with open input:* short two-button dialogs.

**Conflicts and edge cases**
- Someone else deletes or moves an item while it is open: the overlay says "This item is no longer in your library" and edit mode ends; open input is offered a copy.
- Item still processing: Move and Delete show disabled with the reason (Delete allows it per its own spec).
- A move into a destination that changes while the dialog is open: the confirm re-checks and shows the new preview (Delete D6 pattern).
- Moving the only item out of a hand-picked collection or Series that would become empty: the dialog says it will be removed (Delete D4).
- Protected originals and As-is folders: moves change only the Library, never the file (stated in the dialog).
- Wrong-kind, locked provider facts and not-allowed people: the control is absent or disabled with a reason, never hidden without a trace.
- New media types (Videos now; future sources such as other video platforms): they register what list, if any, they can be matched against. No list means D14; a list means D12. No type needs its own move screen.

## 5. Data / model changes
- None for the overlay and banner.
- An "unmatched" flag on an item that was added anyway (D13), so it can be shown as "Not matched" and offered a later Match. Pre-release: existing libraries are unaffected (no such items exist yet).

## 6. Work packets
- **WP1 Overlay host and navigation** (Complex). Back closes, reload reopens in read mode, in-app links by ID. *Done when* Back from an open title lands on the previous page and reload keeps the overlay.
- **WP2 "..." menu** (Standard). Alignment, icon column, placement and flip, Edit entry, desktop gate, "Edit on a computer" line. *Done when* menu tests show the entries per device and permission.
- **WP3 Banner and section slot** (Complex; depends WP1, WP2). Replaces `EditingModeBar` and `EditingDrawer`; sticky banner, Done, Save-or-discard, one section slot (Artwork, Match and Identity, History). *Done when* no drawer renders for any item type and Esc closes a field before leaving.
- **WP4 Artwork and Match and Identity sections** (Complex; depends WP3). Re-house the shared editor sections; artwork applies immediately; Refresh Metadata moves here. *Done when* the Needs Review modal still works with the same sections.
- **WP5 Match step and moves** (Complex; depends WP4). Shared Match screen and mapping table, D12 to D16, Add anyway and the unmatched flag, Engine preview and re-check. *Done when* a song cannot be dropped into an album that lacks it without Match or Add anyway, and Add anyway leaves no track number.
- **WP6 Batch section** (Complex; depends WP3, WP5). Selection bar, No change / Mixed, Add / Replace / Remove, atomic apply with result line. *Done when* a 3-episode batch applies once and a failing row goes to Needs Review.
- **WP7 Delete and confirmation dialogs** (Standard; depends WP3). Banner "..." with Delete, shared dialog style (D17), re-wire the Delete spec's entries (its WP4 location moves). *Done when* no browser pop-ups remain.
- **WP8 Per-type pass and docs** (Standard; depends all). Movie, show, season, episode, book, series, album, audiobook, comic, Videos; guardrail tests; docs; supersede Editing Mode D1 and D8. *Done when* each type's edit, move and delete path is covered by a test.
Collisions to coordinate: Delete from library is In progress; its detail-page entry (its WP4) goes in the banner "..." and the page "..." per D18 and D19, so whoever builds second adapts (ask Shy if unclear); the Videos spec (when it lands) builds its editing on this banner, not the drawer; Universe (no new tabs).

## 7. Risks and open questions
- **Risk:** the editor behind the old drawer is very large; the Needs Review modal still uses it. Tests must cover both.
- **Risk:** sections push content down; mitigated by a sticky banner and a capped section height.
- **Risk:** Match can feel like friction. Mitigation: best-guess pre-selected, one confirm.
- **Collision:** mockups show Universe and Related tabs on show pages; existing decisions rule them out and this spec does not add them.
- No open questions. Q1 to Q12 answered (D11, D19).

## Plain English summary
Open a title and it floats over where you were; Back puts you back. To edit, choose Edit in the "..." menu: a purple bar with Done appears and the page gains pencils. Artwork, matching and batch edits open on the page, never in a squeezed drawer. Moving something is always "match it first": a song can only join an album by being matched to a real track, so numbering stays right; if you insist, you can add it without a number. Deleting always shows exactly what will be removed. The same rules cover music, TV, books, audiobooks, comics, movies and Videos, so new kinds of media slot in without new screens.
