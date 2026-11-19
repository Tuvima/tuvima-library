# Spec: View polish, smart search bar, and automatic People (faces)

> **Status:** In progress · **Version:** 1.1 · **Last updated:** 2026-11-19 (locked by Shy 2026-11-19) · **Related docs:** [View section](view-section.md) · [Photos and documents editor](photos-documents-editor.md) · [Look pass v3](look-pass-v3.md) · [Product overview](../product/overview.md) · mockups: `engineering/design/mockups/view-people-search/mockups.html`

> **Version note (2026-11-19, Shy):** adds WP13, the photo viewer opening small, as part of the UI uplift. The Locked body above is unchanged.

## 1. Goal

Photos should feel like the best photo apps without sending anything off your computer: search that understands people, places, years and even words inside screenshots, and a People area that finds and groups faces by itself, then asks you to confirm names. Every part works on a phone as it does on a desktop.

## 2. Scope

**In scope**
- Wave 1: View polish (44 px controls, remaining native drop-downs, untranslated text, type scale) and a plain disclosure line under the Places map.
- Wave 2: one smart search bar on Photos (suggestions become combinable chips; words in photos).
- Wave 3: automatic face detection and grouping; rebuilt People page, person page, review ("Is this Sam?"), viewer People and Place sections, one-time model download listed in Tools.

**Out of scope (later specs)**
- Meaning search, pets and things (needs a larger vision model; asked again then).
- Smart galleries by person or place, Trips memories, stacks for bursts and RAW+JPEG.
- Documents page changes. Sharing a person with other household members.

## 3. Decisions made

Shy, 2026-11-19 (Q100 to Q109) unless noted.
- **D1.** Face recognition is **on automatically** (Q100). It still never runs on the Vault or on another person's photos, only on photos you may see in Mine and, for a person's own face data, only used inside the profile that owns them (see D14). Each person has an off switch and "Delete all face data".
- **D2.** The face models are **built into Library**, downloaded **once, automatically**, and listed in **Tools** (Q101, Q100 follow-up). Models are never copied into the repository or build output.
- **D3.** Naming is suggestion-first: after you name one group, Library offers "Is this Sam?" with Yes, Not Sam, Skip (Q102 A). Nothing is named without a yes.
- **D4.** People shows a **"Who's this?"** row on top for the biggest unnamed groups (Q103).
- **D5.** One **smart search bar**: suggestions grouped as People, Places, Years, Cameras, Tags; a chosen suggestion becomes a chip; chips combine (Q104).
- **D6.** Words inside photos (screenshots, receipts) are searchable using the OCR already in the app (Q105 yes).
- **D7.** Places stays its own page; the viewer gets a small map (Q106). Map tiles stay online with a plain disclosure line and a built-in-map-only choice (Q107).
- **D8.** Meaning search and pets and things come **after faces**, as their own spec (Q108).
- **D9.** Order: polish, then search, then faces (Q109).
- **D10.** Everything uses the shared App components and the Look v3 tokens; View-specific fixes only, no restyling of shared parts (nothing fixed twice).
- **D11.** Phone parity (standing rule): every flow, including naming, merging and the viewer sections, works on phones; layout changes only. Editing works on phones per universal-editor-v6.
- **D12.** Delete follows the app rule: one "Are you sure?", then it acts at once; no toasts; tooltips use the app tooltip.
- **D13.** Model choice is behind a swap point (one face-detection role, one face-matching role) so models can change without redoing pages. Leading candidates: YuNet for finding faces and SFace for matching, run through ONNX Runtime; each needs a license check (AGPLv3-compatible) recorded before building. The research-only set used by Immich is not allowed.
- **D15.** If a face model fails the license check, pick another permissive model and re-ask Shy only if none exists (Q110 A). People already named in file metadata show as named people and are not overwritten (Q111 A).
- **D14.** Face data (positions, fingerprints, groups, names) is stored in Library's own data store, is included in backup like other personal data, is never sent anywhere, and is removed by "Delete all face data" and when a profile is deleted.

## 4. UI behaviour

