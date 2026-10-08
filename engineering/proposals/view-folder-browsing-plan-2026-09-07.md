---
title: "View: Timeline, Folders, and Galleries"
summary: "Preserve named archives and nested roots while keeping the personal timeline intentional."
audience: "developer"
category: "proposals"
product_area: "view"
---

# View: timeline, folders, and galleries

Status: proposed September 7, 2026. Extends the storage-organization proposal with the end-user browsing contract. No runtime changes.

The [phased delivery plan](./view-phased-delivery-plan-2026-09-07.md) is authoritative for implementation order, per-profile optional logins, the Profiles/Shared layout, and physical promotion of accepted managed personal files into Shared. Earlier conflicting ownership, layout, rollout, or reference-only retention details below are superseded.

## Product decision

View needs three complementary ways to reach the same authorized media:

- **Timeline:** personal memories organized by date, with explicit source inclusion.
- **Folders:** named physical collections such as Car Videos, Home Movies, and hierarchical archives.
- **Galleries:** manually selected or rule-driven groups that can span folders without moving files.

Add Folders as a first-class View destination. Recommended navigation is `Timeline | Folders | Galleries | People | Places`, with Timeline keeping `/view` and replacing its current Photos label. This deliberately revises the current four-destination View contract; update the shared navigation, mobile treatment, docs, and guardrails together when implemented. Do not add these source folders to the global application navigation or create separate user-facing libraries per folder.

## Comparison informing the recommendation

