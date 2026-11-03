# Global bookmarks: promote and find bookmarks (every media type)

> **Status:** In progress (stage 1 video and audiobooks built; EPUB and PDF/comic next) · **Version:** 4 · **Last updated:** 2026-11-03 (locked by Shy) · **Related docs:** [Videos spec](videos.md) (D43, D46) · [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) · Mockups: [`../design/mockups/global-bookmarks.html`](../design/mockups/global-bookmarks.html)

## 1. Goal

People bookmark a film for themselves. An administrator can see what the household bookmarked and promote the good ones to **Global**, so everybody gets them. Bookmarks from outside (another library, later Tuvima itself) arrive through a "Find bookmarks" panel in the player that works like Find another subtitles and lyrics, with a side-by-side check against what you already have. Everything happens in the player; there is no Settings page and no bulk import.

## 2. How it works today (audit, from the code)

1. Two shared ideas exist: an **Everyone** bookmark (made one at a time by an administrator, Videos D43) and **bookmark packs** (D46), a per-title file any profile imports for itself.
2. Packs are per person, per title and additive (`bookmark_packs`, `bookmark_pack_items`), read-only, shown under the pack's name with a blue dot and an on/off switch each. An administrator's import is not shared with anyone.
3. Export is a profile-level action: you pick some of your own bookmarks and save a pack file. Vault titles never export or import.
4. An administrator cannot see another profile's bookmarks at all today.

## 3. Shy's decisions (2026-11-03)

- One Global set per title, labelled "Global"; pack names are irrelevant.
- The editor is not used for bookmarks. The player is the place to promote, add and edit.
- No "Share to Global" for ordinary users.
- Rail ticks off by default, optional.
- Promote is immediate, with no confirmation: nobody loses a bookmark, everybody gains one.
- Export is not a profile function. No Settings page and no bulk import (01:46Z): use a subtitles/lyrics-style mechanic instead.
- A bookmark file's times may be a few seconds off; people need to compare side by side and see how close each one is.
- Later, Tuvima acts as the "provider" for bookmark sets, as it does for subtitles.

## 4. Proposed behaviour

- **D1. Filter.** Everyone: All · Mine · Global. Administrators also get "Everyone's" (D2). Ordinary users never see other people's bookmarks.
- **D2. Everyone's (administrators only).** Lists every profile's personal bookmarks on this title in time order, with owner name, time and note. Not in the Vault, not on phone or TV.
- **D3. Promote.** A button on each Everyone's row, with an inline name field. It makes the bookmark Global at once (row glow, no toast, no confirm). A bookmark from a profile at the same spot (within 3 seconds, my default) is replaced and deleted, so that person now sees the Global one under its name.
- **D4. Administrators add and edit Global in the player.** The "Share with everyone" switch stays (admin-only, off by default); admins rename, edit and delete Global rows.
- **D5. Rail ticks are off by default.** A per-profile "Show bookmarks on the bar" option turns them on. The list is unaffected. Changes the always-on bubbles in the player spec.
- **D6. Find bookmarks (new).** In the bookmark panel, "Find bookmarks" opens a list of bookmark sets for this title. Today the only source is **Your file** (choose a bookmark file); it is built so other sources, including Tuvima as a provider like subtitles, can be listed beside it later. Each set shows name, author, count and whether it is for your cut of the film (Matches, Different cut with Align by chapter, or not this title).
- **D7. Side-by-side compare.** Choosing a set opens a table: each incoming bookmark (time, note) beside the nearest bookmark you already have (Global or Mine), with a closeness label based on the gap:
  - **Same spot**: up to 3 seconds.
  - **Close**: up to 30 seconds.
  - **Far**: up to 3 minutes.
  - **New**: nothing nearby.
  The gaps are not hard-coded: they live in `config/bookmarks.json` (Shy, 2026-11-03), one block per kind of position (D12), editable without a Settings page. The gap is always shown beside the label. Each row shows the two bookmarks side by side as two tappable cards: **Incoming** and **Yours** (Shy, 2026-11-03 01:59Z: click one, the player jumps to that spot, and you choose the one you like better; no multi-button choices). Tapping a card does both things at once: the player seeks to that spot (or page, or place in the text) and that card becomes the one kept (highlighted with a tick). The other is dropped when you Apply. A New row (nothing nearby) has just the Incoming card, ticked to add; tap it to preview, tap the tick to leave it out. Defaults: Same spot keeps Incoming, Close and Far keep Yours, New is added, so Apply does the sensible thing with no clicking. Nothing changes until Apply.
