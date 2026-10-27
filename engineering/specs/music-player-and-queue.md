# Spec: Music player, queue, lyrics and Songs list

> **Status:** Locked (Shy, 2026-10-27) · **Version:** 2 (replaces v1, same day; nothing was built on v1) · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Presentation rules](../../docs/product/presentation-rules.md) · [Design system](../design/design-system.md) · bug log items F44, F52, F53, F54, F55, F56, F57, F64, F65 (also touches F45, F46, F48)

## 1. Goal

Make the music player and the Songs list feel finished: song lengths are right, the queue and history are tidy and easy to read, lyrics can be closed, and it is always clear where lyrics come from and how to get them. Today each of these is a small, separate annoyance, but they share a few root causes, so one piece of work fixes them together.

### Plain-English walkthrough of the finished experience

1. **Song lengths are right everywhere.** A four-minute song shows 4:03, not 0:04, in the Songs list, the queue and the player.
2. **Up Next (queue) matches Shy's example.** Two tabs, "Up Next" and "History", each with a count. A highlighted "Now playing" card. Under "Up Next", a heading with a small clear icon and a real-looking "Clear" button on the right. Each row: a drag handle in its own column on the far left, then cover, title and artist, the song length on the right, and a "..." menu. The handle never sits on top of the time.
3. **History fits the panel.** No sideways scrollbar. Each row shows cover, title, artist and when it was played ("3 min ago") and a "..." menu.
4. **No mystery text at the bottom.** The "Moved to position 1" line is no longer shown on screen. Moving a song still works exactly the same, and screen-reader users still hear it.
5. **Lyrics can be closed on a computer.** The lyrics card has a visible close button (X), the same as the phone sheet already has.
6. **Lyrics explain themselves.** When a song has no lyrics, the panel says where lyrics come from ("Lyrics come from a .lrc file next to the song, or from LRCLIB, a free lyrics service. Library looks them up when it adds a song") and why this song has none. Anyone using the player also gets "Look up lyrics now" and "Add a lyrics file". When lyrics are shown, a small line says which source they came from.
7. **Lyrics buttons no longer open the old pop-up editor.** Lookup and import happen inside the lyrics panel itself (decision D6).
8. **Songs list matches Shy's example.** A header with a search box, a sort menu and a filter button. Columns: select box, number, cover plus title, favourite heart (centred), time, artist, album, plays, date added, with clear dividers and generous spacing. Add to queue appears only on hover, beside one "..." menu. Delete moves into that menu; there is no Delete column.

### Root causes found (read from the code, 2026-10-27)

- **F44, wrong lengths (0:03, 0:04).** The Songs list takes the song length from the first available of three stored values: the file's measured length in seconds, a second seconds field, then a plain "duration" field. For many songs the first two are missing at the Work level, and the third is filled from Wikidata's "duration" (property P2047). The Engine strips the unit from that Wikidata value (it keeps "3.5" and drops "minutes"), and the Dashboard then reads "3.5" as 3.5 seconds, which displays as 0:03 or 0:04. The retail matching code already knows this field can be in minutes; the display code does not. This is the most likely cause but is not proven: it needs a look at the stored values for a few real songs (Local check L1).
- **F52, handle covers the time.** The row has a 44-pixel column for actions but puts two 44-pixel buttons (drag handle and "...") in it, so they spill left over the time column.
- **F53, sideways scrollbar in History.** History rows reserve a zero-width column for the "Played 3 min ago" text, which cannot wrap, so it overflows the panel.
- **F54, Clear does not look like a button.** It is drawn with no background or border, the same as plain text.
- **F55, "Moved to position 1".** It is a screen-reader announcement that is also printed as visible text and never cleared.
- **F56, no close button.** The phone version of the lyrics card has a close button; the desktop card does not, so the only ways out are clicking the lyrics icon again or pressing Escape, which nobody discovers.
- **F57 and F48, lyrics origin and the old editor.** Lyrics are fetched automatically from LRCLIB (and read from .lrc files placed beside a song) while a song is being added. The Engine can also re-fetch or import lyrics on request, and the Dashboard can already call those actions, but no screen uses them. The only link in the lyrics panel, "Manage lyrics", opens the old pop-up editor (the Details tab), which is a different place from the Editing Mode the rest of the app uses. The empty message says "No lyrics are available for this track" with no reason or next step.

## 2. Scope

