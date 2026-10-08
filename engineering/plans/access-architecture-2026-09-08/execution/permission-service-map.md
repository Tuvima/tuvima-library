# Permission-to-service map

## Integration update — 2026-09-09

The baseline table below is historical. Implemented capability changes: `library.changes.read` and `events.subscribe` now have the durable scoped event service (final producer/edge acceptance remains open); `playback.sessions.read`, `playback.history.read`, and all four `analytics.*.read` IDs have distinct scoped durable read services. `playback.sessions.control`, `system.metrics.read`, `system.audit.read`, `system.logs.read`, `identity.sessions.read`, and `ai.infer` remain unavailable with reasons. P08 enforces host-bound capabilities; P09 exposes the bounded Fandom Lore operation only while its plugin/service is available.

Native supported consent adds `library.changes.read` and `events.subscribe` explicitly. The existing eight default playback scopes stay unchanged; existing device consent is not silently expanded. Source-owned DTO/wire shapes remain under the contract snapshot gate.


Status: source-derived inventory at tentative SHA `1b75af4e76ad4b4afc9ce23769877566f6ec10e2`. `Existing` means a backing route/service exists, not that it already enforces this permission correctly. `Unavailable` is the required P01 registry state until the named packet supplies a tested service. The registry must preserve every ID below.

| Permission ID | P01 state | Existing service/route | Enforcement owner or unavailable reason |
| --- | --- | --- | --- |
| `system.status.read` | Existing | `/system/status`, `/system/readiness`, Engine health | P03; status is currently partly public |
| `system.metrics.read` | Unavailable | Internal `PerformanceMetrics`; no application metrics API | P10 or later explicit service |
| `system.activity.read` | Existing | `/system/activity-status`, `/activity`, `/operations` | P03 |
| `system.audit.read` | Unavailable | Activity/batch records are not a general security audit API | P02 audit writer, later read API |
| `system.logs.read` | Unavailable | No log read endpoint | Later explicit service |
| `library.read` | Existing | `/api/v1/display`, `/api/v1/details`, `/library`, `/works`, `/persons`, `/collections` | P03, filtered by feature/library authority |
| `artwork.read` | Existing | `/stream/...cover|background|logo`, portrait/headshot routes | P03; include Dashboard image proxy |
| `library.changes.read` | Unavailable | Internal SignalR events are global and are not an authorized change feed | P11 |
| `library.files.read` | Existing | library item detail/history and asset/file metadata | P03; must exclude stream/original rights |
| `playback.read` | Existing native | `/api/v1/playback/*`, player capabilities/state | P03 |
| `playback.write` | Existing native | player commands/takeover, encode requests | P03 |
| `queue.read` | Existing native | `/api/v1/player/state` | P03 |
| `queue.write` | Existing native | `/api/v1/player/queue/*` | P03 |
| `progress.read` | Existing native | `/api/v1/progress/*`, audiobook history/bookmarks | P03 |
| `progress.write` | Existing native | progress/status/history and player heartbeat/bookmarks | P03 |
| `downloads.read` | Existing native | encode jobs and offline variant reads | P03 |
| `downloads.write` | Existing native | encode/cancel and offline creation path | P03 |
| `playback.sessions.read` | Unavailable | Current profile player state is not all-session telemetry | P10 |
| `playback.sessions.control` | Unavailable | Existing commands target the bound player; no authorized cross-session service | P10 |
| `playback.history.read` | Unavailable | Audiobook history is narrow profile state, not durable playback history | P10 |
| `analytics.playback.read` | Unavailable | No durable analytics service | P10 |
| `analytics.library.read` | Unavailable | Existing counts are product views, not an application analytics contract | P10 |
| `analytics.users.read` | Unavailable | No user analytics service | P10 |
| `analytics.devices.read` | Unavailable | Device list lacks durable playback aggregates | P10 |
| `metadata.read` | Existing | `/metadata/claims|canonical|editor-context`, timeline | P03 |
| `metadata.write` | Existing | override, artwork, reclassify, canonical mutations | P03 |
| `metadata.match` | Existing | search/apply/retail/Wikidata match routes | P03 |
| `metadata.enrichment.read` | Existing | refresh schedule/status, pass2 status, AI progress | P03 |
| `metadata.enrichment.run` | Existing | hydrate, refresh/run, pass2, universe/lore enrichment | P03 |
| `providers.status.read` | Existing | `/settings/providers/health`, `/providers/catalogue` | P03 |
| `providers.config.read` | Existing | `/settings/providers`, admin provider config | P03; secret values remain write-only |
| `providers.config.write` | Existing | provider settings/credentials/config routes | P03 |
| `ingestion.status.read` | Existing | `/ingestion/operations|presentation`, watcher/activity status | P03 |
| `ingestion.history.read` | Existing | ingestion/activity batch routes | P03 |
| `ingestion.run` | Existing | scan/rescan/reconcile/upload | P03 |
| `ingestion.retry` | Existing | operation retry, asset reread, contribution retry | P03; View contribution retry also needs P04 resource authority |
| `ingestion.cancel` | Existing | operation cancel | P03 |
| `review.read` | Existing | `/review/pending|count|{id}` | P03 |
| `review.resolve` | Existing | resolve/dismiss/skip routes | P03 |
| `collections.read` | Existing | collection catalogue/search/detail/item routes | P03; personal-media expansion also requires P04 |
| `collections.write` | Existing | create/update/delete, membership, placement/artwork | P03; canonical/curated policy |
| `view.shared.read` | Existing | `/view` Shared scope/assets/content/discovery | P04 |
| `view.personal.read` | Existing | Mine and profile-scoped View queries | P04; sensitive, requires bound user/profile or admin Application and one selected scope |
| `view.originals.read` | Existing | `/view/items/{id}/content`, Dashboard `/view-media/{grant}` | P04/P05; sensitive and resource checked |
| `view.upload` | Existing | `/view/uploads` | P04 |
| `view.galleries.read` | Existing | gallery list/detail/items/shares | P04 |
| `view.galleries.write` | Existing | gallery/item/share mutations | P04 |
| `identity.users.read` | Existing | `/accounts`, profiles | P02 |
| `identity.sessions.read` | Unavailable | `/auth/sessions` is authenticated self-service only | P02 must add an administrator/application-safe service or keep unavailable |
| `identity.users.write` | Existing | account/profile grants, invitations, profile management | P02 |
| `identity.applications.write` | Unavailable | Legacy API-key administration is not Application CRUD | P02 |
| `plugins.read` | Existing | `/plugins/approved|{pluginId}|manifest` | P03 for app API; P08 host separation |
| `plugins.jobs.read` | Existing | `/plugins/{pluginId}/jobs` | P03/P08 |
| `plugins.jobs.run` | Existing | `/plugins/jobs/segment-detection/run`, health actions | P03/P08 |
| `plugins.manage` | Existing | enable/disable/settings/manifest/delete | P03/P08 |
| `ai.status.read` | Existing | `/ai/status|models|resources|enrichment/progress` | P03 |
| `ai.infer` | Unavailable | AI is used internally/plugin-side; no bounded application inference API | P09 only if a real plugin service supplies it |
| `ai.manage` | Existing | model download/load/unload/config/profile/benchmark | P03 |
| `network.status.read` | Existing | `/settings/network/status|readiness`, tests | P03 |
| `network.config.write` | Existing | bandwidth/port/router/reset routes | P03 |
| `storage.status.read` | Existing | library configuration/view summary, storage health | P03 |
| `storage.config.write` | Existing | library mutation/reorganization and storage maintenance | P03 |
| `backup.read` | Existing | `/system/backups`, download | P03 |
| `backup.run` | Existing | create/validate | P03 |
| `backup.restore` | Existing | restore and setup restore confirmation | P02/P03; setup state is a distinct principal/path |
| `events.subscribe` | Unavailable | `/intercom` is internal session transport with global broadcasts, not external filtered subscriptions | P11 |

