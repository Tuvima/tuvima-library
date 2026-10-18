# Spec: Item Detail page

> **Status:** In progress · **Version:** 5 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [Registry retirement](registry-retirement.md) · [Presentation rules (public)](../../docs/product/presentation-rules.md) · [Dashboard UI architecture (public)](../../docs/architecture/dashboard-ui.md)

> Version 5 replaces the "as built" v4. It records what the page does today (including PR 222, merged, and PR 223 Editing Mode, assumed merged before packets start) and every decision Shy made on 2026-10-17 about what to change, remove or leave to other plans. Anything labelled "to be confirmed by Claude Code" is a fact I could not check from the planning side.

## 1. Goal

Every title, person, series and collection in the library has one consistent page that answers "what is this, can I play it, who is in it?" with the right layout for its kind of media. The page opens over whatever you were looking at and closes with Back. It is calm and simple: no dead buttons, no hidden half-built features, no recommendations (this is your own media).

## 2. Scope

**In scope:** the detail page for these kinds: generic work, movie, movie series, TV show (a season opens inside its show), TV episode, book, book series, audiobook, comic issue, comic series, music album, person, universe (as a collection item) and collection.

**Out of scope**
- Photos and personal media (they use the View pages) and podcasts (refused).
- Characters, places and anything that links a character or place across a book, film or audiobook. These return only through the future Universe plan; v5 removes them.
- Live updates while an item is still being processed (Q3: its own spec).
- How Editing Mode itself works (PR 223; an as-built Editing Mode spec is a follow-up).
- Building the overlay host (stage U5 of `plans/unified-details`); this spec states the behaviour it must have.
- Player and reader screens, Collections and Universe explorer pages.

## 3. Decisions made

Carried over from v4 (still true unless amended):
- **D1 (amended).** One page for everything, shown as an overlay over the page you came from (see D12). Per-type pages stay gone.
- **D2 (amended).** TV episodes and TV seasons live inside their show. Opening either shows the show's page with that season or episode focused. Opening an episode or season on its own never shows "not found".
- **D3 (amended).** Corrections happen on the page itself in Editing Mode (desktop only, PR 223): pencils beside fields, a side panel for artwork and matching. The floating editor window remains only for Needs Review, browse rows, people, series and curated collections. This replaces v4's "editor is a window over the page" rule.
- **D4.** Changing the match refreshes the top of the page immediately.
- **D5.** Formats are not separate pages; availability chips show the other formats.
- **D6.** Empty tabs are hidden.
- **D7 (amended).** Phones and tablets are for browsing and playing only. No Edit button, pencils or side panel. Phones keep a More sheet with a disabled "Edit on a computer" row.
- **D8.** Items you may not see do not exist ("not found"), so nothing leaks.

