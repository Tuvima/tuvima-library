# Repository inventory and migration risk map

Status: source review for [the proposed Access refactor](./plan.md), not an implemented security audit or a runtime test result.

Baseline: `main`, `3af3ae3cf57dac270dd99b35a408af48e6c2b069`, 2026-09-08. The working tree was clean before these planning files were added. Existing detached worktrees were found; none were changed or repurposed.

## Findings that change the implementation order

| Finding | Evidence in the current repository | Consequence |
| --- | --- | --- |
| Account/grant concepts already exist, but do not own administrator authority | `src/MediaEngine.Domain/Entities/Account.cs`: Account has identity/local/enabled fields; grant has account/profile/default/date fields. | Extend these concepts rather than introduce a parallel user identity. |
| Human and native authority derives from profile role | `src/MediaEngine.Api/Security/TuvimaAuthentication.cs`, `ClientAuthorizationService.cs`; `src/MediaEngine.Domain/Aggregates/Profile.cs`. | Both principal paths must change together; simply replacing Settings permissions would leave the old authority operative. |
| Administrator policy currently requires elevation for session principals | `src/MediaEngine.Api/Program.cs` policy registration; `Security/AdministratorElevationAuthorization.cs`. Non-session admin identities bypass that handler's session-elevation lookup. | Replace the policy with effective-account/grant authority and optional protection. Test no-PIN defaults and service principal distinctions. |
| Native client scope checks have a Dashboard service exception | `Security/ClientScopeFilter.cs` permits the Dashboard service claim or a matching scope. `ClientAuthorizationService.cs` uses a static consumer catalogue and profile-derived role. | Do not preserve a transport-only permission bypass. Bind delegated clients to Application and Account as well as Profile. |
| Account grant mutations need broader transactional invariants | `src/MediaEngine.Storage/AccountRepository.cs` clears defaults transactionally during grant upsert, but the reviewed flow has no maximum-eight check; account endpoint create/revoke spans several repository calls. | Keep existing transaction support; enforce count/default/admin and mutation invalidation as one operation. |
| Local-only creation currently requires one profile | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs`, local account create validation. PIN login resolves a local account from profile identity in the current identity service. | The proposed eight-grant model must address trusted local account selection rather than blindly remove the count check. |
| View already isolates normal Mine/Shared and has resource-specific shares | `Services/View/ViewScopeResolver.cs`, `ViewResourceAuthorization.cs`, `IViewRequestProfileContext.cs`. | Extend the current boundary. Keep Gallery-specific proofs and same-not-found denials. Do not build a second View access subsystem. |
| Another library evaluator allows broader private sharing semantics | `src/MediaEngine.Domain/Services/LibraryAccessEvaluator.cs` supports Household, AuthorizedProfileIds, and role-based admin. | Audit its callers and remove its use as an alternative route to private root access. This source finding alone does not prove an exploitable API path. |
| Shared ownership/contribution infrastructure already exists | `ViewSharedContributionService.cs`, `ViewSharedTransferService.cs`, `ViewSharedContributionHostedService.cs`; View endpoint contribution/admin source routes. | Reuse Shared Library and verified transfer behavior; do not repurpose another profile's Personal Space as Shared. |
| Access UI is already an umbrella but remains fragmented | `UsersAccessSettingsTab.razor` dispatches Accounts, Authentication, API keys, Session Policy, and default Users. `SettingsNav.cs` labels Access as Users & Access. | Consolidate real URLs and remove duplicate concepts. View library settings and Local AI already have appropriate separate areas; do not unnecessarily move them again. |
| Authentication has substantial existing machinery | `FirstPartyIdentityService.cs`, `AccountPasskeyStore.cs`, `ExternalAuthenticationRegistration.cs`, `AuthSettings.cs`, `AccountSettingsTab.razor`. | Reuse account passwords, recovery, passkeys, OAuth/OIDC, SMTP configuration, and sessions. Implement missing configurable policies rather than show inert toggles. |
| Authentication settings are largely explanatory today | `SecurityTab.razor` reports configured status and says session duration is not separately configurable. | The requested controls require backend work; this is not only a form layout assignment. |
| Tool execution lacks plugin identity at its boundary | `src/MediaEngine.Plugins/PluginContracts.cs`: ResolveTool takes pluginId but RunTool does not. `PluginToolRuntime.cs` downloads and launches tools. | Bind the whole runtime to host-established plugin identity and gate side effects. Updating only manifests would not implement enforcement. |
| AI checks declared roles, separate from the host permission list | `PluginAiClient.cs` validates `AiPermissions` and on-demand heavy-role restrictions. `PluginManifest.cs` separately contains Permissions. | Preserve role/resource checks and additionally require the general host capability. |
| Events currently broadcast globally | `src/MediaEngine.Api/Services/SignalREventPublisher.cs:39` uses `Clients.All`; `ProviderHealthMonitorService.cs` has additional direct global sends; Intercom is an empty server-to-client Hub. | Event filtering must include every publisher, not just a new subscription method. Keep external callers away from raw broadcasts. |
| Playback session state exists but is not the requested telemetry model | `src/MediaEngine.Storage/Playback/PlayerSessionRepository.cs` reads profile-scoped current player state/queue and heartbeats. | Build durable lifecycle telemetry from the player, retaining existing playback control semantics. Do not count ingestion Activity as playback history. |
| Existing route checks are source scans | `tests/MediaEngine.Api.Tests/RouteAuthorizationGuardrailTests.cs` uses regex guard detection and explicit exemptions. | Add actual mapped metadata coverage and request behavior tests. A recognized guard name cannot prove account/resource policy correctness. |

## Old-to-new migration map

| Old source of truth or surface | Target | Owning packets |
| --- | --- | --- |
| Profile.Role / AppRoles household tiers | Account eligibility + active grant; profile experience restrictions separate | P02-P05 |
| Session/admin elevation as the default gate | Optional grant PIN with server unlock lifecycle | P02, P05 |
| Profile-specific general library authority | AccountFeatureGrant + catalogued AccountLibraryGrant | P02-P04 |
| ApiKey entity/Role and Guest API Keys | Application + multiple identity-only credentials + permissions | P02, P06 |
| Static ClientApiScopes authority | Central registry; live Application + delegated human intersection | P01-P03 |
| Personal library household/AuthorizedProfileIds | Owner active profile/admin explicit scope, with separate Gallery share proofs | P04 |
| Split Users/Accounts/Profile Grants | Users table and profile-grant management | P05-P06 |
| Separate Session Policy subsection | Server Authentication page | P05, P07 |
| Global role-based UI visibility | Server capability response from one evaluator | P05-P07 |
| Manifest declarations without host gating | Host-bound permission gate and wrappers | P08 |
| No common plugin service authorization contract | Registry-backed gateway and operation descriptors | P09 |
| Global external event access | Event subscription + underlying permission + resource filtering | P11 |
| No application webhook transport | Signed, filtered, persistent bounded delivery | P12 |
| Transient/profile player state alone | Durable session facts and scoped analytics | P10 |

The pre-beta replacement intentionally does not transform legacy key Roles into new permissions. Existing native route/JSON compatibility is independent of preserving obsolete database records. Old source roles and navigation must be removed during their cutover rather than retained as silent fallback until final cleanup.

## Endpoint and consumer inventory for P00

A source search for `RequireAdmin|RequireAnyRole|RequireAdminOrStandardUser|AppRoles|ProfileRole|AuthPolicies` found 48 endpoint files. This is a candidate count, not an endpoint count and not a runtime coverage claim. The registered endpoint matrix must also include service/client metadata and registrations outside that folder.

Assign coverage across these families:

- Identity: Account, Authentication, Profile, Setup, ClientAuthorization, Admin, System, and host recovery/CLI. Separate self-service `/me`, bootstrap/discovery, administrator management, and service integrations.
- Catalogue: Display, Search, Detail, Library/LibraryItem, Work, Person, Character, Timeline, UniverseGraph/UniverseLore, Read/Reader, collections including personal-media sources, artwork/file helpers.
- Experience: Player, Stream, HlsStream, PlaybackSegment, queue/progress/download handlers and public client v1 groups. Include tokens or URLs minted by one handler and consumed by another.
- Canonical mutations: Metadata and its navigator partial, ItemCanonical, Canon, enrichment/refresh/deferred, Review, library mutation/reorganization, and collection administration.
- Administration/integrations: Settings/UISettings, Network, server folders, ingestion/operations/activity, providers/catalogue, AI/capabilities/plugins, reports/maintenance/debug and development seed endpoints. Classify operational resource read versus secret configuration read separately.
- Dashboard edges: `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs`, Dashboard authentication endpoints, View/stream/media proxies, session/assertion handlers and client calls.
- Realtime: `/intercom`, connection token issuance/validation, publisher implementations and direct `Clients.All` send sites.

Inbound credential consumers to inspect:

| Consumer | Files or area | Cutover handling |
| --- | --- | --- |
| Engine API credential authentication | `Security/TuvimaAuthentication.cs`, `Services/ApiKeyService.cs`, `ApiKeyLookupCache.cs`, Storage ApiKeyRepository | Replace with Application resolution and live grants. |
| Retained API-key middleware and role filter | `Middleware/ApiKeyMiddleware.cs`, `Security/RoleAuthorizationFilter.cs` | Determine which parts are live versus retained; remove obsolete authority. Current fluent Require helpers use ASP.NET authorization policies. Do not assume the old Items-based filter is the live route implementation. |
| Key administration | AdminEndpoints, AdminContracts, ApiKeysTab, settings clients | Replace with Application/credential flows and invalidate cached authority. |
| Dashboard transport identity | DashboardEngineAuthenticationHandler, DashboardSessionAccessor, ActiveProfileAccessor/SessionService, ViewProfileAssertion handlers, Web Program/appsettings | Preserve service authentication, require a real user session for interactive authority, remove any stale static-key role assumptions. |
| Native/public client callers | ClientAuthorizationService, ClientAuthorizationEndpoints, ClientScopeFilter, Contracts Authentication, ClientAuthorizationRepository, client source/scripts | Registry IDs and delegated Application/account binding; test paired access/refresh/revocation. |
| Intercom transport | IntercomTokenService and authentication middleware/filter | Session/account audience remains authenticated; add audience/resource filtering rather than broadening a token into all-event access. |
| Provider API keys | ProviderCredentialService, provider configuration, adapters and provider settings | Outbound provider secrets are not Tuvima Application credentials. Do not rename or migrate them with inbound keys. |

## View identifier surface checklist

`ViewEndpoints.cs` currently exposes scopes/preferences, assets/folders, folder pin/timeline policies, uploads, item detail/content/thumbnail, Shared contribution preview/submit/list/detail/cancel/decision/retry/direct-add, share targets, admin per-profile sources/reconcile, Galleries/members/position/shares, and generated item flag handlers. `ViewDiscoveryEndpoints.cs` adds people/places. `CollectionPersonalMediaEndpoints.cs` adds Gallery discovery and collection-linked View sources.

Trace each accepted `profileId`, `scopeProfileId`, `libraryId`, `sourceId`, asset/item ID, folder/path, Gallery ID, and contribution ID from route/body/query into the storage query and original-file operation. Include indirect IDs in saved View rules and collection sources. Browser IDs select a target; they do not establish authority.

## Existing tests to extend

| Area | Existing entry points | Missing target coverage |
| --- | --- | --- |
| Identity | `MediaEngine.Identity.Tests/FirstPartyIdentityServiceTests.cs`, `ProfileServiceTests.cs`; Storage profile invariants | Account/grant admin matrix, transactional eight-profile enforcement, optional grant PIN, Application binding and invalidation |
| Library access | `MediaEngine.Domain.Tests/LibraryAccessEvaluatorTests.cs`; API detail authorization tests | Account features plus actual library grants, resource/cache/count enforcement, removal of private root sharing |
| Native | API `ClientAuthorizationServiceFlowTests`, `ClientApiV1GuardrailTests`, `NativeClientContractFixtureTests`; `tests/native/Test-NativeClientSources.ps1`, `Test-RokuClient.ps1` | Live app/account/profile intersection, re-pairing, revocation, transport-only service restrictions |
| View | API ViewScopeResolver/ResourceAuthorization/RequestProfileContext/ProfileAssertion/ProfilePolicy/EndpointRoute/Discovery/QueryOrchestrator tests | Admin separate scope, cross-profile enumeration across all paths, Gallery-only access, account View gate |
| View storage | ViewLibraryService, ViewStorageService, ViewSharedTransferService, ViewSourceIndexingHostedService tests; Storage View persistence/discovery tests | Shared folder extension without duplicated roots or original-file side effects |
| Web | SettingsNav, TrustedViewProfileAssertion, ViewMediaProxy, ViewLibrarySurface, ViewProfileSettingsUi, ExternalAuthenticationConfiguration tests | Effective capabilities, drawer interactions, optional PIN lifecycle, new navigation and real policies |
| Plugins | API PluginCatalogTests; Web EngineApiClientPluginTests; Storage PluginLoreRepositoryTests | Central host denial before side effects, bound identity, application gateway and registry lifecycle |
| Contracts | `MediaEngine.Contracts.Tests/BoundaryContractGuardrailTests.cs`, `WireContractSnapshotTests.cs`, focused contract tests | New secret-safe Access/event DTOs and unchanged public native shapes |
| Events and telemetry | Existing player repository and Web PlaybackSessionController tests | Authorized event dispatch/revocation, durable telemetry, signed bounded webhook delivery |

The final security audit should execute the full matrix, not treat the existence of these test files as proof that the target is covered.

## Repository constraints to carry into implementation

- Only one root AGENTS.md was found by the reviewed file search. Its pre-beta cutover, Contracts boundary, visual QA, and build/test requirements apply.
- Required local SDK is pinned in global.json; the local Wikidata package feed is a known restore dependency. CI has its own .NET setup and warnings/format/coverage/dependency checks. Use existing checks rather than silently weakening them.
- `scripts/docs/build-docs.ps1` invokes strict MkDocs. The docs workflow also runs `scripts/docs/refresh-codex-context.ps1`; this writes generated `.codex/context` artifacts, so dispatch the docs worker with appropriate writable paths.
- The `.agent/SYNC-MAP.md` maps security, role access, Settings, and realtime guidance to CLAUDE.md. Synchronize these when implemented behavior changes, not during a proposal-only review.
- Existing detached worktrees belong to prior work. Create fresh worker worktrees and keep all runtime databases/config/ports separate.

## Product-owner summary

The repository already has much of the account, authentication, View storage, and playback foundation. The largest changes are moving authority from profiles to accounts, separating applications from credentials, and enforcing permissions consistently at every data entry point. Reusing the existing foundation should save work while keeping the new screens truthful.
