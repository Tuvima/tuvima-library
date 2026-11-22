# Spec: Native apps (TVs, phones and tablets, cars)

> **Status:** Locked (Shy, 2026-11-22 22:51Z; all answers Q210 to Q218 accepted as recommended) · **Version:** 1 · **Last updated:** 2026-11-22 · **Related docs:** [Product overview](../product/overview.md) · [Player controls v1 (the shared contract)](../../clients/contracts/v1/player-controls.md) · [Unified player v2 (archived)](archive/unified-player-v2.md) · [Remote access](remote-access.md) · [Tuvima ID sign-in](tuvima-id-sign-in.md) · [Native Client Delivery](../../docs/product/native-clients.md) · Issues #782 (Roku), #783 (Android TV), #784 (Apple TV), #785 (TV web), #786 (CarPlay), #787 (Android Auto) · Plan: "Testing and building the TV and phone apps" (2026-11-22)

## 1. Goal

Library's apps for televisions, phones, tablets and cars all look and behave like the Dashboard's player and browse screens, so a person who learns Library once can use it on any screen they own. The rules for the player are written once (the player contract), each device draws them in the way its screen and remote allow, and all of it stays on the home network with no password at home.

Why it matters: most people use only their phone (standing rule, 2026-11-12), and a TV or car is where the stories already on their disk are actually enjoyed. Building from one contract keeps six apps from drifting apart (Maintenance, Reliability).

### Plain-English walkthrough of the finished experience

1. **On any TV** (Samsung, LG, Roku, Android TV, Fire TV, Apple TV) the player shows five transport buttons (Previous, back 10 seconds, Play, forward 30 seconds, Next) with Previous and Next dimmed in place. Below them is a tool row (Episodes, Audio & subtitles, Bookmarks, Playback settings) that opens a panel on the right. Paused details, the "Up next" end card and the Autoplay switch match the Dashboard.
2. **On a phone or tablet** the app matches the desktop in every way except setup: Home with My List, Libraries, Details, Search, playback, offline downloads. Editing an item opens the Dashboard in the phone's browser in version 1.
3. **In a car**, the car's own screen shows a short list (Continue, Listen shelves, Audiobooks, Playlists, Recently played) and a Now playing card. Search is by voice. Nothing visual, no video.
4. **Pairing** works the same everywhere: type the server address, a code appears, an administrator approves it in the Dashboard. At home there is no password. Casting works on the home network only.
5. **Prices and licence:** TV apps are free. The iPhone/iPad and Android store apps cost a one-time $4.99 with no further payments. The app code is open under MPL 2.0 in `clients/`.

## 2. Scope

**In scope**
- **TVs:** TV web app (also the code inside the Samsung Tizen and LG webOS packages), Android TV (with Fire TV through the same app), Roku, Apple TV. Each follows the player contract (issues 782, 783, 784, 785), after Shy approves mock-ups of the TV side panels.
- **Phones and tablets:** Android first, then iPhone and iPad. An audit of the Android phone app against the Dashboard, then fixing every gap except setup and editing.
- **Cars:** Android Auto (issue 787) and CarPlay (issue 786) following the contract's car list and Now playing card.
- **Packaging and publishing:** licence files, signing, Android APK on GitHub then Google Play, Roku, Samsung and LG store paperwork, Fire TV sideload, Apple later.
- Local checks for every wave, appended to `engineering/validation/batch.md`, tagged by what is needed (Windows PC, real Roku, real Android TV, needs Mac).

