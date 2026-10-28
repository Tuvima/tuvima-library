# Spec: Links to outside sources

> **Status:** In progress · **Version:** 2 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Presentation rules](../../docs/product/presentation-rules.md) · Bug log entries F39, F59 (follow-ups.md)

## 1. Goal

Every "open this on TheTVDB / Wikidata / MusicBrainz…" link and every linked person on a details page opens the right page, every time. A link that cannot work is never shown. Today at least TheTVDB links land on an error page, and the people listed under "Linked identities" (for example Amy Lee and Ben Moody on Evanescence) do not open.

## 2. Scope

**In scope**
- One shared place that builds every outside link (TheTVDB series, season, episode and person; TMDB; MusicBrainz; Wikidata; Comic Vine), so a wrong web address is fixed once.
- Opening a linked person (band members, "member of", pen names) from the Linked identities list.
- Person pages gain outside links: Wikidata, Wikipedia, MusicBrainz artist and TheTVDB person where we hold the identifier (Q3).
- Linked people (band members, groups, pen names) are matched to Wikidata and Wikipedia so their light page shows a full match (Q2).
- A test that every configured link produces a well-formed address, and a quick check against the live sites.

**Out of scope**
- New kinds of links or new providers.
- Community plugins (parked).

## 3. Decisions made

- **D1.** Link addresses live in one place, not copied into five code files. Reason: Maintenance. The same TheTVDB address is built in the provider settings file, the match screens, the artwork picker and the show and person pages, so one wrong pattern breaks them all.
- **D2.** A link is shown only if we are confident it opens. If the identifier is missing or malformed, the link is hidden, never shown broken. Reason: Reliability and ease of use.
- **D3.** TheTVDB links use TheTVDB's own redirect-by-number address if WP1 shows it works; episodes fall back to a stored short name only if it does not (Shy, Q1 A).
- **D4.** A linked person with nothing in the Library still opens a light page, and the Engine tries to match them on Wikidata and Wikipedia (many, like Amy Lee, will match fully) so the page has a biography, picture and links (Shy, Q2 A).
- **D5.** Person pages show outside links now, not later (Shy, Q3 A).
- **D6.** Links open in a new tab and never navigate away from the Library (as today).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | "Sources" panel lists TheTVDB, TMDB, Wikidata etc.; each opens the matching page in a new tab. |
| Linked identities | Tapping a band member or "member of" group opens that person's page inside the Library. |
| Linked person we hold little about | The page still opens, showing their name, picture and links, with a quiet note that no items in your Library credit them. |
| Missing identifier | The link is simply not shown. |
| Error (outside site down) | Nothing we can control; the link still opens the right address. |

Phone, tablet, desktop and TV behave the same.

## 5. Data / model changes

None required for the base fix. Optionally (Q1) store TheTVDB's short name for a show when it is matched, so we can link to its exact page. Pre-release: a re-match or reset is acceptable.

## 6. Work packets

### WP1 — Find out exactly which addresses fail
- **What it does:** Check each link pattern against the live sites (TheTVDB series, season, episode, person; MusicBrainz; Comic Vine) and record which resolve. Done locally, because the cloud cannot reach those sites.
- **Files likely touched:** none (findings into this spec).
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a table of pattern → works / fails is added to section 9.

### WP2 — One shared link builder
- **What it does:** Move all outside-link patterns into one builder with a test per provider; make the settings file, the match screens, the artwork picker and the show/person pages use it.
- **Files likely touched:** `config/providers/tvdb.json` (lines 905-907), `src/MediaEngine.Api/Services/Details/Internals/ProviderSourceLinkResolver.cs`, `DetailCompositionOrchestrator.Collections.cs:489`, `DetailCompositionOrchestrator.Entities.cs:125`, `MetadataEndpoints.TvdbMatching.cs:159,211`, `MetadataEndpoints.ProviderArtworkPicker.cs:91`, new tests in `tests/MediaEngine.Api.Tests`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a test fails if any configured link expands to a malformed address; no code builds a TheTVDB address on its own.