**In scope**
- Correct song length handling end to end (stored values, Dashboard display, sort order).
- Queue and History layout, Clear button, hidden announcement, close button for the desktop lyrics card.
- Lyrics: clear origin message, source line, admin lookup and import inside the panel, removal of the old editor link.
- Songs list redesign to match the supplied example, including the shared column rules for this view (F45, and F46 for this view only).

**Out of scope**
- The app-wide table and menu rules (F46, F26): this spec only builds the Songs list and queue to that standard and records what the shared rule should be.
- Reworking the pop-up editor versus Editing Mode (F49).
- Audiobook queue and chapters (not changed).
- Lyrics shown on phones and TV (they keep their current layout; they get the new explanation text and source line, which are shared).
- New lyrics providers.

## 3. Decisions made

- **D1.** A song's length is measured from the file when the file can be read; provider values only fill gaps. Reason: the file is what actually plays, and this stops a provider's unit mistake from showing on screen. (Reliability)
- **D2.** Any provider length is stored in seconds, with its unit read from the provider, never guessed from the number. Reason: "3.5" means different things in different units. (Reliability, Maintenance)
- **D3.** Because the data store is pre-release, wrongly stored lengths are fixed by re-reading the files (a reset or a "refresh details" run), not by a one-off conversion step.
- **D4.** The drag handle gets its own column at the left of each queue row; the "..." menu stays at the right. Reason: matches Shy's example and keeps the time readable.
- **D5.** The "moved to position" message becomes screen-reader-only, because it exists to tell blind users that a drag-reorder worked; sighted users see the row move. Pending Shy's answer to Q1 (the text's purpose is explained there).
- **D6.** Lyrics lookup and import live inside the lyrics panel and are open to anyone using the player (Shy, 2026-10-27: lyrics are not an admin function). They use the Engine actions that already exist. The pop-up editor link is removed from the player. Reason: one place to understand lyrics, and no dependency on the F49 decision.
- **D5b.** (Shy, 2026-10-27) The failure message for a queue move is styled as a proper inline notice (warning icon, soft background, short text, dismiss after a few seconds or on the next action), using the shared notice style, not plain grey text.
- **D6b.** (Shy, 2026-10-27) Who may use a lyrics (or any) source is declared by the plugin itself: each provider's details state whether it can be used by any profile or by administrators only. The Engine checks that declaration instead of a fixed rule. Lyrics (LRCLIB) declares "any profile". Only integrated plugins are affected (community plugins stay parked). Shown in the plugin's details as a plain label with an info tooltip.
- **D6c. (v2, from the plugin architecture review)** Lyrics lookup is not tied to LRCLIB by name: it asks whichever lyrics sources are switched on and available to that person, read from each plugin's declared audience (LRCLIB: everyone). A new lyrics plugin works with no Dashboard change. The Lyrics button itself is never hidden (D6d). See the plugin architecture v2 draft (C4, C21, PA7). Reason: Extensibility.
- **D6d. (v2, Shy 2026-10-27, wording from the plugin architecture spec C21/PA7)** The Lyrics button is a core player button and always shows. If the song has no lyrics, pressing it asks the switched-on lyrics sources to find them ("Looking for lyrics…"). If nothing is found it says so; if no lyrics source is switched on it says "No lyrics source is switched on", with a link to Plugins for administrators. Found lyrics are kept in the Library's own folder; with writeback on they are also written beside the song as a .lrc file or into the file's tags, following the existing writeback rules (built under PA7, not here). Plugin actions elsewhere in the player live in the "..." overflow menu, never as loose buttons. This replaces the earlier hide rule.
- **D7.** The desktop lyrics card gets a close button using the same shared close control as the phone sheet.
- **D8.** Delete leaves the Songs list columns and moves into the row's "..." menu, behind the same confirmation as today. Add to queue shows on hover only.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Queue, normal | Tabs "Up Next · N" and "History · N". Highlighted Now playing card. "Up Next" heading, clear icon, and a bordered "Clear" button on the right. Rows: handle, cover, title and artist, time, "..." menu. |
| Queue, empty | "The queue is empty" with a short hint to add songs from the Songs list or an album. "Clear" is disabled. |
| Queue, a move fails | A styled inline notice (warning icon, soft background, "The move did not go through. Try again."), dismissing itself. Success shows nothing. |
| History, normal | Rows with cover, title, artist, "Played 3 min ago", "..." menu. No sideways scrolling at any panel width. |
| History, empty | "Played songs appear here for this session." |
| Lyrics button pressed, song has no lyrics, a source is on | "Looking for lyrics…" with a spinner, then the lyrics, or "No lyrics found for this song" plus "Add a lyrics file". |
| Lyrics button pressed, no lyrics source switched on | "No lyrics source is switched on"; administrators also see a link to Plugins. "Add a lyrics file" is still offered to everyone. |
| Lyrics, found | Lyrics highlighted as they play, close button top right, small "Lyrics from LRCLIB" or "Lyrics from a file next to the song" line. |
| Lyrics, none found | Plain message that says where lyrics normally come from and that none were found for this song. Everyone sees "Look up lyrics now" and "Add a lyrics file". |
| Lyrics, lookup running | The button shows a spinner and "Looking up lyrics…". |
| Lyrics, lookup finds nothing or fails | "No lyrics found at LRCLIB for this song." or "Lyrics lookup could not reach LRCLIB. Try again later." |
| Lyrics, no lyrics source available to this person (none switched on, or not in its audience) | The explanation only, no buttons. |
| Lyrics, lookup service switched off | "Lyrics lookup is turned off. An administrator can turn LRCLIB on in Settings → Providers (turning the source on or off stays an administrator setting; using it does not)." |
| Songs list, normal | Header with search, sort menu, filter button. Columns as in section 1 item 8. Row hover shows add to queue next to the "...". |
| Songs list, loading / empty / error | The existing shared loading, empty and error states; no new wording. |
| Songs list, phone | Stacked card rows as today (the redesign is for tablet and desktop widths). |
| Song length unknown | A dash "–", never "0:00". |

