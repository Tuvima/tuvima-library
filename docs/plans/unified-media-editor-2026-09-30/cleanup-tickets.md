# Unified media editor cleanup tickets

**Status:** Ready for implementation planning; no feature work is claimed by this ticket set.
**Source of truth:** The requested [implementation ledger](implementation.md) and [format capability audit](file-capabilities.md), checked against the current code. The referenced “Partial editor update” conversation is a gap report, not an implementation instruction or proof that a feature works. The supplied mockups guide layout and interaction; their example titles, counts, and confidence scores are not library data.

## Product walkthrough

1. **Keep one editing session while choosing files and moving among tabs.** A user checks the owned files to change, then edits Details, Match & Identity, and Artwork without any tab saving behind their back. The editor remembers the checked set, focused file, draft values, and destination owner. This fixes the current mix of footer saves and immediate mutations. **Tickets C01, C02, C04.**
2. **Review the whole change before committing it.** A review shows each selected file's proposed parent/episode/track, the exact owner of every metadata or artwork change, affected unselected siblings that inherit shared art, and any excluded or blocked row. Stale revisions, uncertain identities, or unauthorized descendants stop Save while keeping the draft available to correct. **C02, C03, C05–C08.**
3. **Save or discard once.** One Save makes the reviewed library changes as a single authorized database operation and records durable file work. Discard leaves the library as it was. The footer then reports what is committed, still synchronizing, blocked by policy, unsupported, or failed; it never calls a file verified before read-back. **C03, C04, C09.**
4. **Make the important corrections within that same experience.** A selected episode can move to another show; tracks can be assigned to a proven MusicBrainz release and its distinct release tracks; an Edition can receive its own cover without changing sibling Editions. After a move, the editor follows the moved file to its new hierarchy. **C05, C06, C07, C08.**
5. **Trust the result after interruption and re-ingestion.** Supported file fields survive a restart and a provider-offline rebuild when their adapters actually write and read them. Unverified formats and fields remain visibly unsupported or pending. Real-format fixtures and reviewed API contracts establish the release boundary. **C09, C10, C11, C12.**

Representative journeys: (a) select one wrongly matched episode from a show, move it to another authorized show, change its still, review both shows' impact, Save, and keep inspecting the moved file; (b) select several tracks, choose an exact original or deluxe release, resolve repeated recordings by release Track ID, Save, and see per-file sync outcomes; (c) change a book Edition's title and cover when it has EPUB and PDF assets, with another Edition untouched. Existing browse, playback, detail routes, permissions, local file policies, and History remain available. This cleanup does not introduce transcoding, splitting, arbitrary hierarchy dragging, automatic quality ranking, or blanket file-format support.

**Observable acceptance:** Each journey uses one checked selection, one complete impact review, and one Save/Discard decision. Discard and rejected saves leave durable library state unchanged. A successful Save cannot partly apply Details, Match, and Artwork in SQLite; file synchronization may complete later and must report its own state. Desktop, tablet, mobile, and 200% zoom keep selection, review, and actions usable. A file moved out of the original parent remains reachable. Unsupported write-back is stated before mutation. The release gate in C12 requires demonstrated tests and visual review, not merely completed code.

## Execution contract and dependency order

These are **one connected cleanup epic**, not independently releasable feature tickets. Sol owns the architecture, contracts, integration, and final acceptance. The requested Terra worker model is unavailable in this session; Sol workers performed the bounded code audits. When Terra is available, assign implementation tickets to Terra against the Sol-approved contracts and keep the integration gate with Sol.

```text
C01 session/ownership contract
  ├─ C02 complete review and draft staging ─ C03 atomic commit ─ C04 unified Dashboard Save
  │                                      ├─ C05 cross-show TV + C08 post-move navigation
  │                                      ├─ C06 exact music identity and save
  │                                      └─ C07 Edition cover and reconciliation
  └─ C09 durable file sync and fingerprinting ─ C10 offline re-ingest proof
                                                └─ C11 real MKV capability gate
All applicable tickets ─ C12 cross-media release acceptance and contract review
```

