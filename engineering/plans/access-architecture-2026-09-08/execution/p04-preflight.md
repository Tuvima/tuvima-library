# P04 View privacy preflight

Status: preflight only at integration SHA `7060a83720a339118715ab4e25ca6e805f8d610b`. P04 implementation remains gated on the P02 schema and request-context commit. P04 consumes P01/P02 `RequestAuthority`; it must not add fields or reconstruct authority from claims, profile roles, route IDs, Dashboard headers, or saved preferences.

## Fixed authorization shape

- Human and delegated View operations require `AccountFeatureId.View` under the common evaluator, an enabled account/grant and exact active profile; delegated clients also retain the Application/consent intersection. Service Applications require the live available View service permission and its resource rules, without inventing an account feature grant. Ordinary Applications using `view.personal.*` require a validated delegated human binding.
- An effective human administrator may select one explicit existing profile's Personal Space. A service administrator Application is the accepted exception: it may read one explicit target profile without a fabricated human/default-profile binding, only while the relevant registered View read service is available, and the selection is audited. `PermissionRegistry.RequiresUserContext` for `view.personal.read`/originals must not erase this exception or weaken ordinary-Application checks. The target profile stays in `ResourceAuthorizationContext`, not `RequestAuthority`.
- Mine always means the active profile's one Personal Space. Shared is the server-owned Shared library, never a union. Ordinary scope options are Mine and Shared; only effective admins/admin Applications receive individual Profile options.
- An explicit unauthorized `scope=profile&scopeProfileId=...` is not-found-equivalent. Fallback is allowed only when resolving a stale saved preference, so `ViewEndpoints.GetScopeAsync` must preserve whether input was explicit or persisted.
- A Gallery grant authorizes that Gallery and its proven manual/smart members and derivatives only, with View still enabled. It never authorizes the owner's root, folders, siblings, or arbitrary assets. Revocation must affect the next asset/original/thumbnail request.

## Current call sites and required cutover

| Surface | Current path | P04 change |
| --- | --- | --- |
| Scope/preferences/timeline | `ViewEndpoints` `/scopes`, `/preferences`, `/assets`; `ViewScopeResolver`; `ViewQueryOrchestrator`; `ViewAssetQueryService` -> `LocalAssetRepository.QueryTimeline` | Replace `ViewRequestProfile(ProfileId, Role)` with the P02 request authority accessor; gate View plus `view.personal.read` or `view.shared.read` before forming `ViewAssetQueryPlan`. Add exact admin Profile resolution. Keep physical library IDs backend-only and in cache/query keys. |
| Counts/folders | `ViewFolderService.ConfiguredSourcesAsync`, `CountItems`, `QueryPaths`, `QueryItemIds`, pins and breadcrumb construction | Resolve the exact authorized library/source before every count or path query. Pins remain viewer-local. Timeline-policy writes require owner or effective human admin and the filesystem mutation gate; admin read selection alone grants no mutation. Replace the synthetic single Shared source with persisted Shared sources. |
| People/Places | `ViewDiscoveryService` -> `ViewDiscoveryRepository.QueryPeople/QueryPlaces` | Keep authorization before SQL and preserve library/Shared predicates inside the aggregation, so counts, cursors, representative IDs, capability flags, and search cannot include another scope. |
| Asset/thumbnail/original | `ViewEndpoints` `/items/{id}`, `/content`, `/thumbnail`; `ViewResourceAuthorizationService`; `ViewResourcePersistenceService`; `LocalAssetRepository.ResolveContent` | Apply View plus resource permission (`view.originals.read` for content; appropriate personal/shared and Gallery checks for metadata/thumbnail) before lookup. Resolve a verified `local_file_sources` row belonging to the authorized library/source; global `local_files.content_hash` dedup must never select a private path sharing the same file ID. Return the same 404 shape for missing and denied. |
| Galleries | `ViewEndpoints.MapGalleries`; `ViewGalleryRepository` including `IsItemSharedWithProfileAsync` | Gate list/create/read/write with View and `view.galleries.read/write`. Preserve repository ownership checks and manual/smart membership proof. Validate every added/cover item against the Gallery's exact Personal Space. Share-target discovery remains policy-gated and contains no media facts. |
| Upload/personal mutation | `/uploads`, flag/lifecycle helpers, `ViewLibraryService.UploadAsync` | Upload has no caller-supplied destination today; retain that property and bind it to the active human profile with View + `view.upload`. Do not let an admin Profile read selection redirect upload, flags, archive, trash, or restore into another profile. |
| Contributions/transfers | contribution endpoints; `ViewSharedContributionService`; `ViewSharedTransferService` | Replace profile-policy-only decisions: submit/cancel remain exact contributor operations; review/decision/retry/direct-add require effective human admin plus the independent policy. Recheck contributor ownership and View at acceptance. Retain preview revision, idempotency, verified-copy-before-publish, and cleanup-pending recovery. |
| Admin sources | `/view/admin/profiles/{profileId}/sources` and `/reconcile` | Replace `.RequireAdmin()` role checks with effective admin-surface authority. Profile routes bind exactly to the target. Add `/view/admin/shared/sources` CRUD/reconcile using the stable Shared library and the same path validation/indexing model; no user-facing duplicate library. |
| Collection expansion | `CollectionPersonalMediaEndpoints`; `CollectionPersonalMediaService`; `CollectionViewSourceRepository.GetAuthorizedProjectionAsync` | Remove `ProfileRole.Administrator` and profile-only collection decisions. Authorize the collection, View, selected exact profile, and each Gallery/rule source before projection/expansion. A shared Gallery reference exposes only its members; a private smart rule remains owner/exact-admin scoped. Keep the schema ban on individual local-asset references. |
| Dashboard proxy handoff (P05-owned) | `ViewMediaProxyEndpoint`, `ViewMediaGrantService`, `ViewMediaEngineClient`, `ViewProfileAssertionHandler` | Treat the opaque grant's profile/library fields as selectors only. Forward the P02 interactive authority snapshot and make the Engine reauthorize the exact asset on every GET/HEAD/range request; no seed Owner or transport-only fallback. |