**Out of scope**
- Editing items in a native app (version 1 opens the Dashboard in the phone's browser; see D7).
- Away-from-home use inside the apps beyond what [Remote access](remote-access.md) already defines.
- Photos and Books reader parity beyond what the audit finds as a gap on the phone (each is its own spec if large).
- Any change to the Engine's `/api/v1` beyond connection points the audit proves are missing (listed as packets then).
- Selling or hosting anything on Tuvima's side; no Tuvima-hosted account is created by this spec.
- Amazon's own Vega OS (non-Android Fire TV sticks); decided only after the check in Q214.

## 3. Decisions made

- **D1.** The apps are open source under **MPL 2.0** and live in this repository under `clients/`, each client folder with its own `LICENSE`. The repository itself stays AGPLv3. Outside contributions are accepted with a sign-off line. A lawyer reads the licence choice before the first App Store submission. Reason: MPL 2.0 can be combined with store terms that plain AGPL cannot (Maintenance, Extensibility). *(Shy, 2026-11-22)*
- **D2.** **Build order:** TV player contract on the TV web app first, then Android TV, then Roku; then Android phone parity; then cars and store packaging; Apple last (Apple TV, CarPlay, iPhone and iPad). *(Shy, 2026-11-22)*
- **D3.** **Mock-ups first for TV side panels.** The contract calls the TV panel layout "a first definition". One planning step produces mock-ups of the panels, paused details and end card that Shy approves once for every TV before any TV build starts. *(Shy, 2026-11-22)*
- **D4.** **Hardware:** Shy owns a real Roku TV. No Samsung or LG TV: TV web plus the Tizen and webOS simulators cover them. An Android TV box or the emulator is optional. Apple work waits on a Mac, and the Mac decision is made when Apple work starts. *(Shy, 2026-11-22)*
- **D5.** **Price:** TV apps are free. The iPhone/iPad and Android store apps are a one-time **$4.99**, with no further payments; the web Dashboard and the open code stay free. Store pages say plainly that a Library server is needed. *(Shy, 2026-11-22)*
- **D6.** **Publishing:** Android APK on GitHub first, then Google Play after phone parity; Apple later. *(Shy, 2026-11-22)*
- **D7.** **Phone editing in version 1:** browse, play, My List and everything else the Dashboard offers except editing; editing an item opens the Dashboard in the phone's browser. This is a deliberate, stated exception to the phone-equals-desktop rule and is revisited in a later version (Q211). *(Shy, 2026-11-22)*
- **D8.** **Fire TV** is reached by sideloading the Android TV app first. Google Cast does not work on Fire TV (the app hides its Cast button there). Newer Fire TV sticks that run Amazon's Vega OS may not run Android apps; no promise is made until Q214 is answered. *(Shy, 2026-11-22)*
- **D9.** **Cars are specialised devices in this same spec** (Shy, 2026-11-22), so TVs, phones and cars share one contract, one pairing flow and one batch of Local checks.
- **D10.** **Standing rules carry over:** phone equals desktop except setup (2026-11-12); casting works on the home network only and only on a current match (2026-11-19); no-password profiles work on any local network and public internet access is explicit opt-in; the Android phone app has no Bookmarks for music (2026-11-21).
- **D11.** Apps talk only to the Dashboard's `/api/v1` and pair with a code approved in the Dashboard. Nothing sensitive (signing keys, store accounts, tokens, signed URLs) is ever in the repository.
- **D12.** Words in every app are the Dashboard's words (Library, Series, Universe, Work, Edition, Needs Review), US English; a TV show is a show and a book sequence is a series.
- **D13.** (Q210) Wave order is as in section 6; Android phone work may start in parallel with the TV waves only when a build slot is free. The phone audit (WP3) runs alongside Wave 0 regardless.
- **D14.** (Q211) Phone editing stays "edit in the browser" (D7); whether to build a native editor is decided after the WP3 audit, in a later version of this spec.
- **D15.** (Q212) One $4.99 covers phone and tablet on each store (one iOS listing, one Android listing).
- **D16.** (Q213) Android Auto ships in Wave 4; CarPlay ships with Apple in Wave 5.
- **D17.** (Q214) Docs say "Android-based Fire TV devices (sideload)" and nothing is promised for Vega OS sticks until a supported-model list exists (WP7).
- **D18.** (Q215) The Mac decision is made when Apple work starts: rent a cloud Mac for the first checks, buy one only if the Apple apps sell.
- **D19.** (Q216) Google Play starts with a closed test with a few friends before the public release.
- **D20.** (Q217) All TV apps are free.
- **D21.** (Q218) Outside contributions need the sign-off line only; a contributor agreement is added only if the lawyer says the Apple build needs it.

## 4. UI behaviour

The player's order, names, defaults and panels are **not** repeated here: they are in the contract (`clients/contracts/v1/player-controls.md`), and a test compares it with the Dashboard. This section says what differs per device and what each state looks like.

### Per device

| Device | What it is | How it differs |
|---|---|---|
| TV web (Samsung Tizen, LG webOS, any TV browser) | One plain JavaScript app, wrapped by two packages | Remote keys (arrows, Select, Back, media keys); overlay hides after 5 seconds, never while a panel is open; Back closes the panel first |
| Android TV and Fire TV | Kotlin app | Same panels; Cast button on Android TV only, and not on Fire TV; Fire TV remote has no dedicated Rewind and Fast-forward on every model, so the transport row covers them |
| Roku | SceneGraph/BrightScript | Same panels; on-screen keyboard for the server address; Rewind and Fast-forward map to the skip buttons; text sizes checked from the sofa |
| Apple TV | Swift | Same panels; Siri Remote swipes move focus; lock screen and Now playing fields as the contract lists |
| Android phone and tablet | Kotlin app | Matches the Dashboard's phone layout; bottom sheets for panels; portrait, with video rotating to landscape; Picture-in-Picture |
| iPhone and iPad | Swift app | Same screens as Android; iPad keeps all orientations; downloads in the background |
| Android Auto and CarPlay | The car draws it | A list in the contract's order and a Now playing card with the contract's buttons; voice search; no pictures to browse, no video |

### States (every client)

| Situation | What the person sees |
|---|---|
| Normal | The screen as in the Dashboard, adapted to the device |
| Empty (nothing in the Library or a shelf) | The Dashboard's empty wording, with nothing dead to press; a TV never shows an empty row |
| Loading | The app's normal loading state (never a blank screen); a row appears as soon as it has items |
| Error (Engine unreachable) | "Can't reach your Tuvima server" with Retry; the app tries the home address first, then a saved outside address if one exists |
| Not allowed | The Engine's own sentence (profile cannot see it, photo access not granted, Vault locked); a Vault item sends no lock-screen metadata while locked |
| Not paired | The server-address screen, then the pairing code and the page to open |
| Partly ready (artwork or lyrics still arriving) | Plain cover or "Looking for lyrics…" and the screen fills in without moving the controls |
| Control cannot act | Dimmed in place, never hidden; a tool the media or app does not support is absent |

### Phone and tablet rules

- Everything the desktop has is there, except setup and editing (D7). Anything left out is listed by the audit (WP10) and either built or recorded as a decision with Shy.
- My List plus/tick everywhere (a heart only on songs); a My List row on Home; Add library is not shown (setup).
- Editing opens the Dashboard in the phone's browser, signed in by the same profile.

### Cars

- The list order is Continue, the Listen shelves, Audiobooks, Playlists, Recently played.
- Now playing card: title, second line and artwork as the lock-screen table says. Music buttons: Previous, Play, Next, plus Shuffle and Repeat. Audiobook buttons: back 10 s, forward 30 s (the profile's skip seconds), Previous chapter, Next chapter, plus the Speed presets. Previous in music restarts a song after 3 seconds.
- Voice search through the car's assistant. A Vault item or a hidden episode title never appears on the card.

## 5. Data / model changes

- **None to the data store.** Native clients use the existing `/api/v1`, device pairing, paired-device tokens and per-device settings.
- The audit (WP10) may find screens whose connection points are missing from `/api/v1` (older notes name Read, View and Collections). Each becomes a packet with Contracts types and fixtures updated together; no new wire shape is added without them.
- New files: `clients/<name>/LICENSE` (MPL 2.0), a short `clients/CONTRIBUTING` note with the sign-off line, store listing text kept outside the repo where it contains accounts or keys.
- Pre-release: re-pairing every app after a reset is acceptable.

## 6. Work packets

Waves run one after another; inside a wave, at most two packets at once on different folders. Each wave ends with the build, the targeted tests (`tests/native/Test-TvWebClient.ps1`, `Test-RokuClient.ps1`, Android Gradle build and unit tests, the contracts test), a reviewer pass and its Local checks appended to the batch list.

### Wave 0 — Licence, mock-ups and the phone audit

### WP1 — Licence files and contribution note
- **What it does:** adds an MPL 2.0 `LICENSE` to each folder in `clients/`, adds the sign-off note, and records the dependency licence check for every client library (hls.js, Android player libraries, Swift packages).
- **Files likely touched:** `clients/*/LICENSE`, `clients/README.md` or `CONTRIBUTING.md`, `docs/` licence page (to be confirmed)
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** every client folder has the licence, the licence check list is in the PR, and no AGPL-incompatible library appears.

### WP2 — TV panel mock-ups
- **What it does:** a planning step (no app code) produces numbered HTML mock-ups of the TV transport row, tool row, each side panel, paused details, end card and Find subtitles window at 1920×1080, plus the Roku and Apple TV focus rules, for Shy to approve. The contract's "first definition" text is then updated to the approved layout in the same change that locks it.
- **Files likely touched:** `engineering/design/mockups/` (to be confirmed), `clients/contracts/v1/player-controls.md`
- **Depends on:** none
- **Size:** Standard
- **Done when:** Shy approves the mock-ups in the thread; the contract text matches them and its contracts test passes.

### WP3 — Android phone audit
- **What it does:** walks the Android phone app against the Dashboard's phone layout screen by screen and records every gap (Home, My List, Libraries, Read, View, Collections, Universe, details, search, downloads, profile switch, settings) and which `/api/v1` connection points are missing.
- **Files likely touched:** a report under `engineering/validation/` (no app code)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a table of gaps, each marked "build" or "decision for Shy", is posted; WP10 is sized from it.

### Wave 1 — TV player contract on the TV web app

### WP4 — TV web player follows the contract (issue 785)
- **What it does:** the profile's skip seconds on Left, Right, Rewind and Fast-forward; the five transport buttons; the tool row with right-hand panels (Episodes, Audio & subtitles, Bookmarks with Everyone | Mine, Playback settings); paused details; end card; Autoplay switch; Back closes the panel first; overlay never hides while a panel is open; Samsung and LG packages rebuilt.
- **Files likely touched:** `clients/tv-web/player.js`, `app.js`, `nav.js`, `styles.css`, `clients/tv-web/platforms/*`, `tests/native/`
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** `Test-TvWebClient.ps1` passes including new key-map and panel tests, and a keyboard-only run at 1920×1080 shows every contract table row.

### Wave 2 — Android TV and Roku

### WP5 — Android TV player follows the contract (issue 783)
- **What it does:** the same player on Android TV (transport, tool row, panels, Bookmarks, end card, Autoplay, key map); music and audiobooks as a full-screen Now playing with Lyrics or Chapters as a side panel; Cast button unchanged where it exists, hidden on Fire TV.
- **Files likely touched:** `clients/android/tv/src/main/`, `clients/android/core/`
- **Depends on:** WP4
- **Size:** Complex
- **Done when:** Gradle build and unit tests pass; the emulator shows every contract row; Cast is absent when the device is a Fire TV.

### WP6 — Roku player follows the contract (issue 782)
- **What it does:** transport, tool row, side panels, Bookmarks Everyone | Mine, paused details, end card, Autoplay, key map including Rewind and Fast-forward, Back order.
- **Files likely touched:** `clients/roku/components/`, `clients/roku/source/`, `tests/native/Test-RokuClient.ps1`
- **Depends on:** WP4 (WP5 may run beside it)
- **Size:** Complex
- **Done when:** `Test-RokuClient.ps1` passes; sideloaded on the real Roku, every contract row shows and Back behaves as the key map says.

### WP7 — Fire TV support note and sideload guide
- **What it does:** confirms which Fire TV models run the Android TV app, hides Cast there, and writes the sideload steps in the public docs; lists unsupported (Vega OS) models.
- **Files likely touched:** `clients/android/README.md`, `tuvima-docs` page (to be confirmed)
- **Depends on:** WP5, Q214
- **Size:** Standard
- **Done when:** the docs page names supported and unsupported models and the APK installs on an emulator image that stands in for Fire OS (a real stick is a Local check if Shy owns one).

### Wave 3 — Android phone parity

### WP8 — Missing `/api/v1` connection points
- **What it does:** adds only the connection points the audit proves are missing, with Contracts types, fixtures and tests.
- **Files likely touched:** `src/MediaEngine.Api/`, `src/MediaEngine.Contracts/`, `tests/`
- **Depends on:** WP3
- **Size:** Standard
- **Done when:** Contracts tests and fixtures pass and each new point has a client reading it.

### WP9 — Phone browse and My List parity
- **What it does:** Home with My List row, My List page and plus/tick everywhere, Libraries (Watch, Read, Listen, View, Collections, Universe), Details, Search, profile switch, as listed by the audit.
- **Files likely touched:** `clients/android/mobile/`, `clients/android/core/`
- **Depends on:** WP3, WP8
- **Size:** Complex
- **Done when:** every audit row marked "build" is closed and a phone-width emulator pass shows no sideways scroll.

### WP10 — Phone playback parity and "Edit in browser"
- **What it does:** video, music and audiobook players follow the phone sections of the contract; the lock-screen card uses the contract table; Edit opens the Dashboard in the phone's browser; downloads and offline playback checked.
- **Files likely touched:** `clients/android/mobile/`, `clients/android/core/`
- **Depends on:** WP9
- **Size:** Complex
- **Done when:** Gradle tests pass and a phone emulator run shows every phone row of the contract; Edit opens the browser on the item's own page.

### Wave 4 — Cars and store packaging

### WP11 — Android Auto list and Now playing card (issue 787)
- **What it does:** the car list in the contract's order, the Now playing card fields and buttons, voice search.
- **Files likely touched:** `clients/android/mobile/` media service, `clients/android/core/`
- **Depends on:** WP10
- **Size:** Standard
- **Done when:** unit tests for the list order and button sets pass and the Desktop Head Unit shows the list and card.

### WP12 — Android packaging and Google Play
- **What it does:** release signing (keys outside the repo), version numbering, APK on GitHub releases, Google Play listing (price $4.99, plain "needs a Library server" text), privacy answers.
- **Files likely touched:** `clients/android/*/build.gradle.kts`, release workflow, store text outside the repo
- **Depends on:** WP10, Q216
- **Size:** Standard
- **Done when:** a signed APK builds in CI with secrets from the CI store and installs on an emulator.

### WP13 — Roku, Samsung and LG store paperwork
- **What it does:** icons, splash, manifest fields, listing text; Roku's sign-in rule (Roku must confirm the browser-approved device flow meets its criterion); Samsung and LG submission packages.
- **Files likely touched:** `clients/roku/manifest`, `clients/roku/images/`, `clients/tv-web/platforms/*`
- **Depends on:** WP4, WP6
- **Size:** Standard
- **Done when:** packages build and pass each store's own validator that can run offline; submission itself is a Local check for Shy.

### Wave 5 — Apple, last

### WP14 — Apple TV player follows the contract (issue 784)
- **What it does:** same player, key map and lock-screen fields on Apple TV.
- **Files likely touched:** `clients/apple/`
- **Depends on:** WP2, Q215 (a Mac)
- **Size:** Complex
- **Done when:** a macOS CI build compiles it and, on a Mac, every contract row shows.

### WP15 — CarPlay list and Now playing card (issue 786)
- **What it does:** the car list and card as in WP11; signed builds need the CarPlay Audio entitlement from Apple.
- **Files likely touched:** `clients/apple/iOS/CarPlaySceneDelegate.swift`, `clients/apple/Shared/`
- **Depends on:** WP14
- **Size:** Standard
- **Done when:** macOS CI build compiles; the simulator CarPlay window shows the list and card.

### WP16 — iPhone and iPad parity and App Store packaging
- **What it does:** brings the iPhone and iPad app to the same screens as the Android phone, with Edit in browser, then signing, privacy answers and the $4.99 listing.
- **Files likely touched:** `clients/apple/iOS/`, `clients/apple/Shared/`, `clients/apple/project.yml`
- **Depends on:** WP10, WP14, Q215
- **Size:** Complex
- **Done when:** a macOS CI build and tests pass and a Mac run shows each phone row.

### WP17 — Public docs
- **What it does:** updates the public docs (`tuvima-docs`: pairing each device, sideloading, Fire TV, store links, cars, the licence) with every wave.
- **Files likely touched:** `tuvima-docs` `content/products/library/` (to be confirmed), `docs/product/native-clients.md`
- **Depends on:** each wave
- **Size:** Standard
- **Done when:** the docs site build and link checks pass and each device has a "get started" page.

## 7. Local checks

Each is appended to `engineering/validation/batch.md` with its tag. The cloud can prove builds, unit tests and the contract tests, not the feel of a remote or a real screen.

**TV web (Windows PC)**
- **L1.** Serve `clients/tv-web`, open at 1920×1080, pair, then use only the keyboard: every contract row appears (transport, tool row, each panel, paused details, end card). Failure: any control hidden instead of dimmed, a panel that never closes, or the overlay hiding while a panel is open.
- **L2.** Tizen and webOS simulators load the same app and play a video. Failure: blank screen or no Back behaviour.

**Android TV and Fire TV (real Android TV or emulator; Fire TV only if owned)**
- **L3.** Emulator TV: every contract row; Cast button present on Android TV and absent when the device reports Fire TV.
- **L4.** If a Fire TV stick is owned: sideload the APK and play a video; note the model.

**Roku (real Roku)**
- **L5.** Sideload; pair; play an episode: transport row, tool row, panels, end card; Rewind and Fast-forward skip by the profile's seconds; Back closes panel then overlay. Text readable from the sofa. Failure: any control that moves or any dead button.

**Android phone (Windows PC emulator, real phone)**
- **L6.** Pixel emulator: Home with My List row, Libraries, Details, Search, downloads at 360, 390 and 412 wide with no sideways scroll; Edit opens the Dashboard in the browser.
- **L7.** Real phone (Wi-Fi, home network): video, music and audiobook play; lock-screen card fields match the contract; Picture-in-Picture works.

**Cars**
- **L8.** Android Auto Desktop Head Unit: list in the contract's order, Now playing card buttons, voice search. A real car check is optional.
- **L9.** CarPlay simulator on a Mac (needs Mac).

**Apple (needs Mac)**
- **L10.** Apple TV and iPhone/iPad: every contract row, offline downloads, lock-screen card.

**Stores**
- **L11.** Shy submits the Roku, Samsung, LG, Google Play and (later) App Store listings and records the outcome; Roku's sign-in rule answer is recorded.

## 8. Risks and open questions

- **Risks:**
  - The Android phone audit may find much more than expected; waves 3 and 4 may grow (we would notice from the audit table in WP3).
  - Roku store approval is out of our hands and blocked until Roku confirms the pairing flow.
  - Apple needs a Mac and a yearly developer account (about $99, approximate; verify), plus an Apple entitlement for signed CarPlay builds that may take time to be granted.
  - Store cuts (about 15 to 30 percent, approximate; verify) mean $4.99 nets roughly $3.50 to $4.25.
  - The phone-editing exception (D7) conflicts with the phone-equals-desktop rule and will be noticed by people who edit on a phone.
  - Newer Fire TV sticks may not run Android apps at all.
  - MPL 2.0 lets someone build a closed app that uses our rules; they cannot copy changes to our files into a closed product. Premium features live behind Tuvima ID on the server, so a fork cannot unlock them.
  - The licence text is not legal advice; a lawyer reads it before the first App Store submission.
- **Open questions:** none. Q210 to Q218 were answered by Shy on 2026-11-22 as recommended and are recorded as D13 to D21.

## 9. Gaps and drift (optional — for as-built or revised specs)

- **Built and matches the spec:** none yet (new spec).
- **Built differently (what changed):** none yet.
- **In the spec but not built yet:** everything in section 6. Today's state: TV web has Connect, Pair, Home, Watch/Read/Listen, Collections, Universe, Search, Details, Player, Profiles, Settings, Find another and Lyrics, but not the shared controls (785); Roku has pairing, rows, details, playback, Profiles and Find another but no skip buttons, Episodes, Bookmarks, Playback settings, end card or music Now playing (782); Android TV has browse, playback, Universe, Find another and Lyrics but no transport buttons, tool row, Bookmarks or end card (783); the Android phone is the most complete app, its gaps unknown until WP3; Android Auto browses a Library root (787); iPhone/iPad has a real first version needing a Mac, CarPlay lists Listen (786); Apple TV is the first pilot (784).

## Plain English summary

This spec puts all the native apps in one plan: TVs, phones and tablets, and cars. The rules for how the player looks and behaves are already written once, so each device just follows them. We first draw the TV panels and get your approval, then build the TV web app (which also covers Samsung and LG), then Android TV and Roku, then bring the Android phone up to match the desktop (except setup, and editing opens the browser for now), then cars and store packaging, and Apple last. TV apps are free, the iPhone and Android apps cost $4.99 once, and the code is open under a licence that the app stores accept. Fire TV works by installing the Android TV app by hand, except for newer sticks that may not run Android apps. Every wave adds its checks to the shared list, tagged by what you need (your PC, the Roku, or a Mac). All nine questions are answered, so the first wave (the licence files, the TV mock-ups and the phone audit) can start.
