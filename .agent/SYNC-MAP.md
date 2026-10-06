# Antigravity ↔ Claude Code — Sync Map

> **Last synced:** 2026-10-06
>
> **Canonical source of truth:** `CLAUDE.md` (repo root)
>
> This file tells Antigravity (Gemini) how its `.agent/` files relate to `CLAUDE.md`, the project memory used by Claude Code.

---

## Rules

1. **`CLAUDE.md` is authoritative.** If information conflicts between `CLAUDE.md` and an `.agent/` file, `CLAUDE.md` wins.
2. **After changing any `.agent/` file**, update the corresponding `CLAUDE.md` section listed below.
3. **After changing `CLAUDE.md`**, update the corresponding `.agent/` file(s) listed below.
4. **Update the "Last synced" date** at the top of this file whenever a sync is performed.

---

## Mapping: `.agent/` → `CLAUDE.md`

| `.agent/` file | `CLAUDE.md` section(s) | Content |
|---|---|---|
| `features/INGESTION-PIPELINE.md` | §3.1 (Watch Folder), §3.13 (Hydration Pipeline), §3.14 (Media Type Disambiguation) | Ingestion flow, three-stage pipeline, review queue, media type disambiguation |
| `features/METADATA-MANAGEMENT.md` | §3.2 (Weighted Voter), §3.6 (Metadata Adapters) | Claim system, trust weights, provider config |
| `features/METADATA-PRIORITY.md` | §3.6 (Metadata Adapters) | Provider priority, field weights, harvest pipeline |
| `features/API-SECURITY.md` | §3.3 (Security) | Auth, rate limiting, path traversal, SignalR auth |
| `features/ROLE-ACCESS-MODEL.md` | §3.3 (Security) | Account, profile grant, and Application authority |
| `features/LIBRARY-DASHBOARD.md` | §3.11 (Settings), §6 (Dashboard UI) | Current Home, Read, Watch, Listen, Collections, Search, detail pages, inline editing, Review Queue, and Settings/Admin model |
| `features/SETTINGS-OVERVIEW.md` | §3.8 (Activity Ledger), §3.12 (Device Profiles) | Settings pages, activity timeline, device cascade |
| `features/REALTIME-INTERCOM.md` | §3.4 (Dashboard UI — Real-time updates) | SignalR events, live dashboard updates |
| `features/COLLECTION-SYSTEM.md` | §1 (Grouping Model), §3.13 (Universal Parameterized Collection System) | Library/Universe/Series/Work/Edition/MediaAsset hierarchy. Universal parameterized collection model: 6 collection types, rule predicates, hybrid resolution, collection placements, /collections Collections page |
| `skills/DASHBOARD-UI.md` | §6 (Feature-Sliced Layout) | File locations, component placement, CSS properties |
| `skills/METADATA-SCORING.md` | §3.2 (Weighted Voter) | Scoring engine operations, claim persistence |
| `skills/INGESTION-PIPELINE.md` | §3.1 (Watch Folder), §3.7 (Library Organization) | File watcher operations, sidecar system |
| `skills/SETTINGS-MANAGEMENT.md` | §3.11 (Configuration Architecture) | Config directory layout, provider config format |
| `skills/API-SECURITY.md` | §3.3 (Security) | Security implementation patterns |
| `FIX-PLAN.md` | Not in CLAUDE.md (standalone) | Tiered issue tracking, execution priority |
| (covered by `features/COLLECTION-SYSTEM.md`) | §3.13 (Universal Parameterized Collection System) | Universal parameterized collection model, Collections page, collection builder |

---

## What to do after making changes

### If you modified a feature or skill file:
1. Open `CLAUDE.md` and find the matching section from the table above.
2. Update that section to reflect the new information.
3. Update the "Last synced" date at the top of this file.

### If you created a new feature or skill file:
1. Add a row to the mapping table above.
2. Add a matching section to `CLAUDE.md` (typically in §3).
3. Claude Code's `CLAUDE.md` §5.3 has the reverse mapping — add the new file there too.

### If `CLAUDE.md` was updated by Claude Code:
1. Check `CLAUDE.md` §5.3 for the sync mapping table.
2. Update the corresponding `.agent/` files from the table above.
3. Update the "Last synced" date at the top of this file.


The October 4 Home/player remediation synchronizes Dashboard behavior in `AGENTS.md`, `CLAUDE.md`, `features/LIBRARY-DASHBOARD.md`, `skills/DASHBOARD-UI.md` and the Dashboard/playback architecture documents.

The remediation follow-up also synchronizes the retail matrix in METADATA-MANAGEMENT, METADATA-SCORING, configuration references and scoring architecture; compact primary actions reflect the product owner correction. Historical reports describe their original commit only.

The October 5 player panels and shared-controls delivery synchronizes `AGENTS.md`, `CLAUDE.md`, `features/LIBRARY-DASHBOARD.md` and `skills/DASHBOARD-UI.md` with `docs/architecture/playback.md`.

The October 6 CSS ownership implementation synchronizes AGENTS, CLAUDE, Dashboard feature/skill guidance, Dashboard architecture and UI consistency standards. Final evidence separately records the bundle gate and capability limits; broader interop work remains open.