Immich offers an explorer-like folder view alongside its timeline. Its FAQ distinguishes external folder structure from automatically created albums. Tuvima should likewise preserve folder identity directly instead of creating a Gallery for every directory. [Immich Folder View](https://docs.immich.app/features/folder-view/), [Immich FAQ](https://docs.immich.app/FAQ/)

Google Photos albums are independent groupings: removing an item from an album or deleting the album leaves the media in the Photos library. Tuvima Galleries should have that separation from physical storage. This does not imply Google Photos albums preserve an arbitrary server directory tree. [Google Photos album help](https://support.google.com/photos/answer/6128849?co=GENIE.Platform%3DDesktop&hl=en)

Our product choice combines a date-first personal view, reliable server-folder navigation, and virtual curation. Mixed documents/audio make folder browsing particularly important for Tuvima.

## Individual folders

If the user adds Car Videos and Home Movies independently, Folders home shows two named cards. Each has representative authorized artwork, a concise item/type summary, and an offline indicator only when relevant. Names are user labels; drive letters and storage internals belong in Details/settings. Duplicate labels are distinguished by owner and friendly root context.

Opening Car Videos presents:

1. Breadcrumb `View > Folders > Car Videos`.
2. The folder title and truthful content summary.
3. Immediate subfolders, followed by files directly inside this folder.
4. Search scoped to this folder, media-kind filters, and name/date sort. Default hierarchy browsing sorts folders by name.
5. An explicit **All items below this folder** view to browse descendants together, optionally by date. This presentation choice does not change the source's recursive indexing setting.

Use the same media viewer and item identity as Timeline/Galleries. Show playable video thumbnails and duration, document covers/icons, and meaningful audio presentation where supported. A supported item without a thumbnail receives a real file-type placeholder. Unsupported file types are not silently described as imported View items.

The personal Timeline directory is represented by one friendly Personal Media folder entry, not top-level cards for every year/month/day. Date directories remain browsable beneath that entry for users who want to inspect them.

## Adding a parent root

For this filesystem:

```text
E:\Archive\
  Car Videos\
    Track Days\
    Repairs\
  Home Movies\
    Birthdays\
    Holidays\
  introduction.mp4
```

Adding Archive recursively creates one configured source. Descendant directory entries are browsable nodes, not additional sources or Galleries. Default Folders home shows Archive; opening it shows Car Videos, Home Movies, and introduction.mp4. Deeper folders remain nested with clickable breadcrumbs.

Offer a display-only choice during setup or source editing:

- **Show this root:** one Archive card; hierarchy underneath. Default for predictable behavior and large trees.
- **Show its immediate subfolders:** Car Videos and Home Movies appear directly on Folders home, labeled with Archive as context. Show an explicit **Files in Archive** entry when root-level files exist, and keep **Browse Archive** available. This entry is a view of actual root files, not a fabricated physical folder.

Only the first level is promoted. Never flatten every descendant into a top-level card. New direct children appear after indexing when this display option is enabled; exclusions and authorization apply before generating cards. Switching display style does not rescan, move files, change permissions, or create extra configured sources.

Offer **Pin to Folders home** for a deeply nested favorite. Pins appear in a clearly labeled shortcut area and retain their real breadcrumb path. They do not add another scan source or duplicate content.

## Timeline inclusion

Indexing, discoverability, timeline placement, and sharing are independent:

| Folder | Recommended initial timeline placement |
| --- | --- |
| Personal Media / Timeline | Included |
| Additional mixed or specialist folders | Excluded until the user opts in |
| Home Movies when added explicitly | Ask; do not infer eligibility from its name |

Use **Include in main timeline** in the add-folder form, with a preview of its effect. Excluded folders remain available in Folders, authorized View search, and explicitly selected Galleries. This must not reuse Archive, Hidden, source Enabled, or access policy to suppress timeline placement.

For a parent root, use one inherited setting with explicit branch overrides where necessary: Archive off, Home Movies on, Car Videos off. Store branch rules relative to stable source identity and expose inheritance clearly. A branch override cannot widen permissions beyond its owner/source. New branches inherit the parent; no name-based guesses.

Provide a Timeline **Sources** filter for temporary exploration, including folders not normally shown. Mark non-default scope clearly and provide reset. Reset restores the normal timeline inclusion policy, not every indexed source. Folder-specific date views are always available without changing main-timeline membership.

Use capture time when trustworthy. Unknown dates receive an Undated group, not a fabricated recent date from copied files. Store any manually assigned dates with their provenance. Timeline and search deduplicate an asset reached through multiple authorized source paths; folder browsing shows the actual occurrence in each folder. A Gallery points to the asset, not to a thumbnail copy.

## Galleries and sharing

Folders show physical membership and follow indexing changes. Manual Galleries are curated selections. A deliberate **Create Gallery from folder** action may offer a one-time selection or a dynamic folder rule; dynamic rules explicitly choose whether descendants are included and use stable source-scoped folder identity.

Do not create thousands of Galleries from scanned directories. A car compilation can combine selected clips from several folders in one Gallery without changing disk layout. Removing its Gallery membership does not detach the source or delete the file.

Keep Mine/Shared/profile scope consistent across all View destinations. Folder names, thumbnails, breadcrumbs, and counts require authorization, not just the underlying file-open action. Gallery sharing grants the authorized Gallery view; it does not grant browsing of the containing filesystem folder. Household ownership is an explicit choice as described below, never an implied effect of adding a root.

## Personal and household spaces: clarification after product review

Index supported personal/mixed media from explicitly configured sources into View. This does not mean indexing every disk folder or automatically importing the Read/Watch/Listen catalog into View. Sources build the indexed inventory; Timeline, Folders, Galleries, curation, and authorized sharing operate within View. Physical organization remains a separate managed-storage policy: creating a Gallery or changing scope never moves originals.

Recommend two ownership scopes within the existing personal media kind, not a third library kind:

- **Personal Space:** tied to a stable profile identity, private by default, with a provisioned managed destination and optional additional sources. The readable username directory is a label, not the security boundary.
- **Family Library:** a household-owned View space with designated owners/curators and explicit member access. It may receive imports directly and own managed or read-only attached sources without assigning them to a particular family member. Illustrative storage is `View/Shared/Timeline/...` and `View/Shared/Folders/Home Movies/...`; it is a sibling of user directories, not inside a user's home. Reserve/disambiguate these directory labels.

Personal profiles receive automatic home provisioning and personal defaults; additional folders use the same source capabilities regardless of scope. A household must have durable identity and custodianship independent of one profile. This deliberately extends the current profile-owned bridge model and requires explicit ownership, authorization, retention, and API changes. Do not represent the family as a fake login/profile or silently reuse profile-based shared aggregation.

Keep the existing concept of shared visibility distinct from household ownership. In View use a space selector such as **Mine / Family Library / Shared with me**, with authorized profile exploration where applicable. Timeline/Folders/Galleries remain navigation destinations beneath the selected scope. A family collection must not become a catch-all for every item a profile happens to share.

The primary family workflow belongs in View, not library settings:

1. A user imports/indexes their personal media and privately selects candidates.
2. **Share Gallery** exposes a personal selection under its existing ownership. **Contribute to Family Library** submits selected items for durable household membership; show the difference plainly.
3. Contributors can submit their own authorized selections. Designated curators can accept, decline, and curate contributions. Curators cannot browse private personal media merely because they manage the family space. A curator with authority over the selected source can add directly.
4. Accepted items appear in the family Timeline and can be organized into family Galleries. Preserve contributor, original source, capture metadata, and provenance. Different selections from multiple people combine into one family Gallery without combining their private source folders.

Household acceptance must provide retention, not merely a fragile reference to a personal path. Reuse the same indexed asset and a verified durable managed occurrence when feasible; a household ownership reference prevents personal removal from deleting that shared retained content. If the only original is in a removable/read-only external source, explicitly import a copy into household storage for a durable contribution, or offer clearly labeled linked sharing without retention guarantees. Do not promise zero physical copies in all cases. Never move or delete read-only originals. Moving an already managed file to a different physical household layout is a separate, explicit operation and is not required for logical family membership.

Keep **Remove from my space**, **Remove from Family Library**, **Remove from Gallery**, and **Delete retained file** distinct. Personal deletion/account removal cannot silently destroy accepted family content. A family's removal affects its membership and any eligible household-held copy, not a contributor's original. Explain the retention consequence before contribution. Deduplication must not leak the existence of another user's private files; show one authorized asset in aggregate views while preserving ownership and source occurrences.

Settings only supplies the necessary source owner choice (personal profile or Family Library), destination, and access/curator policy. Contribution review, selection, Gallery building, and family browsing belong in View and must not enter the catalog Review Queue. Directly adding a family source is an explicit bulk household-access decision, previewed before indexing; it bypasses individual contribution review only with authorized curator intent.

Additional acceptance scenarios: import directly to household storage; combine accepted selections from two profiles into one family Gallery; decline a contribution without touching the original; share a personal Gallery without changing ownership; remove a personal membership while retaining its accepted family item; detach an external linked source and distinguish unavailable linked media from durable accepted copies; remove a curator/profile without orphaning household ownership; and verify private siblings, filenames, and duplicate-existence information remain inaccessible.

## Managed organization must preserve topic identity

Refine the storage proposal: calendar organization must not merge Car Videos and Home Movies into one unnamed date archive.

For individually managed folders, retain the named destination boundary:

```text
View/shaya/Folders/Car Videos/2024/11 - Nov/...
View/shaya/Folders/Home Movies/2024/11 - Nov/...
```

For a managed parent root, **Keep original folders and names** remains the default. If calendar organization is chosen, explicitly preview its boundary: organize the entire root as one collection, or preserve immediate child categories and organize within each. Preserve categories is recommended for a root containing named collections. Deeper restructuring must be visible in preview; no heuristic stripping of year-like or topic-like directory names. Existing files do not move when this preference is saved.

Presentation remains independent: show-root/show-children/pinning do not alter any organization boundary. Linked read-only roots always retain physical structure. Managed imports that flatten original paths must preserve original-relative-path provenance; their actual Folders view still shows the current physical layout, not an invented historical tree.

## Wizard and immediate actions

Keep Choose type, Configure storage, Review. Within View Add folder, ask:

1. Owner and path, including whether to index subfolders.
2. Read-only attachment or managed destination; managed organization and boundaries only when applicable.
3. Friendly name and display choice (root or immediate subfolders).
4. Include in main timeline, with optional branch choices for a parent root.

Review must preview both **where files live** and **how View displays them**. These are different consequences. For Archive, show proposed folder cards and timeline membership before commitment. Cancel leaves configuration untouched; final Add saves immediately and shows actual indexing progress separately.

Settings > Libraries > View manages roots, ownership, policies, and health. View > Folders is the everyday browsing surface. Use common section components and retained breadcrumbs for both, but keep disk-management actions out of ordinary card navigation. An authorized Add folder shortcut can open the shared setup form.

## Operational edge cases and implementation scope

- Parent/child source overlap: when Car Videos is already covered by Archive, offer to open or pin the existing branch rather than registering an overlapping scan. Different access boundaries require deliberate source configuration, not a silent duplicate.
- Unchecking recursive indexing excludes descendants from the indexed source; the review must make that consequence explicit. Root promotion is unavailable if children are not indexed.
- Large roots need lazy, paged children and indexed counts. Opening a folder must not synchronously enumerate an entire NAS subtree. Display saved indexing state and incremental updates.
- Offline roots retain their identity and known indexed state with an unavailable message. Do not interpret transient unavailability as deleting the source or its Gallery membership.
- A physical rename/move discovered externally requires reliable identity evidence before transferring pins, branch rules, and dynamic Gallery references. Ambiguous changes become unavailable/attention states; title matching cannot establish ownership or identity.
- Distinguish **Unpin**, **Exclude from timeline**, **Disable indexing**, **Detach source**, **Remove from Gallery**, and file deletion. Read-only attachments expose no disk mutation. Detachment requires confirmation and does not delete files.
- Index storage-relative directory nodes and asset occurrences without putting source paths into unrestricted URLs. Permission-filter summaries and paginated folder endpoints server-side. Derive display entries from the existing source graph; do not introduce a third library kind or per-directory personal-library bridges.
- Persist main-timeline policy independently from source enabled state and personal Archive/Hidden states. Add inherited branch rules and user display/pin preferences with distinct ownership of each setting.
- The current View page has no full folder browser. This is new browse/index/query work and a deliberate navigation-contract change, not just a new settings card.

Acceptance scenarios: add the two named folders separately; add their parent; promote first-level folders; retain direct root files; pin a nested branch; include only Home Movies in Timeline; index a new child; encounter an offline root; avoid overlapping registrations; view a shared Gallery without disclosing private siblings; and switch physical organization without silently moving originals. Test the same behavior on desktop and mobile with breadcrumbs, pagination, scoped search, and unique asset counts.

## Product-owner summary

Personal memories stay easy to browse by date. Car Videos and Home Movies remain recognizable folders, and adding their parent preserves the tree while optionally surfacing those folders directly. Galleries provide cross-folder curation. Users choose which folders join the main timeline without copying files, losing folder organization, or changing sharing permissions.