**Photos search bar (mock screen 1).** Typing opens suggestions under the bar (a bottom sheet on phones): People with counts, Years, Places, Cameras, Tags. Choosing one adds a chip; chips combine and narrow the timeline; the chip's X removes it. Plain text still searches dates, devices, locations, tags and (if on) words in photos. The placeholder says what it searches.

**People (screens 2 and 7).** "Who's this?" row (biggest unnamed groups first, sideways scroll on phone), then named people, most photos first; a count badge on the People rail item and phone tab shows groups waiting; "Show hidden" reveals hidden people.

**A person (screen 3).** Face cover, name, counts and first-seen year; "Appears with"; filters (All, Favorites, Videos); one menu: Rename, Merge with…, Choose cover photo, Hide this person, Remove from all photos… (confirm once).

**Naming (screens 4 and 7).** Dialog on desktop, full-screen step on phone: grid of faces, untick any that are someone else, then Yes, Not Sam, Skip.

**Viewer (screens 5 and 7).** Side panel (bottom sheet on phone) gains People (tap a person to show their face box on the photo; unknown faces say "Name this face") and Place (small map; opens Places at that spot). Panel stays read-only apart from Edit.

**Settings (screen 6).** In the profile's photo settings: "Recognize people in my photos" (on), "Read words in my photos" (off until switched on, then it processes existing photos), face data size, "Delete all face data".

| Situation | What the person sees |
|---|---|
| Normal | As above. |
| Empty | People: "No people yet. Library is looking through your photos" with progress while processing; Who's this? row hidden. |
| Loading | Existing loading states; suggestions load after the bar opens. |
| Error | Suggestions fail quietly to plain text search; People shows the app error state with Retry. |
| Partly ready | Face processing in progress: People fills as groups form; a line shows "Looking at 1,240 of 8,900 photos" and links to Library activity. Models still downloading: "Getting the face tools" with a link to Tools. |
| Kid profiles, no access | Follow existing kind access; no People for profiles without photos. |
| Off | People shows "Face recognition is off for your photos" with a switch. |

## 5. Data / model changes

- New stored information per photo: faces (position, quality, a numeric fingerprint), groups of look-alike faces, and a person record (name, cover, hidden, merged). Text found in photos (searchable). All in the data store; pre-release, so reset and reingest is acceptable.
- Existing "People" from file metadata names stay and appear as already-named people; they are not overwritten.
- New Engine query: search suggestions (people, places, years, cameras, tags) per allowed scope with the same authorization as the timeline.
- New Contracts types for suggestions, faces, groups, people and the review; wire names recorded in Contracts tests and fixtures.
- New entries in Tools for the two models; new background work (face scan, text scan) through the existing durable jobs and resource limits, paused when the machine is busy.
- Documentation: backup table list, database schema, presentation rules, AI/Tools pages in tuvima-docs.

## 6. Work packets

### Wave 1: polish
**WP1 — View polish.** 44 px Add media, replace remaining native selects (Density, Places time range, Contributions, gallery share permission) with the app select or phone sheet, move literal English text to language files, use the type scale. View-only. Files: View pages and their CSS (to be confirmed). Depends on: none. Size: Standard. Done when: UI guardrail tests pass and no native select or literal English remains in Photos, People and Places.

**WP2 — Map disclosure.** Plain line under the Places map naming the tile source, plus "built-in map only" choice saved per profile. Depends on: none. Size: Mechanical. Done when: line shows; choosing built-in stops the tile request.

### Wave 2: search
**WP3 — Suggestions query.** Engine query and Contracts type for People, Places, Years, Cameras, Tags with counts, per allowed scope. Depends on: WP1. Size: Standard. Done when: tests show other people's scopes are never read and counts are right.

**WP4 — Smart search bar.** Suggestion list (sheet on phone), chips, combined filters, filter state holds chips. Depends on: WP3. Size: Complex. Done when: tests show chips combine and clear; phone sheet opens; keyboard works.

**WP5 — Words in photos.** Background text scan with the existing OCR, switch in settings, results searchable. Depends on: WP4. Size: Standard. Done when: a screenshot with known text is found by that word after the scan; off means nothing is scanned.

