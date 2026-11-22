# Spec: Find another, clearer lyrics and subtitle results

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-11-21 · **Related docs:** [Product overview](../../product/overview.md) · unified player v2 D17 · review mock-ups 2026-11-18 D7 · PR 1278 (all results listed) · the "Fix lyrics and subtitle search results" work (row alignment, art overflow, result order)

## 1. Goal
When someone opens "Not right? Find another", each result shows the actual words it contains and two plain checks (does it follow the song, does its length fit), so the right one is obvious at a glance. Lyrics and subtitles use the same row.

## 2. Scope
**In scope**
- A new result row for the shared pick window, used by lyrics and subtitles.
- Removing the source name (LRCLIB, SubDL) and all album or poster art from result rows.
- A preview of the real text in each lyrics row, and a tap-to-read preview for subtitle rows.
- Two checks: Synced or Words only; Fits the song or the film, or how far off it is.
- Phone bottom sheet with the same content.

**Out of scope**
- Row alignment, art overflow and result order bugs (owned by the fix thread; this spec builds on that PR once merged).
- Which sources are searched; language and style profile settings.
- Where a source is credited (stays in Playback information).

## 3. Decisions made
- **D1.** No album art or poster in result rows (Shy, 2026-11-21). A result does not say which album or release it belongs to, so a picture would mislead.
- **D2.** No source name in the row (Shy, 2026-11-21). The two checks replace it.
- **D3.** Lyrics rows show the real lyrics (Shy, 2026-11-21): the first three lines, expandable to six.
- **D4.** Same row for subtitles (Shy, 2026-11-21).
- **D5.** The best match is always first. Whatever the fix thread finds about why Library's order differs from lrclib.net (for example the track the website puts first for "Smile Empty Soul Silhouettes") is a build packet of this spec's order rule: results are ordered by how well the typed words match, then by fit to the song's length, and an "In use" result keeps its row but never outranks a better match.
- **D7.** (Shy, 2026-11-21, Q149A) TV gets Find another with the same rows and no search box.
- **D8.** (Shy, 2026-11-21) The search box always starts as "Artist Song name" for lyrics (title and year for a film). If that identifies the right item, the right lyrics or subtitles must come back from it.
- **D9.** (Shy, Q144A) The row title is the source's own name for the song (title, artist, album, length).
- **D10.** (Shy, Q146A) For subtitles, the best match reads its file at once; any other row reads its file when selected (it uses the source's daily allowance).
- **D11.** (Shy, Q147A) Length wording: "Fits the song" or "N seconds longer/shorter", amber up to 15 seconds, red beyond.
- **D12.** (Shy, Q148A) Phone: a bottom sheet with the full list, same content as the desktop. The earlier 3-line expand (Q145) is replaced by the side pane on desktop, tablet and TV.
- **D6.** Dark only, square pills, no toasts, 44 px controls, US English.

## 4. UI behaviour
**One layout on desktop, tablet and TV (Shy, 2026-11-21: the TV version reads better):** the list on the left (name and the two checks) and a pane on the right with the actual words of the selected result (six lyric lines, or the first subtitle lines with times and the timing check). The in-row quote and "Show more lyrics" from the first mock are replaced by this pane. Phone keeps a single column with the lines inside the row, because there is no room for two panes. Selecting another row on desktop or TV loads its lines in the pane (subtitles: reads that one file).

**Lyrics row (columns: text, then pills at the right)**
1. Title line: the source's own song name; then artist, album and length in muted text.
2. The lyrics: three lines in a quote block; "Show more lyrics" opens up to six lines in place.
3. Status line, always in this order: **Synced** or **Words only, not timed**; then the length check: **Fits the song** (green), **N seconds longer/shorter than the song** (amber to 15 seconds, red beyond). Left out when the source gave no length.
4. Right: "Best match" on the first row, "In use" on the current one. The selected row has the purple outline. Use this and Cancel stay in the footer.

**Subtitle row**
1. Title line: the release name; below it the language (only when the list mixes languages) and "Hearing impaired" as plain text.
2. Status line: **Made for your file** or **A different release, may be out of step**; after a preview, the timing check: **Fits the film** with where the file ends versus the film's length, or how many minutes short or long.
3. Preview: the best match shows three real lines with the time each appears. Other rows show a **Preview** link that reads that one file and fills the row in.

| Situation | What the person sees |
|---|---|
| Loading | The existing one-line "Looking for…" text; rows appear together |
| Empty or source problem | Unchanged plain sentences from the shared window |
| Preview loading | The Preview link becomes "Reading…" for that row only |
| Preview fails | One line under the row: "Could not read this one"; the row can still be used |
| TV (D7) | No search box and no typing. A full-screen list of the same rows, moved with the remote's arrows, Back closes it; the selected row opens the lyrics or subtitles it shows in full, and OK on Use this applies it. Subtitle previews read on focus. |
| Phone | A bottom sheet with the same rows one column wide, long release names wrap, Cancel and Use this pinned at the bottom. Nothing left out. |

## 5. Data/model changes
- Lyrics results carry the source's own title, artist, album and length, and up to six preview lines (today: two joined lines).
- Subtitle results gain an optional preview (first three spoken lines with times) and the file's end time, fetched only on tap, except for the best match.
- Both carry a three-state length check computed from the existing length-match score, plus the song or film length used. Wire names added to Contracts with fixtures.

## 6. Work packets
1. **Order rule** (Standard): results ordered per D5. Depends on the fix thread's finding. Done when the lyrics search for "Smile Empty Soul Silhouettes" lists the same first result as lrclib.net.
2. **Lyrics data** (Standard): title, artist, album, length and six preview lines in the lyrics result. Done when the result carries them.
3. **Subtitle preview and timing** (Standard): on-demand preview, best match automatic, end time versus film length. Done when one tap reads exactly one file.
4. **Shared row** (Standard): new row in the pick window, no art, columns as the mock, status line, expand, phone sheet. Depends on 2, 3 and the fix thread's PR.
5. **Guardrail and tests** (Mechanical): no art in rows, no source name in rows, row alignment at 1024, 1280, 1440 and phone.

6. **Subtitle search mechanics** (Standard; owned by the "Fix lyrics and subtitle search results" thread, which tests live on the real PC; this spec takes its findings and does not investigate separately): reproduce the Ferrari case (the same film found by hand on SubDL's site but missing in the app) and compare SubDL's answer by TMDB id, by IMDb id and by name and year, then fix the cause. Suspected from the code, not yet proven: for a movie the app tries only the first search that fits (TMDB id), never falling back to the IMDb id or the name and year; a strict "same title" check can reject an answer outright; results are dropped when a file's language label differs from the requested one; results whose file is not an accepted type are dropped; and an empty answer is remembered for 15 minutes. Done when a film found by hand on SubDL also lists in the app, with a log line saying which search answered.

## 7. Local checks
- Lyrics and subtitle lists on real files at 1024, 1280, 1440 and phone widths.
- A song whose lyrics are Words only; a live version with a long length gap.
- Ferrari (or the film that failed) with a SubDL key: the same releases appear that the SubDL site shows. Needs your key and real file, so the cloud cannot check it.
- A film with a SubDL key: best match preview, Preview on another row, the timing check.

## 8. Risks and open questions
- Reading subtitle files uses the source's daily allowance; that is why previews are on tap.
- The length check for subtitles needs the file read first, so it appears only after a preview.
- None open. Mockups: `engineering/design/mockups/find-another-results-v2/mockups.html` is not committed with this spec; the UI is fully described in section 4 (list left, words pane right on desktop, tablet and TV; single column on phone; no album art, no source name).

## Plain English summary
Each result will show the real first lines of the lyrics (or subtitles) and two simple checks: does it follow the song, and does its length fit. No album art and no source name, so nothing misleading. The best match comes first, and the phone gets the same thing as a sheet.
