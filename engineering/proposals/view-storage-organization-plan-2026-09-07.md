---
title: "View Storage and Folder Organization"
summary: "A central per-user destination, readable calendar organization, and independently governed mixed-media folders."
audience: "developer"
category: "proposals"
product_area: "settings-libraries"
---

# View storage and folder organization

Status: proposed September 7, 2026. Refines the View section of the immediate-actions proposal. No application or media changes are included in this planning work.

The [phased delivery plan](./view-phased-delivery-plan-2026-09-07.md) is authoritative for implementation order, per-profile optional logins, the Profiles/Shared layout, and physical promotion of accepted managed personal files into Shared. Earlier conflicting ownership, layout, rollout, or reference-only retention details below are superseded.

The [folder-browsing refinement](./view-folder-browsing-plan-2026-09-07.md) defines how personal and additional folders appear in View, including parent-root presentation, timeline inclusion, Galleries, and preservation of topic folders during managed organization.

## Recommendation

Keep one View experience and one Personal Space per enabled profile. Provide a central root with readable per-user storage labels, one default personal-media destination per profile, and any number of separately governed mixed-media folders. Source/device provenance remains in the database; it should not force UUID directories into the user's archive.

Treat ownership, physical destination, management permission, and naming policy as separate decisions. A folder being mixed media does not make it shared, and being writable does not authorize Tuvima to reorganize its existing contents.

## Immich findings and coexistence

Immich documents an optional storage-template feature whose default template is `Year/Year-Month-Day/Filename.Extension`. It supports user storage labels and custom naming. Its date tokens use the server timezone. The user's `2024/11 - Nov/Nov 08 - 10.34.29VID.mp4` is a custom readable-calendar layout, with the day in the filename rather than a separate day directory. [Storage Template](https://docs.immich.app/administration/storage-template/)

Immich's external-library guide supports user-owned external folders mounted read-only. [External Library](https://docs.immich.app/guides/external-library/)

Recommended deployment models:

| Existing setup | Tuvima behavior | Immich behavior |
| --- | --- | --- |
| Tuvima owns organization | Writes new imports into the chosen managed destination | Indexes those photos/videos as a read-only external library |
| Immich owns organization | Links the relevant original-media folders read-only | Continues managing its own storage |
| An existing manually organized archive | Links the archive without renaming, moving, deleting, or writing sidecars | Can independently index it read-only |

Do not let both applications organize the same physical folder. Matching filenames alone does not provide database, albums, faces, favorites, sharing, or deletion synchronization. Do not write into Immich's internal upload/data tree or index its thumbnail/transcode/cache directories as originals. A path later moved by its owner must be reconciled as a source-path change, without a second application attempting to move it back.

## Proposed physical structure

Example central root:

```text
D:\PersonalMedia\                       # configured View root
  shaya\                               # stable readable storage label
    Timeline\                          # default photo/video destination
      2024\
        11 - Nov\
          Nov 08 - 10.34.29VID.mp4
          Nov 08 - 10.35.12IMG.jpg
      Undated\
        original-file.jpg
    Folders\                           # independently configured mixed folders
      Family Archive\
        Photos\
        Documents\
        Recordings\
  alex\
    Timeline\
    Folders\
```

`Timeline` keeps a clean photo/video subtree for tools such as Immich. `Folders` gives documents, recordings, and curated mixed archives their own destination without forcing them into capture-date folders. Both appear together in the owning profile's View. The two physical subtrees are disjoint scan roots; the profile directory is structural, not another recursive source. This avoids scanning or importing a file twice.

Additional managed mixed folders may live at another explicitly approved location, such as `E:\Family Archive`; the central root is the default destination, not a requirement to relocate every existing archive. Linked folders stay wherever they already are. Destination selection must be explicit when more than one folder can accept an import. Personal photo/video uploads default to Timeline; other file kinds use a named mixed-folder destination. Mixed upload batches preview routing per group before commitment.

Storage labels are initialized from usernames, validated for filesystem safety and uniqueness, and associated with stable profile IDs. They are not recomputed on login/display-name changes. Show the label and resulting path during setup. Case-insensitive collisions, reserved Windows names, separators, and traversal are rejected. Renaming a profile or deleting/recreating a username must not silently rename or transfer ownership of its directory. Changing an existing storage label/root is a separately supported relocation operation, not an ordinary autosave field.