### WP3 — Fix TheTVDB addresses
- **What it does:** Replace the number-only patterns (`/series/{id}`, `/seasons/{id}`, `/episodes/{id}`, `/people/{id}`) with the form WP1 shows to work (see Q1).
- **Files likely touched:** as WP2, plus the matched-show store if Q1 = slug.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** the Continental's series, a season, an episode and a cast member open the right TheTVDB page.

### WP4 — Make linked people open
- **What it does:** Let a person page open even when no owned item credits them, make sure linked members are looked up on Wikidata and Wikipedia so the page is filled in, add the person outside links (D5), and confirm the cards point to the right address.
- **Files likely touched:** `DetailCompositionOrchestrator.Entities.cs:82-89` (returns "not found" when a person has no credits), `OverviewTab.razor`, `PersonCreditCard.razor`, tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test opens a band member with no credits and gets a page; Evanescence's members open.

### WP5 — Review and docs
- **What it does:** Independent review against this spec; short note in the provider docs on how to add a link pattern.
- **Depends on:** WP3, WP4
- **Size:** Mechanical
- **Done when:** reviewer has no must-fix items.

## 7. Local checks

- **L1.** Real library: open The Continental; click TheTVDB series, a season, an episode; each opens the right page. Failure: a TheTVDB error or the wrong show.
- **L2.** Open Evanescence (a group with members); click Amy Lee and Ben Moody; each opens a page. Failure: blank page, "not found" or nothing happens.
- **L3.** Click one Wikidata, TMDB and MusicBrainz link on a movie, a show and an album.

## 8. Risks and open questions

- **Risks:** TheTVDB may change its address scheme again; the test in WP2 catches malformed addresses but not a site change, so L1 stays on the checklist.

Root causes found (from reading the code; not yet confirmed on real media):

1. **TheTVDB (F39).** Every TheTVDB link is built as `thetvdb.com/series/{number}`, `/seasons/{number}`, `/episodes/{number}`, `/people/{number}`. TheTVDB's website identifies shows by a short name (for example `/series/the-continental`), so number-only addresses are unreliable and for episodes and seasons are very likely wrong. Nothing in the code stores that short name. I could not test the live site from the cloud (WP1 does).
2. **Linked identities (F59).** The cards link to the right Library address, but the page behind it gives "not found" for any person that no owned item credits (`DetailCompositionOrchestrator.Entities.cs:82-89`), and band members found from Wikidata typically have no owned credits. This is the most likely reason none of the Evanescence links open; it is a reading of the code, not yet reproduced.
3. **Link patterns are copied in five places**, so the same mistake appears everywhere and no test checks any of them.

- **Q1-Q3:** answered by Shy on 2026-10-27 (1A, 2A with Wikidata and Wikipedia matching, 3A); recorded as D3-D5. No open questions remain, so this locked by Shy 2026-10-27.

## 9. Gaps and drift

- **WP1 (live address check) is parked for SHY-DESKTOP** (Local check L1 in `engineering/validation/batch.md`); the cloud cannot reach thetvdb.com. Until it runs, the build uses TheTVDB's redirect-by-number form (`thetvdb.com/dereferrer/{series|season|episode|people}/{number}`, D3's first choice). Fill the pattern → works / fails table here once tested.
- **Linked-member matching (D4) already existed:** group members found from Wikidata are created with their Wikidata id and queued for full hydration (`MetadataHarvestingService.ResolveGroupMembersAsync`), so they get Wikipedia text and pictures. No new matching code was needed; the fix was the page refusing to open (WP4).

## Plain English summary

TheTVDB links are broken because we link by a number, while TheTVDB's site wants a show's short name; fixing it means building links in one shared place and using an address that works. The band-member links likely fail because the page they lead to refuses to open for people none of your files credit; we'll let it open as a light page. The spec has five small pieces, plus three quick checks on your PC.