- **D8. Who it applies to.** An administrator's Add or Replace affects Global (a Replace of a profile's personal bookmark at the same spot follows D3). Anyone else's affects only their own Mine, and they cannot Replace Global. An administrator also gets **Replace the whole Global set with this one**, which marks every existing Global row as replaced in the table (Shy: answer 2B).
- **D9. Export.** An administrator's **Export Global bookmarks** in the same panel saves a file of this title's Global bookmarks only, never anyone's personal ones. There is no profile-level export and no whole-library export. Vault excluded.
- **D10. Packs retired.** No pack name, blue dot, per-pack switch or Remove pack. A bookmark file becomes ordinary bookmarks after Apply.
- **D11. Wording.** "Global" replaces "Everyone" everywhere.

## 4b. All media types (Shy, 2026-11-03 01:56Z)

The Global idea, Promote, Find bookmarks and the compare table apply to every kind of media that has bookmarks. Today's code: films, episodes, videos and audiobooks share one store (`audiobook_bookmarks`, keyed to the work and a time in seconds, with the Global flag). EPUB books use a separate reader store (`reader_bookmarks`: per user, per file, chapter plus a position inside it, no Global idea). Comics and PDFs have no bookmark store of their own that I found; music and photos have none.

- **D12. Three kinds of position, one set of rules.** Closeness is judged by the kind of place, with thresholds in `config/bookmarks.json`:

| Position kind | Media | Same spot | Close | Far |
|---|---|---|---|---|
| Time | Films, episodes, videos, audiobooks, (music, later) | up to 3 s | up to 30 s | up to 3 min |
| Page | Comics, PDFs | same page | up to 2 pages | up to 10 pages |
| Text | EPUB books | same paragraph (same saved location) | same chapter | up to 2 chapters away |

  Anything further is **New**. The Engine decides closeness (one place), and the file lists these three blocks so they can be tuned later.
