# Vault surface inventory

For WP17 (the full leak sweep). Rule (Videos spec D17): for anyone without an unlocked NSFW session, a Vault item is absent. Every action that lists, searches, counts, streams, serves artwork or thumbnails, reports activity or emits a webhook answers exactly as it would for an item that does not exist: HTTP 404, never 403, same body.

Who is "unlocked": a human on their own Dashboard device session whose profile is eligible (not Kids, no content limit), whose mode allows it (PIN entered, or Allowed), and whose idle limit has not run out. Service apps, paired apps (Android, Apple, Roku, TV web), cast, anonymous callers and other device sessions are never unlocked. Vault membership is a library fact: the item's `media_assets.library_id` is in `vault_libraries`.

Status: **Covered** = gated in the Engine with a named test. **Open** = known gap, owner noted; not a release blocker for WP9 but WP17 must close or consciously accept it.

## Central choke points

| Choke point | Where | Status | Test |
|---|---|---|---|
| Who may unlock (session, profile, mode, expiry, Kids) | `NsfwAccessRepository.IsUnlockedAsync`, `VaultAccessResolver` | Covered | `Vault_TheResolver_NeverUnlocksAnythingButAPersonOnTheirOwnDashboardSession`, `Vault_AKidsOrContentLimitedProfile_CanNeverUnlock_SoItNeverSeesTheVault`, `Vault_ASessionThatIsLocked_LosesTheVaultAtOnce_ForEveryWayItCanBeLocked` |
| Lists built from the display projection | `AuthorizedDisplayProjectionReadService` + `ContentLimitPolicy` (VaultVisibility) | Covered | `Vault_ALockedProfile_SeesNoVaultItemInAnyList_WhateverItsGrantsOrRole`, `Vault_AWorkWithAnyFileInTheVault_IsHiddenAsAWhole_FromALockedProfile` |
| Single file / entity access (detail, stream, download, subtitles, trickplay) | `CatalogueResourceAuthorizationService` + `RequireCatalogue*Access` filters | Covered | `Vault_ALockedProfile_GetsNotFound_NeverForbidden_ForAVaultFile_ForEveryPermission`, `Vault_ALockedProfile_GetsExactlyTheAnswerAMissingFileGets` |
| Artwork and thumbnails of a work or file | `CatalogueArtworkAccessFilter` (everything not Allowed is 404) | Covered | `Vault_AnArtworkVariantOfAVaultItem_IsNeverServedToALockedProfile` |
| Events to apps and webhooks | `ApplicationEventProducer` + outbox trigger `trg_application_events_no_vault` | Covered (trigger) | `AnEventAboutAVaultItem_IsNeverStoredForAppsOrWebhooks_ButAnOrdinaryOneIs` |
| Live pushes to open screens | `IntercomAudienceAuthorizer` (before the administrator shortcut) | Covered | `LiveAudience_VaultEvents_ReachOnlyAnUnlockedSession_AdministratorsIncluded` |
| Personal activity history | `PersonalActivityRecorder` + `trg_personal_activity_no_vault` + repository filter | Covered | `ActivityAboutAVaultFile_IsNeverStored_ButAnOrdinaryFileIs`, `VaultActivity_IsAbsentFromTheListTheDateRailAndTheSummary` |
| Playback telemetry scope | `PlaybackTelemetryRepository.ScopeCommand` | Covered by code; no dedicated test | (WP17: add) |

## Surfaces

| Surface | Locked answer | Status | Test / note |
|---|---|---|---|
| Home shelves, Continue watching, Recently added, Watchlist/My list, favourites | absent | Covered | `Vault_ContinueWatchingAndStates...`, `Vault_RecentAdditions...`, `Vault_Favourites...`, `Vault_HomeCollections...` |
| Library lists, counts, work feed | absent | Covered | `Vault_ALockedProfile_SeesNoVaultItemInAnyList...` (projection); `LibraryWorkFeedReadService` filtered by `VaultSql.NotVaultAsset` |
| Search (collection/work search, people search results built from works) | absent | Covered by projection and `CollectionSearchReadService` filter | covered through the list test; raw filter has no dedicated test (WP17: add) |
| Work / person / collection detail pages | 404 | Covered | `Vault_ALockedProfile_GetsNotFound...` |
| Stream, HLS, direct download, subtitles | 404 | Covered | resource authorization tests above; the HLS endpoint's 404 mapping is code-covered only |
| Artwork routes for Vault items | 404 | Covered | `Vault_AnArtworkVariantOfAVaultItem...`; `GET /api/v1/vault/artwork/{id}` returns 404 unless unlocked |
| Unlocked session | visible, Watch feature required, account library grants not needed | Covered | `Vault_AnUnlockedProfile_SeesVaultItems...`, `...OnlyWithTheWatchFeature` (a missing Watch feature also answers 404) |
| Service apps, paired apps, cast | absent always | Covered | `Vault_AServiceApplicationAndAPairedApp_NeverSeeTheVault...` |
| Needs Review queue and counts | absent (even for unlocked, until a viewer-aware version exists) | Covered by code (`VaultSql.NotVaultEntity` on four queries) | no dedicated test (WP17: add) |
| Library overview: recently added, ingestion jobs | absent | Covered by code | no dedicated test (WP17: add) |
| Applications / webhooks outbox | never stored | Covered | trigger test above |
| NSFW state, PIN, access routes | 404 for ineligible callers; PIN failures 428/429 | Covered | `VaultAdministrationServiceTests`, `NsfwPinTests`, `VaultAccessStorageTests` |

## Open items for WP17

1. Ingestion queue progress counts: `media_operations` has no library, so a Vault file's progress can show in an administrator's counts.
2. Read services not yet viewer-aware and not filtered: `CollectionBrowse`, `CollectionCatalog`, `CollectionMediaLookup`, `LibraryCuration`, `ArtworkLibrary`, `ProfileOverview`, `IngestionPresentation`. They are administrator or settings screens; they must hide the Vault even when unlocked until WP10/12 add viewer-aware versions.
3. Canonical artwork library routes: Vault artwork is meant to live only in `vault_artwork`; ingestion (WP11/12) must never write Vault artwork to the shared artwork tables.
4. `system_activity` entries can name a Vault file (for example an ingestion step). Decide whether the Engine log view must redact them.
5. Shared records: a Vault work must never merge with an ordinary work or join a shared Series, Person or Tag. The data structure keeps Vault names in separate tables, but ingestion non-linkage is not trigger-enforced.
6. Library creation writes only the `vault_libraries` row. The folder, name, `libraries.json` entry and one-library-per-type handling belong to ingestion work (WP11/12).
7. Telemetry exclusion, Needs Review, library overview and collection search filters have code coverage only; each needs a dedicated test.
8. Dashboard (WP10): idle lock and profile-switch lock are enforced by the Engine; the Dashboard must call keepalive and lock.