New on 2026-10-17 (Shy):
- **D9. Series tab: no.** Items in a series do not get a Series tab; the Overview already lists the series. Fix the docs. (was Q1)
- **D10. Audiobook Tracks tab: keep.** Fix the docs. (was Q2)
- **D11. Universe and Related tabs removed entirely** from every detail page, on every device. This reverses the earlier "hide behind one switch" (PR 222): the switch and the code behind the tabs are deleted, with docs and tests. "More from your library" recommendations are not wanted in a personal library. Universe pages stay reachable as collection items; the Universe tab comes back only through the separate Universe plan. (Q6, G9)
- **D12. Overlay behaviour.** A title opens as an overlay over the current page. Back (and the close button and Esc) dismisses the overlay and returns to the page underneath. Reloading while an overlay is open reopens that same overlay over Home, so you keep your place. There is no shareable link: "Copy library link" is removed, and in-app links use the item's ID. (The overlay build is U5 in `plans/unified-details`; its "shared links" gate is dropped.)
- **D13. Seasons route into the show page**, with that season selected (mockup approved 2026-10-17). (Q4, G13)
- **D14. Read + Listen stub removed** until a spec exists. (Q5)
- **D15. Live updates** are a separate spec. (Q3, G16)
- **D16. Characters removed entirely.** The Character page kind, Characters, Portrayals and Relationships tabs, the episode Characters tab, character chips and the "View Appearances" button are removed and deprecated, including code and references. Character and place links across formats belong to the Universe plan. Universe pages keep only Overview, the owned items in them and Details until then. "Cast" (the people on a movie, show or episode) stays; it links to person pages. (G5, G9)
- **D17. No button without a destination.** The generic "Open" fallback is removed. Instead, make the situation impossible: a collection cannot be saved empty (already true; confirm that taking out the last item is also refused). A page with no playable item shows no main button. A test asserts every action has a destination. (G5)
- **D18. Clear errors.** "Not found", "not allowed" and "Engine unreachable" read differently; only the last offers Retry. Profile-access failures still read "not found" (D8). (G7)
- **D19. The chosen tab is kept** in the page's navigation state, so reload and Back return to it. (G8)
- **D20. Line under the title** follows the written secondary-title rules for every kind; TV episodes get no line instead of falling back to the description. (G10)
- **D21. No Registry tab on the detail page and no `?context=admin` address flag.** The Registry retirement spec owns the removal; this spec only states the end result. (G6)
- **D22. Editor start tabs** (G11) are dropped: superseded by Editing Mode.
- **D23. Remove the unused profile ID** the Dashboard sends. (G14)
- **D24. Standard breakpoints** (600, 840, 1180 px) on the Details pages. (G15)
- **D25. WP10 carries Registry retirement WP1** (Shy, 2026-10-17): the Registry tab and `?context=admin` removal, and moving identity status and artwork source into the editor's Match & Identity section, are built once, here, right after WP2.

Answers to the WP1 report (Shy, 2026-10-17):
- **D26. A curated collection whose last item leaves the library is deleted automatically.** The editor still refuses to save or empty a collection to zero items (D17). Deleting a library item that sits in a manually curated collection must warn first; automatic collections need no warning because they follow the canonical metadata. The app has no delete action for library items today; the Engine's unused delete actions are removed by Registry retirement WP3, and the warning belongs to a future "Delete from library" spec that rebuilds deletion (Shy, 2026-10-17).
- **D27. Character-only Engine actions are removed** (character portraits, a universe's character list, a person's character roles), with their Dashboard wrappers and tests. Actions shared with Universe Explore and the entity editor stay.
- **D28. Person pages drop the separate "characters played" section;** role names stay on their credits.
- **D29. "Copy library link" on the Home hero and the Listen page's own Related tab are removed too**, matching D11 and D12.
- **D30. Series, collections, universes and people have no line under the title** (built in WP9; recorded in `docs/reference/secondary-title-text.md`).
- **D31. The Details tab drops its "Identity" status line** (Shy, 2026-10-17); identity status lives in the editor's Match section for server administrators (Registry D4).
- **D32. Playlists are exempt from D17 and D26** (Shy, 2026-10-17): only manually curated collections must never be empty and are removed when their last item leaves the library. Playlists may be empty and are never removed automatically.

## 4. UI behaviour

### Layout by kind of media

| Kind | Top of page (hero) |
|---|---|
| Books, comics, generic works | Large cover |
| Movies, TV | Edge-to-edge backdrop; an episode uses its own still |
| Music albums | Blurred cover behind a sharp album image; track list and credits below |
| Audiobooks | Larger cover; chapter/track table |
| Series, collections, universes | Stack of up to four covers, no backdrop |
| People | Portrait; one continuous page with no tabs |

The hero shows artwork, title or logo, a line under the title (D20), main credits, fact chips, genres and a short description.

### Overlay

The page opens over the one you came from (phone: bottom sheet; larger screens: a large window). Back, the close button and Esc dismiss it. Seasons and episodes appear inside their show's overlay with a season chip row and the episode list (approved mockup, 2026-10-17).

### Actions