## P02 DDL required before dispatch

The current `view_sources` and `local_items` require a Personal Space/profile, while `view_shared_assets` merely marks the same profile-owned item. That cannot represent server-owned Shared folders and makes Shared depend on private identity. P02 should rebuild the pre-beta schema as follows:

```sql
CREATE TABLE view_shared_library (
  singleton_key INTEGER PRIMARY KEY CHECK (singleton_key = 1),
  library_id BLOB NOT NULL UNIQUE,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
-- Rebuild view_sources: add scope_kind CHECK IN ('personal','shared'),
-- nullable personal_space_id, and explicit library_id.
-- Rebuild local_items: add scope_kind; personal_space_id and owner_profile_id nullable.
-- CHECK personal => both personal IDs present; shared => both are NULL.
-- Extend view_shared_assets with origin_item_id BLOB NULL REFERENCES local_items(id) ON DELETE SET NULL;
```

Use `BEFORE INSERT/UPDATE` triggers (or composite foreign keys with equivalent strength) on `view_sources` and `local_items`: personal rows' `(personal_space_id, library_id)` must match `view_personal_spaces`, and personal items' `owner_profile_id` must also match that space's owner; shared rows' `library_id` must match singleton `view_shared_library`. Add partial unique indexes for personal `(personal_space_id, source_key)` and Shared `(library_id, source_key)` because NULL does not protect Shared uniqueness. Bootstrap one stable Shared library ID only after checking it does not collide with a Personal Space or other trusted server library identity, and reject later cross-scope collisions; never infer Shared from NULL and never create a synthetic profile.

P04 can own a new `IViewSharedLibraryRepository` (`GetAsync`, `GetSourcesAsync`, `UpsertSourceAsync`, `DeleteSourceAsync`) and its Storage implementation, plus explicit scope fields in `IViewAssetQueryBackend` plans and `IViewResourceStore` descriptors. P02 owns the DDL/bootstrap. Transfer acceptance should create a distinct Shared-scoped `local_items` row after all destination files verify, reuse global hash-deduplicated `local_files`, attach only Shared-library/source `local_file_sources`, and retain `view_shared_assets.original_profile_id` plus `origin_item_id` provenance. Linked personal items remain; managed personal cleanup starts only after Shared publication verifies.

## Tests to reuse and gaps

Reuse `ViewScopeResolverTests`, `ViewResourceAuthorizationTests`, `ViewQueryOrchestratorTests`, `ViewDiscoveryServiceTests`, `ViewGalleryShareTargetTests`, `ViewSharedTransferServiceTests`, Storage View persistence/discovery/smart-Gallery tests, and `CollectionViewSourceRepositoryTests`. Reverse the current tests that expect an explicit unauthorized Profile request to fall back.

Add one matrix across scope, counts, folders, People/Places, gallery/list/member, item/thumbnail/original, collection source, contribution ID, and admin source IDs: A cannot distinguish B from missing. Add exact B-selected admin isolation from A/C; same admin account on a non-admin-enabled grant; owner with account View denied; ordinary versus admin Application behavior; Shared read from account View; Gallery member-only access and immediate revoke; spoofed upload/profile/source rejection; verified Shared file-source selection under global hash dedup; multiple Shared sources with no profile owner; and clean read/profile registration producing no directories. Keep all existing transfer safety cases for pending/declined, linked copy, managed move, collision, changed source, and cleanup retry.

Plain English: P04 will make every View list, count, file, Gallery, upload, contribution, and collection reference use one trusted access decision. Private profiles stay separate, while Shared becomes a real server-owned library with multiple explicit sources and preserved transfer provenance.