## Native compatibility constraint

The ten existing IDs in `ClientApiScopes` are `library.read`, `artwork.read`, `progress.read`, `progress.write`, `queue.read`, `queue.write`, `playback.read`, `playback.write`, `downloads.read`, and `downloads.write`. P01 must source them from the registry without changing device authorization/token JSON. P02 binds live tokens to Application + Account + approved Profile; P03 evaluates the delegated human intersection on every resource.

## Plugin host capabilities

`media.read`, `network.http`, `process.execute`, `tool.download`, `ai.infer`, and proposed `storage.plugin` are host capabilities, not Application catalogue grants. P08 owns `IPluginPermissionGate` and host-bound execution context. Plugin application permissions use a collision-checked `plugin.{plugin-id}.{service}.{action}` namespace and remain unavailable when the plugin/service is disabled (P09).

## Credential boundary

Inbound Tuvima identities are current `X-Api-Key`, Dashboard `X-Tuvima-Service-Key`, browser session `X-Tuvima-Session`, native Bearer access/refresh tokens, signed `X-Tuvima-View-*` assertions, and short-lived Intercom Bearer tokens. Outbound provider credentials live behind `ProviderCredentialService`/provider configuration and must not be migrated into Applications. Docker/config bootstrap and native test scripts must be re-audited at P02/P13 without recording secret values.