- **One large primary button** that fits the media: Watch / Resume S5 E1, Read / Continue Reading, Listen / Continue Listening, Play + Shuffle (albums). When nothing is playable there is no main button.
- **Round buttons:** My List and Rate. No "Read + Listen" stub.
- **More menu:** Restart from beginning; Mark as watched/read (or unwatched/unread); Hide from / Show in Continue; history; for albums Play next / Add to queue / Add to playlist; Edit details (desktop administrators, enters Editing Mode). No "Copy library link".

### Tabs by kind

| Kind | Tabs |
|---|---|
| Movie, TV show (and its seasons) | Overview, Cast, Details |
| TV episode | Overview, Cast, Details |
| Book, comic issue, work | Overview, Credits, Details |
| Audiobook | Overview, Tracks, Details |
| Book, comic, movie series; music album; collection | Overview, Details |
| Universe | Overview, Details (owned items listed on Overview) |
| Person | none (one page) |

- **Overview:** description with its source, a short credits preview, and the series/episode/works list.
- **Details:** file facts first (one block per file: properties, codecs, length, resolution, audio and subtitle tracks, container, added date, where stored), then a read-only History. File path and History show only to people already offered Edit, History on desktop-class screens only. (as built, PR 222)

### Who sees what

| Person | Sees |
|---|---|
| Administrator on a desktop with permission | Everything, plus Edit details (Editing Mode); greyed out while the item is still processing |
| Any profile | Personal actions (progress, mark watched, history, restart) for the active profile; items outside their access appear as "not found" |
| Phone or tablet user | No Edit, pencils or panel; More opens as a sheet |

### States

| Situation | What the person sees |
|---|---|
| Normal | As above |
| Empty | "No overview is available yet.", "No biography…", "No linked people…"; missing artwork falls back to a generated image. Empty collections cannot exist (D17) |
| Loading | First visit: skeleton hero ("Loading details"). Moving between items keeps the old page visible, dimmed, with a thin progress bar |
| Error | Three messages: not found, not allowed (reads as not found for access limits), and Engine unreachable with a Retry button (D18) |
| Partly ready | Edit greyed out while processing; credit cards flagged while people enrichment is pending |
| Live updates | None for now (D15) |

## 5. Data / model changes

None to stored data. Removals only: the Engine stops building the Universe and Related tabs, Characters, Portrayals and Relationships content, the Sync/Read+Listen pieces and the unused profile ID. Pre-release, so no migration or compatibility shim is needed.

## 6. Work packets

Claude Code confirms file lists; paths below come from v4 and PRs 222 and 223 and may have moved.

### WP1 — Confirm what exists before removing it
- **What it does:** read-only check, reported back before WP2 to WP5 start: (a) every place a Character page or character link is reachable; (b) which pages ever show the generic "Open" button; (c) whether the collection editor lets a saved collection be emptied; (d) every reference to the Universe/Related switch.
- **Files likely touched:** none (report only)
- **Depends on:** none · **Size:** Mechanical
- **Done when:** a short report with `file:line` anchors exists and Shy has seen the answers to (a) to (c).

### WP2 — Delete Universe/Related tabs and dead pieces
- **What it does:** remove `DetailTabVisibility.ShowUniverseAndRelatedTabs` and `DetailTabCatalog` entries for Universe and Related, the Related recommendation service and shelf, the Universe tab panel, panels for tab keys the Engine never sends (`episodes`, `items`, `issues`, `media`, `works`, `albums`, `timeline`, `sync`), the Sync panel, the "Read + Listen" stub, the always-false primary-module flag, duplicate refresh methods and unused parameters (D9, D11, D14).
- **Files likely touched:** `src/MediaEngine.Contracts/` (tab visibility), `src/MediaEngine.Api/Services/Details/` (tab catalog, recommendation service, composer), `src/MediaEngine.Web/Components/Details/DetailPage.razor`, `OptionalSyncPanel.razor`, `DetailTabNavigation.cs`; to be confirmed by Claude Code
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** build and Dashboard/Contracts tests pass; no code or doc mentions the switch, a Related tab or a panel for an unsent tab key; contract fixtures are refreshed with `tools/Update-ContractFixtures.ps1`.