Phone and TV: Up Next and History use the same rows; the drag handle is hidden on touch devices that already reorder through the "..." menu (Move earlier, Move later).

## 5. Data / model changes

- Song length keeps its current stored field names; what changes is which value wins and that provider lengths are stored in seconds. No new fields.
- No change to the wire format between Engine and Dashboard is expected. If a "source" label for lyrics needs to be sent, it is added to the existing text-track information and the Contracts fixtures are refreshed.
- Existing libraries: pre-release, so a re-read of the files (refresh details) corrects any songs that already hold a wrong length. No migration.

## 6. Work packets

### WP1 — Diagnose and fix song length
- **What it does:** Confirms the cause with a failing test using a Wikidata-style "3.5 minutes" value, then makes the file-measured length win, makes the provider transform keep the unit (store seconds), and makes the Dashboard length reader refuse to treat tiny fractional values as seconds. Sorting by time uses the same value.
- **Files likely touched:** `src/MediaEngine.Providers/Models/ValueTransformCatalog.cs`, `config/providers/wikidata_reconciliation.json`, `src/MediaEngine.Web/Components/Library/LibraryHelpers.cs`, `src/MediaEngine.Web/Components/Listen/ListenSongTable.razor`, the audio file reader (`src/MediaEngine.Processors/Processors/AudioProcessor.cs`) if the file value does not reach the Work, plus tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test with a minutes-based provider value and no file value no longer shows 0:03; a test with both shows the file value; sorting by time orders a mixed set correctly.

### WP2 — Queue and History layout
- **What it does:** Gives the drag handle its own left column, keeps the time visible, gives History a proper "played" column that cannot overflow, makes Clear a real button, and applies the same row layout to Now playing, Up Next and History.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/PlaybackContextRow.razor` and `.razor.css`, `src/MediaEngine.Web/Components/Listen/PlaybackContextPanel.razor` and `.razor.css`, tests in `tests/MediaEngine.Web.Tests/PlaybackContextPanelTests.cs`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test shows the handle and the time in separate columns; a History row at a narrow panel width has no horizontal overflow (checked in the browser check); Clear uses the shared button style.

### WP3 — Move announcement
- **What it does:** Makes the successful-move text screen-reader-only and shows only failures visibly.
- **Files likely touched:** `src/MediaEngine.Web/Components/Listen/PlaybackContextPanel.razor` and `.razor.css`, its test.
- **Depends on:** WP2 (same files)
- **Size:** Mechanical
- **Done when:** after a move, no visible text is added; the live region still contains "Moved to position N"; a failed move shows a visible message.

### WP4 — Close button on the desktop lyrics card
- **What it does:** Adds the shared close control to the desktop popover surface, with keyboard focus returning to the lyrics icon.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/PlaybackPopover.razor` and CSS, `tests/MediaEngine.Web.Tests/PlaybackPrimitiveTests.cs`.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a test finds a close button with the accessible name "Close Lyrics" on the desktop surface, and clicking it closes the card.