The dependency diagram is the release order, not a claim that every implementation task must be serial. C05 and C06 can develop in parallel once C01–C03 contracts are fixed. C11 is a capability investigation and may run in parallel; rich MKV support stays disabled unless its gate passes. Keep the existing same-show TV path working throughout migration. Preserve the current supported Details controls, provider refresh, artwork upload/removal, history, and policy checks while adapting their save behavior.

## Technical cleanup tickets

### C01 — Define one editor session and explicit ownership

**Work package:** W1/W2/W4. **Owner:** Sol contract; Terra implementation when available. **Depends on:** none.

- Specify a typed draft keyed by target entity, profile, edit scope, and revision. Keep checked assets separate from the focused inspector row. Model Details, identity, artwork, Edition, and file-specific changes with their actual owners; never apply a pre-move draft to a new parent by inference.
- Define a server-read capability/ownership matrix for Work, Edition, Asset, show, season, episode, album release, and track, including truthful affected descendant counts and a 1,000-file frozen selection limit.
- **Done when:** tab switches retain every draft; an unrelated target cannot inherit it; changing selection/owner invalidates the relevant review; the navigation guard sees the same pending state as Save/Discard. Add focused session, selection, and owner tests.
- **Code seams:** `SharedMediaEditorShell.razor.cs`, `SharedMediaEditorShell.SharedEntity.cs`, `SharedEntityEditorWorkspace.razor.cs`, `MediaEditorNavigationGuard.cs`, owned-child read contracts.

### C02 — Stage every mutation and review the complete impact

**Work package:** W2/W4/W5. **Owner:** Terra UI/API against C01 contract. **Depends on:** C01.

- Convert currently immediate Match candidate, artwork URL/upload/preference/removal, and Details actions into drafts. Preview the exact accepted/excluded files, destination identities, proposed claims, artwork owners, inherited siblings, permissions, capability, and post-commit file work. Preserve existing individual correction and refresh actions with explicit handling where an action truly cannot be staged.
- Bind review to actor, selected IDs and revisions, owners, provider/local proof, and proposed values. Recompute or reject on any changed fact. Candidate and preferred-image styling must distinguish *draft* from *saved*.
- **Done when:** Discard after editing multiple tabs changes no durable state; review lists all directly and indirectly affected files; stale or incomplete review blocks Save without losing user choices; no tab silently commits an editor change.
- **Code seams:** shell candidate handlers, artwork handlers, `MediaEditorPairingPreview.razor`, `MetadataEndpoints.ParentFirstPairing.cs`, artwork and shared-entity endpoints.

### C03 — Commit the reviewed change as one library transaction

**Work package:** W2. **Owner:** Sol architecture/integration; Terra Storage/API implementation. **Depends on:** C01, C02.

- Extend the guarded TV commit pattern to a composite, actor-authorized plan covering Details, pairing, artwork, Edition, preference, and applicable file-level facts. Recheck revisions, identity proof, complete descendants, and permissions in the transaction. Persist one operation receipt and durable post-commit intents with idempotent replay. Keep provider requests and physical file writes outside the SQLite lock.
- Define explicit conflict and recovery semantics: an invalid plan commits nothing; retry of an already committed operation returns its original receipt; a file-sync failure cannot roll back a committed library transaction and must remain visible as pending/failed work.
- **Done when:** a forced late SQL error rolls back all library edits and intents; concurrent or replayed Save cannot duplicate them; a changed parent, artwork preference, or selected-file revision produces a conflict; every committed descendant receives the correct sync intent.
- **Code seams:** `MediaEditorCommitRepository.cs`, `MetadataEndpoints.ParentFirstPairing.cs`, shared entity/canonical endpoints, `ApplicationEventOutboxWriter.cs`. An event outbox alone is not yet a file-mutation command queue.

### C04 — Wire one Dashboard Save, Discard, and navigation boundary

**Work package:** W4. **Owner:** Terra Web implementation; Sol integration. **Depends on:** C03.

- Replace the independent pairing Save and piecemeal shell save with one footer review/Save/Discard path for the complete draft. Keep pending changes when Save conflicts or synchronization remains incomplete; show committed versus file-sync state separately. Save-and-leave and Save-and-switch must use the same receipt and guard.
- **Done when:** a combined title, match, and cover edit requires one review and one Save; closing, routing, or changing targets prompts for every unsaved kind; retry does not apply already committed work twice; failed Save never navigates away or drops selected files.
- **Code seams:** `SharedMediaEditorShell.razor(.cs)`, `SharedEntityEditorWorkspace.razor.cs`, `MediaEditorPairingPreview.razor`, `MediaEditorNavigationGuard.cs`.