### WP3 — Remove characters from the detail pages
- **What it does:** remove the Character page kind, Characters, Portrayals and Relationships tabs, the episode Characters tab, character chips and the "View Appearances" action, plus the Universe page's Characters, Cast and Relationships groups (D16). Cast for movies, shows and episodes stays.
- **Files likely touched:** `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.Entities.cs` (character and universe builders), `…Readers.cs`, `DetailRouteHost.razor`, contracts for detail entity types; to be confirmed by Claude Code
- **Depends on:** WP1, WP2 · **Size:** Complex
- **Done when:** no route, tab or action refers to characters on detail pages; a test lists the exact tab set per kind (section 4); docs updated.

### WP4 — Seasons open inside the show page
- **What it does:** a season route resolves to its show's page with that season selected, as episodes already do (D2, D13).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailRouteHost.razor`, detail request routing, show composition; to be confirmed by Claude Code
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** opening a season shows its show with that season chip selected and its episodes listed, at phone, tablet and desktop width.

### WP5 — Every button goes somewhere
- **What it does:** remove the generic "Open" fallback; pages with nothing playable show no main button; make sure an empty collection cannot be saved or emptied; add the test that every action has a destination (D17).
- **Files likely touched:** `…DetailCompositionOrchestrator.ViewModelBuilder.cs` (primary-action fallback), `…Work.cs`, `DetailRouteHost.razor:121-127`, collection editor and its Engine route; to be confirmed by Claude Code
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** a test asserts no action in any view model lacks a destination; saving or emptying a collection to zero items is refused with a clear message.

### WP6 — Overlay-ready navigation (Back, no link copy)
- **What it does:** remove "Copy library link"; in-app links use item IDs; the detail overlay uses a navigation entry so Back closes it, and reload reopens the same overlay over Home (D12). The overlay host itself is built in stage U5 of `plans/unified-details`; this packet only removes the old link behaviour and states the requirement there.
- **Files likely touched:** More-menu action builder, `DetailPage.razor`, navigation helpers; to be confirmed by Claude Code
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** More menu has no link-copy item; Back closes an open title and lands on the previous page (once the overlay host exists); docs say there are no shareable links.

### WP7 — Keep the chosen tab
- **What it does:** remember the selected tab in navigation state so reload and Back return to it (D19).
- **Files likely touched:** `src/MediaEngine.Web/Services/Navigation/DetailTabNavigation.cs`, `DetailPage.razor`
- **Depends on:** WP2, WP6 · **Size:** Standard
- **Done when:** reloading with Cast open reopens Cast.

### WP8 — Distinct error states
- **What it does:** three messages, Retry only for connection failures (D18).
- **Files likely touched:** `src/MediaEngine.Web/Services/Integration/EngineApiClient.Details.cs:116-157`, `DetailRouteHost.razor:37-45`, tests in `tests/MediaEngine.Web.Tests/`
- **Depends on:** none · **Size:** Standard
- **Done when:** a component test renders each failure kind with its own message; profile-access failures still read "not found".

### WP9 — Line under the title for every kind
- **What it does:** apply the secondary-title rules to TV shows, series and albums; TV episodes get no line (D20).
- **Files likely touched:** `…DetailCompositionOrchestrator.Readers.cs:621-664`, `…Work.cs:241`, `docs/reference/secondary-title-text.md`
- **Depends on:** none · **Size:** Standard
- **Done when:** composition tests cover each kind against `secondary-title-text.md`.

### WP10 — Registry tab and address flag gone from Details
- **What it does:** make sure the detail page has no Registry tab and ignores `?context=admin` (D21). Whichever lands first (this or the Registry retirement work) does it; the other verifies.
- **Files likely touched:** `DetailPage.razor`, tab catalog; to be confirmed by Claude Code
- **Depends on:** coordinate with `registry-retirement.md` · **Size:** Standard
- **Done when:** a test shows no Registry tab for any role and no effect from the flag.

