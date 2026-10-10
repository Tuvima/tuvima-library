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

1. **Keep one active editing target.** The header names the selected Work, Edition, or Asset, while adjacent scope selectors show parent and sibling context. Ordinary single-file items stay compact; meaningful edition or release levels appear only when Engine evidence supports them. **Work packages W0, W1, W4.**
2. **Show versions without treating them as a batch edit.** A movie with multiple cuts, an episode with multiple encodes, or a book with multiple editions remains under one content identity. Selecting a version changes the active target; it does not silently select siblings or apply the first file's facts to the group. **W1, W2, W3, W4.**
3. **State the owner and persist supported artwork directly.** The UI identifies whether a change belongs to a Work, Edition, season, episode, or Asset. Supported artwork selection, import, and unlink persist at that owner; inheritance resumes when an override is removed. **W1, W2, W5.**
4. **Review exact music relocation.** Recording identity alone cannot move a track between releases. One selected music file can move after the Engine freezes an exact MusicBrainz release manifest and the user chooses its release Track ID; source revision, destination, sibling, and job checks protect the atomic save. Other cross-owner moves without a dedicated transaction remain blocked. **W2, W3, W6.**

Acceptance is observable with one Work and one file, one Work with multiple Assets, one Work with multiple meaningful Editions, one Edition with multiple Assets, a show with two versions of one episode, and original and deluxe music releases. Each journey has one highlighted target. A reviewed exact release-track move changes only its selected file; unsupported or ambiguous cross-owner moves preserve the current identity. A multi-episode physical file with no safe many-to-many representation enters review with its evidence retained.

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

Return actual Work, Edition, and Asset IDs, ownership IDs, edition labels, and bounded selector context for the current target. Large sibling sets are queried through a searchable selector; they are not rendered as a persistent owned-file pane.

### W2: session and commit boundary (walkthrough 2–3)

Carry the selected target identity, owner, and revision together. Immediate artwork actions snapshot that context before awaiting and ignore stale UI results. Identity Apply refuses a stale revision. The editor exposes no multi-file checklist or batch Save surface.

### W3: identity pairing (walkthrough 2, 4)

Preserve editions when a work is rematched. An asset move must not mutate an edition still used by unselected assets. Music release selection requires exact release evidence; recording identity alone cannot move membership. Multi-episode files link to every episode they cover only when the filename gives an unbroken run that the TV provider's episode list confirms (up to six episodes); anything else stays in Review, where the **This file covers** picker fixes it.

### W4: shared editor tree (walkthrough 1–2)

Render Work, optional Edition, and Asset choices through adjacent context selectors. Collapse only levels marked redundant by the Engine while retaining their IDs in target state. Keep selector search and the target inspector usable with large collections and narrow screens.

### W5: artwork (walkthrough 3)

Resolve preferred artwork by role and owner. An edition override points to a managed image; removing it restores inheritance without deleting that image. Show whether the selected artwork applies to a work, edition, season, or episode and the number of owned files affected.

### W6: write-back and re-ingestion (walkthrough 4)

Compose writable fields by `ClaimScopeCatalog`, with separate structural, Work, Edition, and Asset sources. Persist namespaced, versioned local edition identity only on supported formats; read it back before claiming round-trip success. Verify one Work with multiple Editions and one Edition with multiple Assets. Report partial field/scope failures instead of a blanket synchronized state.

### W7: integration (walkthrough 1–4)

Use deterministic cross-media fixtures for movie cuts, an episode with two files, original and deluxe music releases, two ISBN book editions, audiobook narration and parts, and a comic variant. Exercise keyboard and responsive layouts, scoped artwork, match preservation, and supported write-back/read-back. Keep unsupported cases explicit.

## Plain-English completion summary

The editor exposes meaningful Editions and physical files while keeping one active target. Supported owner-scoped artwork changes directly, and uncertain identities retain their current placement. Exact one-file MusicBrainz release-track relocation has an atomic save; other unimplemented cross-owner moves remain blocked. Broader live provider and cross-media verification remains in progress.