### C05 — Move reviewed TV episodes between shows

**Work package:** W2/W3. **Owner:** Terra API/Storage implementation; Sol contract review. **Depends on:** C03.

- Represent source and destination show/season identities independently. Authorize both hierarchies and every affected file; update both ownership summaries and inherited-artwork impacts atomically. Preserve the rule that assets belonging to one Edition move together, or make an explicit, reviewed Edition split before permitting a subset.
- **Done when:** a selected episode moves between two existing authorized local shows; excluded episodes and siblings stay put; an emptied origin is valid; stale parents, unauthorized destinations, ambiguous episodes, and incomplete Edition selections fail without partial changes. Add route, review-token, rollback, replay, and storage tests.
- **Code seams:** `MetadataEndpoints.ParentFirstPairing.cs` currently only issues a Save token for same-show sources; `MediaEditorCommitRepository.VerifiedTvEpisodeMove` carries one show identity.

### C06 — Prove exact music release-track identity, then enable Save

**Work package:** W1/W2/W3. **Owner:** Terra identity/Storage/API implementation; Sol identity review. **Depends on:** C01–C03.

- Persist and read an unambiguous `(MusicBrainz release MBID, release Track MBID)` target at Edition/track scope. Keep Recording MBID separate; retain disc/track position as ordering, never as substitute identity. Reconcile meaningful release Editions with an auditable dry run; conflicting canonical and bridge claims or legacy technical Editions remain blocked until resolved.
- Issue reviewed music Save tokens only for exact, authorized local targets and commit accepted mappings with the composite transaction. Preserve read-only preview when target proof is missing.
- **Done when:** original and deluxe releases sharing one recording stay distinct; repeated recordings on one release map to distinct release tracks; missing Track MBID, release-group-only evidence, title/position similarity, changed membership, or conflicting release IDs cannot Save. Cover original/deluxe, multiple encodes, replay, and rollback in tests.
- **Code seams:** `ParentFirstPairingEngine.cs` already retains release Track MBIDs; `PairingAssetReadService.cs` distinguishes Edition evidence from album context; `MetadataEndpoints.ParentFirstPairing.cs` currently disables music Save.

### C07 — Expose staged Edition and exact-release cover editing

**Work package:** W1/W2/W5. **Owner:** Terra API/Web implementation; Sol ownership review. **Depends on:** C03; C06 for music release covers.

- Add a public, authorized Edition cover owner/scope and staged preference review using the existing guarded Edition artwork repository. Preserve Work inheritance, legacy asset mirror/resolver behavior, and sibling Edition isolation. Show the actual owner and every inheriting file before Save; later assets in that Edition inherit the preference.
- **Done when:** changing one book, comic, audiobook, or movie Edition cover affects only that Edition; a music cover requires exact release identity; stale artwork hash/revision, unauthorized descendants, and incomplete impact block Save. Add API authorization/staleness and UI scope tests.
- **Code seams:** `MediaEditorEditionArtworkRepository.cs` has internal verification; focused-file effective-cover reading exists, but the public mutation path does not.

### C08 — Follow a moved file and refresh both hierarchies

**Work package:** W4. **Owner:** Terra Web/contracts implementation. **Depends on:** C05 and the C03 receipt contract.

- Return authorized destination owner identities in committed and replayed receipts. Refresh source and destination browser state and keep focus on the moved asset. Define behavior for a batch with several destination parents rather than arbitrarily choosing one.
- **Done when:** the moved file stays inspectable after Save, including when its old show is empty; retry returns the same destination; multiple destinations present an explicit choice; unsaved remaining drafts still trigger the navigation guard.
- **Code seams:** `MediaEditorPairingPreviewDto.cs` receipt lacks destination identity; `SharedMediaEditorShell.razor.cs` currently reloads the original parent after pairing Save.

### C09 — Make physical file synchronization durable and truthful

