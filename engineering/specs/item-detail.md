# Spec: Item Detail page (as built)

> **Status:** Draft (as-built) · **Version:** 4 · **Last updated:** 2026-10-17 · **Rebuilt from code on 2026-10-17** · **Related docs:** [Product overview](../product/overview.md) · [Registry retirement](registry-retirement.md) · [Presentation rules (public)](../../docs/product/presentation-rules.md) · [Dashboard UI architecture (public)](../../docs/architecture/dashboard-ui.md)

> This spec describes what the app does **today**, rebuilt by reading the code. The March 2026 Item Detail spec was not available (no reference copy in the repo), so section 8 lists what looks half-built, inconsistent or unclear instead of comparing against it. Shy decides each gap in the planning Project; nothing here is Locked.

## 1. Goal

Every title, person, series and universe in the library has one consistent page that answers "what is this, can I play it, what's it connected to?" — with the right layout for its kind of media — and is the place an administrator starts a correction.

## 2. Scope

**In scope:** the detail page for 16 kinds of thing: generic work, movie, movie series, TV show, TV season, TV episode, book, book series, audiobook, comic issue, comic series, music album, person, character, universe and collection.

**Out of scope:** photos and personal media (they use the View pages), podcasts (refused), the editor itself (it opens over this page), the player and reader screens, Collections/Universe explorer pages.

## 3. Decisions made (as found in the code)

- **D1. One page for everything.** Every kind uses the same page at `/details/<kind>/<id>`. The older per-type pages (separate movie, TV show and book pages) are gone.
- **D2. TV episodes live inside their show.** An episode opens as its show's page focused on that episode; opening an episode on its own shows "not found".
- **D3. Corrections never leave the page.** "Edit details" opens the shared editor as a window over the page; the page's address doesn't change. Series and curated collections open the collection editor instead.
- **D4. Changing the match refreshes the page immediately.** After a rematch or artwork change, the top of the page (the hero) reloads while the editor stays open.
- **D5. Formats are not separate pages.** If you own a book as both ebook and audiobook, the page shows availability chips ("Audiobook available · 12h") rather than separate entries.
- **D6. Empty tabs are hidden.** A tab with nothing in it (no cast, no tracks, no related items) doesn't appear.
- **D7. Phones are for browsing and playing, not editing.** Edit is hidden on phones; the More menu becomes a bottom sheet with a disabled "Edit on a computer or tablet" row.
- **D8. Items you're not allowed to see don't exist.** If a profile lacks access (library access or content limit), the page says "not found" rather than "forbidden", so nothing leaks.

## 4. UI behaviour

### Layout by kind of media

| Kind | Top of page (hero) |
|---|---|
| Books, comics, generic works | Large cover |
| Movies, TV | Edge-to-edge backdrop; an episode uses its own still |
| Music albums | Blurred cover behind a sharp album image; track list, credits rail and "More by" shelf below |
| Audiobooks | Larger cover; chapter/track table |
| Series and collections | Stack of up to four covers, no backdrop |
| People | Portrait; one continuous page with no tabs |

The hero shows artwork, title or logo, a line under the title (tagline/subtitle, falling back to a short description with a "(more in overview)" link), main credits, fact chips (year, runtime…), genres and a short description.

### Actions

- **One large primary button** that fits the media: Watch / Resume S5 E1, Read / Continue Reading, Listen / Continue Listening, Play + Shuffle (albums), or Open. Once started it shows progress ("45% watched") and a slim progress strip.
- **Round buttons:** My List and Rate (Like / Not for me). Books owned as both ebook and audiobook also show a disabled "Read + Listen — coming soon".
- **More menu:** Restart from beginning; Mark as watched/read (or unwatched/unread); Hide from / Show in Continue; history; for albums Play next / Add to queue / Add to playlist; Copy library link; Edit details (administrators only).

### Tabs by kind

| Kind | Tabs (`Universe` only when a universe link exists) |
|---|---|
| Movie, TV show, TV season | Overview, Cast, Universe, Related, Details |
| TV episode | Overview, Cast, Characters, Universe, Related, Details |
| Book, comic issue, work | Overview, Credits, Universe, Related, Details |
| Audiobook | Overview, Tracks, Details |
| Book, comic or movie series | Overview, Universe, Related, Details |
| Music album, collection | Overview, Details |
| Person | none (one page) |
| Character | Overview, Portrayals, Relationships, Universe, Details |
| Universe | Overview, Characters, Cast, Relationships, Details |