### WP5 — Lyrics origin, lookup and import
- **v2 change (button):** the Lyrics button stays in the dock, full player and phone player at all times (D6d) and starts a lookup when pressed on a song with no lyrics. The lookup, writeback and overflow-menu structure are built by the plugin architecture work (PA7); this packet builds the panel states and wording and uses PA7's find action when it exists.
- **v2 change:** lookup, writeback and the player overflow menu are owned by PA7 of the plugin architecture work; this packet no longer hides anything. Writeback does not exist for lyrics today; PA7 adds it, defaulting to a .lrc file beside the song, with embedded tags only where the library allows tag writeback. The source line shows the name of whichever source supplied the lyrics.
- **What it does:** Replaces the empty message with the origin explanation; adds the source line when lyrics exist; for everyone adds "Look up lyrics now" and "Add a lyrics file" (existing Engine actions, with the Engine permission relaxed from administrators to any signed-in profile); shows the states in section 4; removes the pop-up editor link. Updates the player guide and the lyrics paragraph in the presentation rules.
- **Files likely touched:** `src/MediaEngine.Web/Components/Listen/PlaybackLyrics.razor` and CSS, `src/MediaEngine.Web/Services/Playback/PlaybackLyricsPresenter.cs`, docs `docs/guides/playback-and-reading.md`, `docs/product/presentation-rules.md`, tests in `tests/MediaEngine.Web.Tests/PlaybackLyricsTests.cs`.
- **Depends on:** WP4 (shares the lyrics card), WP5b
- **Size:** Standard
- **Done when:** tests cover each row of the lyrics table in section 4; any profile sees the lookup and import buttons, and the Engine accepts those actions from any signed-in profile (not only administrators); no code path in the player opens the pop-up editor.

### WP5b — Plugin declares who can use it
- **Scope note (2026-10-27):** This packet is only the minimum lyrics needs. The plugin-wide review (audience for every existing plugin, v2 of the Plugins and integrations screens spec, other plugin capabilities) is owned by the separate plugin architecture review thread; if that work lands first, this packet shrinks to setting LRCLIB's audience.
- **What it does:** Adds an optional "who can use this" setting to the provider file format (any profile, or administrators only; default administrators only so nothing opens up by accident), shows it as one label in the plugin's existing detail section (placement inside the redesigned detail of the Locked Plugins and integrations screens spec is a small addition; this spec does not change that spec's meaning, and if Shy wants it formally in that spec it is proposed as its v2 there), and makes the Engine's lyrics and subtitle refresh/import actions follow the declaration of the provider that serves them. Sets LRCLIB to any profile. Updates the provider schema, the provider-format docs and the plugin-details tests.
- **Files likely touched:** `config/schemas/providers/provider.schema.json`, `config/providers/lrclib.json`, `src/MediaEngine.Storage/Configuration/ProviderDeclarativeValidator.TextTrack.cs` (validation), `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs` (permission check), the plugin details screen in `src/MediaEngine.Web`, `docs/reference/providers.md`, tests in `tests/MediaEngine.Storage.Tests` and `tests/MediaEngine.Api.Tests`. Overlaps the Developer tools spec and the Locked Plugins and integrations screens spec (WP3, plugin detail); the build must check its Locked version first and stop and ask if it conflicts.
- **Depends on:** none (WP5 uses it)
- **Size:** Standard
- **Done when:** a profile that is not an administrator can refresh lyrics from LRCLIB but cannot refresh a provider declared administrators-only; the plugin details page shows the label; an unlisted setting defaults to administrators only.

### WP6 — Songs list redesign
- **What it does:** Rebuilds the header (search, sort, filter), column order, dividers and spacing, centres the heart, moves Delete into the "..." menu, shows add to queue on hover only, and shows a dash for unknown length. Records the column rules (padding, alignment) as a short section in the design system so F46 can reuse them.
- **Files likely touched:** `src/MediaEngine.Web/Components/Listen/ListenSongTable.razor` and `.razor.css`, `engineering/design/design-system.md`, tests in the existing Listen table tests.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** a test shows no Delete column and a Delete item in the row menu; the heart column is centred (checked in the browser check); the visual check of the list at desktop width matches the example; the CSS size check passes.

### WP7 — Wrap-up
- **What it does:** Updates docs, appends Local checks to the batch list, runs the full build and tests.
- **Files likely touched:** `engineering/validation/batch.md`, `engineering/README.md`
- **Depends on:** WP1–WP6
- **Size:** Mechanical
- **Done when:** the CI-strength build and the filtered tests pass.