### WP11 — Drop the unused profile ID
- **What it does:** stop sending `profileId` from the detail request (D23).
- **Files likely touched:** `DetailRouteHost.razor`, `EngineApiClient.Details.cs`
- **Depends on:** none · **Size:** Mechanical
- **Done when:** build and tests pass; the detail request has no profile parameter.

### WP12 — Standard breakpoints
- **What it does:** move Details CSS from 720/900/960/1100 px to 600/840/1180 (D24).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/*.razor.css`
- **Depends on:** WP2, WP3 (so removed panels are not restyled) · **Size:** Standard (visual checks at phone, tablet, desktop)
- **Done when:** `ResponsiveBreakpointGuardrailTests` passes without Details exceptions and paired screenshots show no regressions.

### WP13 — Docs and tests match the code
- **What it does:** update public docs and tests to the real tab sets (audiobook Tracks, no Series tab, no Universe, Related or Characters), remove stale `/book/{id}` and `/person/{id}` routes, remove "copy library link" and "modal editor over the page" statements that Editing Mode replaced (D9, D10, D3).
- **Files likely touched:** `docs/architecture/dashboard-ui.md:92,163`, `docs/architecture/architecture-summary.md:79`, `docs/product/presentation-rules.md`, `tests/MediaEngine.Web.Tests/PosterItemViewModelTests.cs:12`
- **Depends on:** WP2, WP3 · **Size:** Mechanical
- **Done when:** docs build passes; no doc or test mentions removed routes, tabs or the link-copy action.

## 7. Risks and open questions

- **Risks:** WP2 and WP3 delete real code: if the Universe plan wants characters back, it starts from history rather than from the old code, which is the intent. WP12 can shift layouts at in-between widths. PR 223 (Editing Mode) is open with merge conflicts today; D3, D7 and the Edit button depend on it merging first.
- **Open questions:** none. Follow-ups outside this spec: an as-built Editing Mode spec; a Universe plan (characters, places, cross-format links, the Universe tab); a live-updates spec.

## 8. Gaps and drift

Each item records Shy's decision on 2026-10-17.

- **G1 Series tab:** change spec/docs to match the code; no Series tab (D9).
- **G2 Audiobook Tracks:** keep the tab; fix docs (D10).
- **G3 Unused panels, Sync and Read + Listen:** drop (D14, WP2).
- **G4 Dead code:** drop (WP2).
- **G5 Buttons with nowhere to go:** characters removed (D16); "Open" fallback removed and the cause prevented (D17).
- **G6 Registry tab by address flag:** gone from Details; Registry retirement owns it (D21, WP10).
- **G7 All errors look the same:** change code (D18, WP8).
- **G8 Tab not kept:** change code (D19, WP7).
- **G9 Universe tab repeats groups:** the tab is removed (D11).
- **G10 Line under the title:** change code (D20, WP9).
- **G11 Editor start tabs:** drop; superseded by Editing Mode (D22).
- **G12 Stale references:** change docs and tests (WP13).
- **G13 Season pages:** route into the show page (D13, WP4).
- **G14 Unused profile ID:** remove (D23, WP11).
- **G15 Off-standard breakpoints:** change code (D24, WP12).
- **G16 No live refresh:** separate spec (D15).
- **Drift from other work:** PR 222 hid Universe and Related behind a switch (now deleted, D11); PR 223 replaces the modal editor on the page (D3).

## Plain English summary

The item page keeps its good parts (one consistent page per title, personal actions, file facts). It loses everything half-built: no Universe, Related or Characters tabs, no recommendations, no "coming soon" or do-nothing buttons, no link copying. A season opens inside its show. Titles open over the page you were on and Back closes them. Errors say what really went wrong, and the page remembers its tab. Characters, places and cross-format links wait for the Universe plan.
