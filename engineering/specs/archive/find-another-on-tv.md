# Spec: Find another on TV (subtitle and lyrics results on the television apps)

> **Status:** Built (awaiting Local checks L1-L7 in `engineering/validation/batch.md`) · **Version:** 1 · **Last updated:** 2026-11-22 (Locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · [Find another, clearer lyrics and subtitle results](archive/find-another-results-v2.md) (D7, D10, D11) · [Unified player v2](archive/unified-player-v2.md) (D12, D29) · `clients/contracts/v1/player-controls.md` (Televisions) · PRs 1288 and 1291 (the list rows and the preview action, already merged)

## 1. Goal
On a television, when the subtitles that came with a film or episode are wrong or missing, the person can open the list of other results, see the actual first lines of each one and whether it fits, and pick one, using only the remote. It is the same list the Dashboard shows, built for a remote and a screen across the room.

## 2. Scope
**In scope**
- The Find another window for **subtitles and lyrics** on the TV web app (Samsung and LG), Android TV, Roku, and the Android phone app (a bottom sheet, same content; Shy, Q149.5), plus the shared groundwork the Apple apps will use.
- A **Lyrics panel** on the TV apps (none has one today), because Find another for lyrics needs somewhere to open from (Q149.1B).
- The Engine connection points the TV apps need to ask for results, read one preview and use one result.
- The entry point: "Find subtitles…" at the foot of an Audio & subtitles side panel, which the three clients do not have yet (see D4).

**Out of scope**
- The rows themselves, their wording, the checks and the order rule (built; Find another v1 D1 to D12).
- A search box or any typing on a television (Find another v1 D7).
- The Apple screens (tvOS and iPhone/iPad): this spec builds only what they will reuse (D11, WP7). Their windows and panels start in their own threads (Shy, Q149.7).
- The rest of the TV player contract (Episodes, Bookmarks, Playback settings panels).

## 3. Decisions made
- **D1.** (Shy, 2026-11-21, Find another v1 D7) TV gets Find another with the same rows and no search box. The list is on the left, the words of the focused result on the right, moved with the remote's arrows; subtitle previews read on focus.
- **D2.** Dark only, 56 px minimum controls, white focus ring, 5 percent safe margins, US English, no toasts (existing TV rules).
- **D3.** The search always starts from the item's own details, so the right result must come back without typing (Find another v1 D8). Because nothing can be typed, the window never shows a search box.
- **D4.** The window opens from **Find subtitles…**, the footer row of the Audio & subtitles side panel, exactly where the contract puts it. Today no TV client has that panel (Roku: none; Android TV: the system's stock subtitle button; TV web: a "Captions" button that cycles). This spec therefore builds a **small Audio & subtitles panel** in each client: Audio rows, Subtitles rows (Off, then each track) and the footer row. Nothing else of the player contract is added.
- **D5.** (Shy, Q149.2) Anyone who can play the item can use Find another, as part of normal playback. The choice is Library-wide (everyone gets it; Go back works once), so the Engine's own refusal still applies to profiles without playback rights and to locked Vault items; those do not see the entry.
- **D6.** (Shy, Q149.1B) TV gets a Lyrics panel in this spec.
- **D7.** (Shy, Q149.3A) The Language button stays, for cases such as anime.
- **D8.** (Shy, Q149.4A) Small Audio & subtitles panel in each client. **D9.** (Shy, Q149.5) All clients are built, including iPhone. **D10.** (Shy, Q149.6A) Previews read after one second at rest.
- **D11.** (Shy, Q149.7) The code supports the groundwork for a tvOS app and the iPhone app; their screens begin separately. Here that means the Engine connection points (WP1), the shared Apple-side models, client calls and the one-second reading rule in `clients/apple/Shared` with unit tests, and the contract wording.

## 4. UI behaviour
**Opening.** Pause or play, press any key to show the overlay, move to the tool row, open Audio & subtitles (a side panel on the right), move to the footer and press **Find subtitles…**. The window fills the screen; the video stays paused behind it, dimmed. Back closes the window and returns to the panel.

**Layout (the one defined in Find another v1, section 4).** A header with the item's name and "Find subtitles" (or "Find lyrics"), and one **Language** button showing the language being searched (the profile's preferred subtitle language; Q149.3). Left, the list: each row has the release name, "Hearing impaired" when it is, the check "Made for your file" or "A different release, may be out of step", and, once read, "Fits the film" or how many minutes short or long. "Best match" and "In use" chips sit at the right of their rows. Right, a pane titled with the focused row's name: the first lines with the time each appears. The footer has **Use this** and **Cancel**.

**Remote.**
| Key | Action |
|---|---|
| Up and Down | Move through the list (the pane follows the focused row) |
| Right | From the list, move to the pane (scrolls it); from the pane to Use this |
| Left | Back toward the list |
| Select on a row | Open that result's words in full in the pane (about 20 lines) and move focus to **Use this** |
| Select on Use this | Apply the result, close the window and the panel, and return to the playing video at the same position with the new subtitles on |
| Select on Language | A short list of the profile's languages; choosing one searches again |
| Back | Close the language list, then the full words, then the window |

**Reading on focus (D1, with D10).** The best match is read as soon as the list opens. Any other row is read only when focus **rests on it for one second**, so scrolling quickly through ten rows reads nothing (the source's daily allowance is limited). A row already read stays read for this window. While reading, the pane says "Reading…" for that row only.

| Situation | What the person sees |
|---|---|
| Loading | "Looking for subtitles…" in the list area; rows appear together |
| Empty or source problem | The same plain sentences as the Dashboard, centered, with Cancel focused |
| A row is being read | "Reading…" in the pane |
| A row could not be read | "Could not read this one" in the pane; the row can still be used |
| No subtitle source switched on | The footer row is hidden in the panel (nothing to search) |
| Profile without playback rights, or a locked Vault item (D5) | The footer row is not shown |
| Applying | The window closes; the panel shows the app's normal Updating state until the new track is ready |
| Lyrics (once a client has a Lyrics panel) | Same window titled "Find lyrics"; the pane shows six lyric lines; the check reads "Synced" or "Words only, not timed" and "Fits the song" or "N seconds longer or shorter" |

## 5. Data / model changes
- **Engine:** the Find another actions exist only on the Dashboard's `/stream/{asset}/text-tracks/...` routes today. The TV apps use the public `/api/v1` door. This adds three actions there (list results, read one preview, use one result), each returning the same fields the Dashboard already gets, with the same permission rules. The wire names are added to Contracts with fixtures.
- **Clients:** none stored. The TV apps already ask for the "play and change playback" permission when pairing, so no one has to pair again.
- Pre-release: no migration needed.

## 6. Work packets
### WP1 — TV connection points for Find another
- **What it does:** exposes list, preview and use-this under `/api/v1` for paired TVs, with the same checks as today, plus the "may this profile change it" answer for D5.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/` (a v1 playback group), `src/MediaEngine.Contracts/Playback/`, `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Contracts.Tests` (to be confirmed by a scout first: whether the app door already passes `/stream` calls for a paired TV).
- **Depends on:** none
- **Size:** Standard
- **Done when:** a paired-device test lists results for a film, reads one preview and uses one; a profile without permission gets a clear refusal; wire fixtures updated.

### WP2 — TV web app: Audio & subtitles panel and the window
- **What it does:** panel plus the Find another window for Samsung and LG; the shared logic (reading after one second at rest, wording) lives in a file Node can test.
- **Files likely touched:** `clients/tv-web/` (new `find-another.js`, `player.js`, `styles.css`), `tests/MediaEngine.Web.Tests/JavaScript/`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a Node test covers the one-second dwell, the "read once" cache and the key map; the README Player gaps list is updated.

### WP3 — Android TV: panel and window
- **What it does:** the same on Android TV with the app's own panel (replacing the stock subtitle button).
- **Files likely touched:** `clients/android/tv/`, `clients/android/core/` (shared model and dwell logic with a unit test).
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** the core unit test passes; the screen follows section 4 in the emulator of the CI build.

### WP4 — Roku: panel and window
- **What it does:** the same on Roku (SceneGraph list left, pane right).
- **Files likely touched:** `clients/roku/components/`
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** a structure check passes in CI; the README gaps list is updated.

### WP5 — Lyrics panel and Find lyrics on TV
- **What it does:** a Lyrics side panel on the TV Now playing screen of each TV client (synced line follows the song) with Find another in its footer, opening the same window.
- **Depends on:** WP2 to WP4
- **Size:** Standard
- **Done when:** the window opens from the Lyrics panel's footer and shows six lines.

### WP7 — Shared Apple groundwork (no screens)
- **What it does:** Swift models for results, previews and the use-this call; the client calls; the one-second reading rule and the "read once" cache; written so a tvOS target and the iPhone app can both use them. No views.
- **Files likely touched:** `clients/apple/Shared/`, `clients/apple/Tests/TuvimaCoreTests/`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the Swift unit tests for the models, the calls and the dwell rule pass in CI.

### WP8 — Android phone app sheet
- **What it does:** the list and words as a bottom sheet from the Audio & subtitles and Lyrics views.
- **Files likely touched:** `clients/android/mobile/`, shared core logic with unit tests.
- **Depends on:** WP1, WP3
- **Size:** Standard
- **Done when:** unit tests pass and the sheet matches Find another v1 D12.

### WP6 — Contract and docs
- **What it does:** the Televisions section of `player-controls.md` gains the Find another window and key map; the public docs page for subtitles mentions TV.
- **Files likely touched:** `clients/contracts/v1/player-controls.md`, `tuvima-docs` playback guide.
- **Depends on:** WP2
- **Size:** Mechanical
- **Done when:** the contract test that reads the contract tables passes.

## 7. Local checks
- **L1.** On a real Samsung or LG TV (or the TV web app in a browser): a film with wrong subtitles, Find subtitles…, move through the list with arrows only, see the pane change, pick one. Fail: a row reads before one second of rest, or a key does nothing.
- **L2.** On a real Android TV and a Roku: same walk-through; Back closes in the order in section 4.
- **L3.** With a SubDL key, check the daily allowance is not used by scrolling quickly.
- **L4.** A profile without playback rights, and a locked Vault item, do not see Find subtitles….
- **L5.** On an Android phone: the same list as a bottom sheet, Cancel and Use this pinned at the bottom.

## 8. Risks and open questions
- **Risk:** a Library-wide change from a couch remote is easy to do by accident; Use this is one extra press from the list and Go back stays available (Dashboard rule).
- **Risk:** Roku has no pointer or keyboard, so Language selection is a short list, not typing.

**Answered (Shy, 2026-11-22):** Q149.1 B, Q149.2 (everyone with normal playback), Q149.3 A, Q149.4 A, Q149.5 all clients including iOS, Q149.6 A.

**Q149.7 answered (Shy):** Apple screens begin separately; this spec leaves the groundwork (D11). No open questions.

## Plain English summary
On a television, "Find another" will show a list of other subtitle choices on the left and the actual first lines of the one you are on, on the right, so you can tell which one fits without typing anything. You move with the arrow keys, press Select to see more, and press Use this to apply it. The Dashboard side is already done; this adds the connection the TV apps use, a small Audio & subtitles panel (the TV apps do not have one yet) and the window itself on Samsung, LG, Android TV and Roku. Lyrics get a new TV lyrics screen and the same window, and the Android phone app gets it as a sheet. The Apple apps get only the shared groundwork now; their screens start separately. Anyone who can play the item can use it.