Wave plan: Wave 1 = WP1 and WP2 (different files). Wave 2 = WP3, WP4 and WP6 (WP3 and WP4 are tiny; WP6 can run beside one of them). Wave 3 = WP5b then WP5. Then WP7. Built in the cloud, validated locally as one batch.

## 7. Local checks

- **L1.** On the real music library, open the Songs list and check ten songs against their real lengths (play the last few seconds). Any "0:0x" still showing counts as a failure. If any remain, copy the stored values for one song so the cause can be fixed.
- **L2.** Play three songs, open Up Next: the handle, cover, title and time are all visible and nothing overlaps; drag one song to the top and confirm no extra text appears at the bottom.
- **L3.** Open History with 5+ songs at the narrowest side-panel width: no sideways scrollbar.
- **L4.** Open Lyrics on a computer: the close button works with mouse and keyboard.
- **L5.** Pick one song with lyrics and one without. The first shows the source line. On the second, as a non-administrator profile, "Look up lyrics now" either finds lyrics or says clearly why not (needs internet and the live LRCLIB service).
- **L5b.** Open LRCLIB's details in Settings → Providers: it states it can be used by any profile. Sign in as a non-administrator profile and run a lyrics lookup: it works.
- **L5c.** The Lyrics button shows on every song. With LRCLIB switched off and a song with no lyrics, pressing it says no lyrics source is switched on (administrators see the Plugins link). Switch LRCLIB on: pressing it on that song finds lyrics, and with writeback on a .lrc file appears beside the song.
- **L6.** Songs list at desktop width next to the supplied example: columns, spacing, centred heart, hover actions, Delete in the "..." menu with its confirmation.

## 8. Risks and open questions

- **Risks:** (1) If the length cause is not the Wikidata unit, WP1 changes more than expected; the first step in WP1 is a failing test and the Local check L1 catches this. (2) Moving Delete into a menu makes it slightly slower to reach; it keeps today's confirmation and any later "Safer deleting" work stays compatible. (3) Lyrics lookup depends on a free outside service (LRCLIB); when it is down, the panel says so.
- **Q1 (F55) — DECIDED by Shy 2026-10-27: hide from view, keep for screen readers, failures shown as a well-styled notice (D5, D5b).** Shy asked what the text is for: it is an announcement for screen-reader users confirming that a reorder worked, which for sighted users is already shown by the row moving. Recommendation unchanged (hide from view, keep for screen readers, show failures). Original question: What should happen to the "Moved to position 1" text?** It is a leftover message meant for screen readers that is also printed on screen. Options: A) hide it from view but keep it for screen readers, show only failures (recommended: the move itself already shows the result, and accessibility stays intact); B) keep it visible but fade it after three seconds; C) remove it entirely.
- **Q2 (F57) — DECIDED by Shy 2026-10-27: anybody.** Lyrics lookup is not an admin function, and the lyrics source should behave that way too: the Engine permission check for the lyrics refresh/import actions must allow any signed-in profile, and the LRCLIB provider entry must not mark lyrics as admin-only. Original question: Who may look up or add lyrics from the player?** Lyrics come automatically from LRCLIB or a .lrc file beside the song, and the Engine can fetch or import on demand. Options: A) administrators on a computer only (recommended: matches "editing is desktop-only" and keeps outside requests under admin control); B) any signed-in person, so households can fix lyrics themselves, which means any profile can trigger requests to LRCLIB; C) no on-demand lookup, just the explanation.
- **Q3 (F64) — DECIDED by Shy 2026-10-27: option A (keep in chooser, off by default).** Original question: Should the Songs list keep the Genre and Edit columns?** Today's table has them hidden or shown by choice; Shy's example does not show them. Options: A) keep them available in the existing column chooser but off by default (recommended: nothing is lost, the default matches the example); B) remove them.

## 9. Gaps and drift

Not applicable (new spec).

## Plain English summary

Several small problems in the music player trace back to a few causes. Song lengths look wrong because a length from Wikidata, which may be in minutes, is being read as seconds; we will trust the file's own length first and read provider units properly. The queue looks cramped because two buttons are squeezed into a space made for one, the History list has text that cannot fit its column, and Clear is drawn like plain text; we will give each its own space and make Clear a real button. The "Moved to position" line is a leftover note for screen readers that should not appear on screen. The lyrics card has no close button on a computer, and its empty message never says that lyrics come from LRCLIB or a lyrics file beside the song; we will add a close button, explain the source, and let administrators look up or add lyrics right there instead of opening the old pop-up editor. Finally, the Songs list is rebuilt to match your example. Three questions for you are in section 8; everything else is a recommendation we will build unless you change it.
