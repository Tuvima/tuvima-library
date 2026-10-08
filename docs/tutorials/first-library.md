---
title: "Your First Library"
description: "Add a governed source, begin catalogued or personal-media intake, and understand where Tuvima shows the result."
audience: "user"
category: "tutorial"
product_area: "library"
tags:
  - "library-folders"
  - "library"
  - "onboarding"
---
This tutorial walks through the first practical loop: configure a governed
source, begin the appropriate intake path, watch catalogued ingestion when
applicable, and understand where the result appears.

**Prerequisite:** The Engine and Dashboard are running. If not, start with [Getting Started](getting-started.md).

## Step 1 - Open the Library Settings

In the Dashboard, open **Settings > Libraries**.

Select **Add library** to use three steps: **Choose type**, **Add folders**, and **Review**. Structured Media creates one Books, Comics, Movies, TV Shows, Music, or Audiobooks library. Personal Media configures the shared View storage root; it does not add a structured library.

Folder selection shows approved storage locations, path breadcrumbs, access checks, and any overlapping libraries. Manual paths receive the same validation. Nothing is committed until the final action.

Open an existing library to manage **Folders**, **Organization**, **File Handling**, and **Advanced Settings** on one page. The breadcrumb retains the library name and a link back to Libraries, including the selected Read/Watch/Listen/View filter. Confirm:

- **Source folder** - where files already live or where new files appear for Tuvima to scan.
- **Primary destination** - the managed source where organized files can live after intake.
- **Organization template** - the folder/file naming pattern used during organization.
- **Path checks** - whether the Engine can read and write the configured paths.
- **Library kind** - catalogued for administrator-created Read, Watch, and Listen libraries.
- **Metadata policy** - enriched for known catalogue works.

Save the settings when they look correct. **Protect existing files** starts on. Existing-library sources stay read-only regardless of this setting. Naming changes apply to eligible new imports; saving settings does not reorganize existing files. Filename collisions preserve the existing file and give the incoming file a unique name.

Personal Media uses one Personal Space per enabled profile. A populated View root cannot be changed through this page because relocation is not available.

## Step 2 - Add Test Media

Copy a small set of supported files into the library's managed source folder, or add an existing read-only folder that already contains them. Start with a few known items rather than a huge collection:

- one EPUB or PDF
- one movie or TV file
- one album track or audiobook
- one comic archive if you use comics
- a few mixed local files if you configured a source for your View Personal Space

For photos, home videos, documents, lectures, audio notes, or private unmatched
content, enable View for the owning profile. Under **Settings > Users**, choose
**Import folder** to copy files into that profile's managed source directory, or
**Link existing folder** to index an external folder read-only. Tuvima will not
send those items through retail providers, Wikidata, or identity review.

Supported formats are listed in [Media Types](../reference/media-types.md).

## Step 3 - Begin The Correct Intake Path

For a catalogued Read, Watch, or Listen library, start the administrator scan
using **Scan now** in **Settings > Operations** when importing an existing folder. The same page shows progress.

For View, select the owning profile and open `/view`. The profile's Personal
Space is the user-facing destination; sources and devices are provenance, not
library choices. Browser upload resolves that space automatically. View
reconciliation exists only for administrator recovery/diagnostics and is not the
normal Photos workflow.

## Step 4 - Watch The Pipeline

For catalogued media, the Engine moves through these broad stages:

1. **Settle** - wait until the file is no longer being copied.
2. **Fingerprint** - compute a stable file identity for duplicate detection.
3. **Scan** - read embedded metadata and artwork where possible.
4. **Classify** - resolve ambiguous formats such as MP3, M4A, MP4, MKV, AVI, or WEBM.
5. **Identify** - compare file data with known works and provider candidates.
6. **Stage 3 retail metadata & primary artwork** - gather primary cover/poster evidence, descriptions, ratings, people seeds, and bridge IDs from configured providers.
7. **Stage 4 Wikidata** - use bridge IDs to resolve canonical identity when possible.
8. **Stage 5 file ready** - store core canonical values and managed artwork.
9. **Stages 6-8 enrichment** - expand people, universe relationships, lyrics/subtitles, and deeper artwork.
10. **Readiness** - decide whether the item is ready for Home, Read, Watch, Listen, Search, or Collections.

