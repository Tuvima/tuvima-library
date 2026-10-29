# Provider settings on plugin pages and an editable Ingestion Flow

> **Status:** Built, awaiting Local checks (2026-10-29) · **Version:** 1 · **Last updated:** 2026-10-29 · **Related docs:** Plugins and integrations screens (Locked, WP3 detail page), Plugin architecture (WP20 metadata sources from plugins), Settings flat menu (Draft, ships a read-only Ingestion Flow tab), follow-ups F85 and F89

## Goal
Everything about a metadata provider (Apple, TMDB, TVDB, ComicVine, MusicBrainz and any new one) lives on that provider's own plugin page, and the Ingestion Flow tab lets an administrator decide which providers run for each media type, in what order, and in what role. A newly added provider plugin can be put to work without editing files.

## What is true today (read from the code)
- The flow is `config/pipelines.json`: per media type a strategy (Cascade, Waterfall, Sequential), an ordered provider list (rank, name, purpose identity or enrichment) and per-field priorities. Books, Audiobooks, Movies, TV and Comics have one provider each; Music has two.
- The Engine already has `GET/PUT /settings/pipelines`, so saving an edited flow needs no new storage.
- The Metadata Providers page (`MetadataSettingsPage`) shows the flow read-only under "Used During Ingestion" and holds each provider's settings (enabled, language strategy, Test connection, advanced connection).
- Plugin-supplied metadata sources are written to `config/providers/` and only read at Engine start, so they never appear in a media type's pipeline.

## Decisions made (Shy, 2026-10-29)
- **D1.** Configure on a provider opens its own plugin page with the breadcrumb (not a pop-up).
- **D2.** A newly added provider plugin appears as Off in the flow; the administrator adds it. It never joins on its own.
- **D3.** A new provider must work without restarting the Engine (P3).
- **D4.** The flow edits order, role and Off only. Per-field winners (Priority Cascade), scoring and strategy stay in config.
- **D5.** A saved change applies to new ingestion only; existing matches are kept. Items already holding a retail match are not re-checked.
- **D6.** The refresh interval for media, people and series lists changes from 30 to 90 days (P5).

## Scope
**In:** provider settings move to the plugin detail page; Configure opens that page (F85); Ingestion Flow becomes editable (order, role, on/off per media type); a new provider plugin shows up in the flow; plain labels and tidy polish from F89 (friendly name in the breadcrumb, readable "last checked" time, no repeated title).
**Out:** editing scoring weights and thresholds, per-field priorities (Priority Cascade), and strategy names (stay in config files); community plugin sharing; Vault rules (already handled by the Vault sections spec).

## Proposed experience
1. **Plugin page for a provider** has tabs Overview, Settings, Activity. Settings holds Provider enabled, Language strategy, Test connection and Advanced connection, exactly today's controls. Breadcrumb reads Plugins > Apple Books, never APPLE_API.
2. **Metadata Providers menu entry and page go away**; old addresses redirect to the matching plugin page.
3. **Ingestion Flow tab** shows one card per media type. Each lists its providers as rows with a drag handle, a role chip (Primary, Fallback, Off) and a green tick or warning when the provider is connected. "Add provider" lists installed providers that can handle that media type.
4. **Edit and save** uses the same pattern as the universal editor: Edit, change, Save changes, Review changes first. Nothing applies until saved. Desktop only.
5. **A new provider plugin** appears under "Add provider" for each media type it declares it can handle, and as a note on its plugin page ("Not used in any flow yet. Add to Movies?"). It never joins on its own.
6. **Guard rails:** each media type must keep at least one identity provider; removing the last one is blocked with a plain sentence. A provider that sends titles online shows the existing privacy (i) tooltip. Changes affect new ingestion only; existing matches are kept; re-checking the library stays a separate action.

## Work packets (proposed)
- **P1 Provider page tabs** (Standard): move provider settings into the plugin detail page; Configure navigates to it; redirects; breadcrumb and timestamp polish. Depends on Plugins screens WP3 built.
- **P2 Editable flow** (Complex): Ingestion Flow tab with reorder, role, Off, Add provider, guard rails, Review changes; saves through `PUT /settings/pipelines`. Depends on P1 and the Settings flat menu.
- **P3 New plugin joins flow** (Complex): plugin-supplied sources load without an Engine restart and are offered in Add provider. Depends on P2.
- **P4 Docs and tests** (Mechanical): public docs page, guardrail tests that the flow never loses its last identity provider.

- **P5 Refresh every 90 days** (Mechanical): change the default in `config/hydration.json` and the matching defaults in code from 30 to 90 days for media (`stage3_refresh_days`), people (`person_refresh_days`) and series lists (`series_manifest_refresh_days`); update tests and any docs or screen text that say 30 days. Done when a new Library shows 90 and the sweep schedules items 90 days out.

## Local checks
Add a provider via a test plugin, put it second for Music, ingest the small generated library, confirm the order is used. (Old-address forwarding is dropped: Shy's standing rule is no URL forwarding.)

## How this fits the 30-day refresh
The nightly refresh (30 days per item, Wikidata-based) is independent of the provider order. A changed flow or a new provider applies to new ingestion only; existing matches keep refreshing from their current source. Shy confirmed (2026-10-29) that items already holding a retail match are not re-checked, so this is a non-issue, and asked that the refresh interval become 90 days (P5). A later optional action could re-check one media type with the new order. P4 adds a test that the sweep never re-matches items because the flow changed, and a Local check.

## Risks and open questions
All earlier questions are answered (see Decisions made). Biggest risk: P3, because providers are created at Engine start today.

## Plain English summary
Each provider's settings move onto its own plugin page, and the Ingestion Flow becomes a screen where you drag providers into order per media type, mark them Primary, Fallback or Off, and add a newly installed provider. Nothing changes until you press Save and review.