- **Overview:** description with its source, a short credits preview, and the series/episode/works list.
- **Details:** type, library status, identity status, source links.
- **Related:** "More from your library", up to 24 items.
- **Universe:** relationship chips plus every media group again.
- A diagnostic **Registry** tab also exists but is being retired — see [Registry retirement](registry-retirement.md).

### Who sees what

| Person | Sees |
|---|---|
| Household administrator with admin mode unlocked, or an app with metadata-write permission | Everything, plus **Edit details** (greyed out while ingestion is still updating the item) |
| Any profile | Personal actions (progress, mark watched, history, restart) for the active profile; items outside their access appear as "not found" |
| Phone users | No Edit; More opens as a bottom sheet; track lists become cards |

### States

| Situation | What the person sees |
|---|---|
| Normal | As above |
| Empty | "No overview is available yet.", "No biography…", "No linked people…", "No related library items…", "No owned library items are available for this section."; missing artwork falls back to a generated identity image |
| Loading | First visit: a skeleton hero announced as "Loading details". Moving between items keeps the old page visible, dimmed, with a thin progress bar |
| Error | Every failure — not found, not allowed, server error, Engine unreachable — shows the same "Detail page not found / could not be loaded from your library", with no retry |
| Partly ready | Edit greyed out while the item is still processing; credit cards flagged while people enrichment is pending |
| Live updates | None — the page refreshes only after an edit or a personal-status change, not while ingestion or enrichment runs |

## 5. Data / model changes

None — this is an as-built description. The page reads the Engine's single "detail" action, which assembles title, artwork, credits, progress, formats and relationships from the library's stored metadata (mostly the resolved "canonical" values), plus the active profile's personal state (saved items, progress, reactions).

## 6. Work packets (candidates — pending Shy's decisions in section 8)

These are proposals only. Each maps to a gap in section 8; Shy decides which to keep, then the spec can be revised and Locked.

### WP1 — Distinct error states
- **What it does:** show different messages for "not found", "not allowed", and "Engine unreachable (retry)" instead of one "not found" for everything (G7).
- **Files likely touched:** `src/MediaEngine.Web/Services/Integration/EngineApiClient.Details.cs:116-157`, `src/MediaEngine.Web/Components/Details/DetailRouteHost.razor:37-45`, tests in `tests/MediaEngine.Web.Tests/`
- **Depends on:** none · **Size:** Standard
- **Done when:** a component test renders each failure kind with its own message; a retry button appears only for connection failures. Profile-access failures still read as "not found" (D8).

### WP2 — Remove dead tab panels and unused pieces
- **What it does:** delete panels for tab keys the Engine never sends (`episodes`, `items`, `issues`, `media`, `works`, `albums`, `timeline`, `sync`), the unused Sync panel, the always-false primary-module flag, duplicate refresh methods and unused parameters (G3, G4).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailPage.razor:79-147,186,560-586`, `OptionalSyncPanel.razor`, `src/MediaEngine.Api/Services/Details/DetailComposerService.cs:64`
- **Depends on:** decision on G1/G3 (keep Series tab or not) · **Size:** Standard
- **Done when:** build and `tests/MediaEngine.Web.Tests` pass; no panel exists for an unsent tab key.

### WP3 — Buttons always go somewhere
- **What it does:** hide or give a destination to actions with no link (character "View Appearances", generic "Open") (G5).
- **Files likely touched:** `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.Entities.cs:276`, `src/MediaEngine.Web/Components/Details/DetailRouteHost.razor:121-127`
- **Depends on:** none · **Size:** Standard
- **Done when:** a test asserts every action in the view model has a destination or is not rendered.

### WP4 — Keep the chosen tab in the address
- **What it does:** remember the selected tab in the URL so reload/back returns to it (G8).
- **Files likely touched:** `src/MediaEngine.Web/Services/Navigation/DetailTabNavigation.cs`, `DetailPage.razor`
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** reloading `/details/movie/<id>?tab=cast` opens Cast.

### WP5 — Line under the title for every kind
- **What it does:** apply the secondary-title rules to TV shows, series and albums, and stop TV episodes falling back to the description (G10).
- **Files likely touched:** `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.Readers.cs:621-664`, `…Work.cs:241`, `docs/reference/secondary-title-text.md`
- **Depends on:** Shy's decision on G10 · **Size:** Standard
- **Done when:** composition tests cover each kind against `secondary-title-text.md`.

### WP6 — Editor opens on a tab that exists
- **What it does:** stop asking the editor for `episodes`/`tracks`/`media` start tabs it doesn't have; map to Details deliberately (G11).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailPage.razor`, `src/MediaEngine.Web/Components/MediaEditor/MediaEditorTabState.cs:47-64`
- **Depends on:** none · **Size:** Mechanical
- **Done when:** no detail-page call requests an editor tab outside Details, Artwork, Match & Identity, History.

