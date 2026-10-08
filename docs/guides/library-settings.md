---
title: "Manage library settings and operations"
description: "Configure safe library sources, check background work, and find provider, profile, and recovery controls as an administrator."
audience: administrator
category: guide
product_area: ingestion
status: current
---

# Manage library settings and operations

Use Settings in Tuvima Library to add folders and check background work. This five-minute guide helps you choose safe [sources](../reference/glossary.md#library-folder) and find the right admin page.

## Choose a safe source

1. Open **Settings > Libraries** with admin access.
2. Choose a Watch, Read, or Listen library.
3. Add or check its source folders. Check that Tuvima can reach and read them.
4. Choose **Existing library** to scan files where they are.
5. Choose **Managed by Tuvima** only if you want allowed file moves or tag writes.
6. Check the rules for intake, duplicates, details, and file layout before saving.

An Existing source is read-only to Tuvima. Tuvima does not move, rename, tag, overwrite, or delete its files. A managed source also needs write access. Its rules must allow each change.

Keep music album folders intact. Use tags or fingerprints to identify music before moving it. A library setting alone is not a reason to rename music files.

## Preview file moves

Switching from Existing to Managed changes the rules for future work. It does not move files at once.

1. Request a reorganization preview where offered.
2. Check the counts for unchanged, moved, renamed, conflicting, unresolved, and blocked items.
3. Confirm that exact preview if the proposed result is right.
4. Check the result. Retry failed items after fixing their cause.

The Engine checks each item again before moving it. A preview can expire. A source or item change can also make it invalid.

## Set up View storage

Use **Settings > Libraries** to choose the single managed View root. Use **Settings > Users** for each enabled profile's Personal Space sources and devices.

**Import folder** copies files into managed storage. **Link existing folder** scans an outside folder read-only. Detaching a source does not delete files. Reconcile is an admin repair action.

Browser uploads work today. Saved source and device records do not mean phone backup or device sync is ready. See [View Personal Space](view-personal-space.md).

## Follow work in Operations

Open **Settings > Operations > Ingestion**, or select the header activity icon. The page updates on its own. It shows the current run, active and queued work, results, provider waits, and recent batches.

A checked or visible file does not mean the whole run is done. Matching, richer details, and file moves may still be in progress. Counts come from saved Engine state. Unknown values stay unknown.

1. Check the current phase and any work that needs help.
2. Use **Scan now** for an extra scan of watched folders.
3. Search batch history or choose a result filter.
4. Select a batch to browse its media.
5. Choose **Show older** to load another page of history.

History starts with the three newest batches. A batch keeps its ID when the Engine restarts and resumes work. There is no separate Activity & Audit page.

## Fix review and provider problems

[Review Queue](resolving-reviews.md) holds items that need a human decision. Work still waiting or retrying belongs in Operations. Review reasons include weak matches, duplicates, failed lookups, naming issues, and conflicting details.

**Metadata Providers** shows setup, key state, health, tests, and source priorities where supported. Healthy, Degraded, Offline, Disabled, Missing Configuration, and Unknown mean different things. No health result does not mean a provider is healthy.

These pages do not show secret values. [Set up providers](configuring-providers.md) before retrying a lookup that failed because of setup.

## Find other admin tools

Settings also holds Users, Local AI, network and remote access, backup/recovery, logs, and [Plugins](using-plugins.md). Your role and the Engine's supported actions control which tools you can use.

Developer Tools is for admin use on a development library, when enabled. Reset & Seed clears the catalog and derived intake state, then queues known test files. It keeps users, sources, and access rules. Advanced resets need a separate confirm step.

<details>
<summary>Technical details</summary>

Operations reads `GET /ingestion/operations`. The service `IIngestionOperationsStatusService` builds one snapshot from saved items, batches, jobs, logs, details, art, people, links, reviews, and provider health. SignalR events refresh that same snapshot. Polling also checks it while work is active or idle.

Catalog sources are in `config/libraries.json`. File layout options also use `config/core.json`. Each source has a stable ID. A destination uses that ID, not its place in the source list. View saves source and device records on their own. Its managed paths use `profiles/<profile-id>/sources/<source-id>` beneath the View root.

Provider files are in `config/providers/*.json`. Keep long-lived keys in ignored `config/secrets/{provider}.json` files. A blank base file does not prove that a key was deleted.

Intake reads the file. Retail Match finds a provider record. Wikidata may add a canonical match. Later jobs add people, links, and art. Jobs can overlap. A valid local or provider sequence can stand even if Wikidata's previous/next chain has gaps. A diagnostic alone need not create a review item.

An outside manifest or expected count does not create owned files. Comic counts and partial totals must not imply a known completion target. More art can arrive after an item is ready to browse.

Batch facts are saved in `ingestion_batch_artifacts`. Batch media details and logs expose audit facts. A selected batch opens `/settings/ingestion?runId=<guid>&view=all`. A grouped lookup may show a batch label until it has per-file results.

A move preview has a short-lived plan fingerprint. The Engine checks the plan, access, rules, and each item before moving it. Some values may be unknown: counts for a folder with no batch, the last move time, provider limits, or direct phase filters.

The development tools include Reset & Seed Test Library, Rescan All Libraries, View Ingestion, and Advanced resets. Fixture cleanup uses a list of generated files. Routine resets do not delete source media.

</details>

## Next steps

- [Add media to your library](adding-media.md)
- [Configure metadata providers](configuring-providers.md)
- [Resolve a review item](resolving-reviews.md)
- [Check feature availability](../product/status.md)