SignalR tells the Dashboard when to refresh the ingestion snapshot while ingestion is running, so you should not need to refresh the page.

Personal View media takes a separate local-only path: settle, fingerprint,
extract safe local metadata, retain source/device paths, group compound files,
and update the local search/timeline index. It does not enter retail matching,
Wikidata, canonical claims, or Review Queue.

## Step 5 - Understand Where Items Appear

Items do not appear everywhere immediately.

- **Home** shows discovery and overview shelves returned by the Engine.
- **Read** shows books and comics.
- **Watch** shows movies and TV.
- **Listen** shows music and audiobooks.
- **Search** finds library items across media lanes.
- **Collections** organizes automatic broader rollups, published curated collections, lane-level shelves, and people when they are backed by real library data.
- **View** exposes Photos, Folders, Galleries, People, and Places. Photos uses
  the saved authorized scope; first use prefers Shared View when permitted and
  Mine otherwise. Revoked saved scopes fall back to Shared when still
  permitted, then Mine. People and Places remain truthful capability states in
  the current Dashboard even though evidence-based Engine queries exist.
- **Review Queue** holds items that need human confirmation.

A catalogued item is eligible for browse surfaces only after it has a real
title, resolved media type, and settled artwork outcome. Items that are
uncertain stay in Review Queue instead of being shown as if they were correct.
Personal View assets are available from local index state and never enter that
catalogue readiness/review gate.

## Step 6 - Resolve Review Items

Open **Settings > Review Queue** if the Ingestion dashboard shows items needing attention.

Common reasons include:

- no reliable retail match
- multiple plausible candidates
- conflicting embedded metadata
- missing bridge IDs for Wikidata
- uncertain media type
- corrupt or unreadable files

Open the item, review the reason, and launch the shared editor. Review changes are applied through Engine APIs and the current surface refreshes after a successful edit.

## Step 7 - Browse The Result

After the readiness gate passes, open:

- **Home** for overview shelves.
- **Read**, **Watch**, or **Listen** for media-specific browsing.
- **Search** to find the item by title, creator, album, or other indexed fields.
- The item's detail page to inspect metadata and make inline corrections.

Tuvima only shows real data returned by the Engine. Empty shelves, unavailable AI states, and missing provider results are not replaced with fake examples.

## Related

- [How to Add Media to Your Library](../guides/adding-media.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [How to Resolve Review Items](../guides/resolving-reviews.md)
- [Troubleshooting](../guides/troubleshooting.md)


### Immediate folder changes and View storage

Use **Add folder** inside the Folders section. Its final confirmation saves the addition; no page-wide Save is needed. Detaching a folder requires confirmation and leaves its files on disk. When detaching a primary that still serves other managed folders, choose its replacement in the confirmation dialog. Names and custom templates use their own Apply dialog.

Open **Libraries > View** for profile-owned sources. Attach existing folders read-only, or create an empty managed folder under a chosen profile. New browser uploads use `View/Profiles/<profile-label>/Timeline/<year>/<month>/<date-time><type>.<ext>`; managed mixed folders use `Folders/<folder-label>` and preserve their hierarchy. Labels stay fixed when display names change. Open **View > Folders** to browse each indexed source with a hierarchical breadcrumb. Administrators grant Shared Library access, submission, and curator review independently under a profile's View access settings. In Mine, select assets and choose **Submit to Shared Library**. Nothing moves while pending. If a curator accepts the contribution, managed originals move only after their Shared copies verify, while linked read-only originals are copied and retained. Track decisions and transfer state under **View > Shared Library > Contributions**; its breadcrumb preserves every ancestor. Shared scope exposes only accepted items through the Shared Library root. Existing files are never relocated merely by changing settings.