### WP7 — Docs and tests match the code
- **What it does:** update the public docs to the real tab sets (audiobook Tracks tab, no Series tab unless kept), remove stale `/book/{id}` and `/person/{id}` routes from `docs/architecture/architecture-summary.md:79` and tests (G1, G2, G12).
- **Files likely touched:** `docs/architecture/dashboard-ui.md:92,163`, `docs/architecture/architecture-summary.md:79`, `tests/MediaEngine.Web.Tests/PosterItemViewModelTests.cs:12`
- **Depends on:** decisions on G1/G2 · **Size:** Mechanical
- **Done when:** docs build passes and no doc or test mentions removed routes.

### WP8 — Standard breakpoints on Details
- **What it does:** move Details CSS from 720/900/960/1100 px to the official 600/840/1180 set (G15).
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/*.razor.css`
- **Depends on:** none · **Size:** Standard (needs visual checks at phone, tablet, desktop)
- **Done when:** `ResponsiveBreakpointGuardrailTests` passes without Details exceptions and paired screenshots show no regressions.

## 7. Risks and open questions

- **Risks:** WP2 removes code paths that a half-finished feature (Read + Listen sync) may want later — removing them is cheap to undo from history. WP8 can shift layouts at in-between widths; needs visual proof.
- **Q1.** Should items in a series get a **Series** tab (docs say yes, code says no)? Recommendation: no tab — the Overview already lists the series; update the docs.
- **Q2.** Is the audiobook **Tracks** tab intended? Recommendation: keep it; update the docs.
- **Q3.** Should the page **update live** while ingestion/enrichment is still filling in an item? Recommendation: yes, but as its own spec (it touches the Engine's live events).
- **Q4.** **TV season pages:** nothing links to them today. Keep, or route seasons into the show page like episodes? Recommendation: route into the show page.
- **Q5.** **Read + Listen** (synced ebook/audiobook): finish it, or remove the "coming soon" stub until a spec exists? Recommendation: remove the stub for now.
- **Q6.** Should the Universe tab repeat every media group (including recommendations)? Recommendation: no — show relationships only.

## 8. Gaps and drift

No reference copy of the March 2026 spec was found (`engineering/specs/reference/` and `docs/specs/reference/` do not exist), so the "matches / differs / not built" comparison is replaced by the list below, as agreed.

**Half-built, inconsistent, or unclear**
- **G1.** Docs say series items open on a **Series** tab first; the code never creates one (the page has an unused panel for it).
- **G2.** Docs say audiobooks have Overview + Details like albums; the code adds a **Tracks** tab.
- **G3.** The page has panels for eight tab kinds the Engine never sends; the **Sync** tab and the **Read + Listen** button are not built (the button shows "coming soon").
- **G4.** Dead code: a flag that is always off, "Back to Watch/Read/Listen" labels that are built but never shown, two identical refresh methods, unused parameters.
- **G5.** Some buttons have nowhere to go: character "View Appearances" and generic "Open".
- **G6.** The **Registry** diagnostics tab is switched on by an address flag (`?context=admin`), not by role — anyone who can read the library can see it by typing the address. Being retired; see [Registry retirement](registry-retirement.md).
- **G7.** All failures look like "not found"; there is no retry when the Engine is briefly unreachable.
- **G8.** The selected tab is not kept on reload (the helper that would do it is computed but unused).
- **G9.** The Universe tab repeats every media group again, including recommendations — unclear if intended.
- **G10.** The line under the title is only set for book-like works; TV shows, series and albums never get one, and TV episodes fall back to the description although the rules say "no tagline".
- **G11.** The page asks the editor to open on tabs it doesn't have (episodes, tracks, media); the editor silently opens Details.
- **G12.** Stale references: the Dashboard code map and architecture summary still listed removed `/book/{id}` and `/person/{id}` pages (the Dashboard map is fixed as of 2026-10-17), and a test still builds `/book/{id}` links.
- **G13.** TV **season** pages: no route opens one; the Engine would build it as a plain work — unclear whether it works.
- **G14.** The Dashboard sends a `profileId` the Engine ignores (it always uses the signed-in profile) — harmless but misleading.
- **G15.** Details CSS mixes the official 840px breakpoint with off-standard ones (720/721, 900, 960, 1100 px) where the rules allow only 600/840/1180 (plus the 1023px touch query).
- **G16.** No live refresh while an item is still being processed.