Keep database files, thumbnails, transcodes, and internal working state in Tuvima's application-data area, outside externally indexed original-media trees. If atomic promotion requires temporary files on the destination volume, use an excluded staging directory and index only finalized files.

## Folder modes aligned with other libraries

Catalogue libraries already distinguish a primary destination, secondary sources, and managed versus existing read-only access. Reuse their conceptual rules and shared controls, while retaining View's local-media identity and profile ownership.

| Choice | Writes | Organization | Intended use |
| --- | --- | --- | --- |
| **Managed by Tuvima — calendar** | New imported copies enter this destination | Selected date-based template | Personal photos/videos or a dated mixed archive |
| **Managed by Tuvima — preserve layout** | New imported copies enter this destination | Keep original relative folders and names | Family archives, documents, recordings, curated mixed folders |
| **Existing folder — read-only** | None | Preserve all existing structure | Immich-owned originals, NAS archives, side-by-side operation |

Management mode and organization are separate controls: calendar and preserve-layout are policies for managed destinations, not new library kinds. A mixed folder can choose either policy. Linked read-only folders do not expose editable naming or writeback controls. Changing a linked folder to managed must explicitly describe the new authority; it never retroactively authorizes reorganization of existing media.

Add an existing folder read-only by default. Copying into managed storage leaves the origin untouched. Managed destinations protect pre-existing files by default; organization applies to arriving imports. Reorganizing already-owned files requires an actual preview and explicit execution action. Do not promise this until a View-specific planner handles assets, compound files, source references, and failures correctly.

Mixed folders have one accountable owner/custodian profile. Sharing follows explicit View/Gallery policies and must not be inferred from folder names such as Family or Shared. Retain current sharing behavior initially; source-specific access lists would be a separate authorization feature, not merely a folder-layout option.

## Organization controls and edge cases

Offer three presets plus a constrained custom template:

1. **Readable calendar**: `2024/11 - Nov/Nov 08 - 10.34.29VID.mp4`. Recommended for new personal timelines, matching the requested example. Optional separate day-directory variant: `2024/11 - Nov/08/10.34.29VID.mp4`.
2. **Immich documented template layout**: `2024/2024-11-08/original-name.mp4`. Label this as a layout preset, not an integration or a guarantee of identical behavior.
3. **Keep original folders and names**: recommended for mixed archives. Relative paths are preserved beneath the selected destination, never copied as absolute source-drive paths.
4. **Custom**: shared validated template editor with supported local-date, original-name, extension, and media-kind tokens; do not pass catalogue author/title/provider tokens into View or promise full compatibility with Immich's template language.

Separately allow original filenames or generated capture-time filenames for calendar layouts. Show concrete photo, video, undated, and document examples before applying. Generated names retain the original filename in provenance and use media-kind suffixes such as IMG/VID only when the type is known.

Rules required before enabling calendar organization:

- Prefer trustworthy embedded capture timestamps and offsets. Use a configured, persisted timezone fallback for naive dates; host timezone changes must not move existing files. Keep source timestamp and chosen interpretation for diagnosis. A timezone override can match the user's Immich setup when needed.
- Missing or invalid capture time goes to `Undated` with the original filename. Do not turn filesystem modification time into an asserted capture date. Optional filename-date parsing must be explicit and previewable.
- A document without a capture timestamp retains its original path/name by default, even in a mixed source. If the source explicitly uses calendar organization for all content, undated documents use `Undated`; do not invent dates.
- Pin month-name language per policy, defaulting to English for the supplied preset. Do not rename files because the UI language changes.
- Handle burst images, same-second videos, DST ambiguities, and name collisions with an atomic unique suffix; never overwrite. Preserve original bytes and extensions.
- Treat Live Photo pairs, RAW/JPEG variants, and sidecars as related file groups. Give related files a consistent stem and collision suffix while retaining their distinct roles. Test same-extension alternatives explicitly.
- Content deduplication preserves independent owner permissions and source references. Do not introduce cross-user hardlinks or shared physical deletion semantics merely to save space.
- Renaming or changing a policy affects future imports. Existing paths remain stable unless a separate filesystem change is previewed and confirmed. Persist policy versions so retries use the same destination decision.