### Wave 3: faces
**WP6 — Model check and Tools entries.** License check for YuNet, SFace and the runtime (AGPLv3-compatible) recorded in dependency rules; models appear in Tools with one-time automatic download. Depends on: none (starts at once, gates WP7). Size: Standard. Done when: license result recorded; Tools shows both models and their state; no weights in repo or build output.

**WP7 — Face scan and grouping.** Background job finds faces in photos you own (never Vault, never other people's scopes), makes fingerprints, groups look-alikes, pauses when busy, resumes after restart; "Delete all face data". Depends on: WP6. Size: Complex. Done when: tests on a small generated library group faces correctly, skip the Vault and other scopes, resume after restart, and delete all removes every row.

**WP8 — Naming and people operations.** Engine actions for name, confirm, Not them, merge, hide, choose cover, remove face; suggestion-first naming. Depends on: WP7. Size: Complex. Done when: tests cover each action and that a wrong group can be fixed.

**WP9 — People pages.** Rebuilt People page ("Who's this?", people, badge), person page, naming dialog and phone full-screen step. Depends on: WP8. Size: Complex. Done when: bUnit tests cover desktop and phone layouts and the one menu.

**WP10 — Viewer sections.** People and Place sections in the viewer side panel and phone sheet; face boxes on tap. Depends on: WP8. Size: Standard. Done when: tests show names, "Name this face" and the map link.

**WP11 — Settings and activity.** Profile switches, face data size, progress in Library activity. Depends on: WP7. Size: Standard. Done when: switches persist per profile and progress shows.

**WP12 — Docs.** tuvima-docs pages and presentation rules for search, People, Tools entries, backup table list and schema. Depends on: WP10. Size: Mechanical. Done when: docs build and the drift tests pass.

### Wave 1 addition (Version 1.1)
**WP13 — Photo viewer opens large.** Shy reports the picture viewer did not open large; treat as a bug. Find the cause first: the viewer frame size (it must fill the window on desktop and be full-screen on phones), or the image it loads first (the `Preview` size rather than the full photo, so the picture looks small until zoomed). Fix so a photo opens fitted to the whole window, then sharpens to the full-size file; keep pinch and swipe. Files: `MediaViewerShell.razor(.css)`, `ViewImmersiveViewer.razor` (to be confirmed). Depends on: none. Size: Standard. Done when: a test or render check shows the viewer stage fills the window at 1024, 1280, 1440 and phone width for a landscape, portrait and panorama photo, and a Local check confirms on real photos. Local check **L8**: open a large photo and a small photo from Photos, People and a Gallery; each fills the window.

## 7. Local checks (added to `engineering/validation/batch.md`)

- **L1.** On a small library, type a person, place and year; chips combine and narrow; on a phone, suggestions open as a sheet.
- **L2.** On real media, let the face scan run: groups are mostly right, speed is acceptable, the app stays responsive. Note faces missed and groups wrongly mixed or split; fix with Merge and Not Sam.
- **L3.** Name one person, accept a suggestion, reject one, merge two groups, hide a person, remove a face, delete all face data.
- **L4.** Confirm nothing is scanned in the Vault or another profile's scope and that face data disappears with the profile.
- **L5.** Screenshots with text are found after switching on words in photos.
- **L6.** Look at 1024, 1280, 1440 and phone width against the mocks; tooltips and menus in the top layer.
- **L8.** See WP13: the viewer opens large from Photos, People and a Gallery.
- **L7.** First run on a fresh data store: models download once, appear in Tools, and the scan starts.

## 8. Risks and open questions

- **Risks:** face scanning is the heaviest background work (Performance): it uses the resource limits and pauses when busy; matching quality varies with children over the years, twins and tiny faces (Reliability): human confirmation and Merge/Not Sam handle it; face data is sensitive (Privacy): local only, never Vault, deletable, backed up like other personal data; an automatic first-run download needs network once.
- **Open questions:** none (Q110 and Q111 answered, see D15).

**Plain English summary:** First a small tidy-up, then one search bar that understands people, places and years, then People that finds faces on your own machine, groups them, and asks you before it names anyone. It is on by default, never touches the Vault or other people's photos, can be switched off or wiped, and works the same on a phone.