- **D13. One bookmark model for all media (Prevent, don't patch).** Book and comic bookmarks move onto the same model as video and audiobook bookmarks (work, profile, position kind, Global flag), so Promote, Everyone's, Find bookmarks and ticks-off work the same everywhere. EPUB bookmarks today belong to one user and a file; they become profile bookmarks on the title. This is the largest piece of work and is staged last (D15).
- **D14. Different editions.** A bookmark set records the title and edition it was made on. Same edition: positions apply directly. A different edition of the same book or film (a different cut, another EPUB): the compare table offers Align by chapter name, as for film cuts today. A book against its audiobook or film adaptation never maps (out of scope). Offered sets are only for titles in your library.
- **D15. Build order.** First video and audiobooks (the store already exists), then EPUB books, then PDFs and comics. Each stage ships Promote, Everyone's, Find bookmarks, Export Global and the ticks-off default for that media.
- **D16. Ticks are off by default for every media type and every source.** That includes sets from Find bookmarks, as with subtitles: a person turns "Show bookmarks on the bar" on to see them (seek rail for time media, page strip for comics and PDFs, progress bar markers for books). Their list rows are still there under Global.
- **D17. Providers later.** The Find bookmarks list is a list of sources. Today: Your file. Later: Tuvima as a provider offering the most popular bookmark sets for a title, as with subtitles, applied for everybody when an administrator picks it. It reads only the title's canonical IDs (the same privacy rule as subtitles), is a plugin that follows the plugin approval rules, and reuses Videos D47 ("Popular Bookmarks"). Not built here, but the panel, the compare table and the config file need no change when it arrives.

## 5. UI behaviour

- **A tall pane, not a small popover** (Shy, 2026-11-03 02:01Z). A film can have 20 or more bookmarks, so the bookmark panel opens as a tall side pane on the right of the player, about 80% of the player height and above the control bar (Shy, 2026-11-03 02:02Z: nothing in the player is full height). The list scrolls inside it, rows are roomy (time, note, owner, marker colour), the row nearest the current time is highlighted and scrolled into view, and Find bookmarks and its compare table use the same tall pane so a long set can be clicked through row by row. The pane never covers the controls bar.

- Filter and rows as in D1 to D3; empty states "No Global bookmarks on this title yet." and "Nobody else has bookmarked this title."
- Find bookmarks and the compare table use the same panel pattern as Find another subtitles: a sub-view with a back arrow, source tags and one primary button.
- Phone and tablet: filter All / Mine / Global; Find bookmarks and Everyone's are desktop and tablet only (touch-friendly), and not on TV.
- Errors in plain words: not a bookmark file, too large, not for this title.

## 6. Data and model changes

Pre-release, so resets are acceptable (CLAUDE.md rule 6):
- Remove `bookmark_packs` and `bookmark_pack_items`; applied bookmarks live in `audiobook_bookmarks`, with `is_global` = 1 for administrator applies. Per-profile "show ticks" is a profile preference.
- Promote and Apply are atomic writes.
- New administrator-only Engine action returning the household's personal bookmarks for one title (Vault refused).
- A compare action: given a bookmark set and a title, return each incoming row with its nearest existing bookmark and gap, so the Dashboard shows no matching logic of its own (Prevent, don't patch: one place decides closeness).
- Backup lists `bookmark_packs`; remove it there and from the public docs page that lists backup tables (drift test) in the same pair of changes. Videos D46 and the player spec get notes pointing here.

## 7. Work packets (draft)

1. **Engine: Everyone's, Promote and Global rules.** Standard. Done when an administrator lists others' bookmarks, Promote makes a Global one and deletes same-spot personal ones atomically, and non-administrators and Vault titles are refused.
2. **Engine: compare and apply bookmark sets, export Global, remove packs.** Standard. Done when a file's rows come back with nearest bookmark, gap and label, Apply honours the per-row choices (and Replace whole set), and pack tables and endpoints are gone.
3. **Dashboard: filter, Everyone's, Promote, tick preference, Find bookmarks and compare.** Complex. Done when it works at 1440 and 1280 and ticks are off for a fresh profile.
4. **Config and closeness rules.** Standard. Done when `config/bookmarks.json` holds the three position blocks, the Engine reads it, and changing a value changes the labels without a rebuild.
5. **Stage 2: EPUB books onto the shared bookmark model.** Complex. Starts with a scout on how reader locations can be compared. Done when a book bookmark can be Promoted, found, compared and Applied like a film bookmark and old reader bookmarks still show for their owner.
6. **Stage 3: PDFs and comics.** Standard. Done when page bookmarks work on the same model, with a page-strip marker option.
7. **Docs and backup list.** Mechanical.

## 8. Local checks

- With two profiles, bookmark the same film at close times, promote one as administrator, and confirm the other profile now sees only the Global one.
- Export Global bookmarks, apply the file in a second library with the same film (and a different cut), and read the Same spot / Close / Far / New labels against what you know.
- On a real EPUB and a comic (stages 2 and 3), promote a bookmark, apply a file from a different edition and check Align by chapter.
- Confirm ticks are hidden on a fresh profile and appear when switched on.

## 9. Risks and open questions

- Administrators can read other people's bookmark notes (Shy's choice). Vault titles are excluded.
- Same-spot Replace deletes a person's bookmark with no undo; accepted by Shy.
- Decided: closeness gaps live in `config/bookmarks.json` (starting values above); anyone can use Find bookmarks, into their own Mine unless they are an administrator.
- Open (small): the Text rule for EPUBs ("same paragraph") depends on how precisely the saved reader location can be compared; a scout must confirm before that stage, and the rule may become "same chapter" for Same spot.
- Later, not in this spec: Tuvima as a bookmark provider, reusing the same panel.

## 10. Plain English summary

(Updated for every media type.)

Everyone bookmarks for themselves. An admin can see everyone's bookmarks in the player and promote the good ones to Global with one click. Bookmarks from outside come in through "Find bookmarks", like Find another subtitles: you see each incoming bookmark next to your nearest one, labelled Same spot, Close, Far or New, and apply the sensible defaults in one click. Films, audiobooks, books, PDFs and comics all work the same way, with "how close is close" kept in an editable config file instead of a Settings page. There is no Settings page and no bulk import, the old packs go away, and bookmark dots on the progress bar are off unless you turn them on.
