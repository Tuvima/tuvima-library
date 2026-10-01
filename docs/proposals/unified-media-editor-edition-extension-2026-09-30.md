---
title: "Unified Media Editor: edition and release extension"
summary: "Ownership and implementation contract for work, edition or release, and physical file editing."
audience: "product and engineering"
category: "proposals"
product_area: "media editor, identity, artwork, write-back"
status: "in progress"
---

# Edition and release extension

## Product walkthrough

1. **Keep the everyday editor short.** A movie, book, album, issue, or episode with one ordinary owned file still opens a simple file list. The user can inspect the file and correct its match in the existing editor. The current four editor destinations, detail pages, playback, and browsing stay in place. When a meaningful cut, printing, narration, or release exists, the file list reveals that named level. **Work packages W0, W1, W4.**
2. **Show each owned version under the same content identity.** A movie with theatrical and IMAX cuts remains one movie. An episode with 4K and 1080p files remains one episode. A book with two ISBN editions remains one literary work. The user can see which files belong to each edition and select a physical file without accidentally changing its siblings. **W1, W2, W3, W4.**
3. **Describe the reach of every change.** A movie match applies to the movie identity; an edition label or cover applies to that edition; a file operation applies to the chosen asset. Artwork can inherit from the parent and a supported edition can override it. The editor states the owner and affected file count before a shared change is saved. **W1, W2, W5.**
4. **Preserve release and file facts.** Music identifies the exact release when the evidence supports it. Track or recording identity does not silently move a file to another release. Metadata written to a file retains the correct work, release or edition, structural parent, and file facts where its format supports them. A later scan reconstructs only identities actually embedded in that file. **W3, W6.**

Acceptance is observable with one work and one file, one work with multiple assets, one work with multiple editions, one edition with multiple assets, a show with two versions of one episode, and different original and deluxe music releases. The interface stays compact for a single file and exposes the hierarchy only when it conveys a real distinction. A multi-episode physical file with no safe many-to-many representation enters review with its evidence retained.

### Scope boundary

This extension does not add transcoding, splitting, automatic quality ranking, preferred playback version, or provider-scraped editions. A resolution or codec difference alone does not prove a separate edition. The supplied screenshots guide layout and language; sample counts, titles, and provider statuses are illustrative.

## W0: repository audit and ownership decisions

The database already has `works.id → editions.work_id → media_assets.edition_id`. `editions` currently stores `id`, `work_id`, `format_label`, and `wikidata_qid`. An edition is created during ingestion before the asset is inserted. The model supports more than one asset under an edition, but current editor navigation resolves a representative asset for a work tree and consequently hides the edition boundary. The paged owned-child read endpoint can expose real edition IDs without loading every asset into the navigator.

`ClaimScopeCatalog` and `WorkClaimRouter` already support Work, Parent, Edition, and Asset destinations, but many existing field mappings still target Parent or Self. In particular, audiobook narrator currently targets Parent; ISBN is not explicitly edition-scoped. Changing those routes without migrating existing claims would strand data in old scopes, so the write contract and migration must move together. `WriteBackService` currently loads canonical values from the asset ID only; its comment about falling back through the edition chain is not implemented. Audio custom IDs include ISBN and ASIN, but no versioned Tuvima edition ID. Re-ingestion therefore cannot be claimed to reconstruct edition identity.

Current music albums and their MusicBrainz release identifiers are represented on parent Works; current ingestion creates an Edition for each incoming asset and uses its `format_label` largely for technical format. Consequently, the existing Edition row cannot be treated as an exact music release merely because it exists. The target ownership matrix below needs a deliberate data migration and exact release evidence before music release artwork or match writes move to Edition scope.

The managed artwork link table has generic `entity_id` and `entity_type`, so an edition link can be represented without copying an image, but the editor artwork scope currently resolves Books and Audiobooks to item/work targets. A link on an edition is not yet enough for an effective-art resolver or UI override. Movie, show, season, and episode roles already have more specific handling. `media_artwork_writeback` tracks physical embedding separately from metadata retagging; its success state requires read-back.

| Media | Structural parent | Work/content identity | Edition or release | Physical asset | Default artwork owner |
| --- | --- | --- | --- | --- | --- |
| Movie | optional film series | movie | meaningful cut or release | video file | movie work; optional edition override |
| TV | show and season | episode | optional episode version | video file | show, season, or episode according to role |
| Music | release group context | recording and release track placement | exact MusicBrainz release | audio file | exact release |
| Book | optional book series | literary work | ISBN/publisher/translation edition | ebook file | edition |
| Audiobook | optional book series | literary work | narration/release edition | part file with embedded chapters | edition |
| Comic | run or volume | issue | optional printing or variant | comic file | issue; optional edition override |

This matrix is the owner for editor operations. Provider identities need their own types: a MusicBrainz release-group ID does not identify a release, and a recording ID does not establish release-track placement. Work matching may not rewrite another edition's membership. The exact source and migration rules for each edition field need verification before write operations are enabled.

## Technical work packages

### W1: read contracts (walkthrough 1–3)

Return actual Work, Edition, and Asset IDs, ownership IDs, edition labels and counts, and a server-produced collapse hint with each owned row. Keep the large-library endpoint paged. The root navigator remains a small work hierarchy; the owned-file pane renders the optional edition level from the read model.

### W2: session and commit boundary (walkthrough 2–3)

Carry selected node identity separately from edit scope and revision. A staged operation identifies the target owner, affected assets, and expected revision. Saving a shared action displays the scope and refuses a stale revision. No multi-file match is inferred from several checked files.

### W3: identity pairing (walkthrough 2, 4)

Preserve editions when a work is rematched. An asset move must not mutate an edition still used by unselected assets. Music release selection requires exact release evidence; recording identity alone cannot move membership. Multi-episode files remain a review case until a safe relationship exists.

### W4: shared editor tree (walkthrough 1–2)

Render work, optional edition, and physical asset levels from Engine data. Collapse only levels marked redundant by the Engine while retaining their IDs in selection state. Keep the file search, filters, and inspector usable with large collections and narrow screens.

### W5: artwork (walkthrough 3)

Resolve preferred artwork by role and owner. An edition override points to a managed image; removing it restores inheritance without deleting that image. Show whether the selected artwork applies to a work, edition, season, or episode and the number of owned files affected.

### W6: write-back and re-ingestion (walkthrough 4)

Compose writable fields by `ClaimScopeCatalog`, with separate structural, Work, Edition, and Asset sources. Persist namespaced, versioned local edition identity only on supported formats; read it back before claiming round-trip success. Verify one Work with multiple Editions and one Edition with multiple Assets. Report partial field/scope failures instead of a blanket synchronized state.

### W7: integration (walkthrough 1–4)

Use deterministic cross-media fixtures for movie cuts, an episode with two files, original and deluxe music releases, two ISBN book editions, audiobook narration and parts, and a comic variant. Exercise keyboard and responsive layouts, scoped artwork, match preservation, and supported write-back/read-back. Keep unsupported cases explicit.

## Plain-English completion summary

The editor already has a place to store editions, but its current read and write paths do not consistently preserve their meaning. This extension makes the ownership of each displayed version explicit before adding changes that could affect multiple files. Implementation and cross-media verification remain in progress.