## View settings page

Retain one **View** row in Libraries and the View filter. Inside View, use the same section format as other library pages:

1. **Folders:** root summary plus a source table with owner, role (personal destination or additional folder), path, mode, organization, subfolders, health, and actions. Put **Add folder** here. Group by owner or use an owner filter when needed. Root/profile directories do not inflate folder counts.
2. **Organization:** root defaults, readable preview, timezone/month language, and clearly labeled per-folder overrides. Managed mixed folders can preserve layout independently of Timeline. Linked folders show their fixed preserve-existing behavior.
3. **File Handling:** original protection, incoming-copy behavior, non-overwriting collisions, compound-file preservation, and detach semantics. Retain only supported controls.
4. **Personal Spaces & Sharing:** profile-to-storage-label mapping, provisioning status, and the canonical access-policy editor. Directory structure is not an authorization boundary.
5. **Local Processing / Advanced:** truthful indexing and thumbnail status, local-only metadata explanation, diagnostic identifiers, and supported recovery actions.

Folder additions save on their final confirmation; detachment uses a confirmation and leaves files on disk. Removing a source must not delete a shared asset still referenced elsewhere. In particular, after consolidating device inputs into Timeline, removing a device/provenance record is different from detaching the Timeline destination. Keep those operations separately named and scoped.

## Wizard changes

Keep three steps and the shell breadcrumb throughout:

1. **Choose type:** Structured Media or Personal & Mixed Media (View). Under View, choose **Set up personal storage** or **Add a mixed-media folder**. If personal storage already exists, offer its settings rather than creating a second root.
2. **Configure storage:** personal setup chooses the central root, enabled profiles and readable labels, template, timezone, and path previews. Additional-folder setup chooses owner, managed destination or existing read-only source, mode, organization, and include-subfolders. Managed import shows origin and destination separately. Existing read-only skips naming settings. Reuse this same form from View's Add folder action.
3. **Review:** exact roots/profile paths, file examples, ownership, existing permissions, copy/read-only behavior, and what happens to existing files. Persist configuration once. Copying/indexing runs with real progress after the configuration operation; do not show files as imported merely because the folder was saved.

Keep access-policy changes separate from selecting participating profiles: this wizard must not silently grant View access or expand sharing. Future enabled profiles receive a unique reserved label and default destination using the same provisioning service.

## Implementation implications and rollout

The current service derives `profiles/<profile-id>/sources/<source-id>` paths; folder imports preserve source hierarchy, and no readable date organization is implemented there. The proposal requires changes to storage, upload/import routing, local metadata extraction before destination calculation, provenance, source enumeration, and atomic file promotion, not just markup.

Separate destination identity from device/import provenance so several inputs can feed one Timeline without overlapping watchers. Persist readable storage labels and folder-level organization policy. Reuse path validation, ownership-aware mutation rules, protection concepts, collision handling, shared controls, and explicit destination selection from catalogue libraries; keep View out of catalogue enrichment and Review Queue.

Implement in order: storage/identity contract; deterministic naming and file-group tests; atomic import/provenance routing; read-only external-folder behavior; settings/wizard; coexistence and responsive verification. Test two users with the same filename, renamed usernames, undated documents, timezone boundaries, Live Photos/sidecars, interruption/retry, offline roots, root overlaps/symlinks, and external read-only mounts.

Under the repository's pre-beta rule, cut over disposable config/index state cleanly rather than adding compatibility readers or automatic migrations. Original media is not disposable. Inventory populated old roots, refuse an implicit move, and require an explicit reimport or separately planned relocation to adopt the new layout. A schema cutover is not permission to delete source files.

## Product-owner summary

Every user gets a readable personal archive beneath one root. Photos and videos can follow the requested calendar layout, while mixed folders can use that same organization or keep their existing structure. Immich can sit beside Tuvima through read-only indexing, with one application responsible for organizing each physical folder. The wizard makes ownership, destination, and file-handling choices visible before anything is saved.
