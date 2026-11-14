# Spec: Playing now, and Activity moved into General (Server > General tabs)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-14 · **Related docs:** [Library activity](library-activity.md) (its Activity page moves to General > Library activity) · [Playback path](playback-path.md) · [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) · issues #1005, #1000, #1002, #1003 · Mockups: [playing-now-and-general-tabs.html](../design/mockups/playing-now-and-general-tabs.html)

## Product walkthrough (read this first)

Today, when someone presses Play on a movie, the Engine may start a software encode of the whole film. Nothing shows that it is running, nothing stops it, and it keeps using the processor after the person has left. After this spec, an administrator opens **Activity** and sees a **Playing and preparing** list at the top: one row per playback session and per encode, in plain words, with a **Stop** button on each encode. The Engine also stops a forgotten encode by itself two minutes after the last viewer leaves.

What an administrator sees, in order (numbers match the mockup markers):

1. **Server > General becomes four tabs: General, Notifications, Library activity, Playing now.** General holds a Right now snapshot (D19), name and address, language and region. Notifications holds the whole notifications panel and email test (moved unchanged). **Library activity** is today's Activity page (what is being added: progress card, Working now, History), moved out of the Libraries page, otherwise unchanged. **Playing now** is new: what is being watched and prepared, for everyone. Two pages, not one, so neither gets crowded; each tab shows a small dot while it has live work. Libraries keeps Libraries, Needs Review, Matching and Rules.
2. **One row per thing, across everyone.** An administrator sees every account and every profile on the server, not only their own. Cover, title (episode rows say "Show · S2E4"), a status line with the person and profile and the device ("Dana · Kids profile on Living room TV"), what it is doing as a plain label, how far along, and how heavy it is.
3. **"What it is doing" labels** (the same four steps as #1004): *Playing directly*, *Repackaging* (no picture re-encode), *Converting audio*, *Encoding picture*. Before any of these has started: *Preparing* with the reason ("Waiting for a free slot", "Reading the file"). Today the Engine only has the encode path, so rows will say *Encoding picture* until #1004 lands; the labels already exist so nothing here changes then.
4. **How heavy.** A word (Light, Medium, Heavy) plus where the work runs: "Processor" or "Graphics card". Heavy means it is taking most of the processor share the Engine is allowed.
5. **Stop on every encode row.** Stopping ends the encode at once and frees the processor. It never deletes or changes your media; at most it discards the half-built streaming copy, which is only a cache. If someone is watching, the Engine asks once ("Dana is watching this. Stop anyway? Their video will pause and start again if they press Play."). If nobody is watching, it just stops (no pop-up, no toast; the row leaves).
6. **Filter by person.** Above the list: a People filter (All people, or one account, then optionally one profile), and a Device filter. The list also groups by nothing; it stays one flat list, newest first. **Stop all unwatched** sits at the top right. It only ever touches encodes nobody is watching, so it needs no confirmation.
7. **Waiting rows.** Streaming limits already say how many encodes may run at once. Extra ones show as *Waiting for a free slot* with their own Stop.
8. **Nobody watching.** When the last viewer leaves, the row says "Nobody watching · stops in 1:42" and counts down. If they come back, the countdown disappears. At zero the Engine stops it and the row leaves.
9. **Playback sessions without an encode** (Playing directly) are listed too, with no Stop (nothing to stop; there is no extra load). This keeps the list honest: you see who is watching, not only what is heavy.
10. **Phone = desktop.** The same section, rows stacked, Stop is a full-width 44 px button under the row. Nothing is left out on a phone.

What stays the same: ingestion Activity (card, Working now, History), the Libraries page, the player itself, Streaming limits (the limit stays; this spec makes it visible and enforced).

## 1. Goal

Make video preparation and playback visible and stoppable, so an administrator can see why the machine is busy and end work nobody needs. Reason: Performance (a forgotten encode stops eating the processor), Reliability (the Streaming limit really holds) and Ease of use (one place to look).

## 2. Scope

**In scope**
- Moving Library activity (card, Working now, History) from the Libraries page into a General > Library activity tab, behaviour unchanged.
- A new "Playing now" tab on General, administrators only, covering every account and profile, with person and device filters.
- Revamping General into four tabs: General, Notifications, Library activity, Playing now.
- Stop on a single encode, Stop all unwatched, confirmation only when someone is watching.
- The Engine stops an encode by itself after a grace period once nobody is watching, and never runs more than the Streaming limit.
- Offline "prepare for phone" encodes (the scheduled and requested downloads encodes) appear in the same list, with Stop that returns the job to "cancelled" (can be requested again).
- Live updates (no refresh), same as the rest of Activity.

**Out of scope**
- Fixing why starting a title is slow and why "Hardware acceleration: none" is logged (issue #1004). Kept as its own bug fix; see decision D12.
- Player screen fixes #1000, #1002 and #1003 (see D13).
- History of past playback (the Engine already keeps playback statistics elsewhere). This list is live only.
- Scrub previews and bookmark pictures: they stay on the existing Background line (Activity D7) and are not stoppable here.
- Non-administrators: no list, no Stop (Activity D3).
- Music, audiobook and book sessions: not in v1 (they do not encode). The list is built so they can be added.

## 3. Decisions made

Drafted by Claude from reading the code and Shy's notes of 14 Nov 2026; every decision below is approved.

- **D1.** (Changed after Shy's 14 Nov notes.) Library activity and Playing now are **two tabs under Server > General**, not one page and not on Libraries. Reason: both are about what the Engine and machine are doing and belong with the server; one combined page would be crowded (Shy). This replaces Library activity D1's "not a tab inside Libraries" in letter, not intent (one page for adding media, the scan card links to it). Alternative homes: System, or leaving Library activity on Libraries.
- **D2.** It is a new small spec (this file), not a Version 4 of Library activity. Reason: Library activity is about adding media and its Version 3 is locked and not built; this is about playing media and has its own Engine work. Maintenance: two small specs are easier to build and review than one growing one.
- **D3.** Administrators only, same rule as Activity D3 (General is already an administrator page).
- **D4.** Rows are live Engine state. Nothing is written to the data store; a restart empties the list (an encode cannot survive a restart anyway).
- **D5.** Stop never touches source media. A stopped playback copy is discarded because it is a cache the Engine rebuilds if needed (partly built copies are not reused in v1).
- **D6.** Grace period: 2 minutes after the last viewer leaves (a stopped, closed or lost player). A paused player counts as watching for 10 minutes. Both numbers are settings in Streaming limits, not code.
- **D7.** Stopping an encode someone is watching asks once; stopping an unwatched one does not.
- **D8.** The Streaming limit becomes real: a start beyond the limit waits ("Waiting for a free slot") and can be stopped while waiting. The same limit covers playback encodes and offline encodes.
- **D9.** Heaviness is a word, never a raw number: Light, Medium, Heavy, with "Processor" or "Graphics card". Worked out from the encoder in use and the encode's share of processor time, sampled every few seconds.
- **D10.** Wording follows the product vocabulary: "Playing and preparing", "Stop", "Stop all unwatched", "Nobody watching". No "transcode", "HLS" or "ffmpeg" on screen.
- **D11.** No toasts. A stopped row simply leaves; a row whose Stop failed stays and shows "Could not stop. Try again." in its status line.
- **D12.** The slow-start fix (#1004) stays a separate bug fix and is built first or beside this. This spec only needs the four labels. Reason: it changes the player's decision path and has its own Local checks; combining them makes one huge risky change.
- **D13.** #1002 and #1003 (same Audio & subtitles panel) are fixed together as one player bug fix; #1000 (Resume button) is a Look fix on the details page and Home hero. None share files with this spec.
- **D15.** **Everyone is visible.** The list covers all accounts and profiles, each row names the person and profile (a Kids profile says so) and the device. A People filter picks an account then a profile; a Device filter picks one device. Filters are remembered per administrator on this device only. Vault titles follow Activity D19: a title from a Vault library shows as "Vault title" with no cover or name to anyone outside the Vault.
- **D16.** **General becomes four tabs**, in this order: General, Notifications, Library activity, Playing now. Notifications gets its own tab because its panel is as big as everything else on General put together. No Advanced tab. Library activity's route and menu row leave the Libraries page (no redirects; the old address is gone). Links retarget: the Libraries scan card's View activity and status line go to Library activity; the Needs Review hand-off to Library activity; the notification bell to Notifications. The top-bar spinner opens Library activity when library work runs, otherwise Playing now. On a phone the tabs follow the standard page-tab pattern.
- **D19.** **A "Right now" snapshot at the top of the General tab.** Two small cards side by side (stacked on a phone): **Adding to your library** ("Idle" or "Updating your library · 62% · Review 3 items") and **Playing now** ("Nothing playing" or "3 watching · 1 preparing · Heavy on the processor"). Each card is a link to its tab, shows a dot while live, and uses the same numbers as the tabs (the one percent from Activity D23, the one need-review count). Administrators only; non-administrators see no snapshot. Idle is shown calmly, never as a warning. Updates live, no toast.
- **D20.** **Libraries becomes the manage-only page.** Tabs: Libraries (add, edit, folders, status per library), Needs Review (the list of items waiting on a person; it stays because it is a to-do list, not live progress), Matching (metadata providers) and Rules. All live progress leaves it. It keeps the one-line status (its first word links to Library activity, PR 987) and the attention strip, so a problem is still visible without opening General.
- **D17.** The two new playback settings (grace period, paused counts as watching) live on the Playback page's Advanced tab beside Streaming limits, with a link from the Playing and preparing section. The section itself holds no settings.
- **D18.** **Tab contents.** Library activity keeps its Version 3 order and its library chips, with no playback in it. Playing now: header (People and Device filters, counts, Stop all unwatched), then the list, then a Streaming limits link. The library chips are not on Playing now; People and Device are not on Library activity.
- **D14.** Phone parity: same rows and same actions at phone width, Stop is 44 px full width, no hover needed. Nothing hidden.

## 4. UI behaviour

| State | What shows |
|---|---|
| Nothing playing or preparing | Section hidden; page unchanged. |
| Loading | The standard loading state until the first live update arrives. |
| Filtered to nobody | "Nothing playing for Dana right now." with Clear filter. |
| Row: playing directly | Cover, title, "Dana on Living room TV", label *Playing directly*, position "42:10 of 2:01:00", no heaviness, no Stop. |
| Row: encoding | Label, "Encoding picture · 18% prepared", a progress bar, heaviness chip, Stop. |
| Row: waiting | "Waiting for a free slot (1 of 1 in use)". Stop. |
| Row: nobody watching | Status "Nobody watching · stops in 1:42"; countdown clears if someone returns. |
| Row: offline encode | Title plus "Preparing for phones (Small)" and "Requested by Dana"; Stop. |
| Stop (watched) | Small dialog over the page: title, one sentence, **Stop** and **Keep going**. |
| Stop failed | Row stays, status line says "Could not stop. Try again." |
| Engine busy ingesting | Nothing special here; library work stays on Library Activity. |
| Phone | Rows stack; chips wrap; Stop full width. Same actions. |
| Tablet | As desktop with two-line rows. |
| TV | Activity is not available on TV (as today). |

Top bar spinner (Activity D2): it also spins while an encode runs. It opens Library activity if library work is running, otherwise Playing now.

## 5. Data and model changes

- **No new tables.** One live registry in the Engine (in memory) holds playback sessions and encodes: id, kind (watching, playback encode, offline encode), asset, who and which device, what it is doing, percent, heaviness, where it runs, started, last viewer left.
- **Wire types** (Contracts): a list of rows and the two actions (stop one, stop all unwatched). Contracts tests and wire fixtures follow (CLAUDE.md never 3).
- **Settings** (Streaming limits, `config/` JSON): `grace_seconds_after_last_viewer` (120) and `paused_counts_as_watching_minutes` (10). Existing `max_concurrent_transcodes` stays and is now enforced across playback and offline encodes.
- **Today's gap found in code (to be confirmed by the first packet):** the playback encode is started with the Engine's lifetime as its cancel signal, so nothing short of shutting down can cancel it, and nothing watches whether anyone is still reading segments. The offline encode queue has its own single-job loop. The registry plus a per-encode cancel handle is the core of the work.

## 6. Work packets

Built in the cloud; one wave after Activity Version 3 WP8 (so the page and its live feed exist), or earlier if Shy prefers.

- **WP1 — Live registry and cancel handles (Complex).** One registry fed by playback telemetry sessions, the streaming package preparation and the offline encode queue; every encode gets its own cancel handle and the Engine stops the encoder process when cancelled. Files likely: `Services/Playback/AdaptiveHlsService.cs`, `EncodeQueueService.cs`, `PlaybackTelemetryLifecycleService.cs`, a new registry (to be confirmed). Depends on: none. Done when: a test starts a fake encode, cancels it, and the process is gone, the slot is free and the cache copy is removed.
- **WP2 — Grace period and the limit (Standard).** Watcher count per encode from segment reads and open sessions; auto-stop after the grace period; start beyond the Streaming limit waits. Depends on: WP1. Done when: tests with a fake clock show stop at 2:00, countdown reset on return, pause for 10 minutes counts as watching, and a third start waits behind a limit of two.
- **WP3 — Heaviness and where it runs (Standard).** Sample the encoder's processor share; map to Light/Medium/Heavy; report "Processor" or "Graphics card" from the encoder in use. Depends on: WP1. Done when: unit tests cover the thresholds with a spy sampler.
- **WP4 — Engine connection points and live feed (Standard).** List (all accounts and profiles, with names, filterable), stop one, stop all unwatched; administrators only; Vault titles masked; live push on change. Contracts types, tests and fixtures. Depends on: WP1, WP2. Done when: permission tests show a non-administrator cannot list or stop; fixtures refreshed.
- **WP5 — Move Activity into General and add the section (Standard).** Add the Right now snapshot cards; turn `GeneralPage` into General, Notifications, Library activity and Playing now tabs (`SettingsNav` page tabs, phone selector, deep links); remove the Activity tab and menu row from Libraries; repoint the scan card, spinner and Needs Review links; add the Playing now tab (row component, People and Device filters, ask-once dialog, phone layout, no toast). Files likely: `Components/Settings/General/GeneralPage.razor`, `Components/Settings/LibraryActivity/LibraryActivityPage.razor`, `Models/ViewDTOs/SettingsNav.cs`, `Components/Settings/Libraries/LibraryStatusLine.razor`, `Components/Navigation/SystemActivityIndicator.razor` (to be confirmed). Depends on: WP4. Library activity Version 3 is already built and merged (PRs 977, 983, 987; Local checks L10-L16 still open), so this builds on top of it. Links to retarget: the Libraries status line's first word (PR 987), the scan card's View activity, the top-bar spinner and the Needs Review hand-off. The Library activity tab's "animates while work runs" behaviour moves with it to General; the Playing now tab animates for playback work. Done when: tests show the three tabs, Libraries without Activity, every old link landing on the right new tab, each state in section 4 and the phone variant; mock-versus-live proof is a Local check.
- **WP6 — Docs (Mechanical).** Public docs page for Activity gains the section; Streaming limits page gains the two new settings. Depends on: WP5.

## 7. Local checks

- **L0.** On the real server with two accounts and a Kids profile playing at once, the list shows each person, profile and device correctly and the filters narrow it. Failure: a profile missing, or a Vault title named.
- **L0c.** On General, the two Right now cards match the tabs while a scan runs and while a title plays or encodes, link to the right tab, and read calmly when idle; on a phone they stack. Failure: a number differs from the tab's.
- **L0b.** Libraries no longer has an Activity tab; the Libraries scan card, the spinner and the Needs Review hand-off all land on General > Library activity; Notifications is its own tab and the email test still works. Failure: any old link lands nowhere.
- **L1.** Start a movie that needs encoding; within a second the row appears in Activity, with a moving percent and a heaviness word. Failure: no row, or a row that never updates.
- **L2.** Press Stop on it: the processor drops within seconds and the title can be started again. Failure: encoder still running (check Task Manager).
- **L3.** Stop while watching: the dialog appears once; Keep going leaves it alone.
- **L4.** Close the player tab and wait: the row shows "Nobody watching · stops in…" and disappears at two minutes. Reopen before zero: the countdown vanishes.
- **L5.** Start three titles with the limit at one: two show "Waiting for a free slot"; Stop one while it waits.
- **L6.** Stop all unwatched with two forgotten encodes and one watched one: only the two stop.
- **L7.** Phone width on a real phone: same rows and same Stop; nothing missing. Check at 1024, 1280, 1440 and phone for the Mock | Live table.
- **L8.** The "Graphics card" label is right on the RTX machine (depends on #1004's detection fix). Needs the real computer.

## 8. Risks and open questions

Risks: the grace period could stop an encode while someone briefly lost Wi-Fi (the 2 minutes covers that; the encode restarts from the start if they return later). Showing every playback session raises a mild privacy point: an administrator sees who watches what; it is the same audience as the existing playback statistics.

None open.


## Answers recorded (Shy, 14 Nov 2026)

Shy answered every open question with the recommended option (questions 90 to 110): General tabs for Library activity and Playing now; this spec owns both tabs with a pointer in Library activity; build the playback path first; auto-stop after 2 minutes (paused counts as watching for 10); ask once before stopping something being watched; player bugs #1000, #1002 and #1003 stay separate fixes; direct-play sessions are listed; tab names General, Notifications, Library activity, Playing now; People and Device filters; Libraries keeps its one-line status; the playback path is its own spec; MKV H.264/AAC direct-plays when the player reports it can and is otherwise repackaged; one quality per start; a hardware card with Test hardware and a setup row; players are measured and failures learned; native apps follow as per-app work; the specs are locked and built playback path first.

## Plain English summary

Library activity moves out of Libraries into Server > General, which becomes four tabs: General, Notifications, Library activity and a new Playing now. Playing now lists what is being watched or prepared, for everyone on the server. Each video that the Engine is converting for playback gets a row with what it is doing, how far along it is, how heavy it is on the machine and a Stop button, and "Stop all unwatched" clears the forgotten ones. If everyone leaves, the Engine stops the work by itself after two minutes and never runs more than your Streaming limit. Fixing why starting a movie is slow in the first place is a separate piece of work, so that most titles never need an encode at all.