**Work package:** W2/W6. **Owner:** Terra Ingestion/Storage implementation; Sol recovery review. **Depends on:** C03 intent contract.

- Persist a per-asset write intent, serialize writes to an asset, recover across restart, and coalesce effective metadata/artwork from all changed owners. Record capability/policy outcomes before mutation. Verify through independent reopen/read-back, then update the content fingerprint and watcher/hash cache; distinguish this fingerprint from the applied configuration hash.
- **Done when:** crash before write, after write, and before completion recording converges on restart without duplicate assets or false success; own watcher events and same-size/near-time external edits preserve asset ID and user state; blocked, unsupported, pending, and verified are distinct receipts. Continue restoring source bytes after failed verification.
- **Code seams:** `WriteBackService.cs`, `AssetHasher.cs`, `FileHashCacheRepository.cs`, `IngestionEngine.Watching.cs`, `media_operations`/event outbox. Current direct writes and status updates do not supply durable file-command replay.

### C10 — Prove portable scoped identity with provider-offline re-ingestion

**Work package:** W1/W6. **Owner:** Terra Ingestion/tests implementation; Sol evidence review. **Depends on:** C06 for music identity and C09 for verified writes.

- Define versioned scoped IDs for supported formats and read them through production ingestion. Run two distinct proofs: same-database refresh after a write and fresh-database reconstruction with provider clients that fail if called. Change filenames between write and ingest where supported so path coincidence cannot satisfy the test.
- **Done when:** the new database restores only the proven Work/Edition/Asset/release-track identity, sequence, canonical fields, and artwork that were actually embedded; unsupported or provider-only facts remain unknown; existing profile progress is not misrepresented as file-portable. Keep real archive/media integrity assertions.
- **Code seams:** `Mp4VideoReadbackTests.cs` leaves TV atoms unverified; `ArchiveMetadataReadbackTests.cs` leaves custom EPUB keys unverified; production ingest and per-format capability audit define the actual promise.

### C11 — Gate rich Matroska support on real-file evidence

**Work package:** W6. **Owner:** Terra format spike; Sol packaging/capability decision. **Depends on:** C09 for production integration; investigation can start earlier.

- Create or document redistributable genuine MKV fixtures and evaluate the actual writer, license, packaging, platform behavior, and rollback path. For each proposed field, prove write, independent reopen/extraction, and preservation of unrelated streams, chapters, subtitles, and attachments. Keep generic title support separate from rich IDs/artwork.
- **Done when:** each advertised MKV capability has a genuine-container fixture and a passing safety/read-back test; otherwise the UI and receipts say unsupported before backup or mutation. An invalid byte array with an `.mkv` extension is only a rejection test.
- **Code seams:** `VideoMetadataTagger.cs`, `MetadataTaggerCapabilityTests.cs`, `VideoMetadataTaggerSafetyTests.cs`, ingestion test fixture inventory.

### C12 — Close cross-media, visual, and wire-contract acceptance

**Work package:** W7. **Owner:** Sol release gate; Terra test/visual evidence when available. **Depends on:** all applicable tickets.

- Exercise one-file and multi-file cases for TV, original/deluxe music, several assets per Edition, sibling Editions, movie, book, comic, and audiobook; include ambiguous rows, permission loss, stale revision, failure/retry, and offline restart. Inspect desktop/tablet/mobile/200% zoom against the supplied compositions, with actual data and truthful image rendition sizes.
- Review API/DTO snapshot diffs deliberately and approve only intended changes. Replace source-text correctness assertions with behavior tests where useful; do not regenerate broad snapshots or expand allowlists to hide failures. Track unrelated baseline failures separately, including the missing `subdl.svg` Web fixture and existing Contracts snapshot/legacy DTO drift.
- **Done when:** integrated build and focused behavior suites pass; provider-backed and real browser journeys are recorded; no editor regression is hidden by the pre-existing solution-wide failures; the implementation ledger and format capability audit reflect the verified result.

## Plain-English completion summary

This ticket set turns the remaining editor gaps into one ordered delivery: draft everything, review the full effect, save the library change once, then verify and recover file updates. It identifies the specific proof needed for cross-show TV moves, exact music releases, Edition covers, and offline restoration. The current editor remains a partial implementation until these tickets pass the shared release gate.
