---
title: "Engine API Reference"
description: "Look up the Engine's HTTP routes, authentication rules, and endpoint responsibilities."
audience: "developer"
category: "reference"
product_area: "api"
tags:
  - "http"
  - "api"
  - "endpoints"
status: current
---

# Engine API Reference

## In this page

Look up the Engine's HTTP routes, authentication rules, and endpoint responsibilities.

## Where this lives in the code

- `src/MediaEngine.Api/Endpoints`
- `src/MediaEngine.Web`
- `src/MediaEngine.Contracts`

Public client base URL: the Dashboard origin plus `/api/v1` (for local development, `http://localhost:5016/api/v1`).

The Engine origin (`http://localhost:61495` in development) is an internal service boundary. Native clients must discover and call the Dashboard origin and never receive the Dashboard service credential or connect to the Engine port.

Interactive documentation: `http://localhost:61495/swagger`

Administrative Engine endpoints require live effective administrator authority (with any configured surface unlock), or the exact registered Application permission where supported. Public v1 client endpoints use scoped bearer tokens issued to a paired device. Every route also enforces its resource/profile scope; the Auth column below is a summary, not a substitute for the mapped endpoint policies. See the [Access implementation status](../../engineering/plans/access-architecture-2026-09-08/execution/status.md) for final cutover gates.

## Public client API v1

API v1 is the frozen native-client contract. Breaking request or response changes require a new major path. Additive fields may be introduced in v1; clients must ignore fields they do not understand.

### Discovery and pairing

1. `GET /.well-known/tuvima` on the Dashboard origin returns the public API base, supported versions, device-authorization and token endpoints, verification URL, and server capabilities. It is anonymous and always answers (there is no off switch for apps). It also carries an optional `outside_address`, an origin such as `https://tuvima.example.com` or a Tailscale address, when the owner has set up a secure outside address in Settings → Network → Remote Access; it is `null` or absent otherwise. Apps use it away from home. The Dashboard fetches it from the Engine action `GET /network/outside-address` (Dashboard service credential only; returns `{ "outside_address" }`). The old Anywhere readiness action and its `409` refusal no longer exist.
2. A television or other input-constrained client posts form data or JSON to `POST /api/v1/oauth/device_authorization` with a stable public `client_id`, display names, version, device class, requested space-delimited scopes, and capabilities.
3. The user opens the returned `verification_uri` on an authenticated browser, reviews the exact client/device/scopes, and approves or denies it. Device codes expire after 10 minutes and polling starts at the returned interval.
4. The device polls `POST /api/v1/oauth/token` using grant type `urn:ietf:params:oauth:grant-type:device_code`. `authorization_pending`, `slow_down`, `access_denied`, and `expired_token` are normal OAuth error responses.
5. Access tokens last 10 minutes. Refresh tokens rotate on every successful `refresh_token` grant and expire after 30 days. Reuse of a consumed refresh token revokes its token family.

Tokens are opaque bearer credentials and must be stored in the platform secure credential store. Device and token identifiers in request bodies or query strings are never authority; the Engine uses the profile, device class, client, and scopes bound to the bearer token. `GET /api/v1/devices` lists the devices of the active profile for a signed-in person (the Dashboard), but a paired app only ever sees its own device in that list, `GET /api/v1/devices/current` describes the caller, `PUT /api/v1/devices/current/capabilities` replaces negotiated capabilities, `PUT /api/v1/devices/current/backup-profile` (body `profile_id`, optional `pin`) chooses whose photos this phone backs up (`428` when that person has a PIN and it is missing or wrong, `429` while that person is locked, `403` when the caller is not the paired app of that device or the person is not in the phone's household), and `GET /api/v1/devices/current/profiles` lists the people this paired app may switch to as `{"profiles":[{profile_id, display_name, avatar_url, is_kids, requires_pin, is_active}]}` (the account's enabled profile grants; `avatar_url` is always null today), `POST /api/v1/devices/current/switch-profile` (body `profile_id`, optional `pin`) switches the app to that person without re-pairing and returns the ordinary token response (`profile_id` is the new person, `device_id` is unchanged; the previous access and refresh tokens are revoked at once, and the PIN is checked at every switch). Switch failures are `OAuthErrorResponse` bodies: `428` `pin_required` (a PIN is needed or wrong; `428` rather than `401` so a retrying client does not replay a wrong PIN), `403` `profile_not_allowed` (the account has no grant for that person; an id that does not exist gets the same answer, so ids cannot be probed) or `forbidden` (the caller is not the paired app of a live device of its own account), `409` `already_active_profile` (the requested person is the one the device is already using; nothing is checked or changed), `429` `pin_locked` (too many wrong PINs) or `rate_limited` (with `Retry-After`), and `401` `invalid_grant` when the device, account or Application changed or another switch from the same device got there first. Both actions are limited to 30 a minute per device, counted across token rotation (a new token does not start a new allowance), and are open through the Dashboard's app door (`POST` only for `devices/current/switch-profile`). `DELETE /api/v1/devices/{deviceId}` immediately revokes that device and all of its tokens. A paired app can only revoke itself (the apps call it with their own device id to sign out): any other id answers `404`, exactly like an unknown device, so a token (for example a TV switched to someone's profile) cannot probe or sign out another device of that profile. A signed-in person keeps the profile-wide behaviour, and managing other devices stays in `/access/devices`. The Dashboard's app door forwards only the anchored routes the apps call: `GET devices`, `DELETE devices/{id}`, `PUT devices/current/capabilities`, `GET devices/current/profiles`, `POST devices/current/switch-profile`, `GET devices/current/playback-preferences`, `GET`/`POST devices/current/downloads`, `DELETE devices/current/downloads/{variantId}` and `DELETE devices/current/downloads/photos/{itemId}`. `GET devices/current` and `PUT devices/current/backup-profile` are not forwarded.

### Scopes and resources

| Resource | Primary routes | Required scope |
|---|---|---|
| Discovery | `GET /.well-known/tuvima` | None |
| Browse/home | `GET /api/v1/display/home`, `/browse`, `/shelves/{key}`, `/groups/{id}` | `library.read` |
| Item lookup | `POST /api/v1/display/items` with `{ "ids": [guid, ...] }` (at most 200 ids; `400` when longer, so the caller splits the list). Returns a `DisplayPageDto` whose `catalog` holds the same cards the lane browse pages return, in the order asked: a Work id gives that Work's card, a show or album root id gives the show or album card. It reads the same authorized projection as `/browse`, so the profile's content limit and the account's lane and library grants apply exactly as they do on a lane page. An id the viewer cannot see, an id that is not a catalogue item (a Collection, a View document) and an id that does not exist all get the same answer: no card. Documents never appear, whatever the profile's Documents setting. Not limited to a lane page's size | `library.read` |
| Continue | `GET /api/v1/display/continue?lane=&mediaType=&limit=` returns the in-progress cards. The finished list that My Library's old History row read is gone; the Activity record (`/api/v1/activity`) is the one list of what a person did | `progress.read` |
| Artwork workspace (Dashboard only) | `GET /api/v1/display/artwork`, `/artwork/assets`, `/artwork/assets/{id}/content`, `/artwork/entities/{type}/{id}`, `/artwork/universes/{id}/entities`, `/artwork/works/{id}/effective` | An administrator or a service Application holding `artwork.read`. A paired Kids or adult (non-administrator) app token answers `403` even though it holds `artwork.read`; a paired administrator's phone would pass the Engine policy, so the app door (which never forwards these routes) is what keeps it out. Art linked to a person, Work or Universe is read as `/stream/artwork/{id}`, a catalogue-checked route open to paired apps |
| Search | `GET /api/v1/display/search?q=` | `library.read`. For a paired app a Collection or Series result carries the cover of a member the profile may see (or none); it never carries the collection's own cover, so a restricted item's poster cannot show through a mixed collection |
| Details | `GET /api/v1/details/{entityType}/{id}` | `library.read` |
| Next episodes | `GET /api/v1/details/show/{id}/next-episodes?after={assetId}&count=3` | `library.read` |
| Collections and Universes | `GET /api/v1/collections`, `/collections/{id}`, `/collections/{id}/items`, `GET /api/v1/universe/{qid}` | `library.read` |
| Owned-file cover | `GET /api/v1/display/artwork/media-assets/{assetId}/effective-cover` (checked against the profile's access to that file) | `artwork.read` |
| Artwork/media bytes | URLs returned by display/detail/manifest models; `/api/v1/stream/{assetId}` supports ranges and signed `/api/v1/stream/hls/...` paths serve adaptive packages | `artwork.read` or `playback.read` as applicable |
| Progress | `GET/PUT /api/v1/progress/{assetId}`, `GET /recent`, `GET /journey` | `progress.read` / `progress.write` |
| Queues | `GET /api/v1/player/state`, mutations under `/api/v1/player/queue` | `queue.read` / `queue.write` |
| Playback session | `/api/v1/player/command`, `/heartbeat`, `/session/takeover` | `playback.write` or `progress.write` |
| Companion | `GET /api/v1/companion/devices`, `GET /session`, `PUT /session/follow`, `PUT /session/manual`, `DELETE /session` (see [Companion](#companion)) | `queue.read` / `progress.write` |
| Playback decision | `GET /api/v1/playback/{assetId}/manifest` | `playback.read` |
| Cast session | `POST /api/v1/playback/{assetId}/cast` with `{"receiver":"google-cast-default"}`. A paired app only (a signed-in Dashboard session or an Application is refused `403`); answers `status` `ready` (with `mediaPath`, `contentType`, `posterPath`, `textTracks`, `expiresAt`), `preparing` (the title is being converted; ask again, no link is issued yet) or `unavailable` (with a plain `message`). The answer carries a secret link and is never cached (`Cache-Control: private, no-store`). Rate-limited to 20 per minute per device | `playback.read`, plus the catalogue's own limits for that title (a Kids limit applies) |
| Cast receiver routes | `GET`/`HEAD`/`OPTIONS /cast/v1/{grant}/media`, `/hls/{packageId}/{**path}`, `/text/{trackId}`, `/poster`. Anonymous on purpose: a TV cannot send a token, so the signed `{grant}` in the path is the credential and the Engine re-checks all of it on every request. One title only, read only, byte ranges supported; `403` for any grant that no longer holds. The Engine answers these only on this computer; the Dashboard relays them for the home network | the cast grant (see [Security](../architecture/security.md)) |
| Downloads | encode/offline routes under `/api/v1/playback` | `downloads.read` / `downloads.write` |
| Device playback preferences | `GET /api/v1/devices/current/playback-preferences` | `playback.read` |
| Device download list | `GET/POST /api/v1/devices/current/downloads`, `DELETE /api/v1/devices/current/downloads/{variantId}`, `DELETE /api/v1/devices/current/downloads/photos/{itemId}` | `downloads.read` (GET) / `downloads.write` (POST, DELETE); keeping a photo (`kind` `photo`) also needs `view.personal.read` |
| Photos | `GET /api/v1/view/timeline`, `/places`, `/people`, `/people/{key}/items`, `/items/{id}`, `/items/{id}/thumbnail`, `/items/{id}/preview` | `view.personal.read` |
| Photo Galleries | `GET /api/v1/view/galleries`, `/galleries/{id}/items` | `view.galleries.read` |
| Photo originals and video | `GET /api/v1/view/items/{id}/original`, `/items/{id}/playback` | `view.originals.read` |

#### Photos (View) for paired apps

The `/api/v1/view` routes give a paired app the active person's own photos and videos, read-only. Every route takes who is asking from the device token's active profile and asks the View authorization service for the **Mine** scope; there is no `scope`, `scopeProfileId` or profile parameter, and sending one changes nothing. Only a paired app may call them (a Dashboard session or service application is refused with `403`, no token with `401`), and the token and the Application must both hold the scope named above (`403` with `insufficient_scope` otherwise). The Dashboard's own `/view` routes are unchanged.

| Route | Answer |
|---|---|
| `GET /timeline?cursor&limit` | `ViewClientTimelinePageDto`: `months` (each `year`, `month` in UTC, and `items`), `next_cursor`, `has_more`. Newest first; `limit` defaults to 100 (maximum 200). A month can continue on the next page, so merge buckets with the same `year` and `month`. An invalid cursor is `400`. |
| `GET /galleries` | `ViewGalleryListResponse` (`owned`, `shared_with_you`). Counts leave documents out. |
| `GET /galleries/{id}/items?cursor&limit` | `ViewClientItemPageDto` (`items`, `next_cursor`, `has_more`), newest first. |
| `GET /people?cursor&limit`, `GET /places?cursor&limit` | The existing `ViewPeoplePageDto` and `ViewPlacesPageDto` (places carry `latitude` and `longitude`). |
| `GET /people/{key}/items?cursor&limit` | `ViewClientItemPageDto` for one person's `key`. |
| `GET /items/{id}` | The existing `LocalAssetDto`: `media_kind` (`image` or `video`), size (`width`, `height`, `duration_seconds`, file `byte_size`), `captured_at`, camera, location. `thumbnail_url` and `content_url` point at the routes below. |
| `GET /items/{id}/thumbnail?size=` | JPEG; `size` is the longest edge: `160`, `320` or `640` (default). Any other value is `400`. |
| `GET /items/{id}/preview?size=` | JPEG; `size` is `1280` or `2048` (default). |
| `GET /items/{id}/original` | The original file with HTTP range support, only for a token that holds `view.originals.read` and may read the item. No address works without the bearer token. |
| `GET /items/{id}/playback` | For a video: a `PlaybackManifestDto` with one `direct-stream` delivery of `/api/v1/view/items/{id}/original`. Personal videos have no catalogue asset, probe or adaptive package, so the catalogue playback is not involved. |

These routes show photos and videos only. A document, someone else's item and an id nobody has all answer with the same `404`, on every route and for every profile, so a profile without Documents cannot learn that a document exists from the timeline, Galleries, counts, people, places, items or files, and nobody can tell a foreign id from an unknown one. Hidden, archived and trashed items stay out of the timeline. A photo or Gallery that reached the caller through a share carries nothing from its owner's space: no owner or space ids, no Smart Gallery rule, no embedded location or date, and no people or tags. Thumbnail, preview and original responses are `Cache-Control: private, no-cache`.

**Consent.** `view.personal.read`, `view.originals.read` and `view.galleries.read` are scopes a paired app may request in `scope` at `POST /api/v1/oauth/device_authorization`, but they are never part of the default scopes. The pairing screen shows them as one separate, unticked item (**Allow access to my photos and videos**); they reach the token only when the person ticks it (`PairingDecisionRequest.scopes` then lists them) and the app asked for them. An approval must name its `scopes`: it grants exactly what the list names within what the app requested (so these three only when listed), and an approval that leaves nothing to grant (omitted, an empty list, or only scopes the app did not request) is refused with `400` and stores nothing, as is a photo scope for an Application that does not hold the three photo permissions. `GET /api/v1/pairing/review/{userCode}` returns `offers_photo_scopes`, true only when the Application holds those permissions; the pairing screen shows the photo item only then. A scope the app did not request cannot be added by the decision. The token's `scope` shows what was granted, and refreshing or switching profile never adds or removes any. The built-in native-client Application also has to hold them (it does, on a new data store and once on an existing one, and an administrator can remove them in Settings, which makes a pairing that asks for them fail with `access_denied` and stops existing devices). Without them every route above answers `403` `insufficient_scope`; a revoked or unknown device token answers `401`. The Dashboard's app door forwards exactly these eleven `GET` routes, with their query string, and nothing else under `view/`.

#### Offline media for paired apps

**Syncing progress recorded offline.** `PUT /api/v1/progress/{assetId}` and `POST /api/v1/player/heartbeat` accept two optional, additive fields: `offline` (boolean, default `false`) and `client_timestamp` (ISO 8601, UTC, when the device recorded the progress). A request without them behaves exactly as before. With `offline: true` and a `client_timestamp`, the later of the server's last update for that asset and the client timestamp wins:

- `PUT /progress`: when `expected_revision` is stale, the Engine does not answer `409`. A later client timestamp replaces the server record (written against the current revision, `last_accessed` set to the client timestamp). An equal or earlier timestamp leaves the server record untouched and the `200` response carries the server's state, so the app compares `revision`, `progress_pct` and `last_accessed` to see which one stands. A non-offline request, or an offline one with no timestamp, still gets `409` on a stale revision.
- `POST /player/heartbeat` has no revision to go stale, so the rule decides whether the heartbeat is applied at all: an offline heartbeat whose timestamp is not later than the server's last update for that asset is ignored (the answer is the current player state), and a later one is applied with the client timestamp as the update time. `offline` and `client_timestamp` are read on this route under those exact snake_case names (the rest of the heartbeat body keeps its camelCase names).
- A `client_timestamp` later than the Engine's clock is clamped to the Engine's clock, so a device with a fast clock cannot make its record win over every later write.
- An applied offline replay also adds one entry to the profile's History, stamped with the client time (UTC). Syncing the same play again (same device, same second) does not add another, and an ignored (older) replay adds none. No extra fields are involved.

**Device playback preferences.** `GET /api/v1/devices/current/playback-preferences` (scope `playback.read`) lets a paired app read, never change, the watching and subtitle preferences of the profile its device is using now. Only a paired app's own bearer token may call it: a signed-in Dashboard session and a service Application are refused with `403` before any data is read, as is a token without a device or active profile, and a token without `playback.read` gets `403` `insufficient_scope`. The profile comes only from the token's active-profile claim (no profile id in the path, query or body), so an app can never read another person's settings, and a switch of person on the device changes the answer. `404` when the profile no longer exists. There is no write route: `PUT`, `POST`, `PATCH` and `DELETE` answer `405`. The answer is a `NativePlaybackPreferencesDto` (camelCase, unlike the snake_case device routes): `{"audioLanguage", "subtitleLanguage", "subtitleMode", "subtitleSize", "subtitleBackground", "skipBackSeconds", "skipForwardSeconds", "autoplayNextEpisode", "resumePlayback", "defaultPlaybackSpeed"}`. These are the values saved in Settings > Playback; with nothing saved they are the defaults, and nothing is written. `audioLanguage` and `subtitleLanguage` are language names such as `English`, not codes. `subtitleMode` is `Auto`, `Always` or `Off` (the "Show subtitles" setting, stored as `Auto`, `Always` or `Never`; `Never` is sent as `Off`). `subtitleSize` is always one of `Small`, `Medium`, `Large` or `ExtraLarge` (matched in any case, anything else is `Medium`). `resumePlayback` is the general Resume setting and `defaultPlaybackSpeed` is the watching speed (1.0 is normal). Listening, reading and sync settings are not part of it.

**Device download list.** The paired app reports what it has stored for offline use. Only the paired app's own bearer token may call these routes: a Dashboard session, another account's token or a service credential is refused with `403`, the device always comes from the token (never from the body), and a device sees only its own rows, for the profile it is using now.

| Route | Body / answer |
|---|---|
| `GET /api/v1/devices/current/downloads` | `200` `{"downloads":[{asset_id, variant_id, kind, photo_item_id, stored_bytes, stored_at, expires_at}]}`, oldest stored first, at most 500 entries of any kind. `kind` is `variant` (the default; `asset_id` and `variant_id` name the offline variant, `photo_item_id` is null) or `photo` (`photo_item_id` is the View item id, `asset_id` and `variant_id` are the empty id, `expires_at` is always null). `expires_at` of a variant is when the Engine will remove it (null when it never does). A kept photo the active person can no longer read (deleted from the index, a share withdrawn, a document rule) is left off the list, so an app purges its local copy when an id it holds is missing; a photo whose file is only missing from disk for now (a library volume that is briefly unmounted) stays listed. All kept photos are decided in one bulk pass (the capability and scope once, the items by list), never one query set per photo. |
| `POST /api/v1/devices/current/downloads` | Body `{asset_id, variant_id, stored_bytes, stored_at}` plus the optional `kind` (`variant` when omitted, or `photo`) and `photo_item_id`. A variant report adds or updates the row for that variant: `204`. `404` when the variant is unknown, belongs to another profile or device, is not ready, has expired, or the profile may not download the asset (the same catalogue check as `GET /api/v1/playback/{assetId}/offline/{variantId}`, so these all look identical). `stored_bytes` is clamped to 0 through the variant's size, and a `stored_at` later than the Engine's clock becomes the Engine's clock. With `kind` `photo` the body names `photo_item_id` instead (`asset_id` and `variant_id` are ignored), the token must also hold `view.personal.read` (`403` otherwise), and the item must be a photo or video with a file that the active person can read through the photo routes (`GET /api/v1/view/items/{id}/preview`: the View Mine scope for the token's profile, never someone else's item, never a document, whatever the person's Documents setting): an unknown id, someone else's item and a document all answer the same `404`. `stored_bytes` is clamped to 0 through 256 MB. A photo has no expiry. A photo whose file is not on disk right now cannot be kept (the preview route answers 404 too). `400` for `kind` `book` (reserved for later), any other unknown kind, or a `photo` without `photo_item_id`. `409` once the device already lists 500 entries of any kind (updating a listed one still works). Only entries the list would show count: before answering `409` the Engine clears this person's kept photos that the list no longer shows them (access withdrawn) and tries once more. |
| `DELETE /api/v1/devices/current/downloads/{variantId}` | Forgets the variant row: `204`, or `404` when it is not listed. |
| `DELETE /api/v1/devices/current/downloads/photos/{itemId}` | Forgets the kept-photo row for that View item and the token's active person: `204`, or `404` when it is not listed. Both `DELETE` routes need `downloads.write`. An app sends this when it purges a kept photo that has gone from the list, so a row the list hides does not keep counting. |

Removal of an expired variant also removes its list row, so a device that still believes it holds the file finds the row gone on its next `GET`. The Dashboard's app door forwards exactly these three route shapes.

**Next episodes.** `GET /api/v1/details/show/{id}/next-episodes?after={assetId}&count=3` returns `{"show_id", "after_asset_id", "episodes":[{work_id, asset_id, title, season_number, episode_number, still_url}]}`: the owned episodes that follow the episode file `after` in the show's watch order (seasons ascending with specials last, then episodes ascending). A combined file that covers several episodes counts once and is skipped when it is the file in play. `count` defaults to 3 and is clamped to 1-10. Library grants and the profile's content limit (Kids) apply exactly as on the other details routes, and an unknown show, an `after` file that is not one of the show's episodes, and one the profile may not see all answer `404`; the last episode answers `200` with an empty list. `after` is required (`400` when missing). The first path segment after `details` is the literal `show` on this route.

#### Long TV episode lists

`GET /api/v1/details/{entityType}/{id}` carries a TV show's seasons in `sequencePlacement.groups`. Each group reports `itemCount` (every episode in the season, owned and missing) and `itemOffset` (position of `items[0]`). **Windowing is opt-in:** a request with no `sequence*` parameter receives the whole list exactly as before (`orderedItems` and every group's `items` complete), so older clients keep working. A client opts in by sending `sequenceLimit` (the Dashboard sends `sequenceLimit=50`). Up to 100 episodes in total a windowed request still lists every episode. A longer show then lists every season but fills `items` for one season only (the season being watched, starting at the window that holds the current episode); `orderedItems` is empty because the episodes are carried once, inside their season.

**A season by its own address.** `GET /api/v1/details/tvseason/{id}` answers with the page of the season's show, opened on that season: the payload's `entityType` is the show and its `sequencePlacement` selects the season. The optional `containerId` is the show's id; when it is omitted the Engine resolves the show from the season. A season id that does not exist, or that the profile may not see (library grants and the content limit apply as on the other details routes), answers `404`; so does a season whose show cannot be resolved.

Optional query parameters on the same route:

| Parameter | Meaning |
|---|---|
| `sequenceGroup` | Season key (for example `season-2`) to read; returns exactly that window. An unknown key falls back to the season being watched. |
| `sequenceOffset` / `sequenceLimit` | First episode and window size (default 50 when a season or offset is named, at most 200). An offset past the end returns the last full window. `sequenceLimit=0`, or no `sequence*` parameter at all, returns every episode, for playback queues and the player's episode picker. |
| `sequenceCurrent` | Episode id to mark `isCurrent` in the answer. |
| `sequenceOnly=true` | Return just `sequencePlacement`, without credits and the rest of the page. |

The routes need no extra scope or permission: they are the same detail request.

#### Collections and Universes (read only)

Paired apps read the same Collections catalogue the Dashboard shows, limited to the profile the device token acts as. The profile comes from the token, never from a query value. Its content limit (Kids and maturity), libraries and features are applied before anything is listed or counted, so an item over the limit appears in no page, no `item_count`, no `total_count` and no artwork preview. There are no write, lore or graph routes on this surface; collection edits, personal-media sources and the relationship graph stay out of the app door.

| Method and route | Answer |
|---|---|
| `GET /api/v1/collections?cursor=&limit=` | `PagedResponse` of catalogue entries (`CollectionManagementCatalogDto`), default 24, at most 250, with `total_count` and `next_cursor`. Collections with nothing the profile may see are left out. |
| `GET /api/v1/collections/{id}` | One catalogue entry. |
| `GET /api/v1/collections/{id}/items?cursor=&limit=` | `PagedResponse` of `CollectionItemDto` (title, creator, media type, cover, detail route, `membership_scope`, `position_label`), default 24, with `total_count` (at most the first 5,000 items). A Series is listed in Series order: the numbered run first (`position_label` is the source's own text, so a decimal such as `1.5` stays `1.5`), then members with no position, then "Also in this world" members (`membership_scope` `Supplementary`, `CollectedContent` or `BroaderContext`), which are never numbered with the run. `sort_order` is the item's place in that list, counted from zero. |
| `GET /api/v1/universe/{qid}` | `UniverseClientDetailDto`: the Universe's name, `item_count` and its member shelves. Each shelf is a `DisplayShelfDto` (the same card shape as `/api/v1/display`, first 24 cards, `seeAllRoute` pointing at `/api/v1/display/groups/{collection_id}`) with the member's own `collection_id`, `group_type` and total `item_count`. |

`cursor` is the `next_cursor` of the previous page; pass it back unchanged. A malformed cursor is `400`. Catalogue entries carry `group_type`, the user-facing kind of group: `show`, `series`, `collection` (a rollup across shelves or a rule-built group) or `list` (a hand-picked or system list); the Universe detail uses `universe`. A catalogue entry that is a Universe has `group_type` `universe` and `universe_qid`, the Wikidata QID (`Q` and digits) that opens `GET /api/v1/universe/{qid}`; `universe_qid` is `null` for every other entry, and it is only set when that route would serve the Universe to the same profile. Authoring fields of an entry (`rule_json`, `rule_hash`, `selected_profile_ids`, `profile_id`, `refresh_schedule`, the `can_*` flags and the collection's own uploaded artwork URLs) are always blank for apps; member artwork is in `artwork_items`.

An unknown id or QID, a collection that belongs to another profile, and one with nothing this profile may see all give the same empty `404`, so an app cannot tell them apart. Collections hold owned media only: a profile that has Documents turned off never sees a document here, because Documents are View files and are not part of any collection's items or counts.

Capability negotiation declares containers, video/audio codecs, subtitle formats, protocols, maximum dimensions/bitrate/channels, HDR, speed, and offline-download support. The playback manifest compares those registered capabilities with inspected source media and returns `directPlaySupported`, `recommendedDelivery`, source/alternate URLs, tracks, chapters, resume state, warnings, and a conversion reason. For HLS it also returns `hlsStatus`, `hlsUrl`, and `hlsExpiresAt`. `preparing` means the client should retry the manifest; `ready` supplies the playable path. Clients must follow the manifest rather than independently guessing whether a file can direct-play.

The HLS URL is a bearer capability scoped to one asset, package, and expiration time. It is intentionally usable by native HLS media stacks that cannot attach application authorization headers to every segment request. Clients must not log, persist, or share it. Expired or modified paths return `403`; missing and traversal-rejected package resources return `404`. Relative URIs in the master and media playlists preserve the same signed path for variant, alternate-audio, caption, and segment requests.

Offline encode jobs created by paired clients are owned by the authenticated
profile and device. Job listing and cancellation use those trusted token claims;
one client cannot enumerate, cancel, discover, or download another device's
work. Public JSON never includes the Engine's local output path. Clients
download only the API v1 URL exposed by their ready offline-variant model.

Most paginated GET endpoints accept `offset`/`limit` query parameters. View
timeline and discovery endpoints use opaque/keyset `cursor` plus `limit`, and
Manual Gallery membership uses position/item keysets. Limits are clamped
server-side so a caller cannot force an unbounded read.

JSON errors use RFC 7807 Problem Details through the shared `ApiErrors`
factories. JSON success bodies use named `MediaEngine.Contracts` types, and
every production route declares its 2xx response metadata for Swagger. Untyped
success metadata is reserved for reviewed bodyless, redirect, file, stream, and
direct-response routes.

`MediaEngine.Contracts` is the sole owner of non-frozen HTTP and SignalR wire
types. Engine endpoints map internal projections explicitly into those
contracts, and the Dashboard may add presentation wrappers without redefining
the JSON payload. The only frozen exception is the exact Universe/Chronicle
SignalR payload `UniverseEnrichmentProgressEvent`; Universe HTTP responses are
not exempt.

The canonical contracts intentionally include fields that existed on only one
side of the former API/Dashboard mirror boundary. Notable examples are
ingestion `count_unit`; person and person-backed collection `roles`; complete AI
settings plus resource `cpu_pressure`; complete managed-collection, placement,
rule, permission, and artwork fields; playback manifest, player, settings,
chapter, bookmark, text-track, and offline-delivery fields; and plugin
manifest, capability, permission, tool, AI-permission, health, job, and approved
catalog fields. These are contract completeness corrections, not separate
compatibility versions.

---

## System

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/system/status` | Service health, version, uptime | None |
| GET | `/system/activity-status` | Sanitized leased/running durable operations used to seed the Dashboard navbar activity indicator; excludes media titles and filesystem paths | Required |
| GET | `/system/watcher-status` | File watcher diagnostic - shows monitored folders and last event | Required |
| GET | `/system/resources` | Local reading for the Settings System summary: Engine version, start time and uptime (`engine_version`, `started_at`, `uptime_seconds`), Engine processor use as a percent of the machine and memory in use and available in MB (`processor_percent`, `memory_used_mb`, `memory_total_mb`), and `drives` (root, label, total and free bytes, and whether each holds the library and/or the data store; a drive holding both appears once) plus `data_store_size_bytes`. Nothing leaves the machine. | Administrator |
| GET | `/system/data-store-version` | Latest upgrade step applied to the data store (`step_number`, `step_name`, `applied_at`); step 0 with no date means none is recorded yet. Shown as a read-only line in Settings. | Administrator |
| GET | `/system/scheduled-tasks` | Every recurring background task with its schedule, next run and last run: `tasks[]` of `key`, `name`, `owner` (`Library`, `Local AI` or `Plugin`), `plugin_id`, `schedule` (a cron expression read from settings at request time, or plain text such as `every 5 seconds` for continuous work), `is_continuous`, `settings_place`, `next_run_at` (UTC, null for continuous work) and `last_run` (`started_at`, `finished_at`, `outcome` of `running`, `succeeded`, `failed`, `skipped` or `interrupted`, `items_processed`, `message`; null when the task has never run). Sorted by name. Reads the durable `scheduled_task_runs` records; webhook delivery reports its newest delivery attempt instead. | Administrator, or application with `system.status.read` |
| POST | `/maintenance/sweep-orphan-assets` | Scan `.data/assets/` for managed files with no database reference and remove them. | Administrator |
| POST | `/maintenance/storage/run` | Run storage maintenance on demand. Supports `dryRun`, cache retention, image retention, and claim compaction batch query parameters. | Administrator |

---

## Library

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/works` | All works with their canonical values | Required |

## Media Management

Library configuration uses schema `7.0`, including explicit server-visible folder-picker roots. Older flat-folder and Photos/General
schemas are rejected during the pre-beta cutover rather than migrated or served
through compatibility routes.

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/settings/libraries` | Return the complete library configuration, including stable sources and universal incoming sources | Administrator |
| PUT | `/settings/libraries` | Validate and replace the complete schema-7 library configuration; the library responses (GET, PUT, mutation) include `import_folder_status` (whether the optional import folder is set and which types still need a destination) | Administrator |
| GET | `/settings/tools` | The last tools check: each tool and AI model with whether it is required, its state (`ready`, `missing`, `problem`, `downloading`, `installer_ready`), what it affects, how to fix it for this install, and how many files are waiting on it, whether the Library can install it (`can_install`, `can_download_again`). Runs a first check if none has run | Administrator |
| POST | `/settings/tools/check` | Look for the tools again, let work that was waiting on them continue, and return the fresh result. Setup's server check (`POST /setup/v1/preflight`) carries the same result as `tools`; a missing tool never makes the check fail | Administrator |
| PUT | `/settings/tools/{id}/location` | Save the folder a person chose as the home of FFmpeg (`ffmpeg`) or Tesseract (`tesseract`). Body: `storage_location_id` and `relative_path` as the folder picker returns them, or `manual_path`. The folder must hold the tool (FFmpeg needs both `ffmpeg` and `ffprobe`), otherwise `400` with a plain message such as "That folder doesn't contain FFmpeg."; an unknown id is `404`. On success the explicit path is saved to `config/transcoding.json` or `config/ocr.json`, the tools are checked again, and the fresh result is returned | Administrator |
| POST | `/settings/tools/{id}/install` | Start downloading FFmpeg (`ffmpeg`) or Tesseract (`tesseract`) from the fixed address and with the checksums in `config/tool_downloads.json` ("Install for me" when missing, "Download again" when broken). Offered only where a row says `can_install` or `can_download_again`: Windows x64, never in a container; a Windows-installer install is offered FFmpeg only. Returns `202` with the tools result, where the row is `downloading` with `download_percent`; FFmpeg is unpacked, checked and put in `{DataRoot}/tools/ffmpeg`, then the tools are checked again so waiting files continue. Tesseract ends as `installer_ready`, with its language data already in place. `409` when that tool is already downloading, `400` with a plain message when it is not offered here, `404` for an unknown id. Every attempt is written to the system activity log | Administrator |
| DELETE | `/settings/tools/{id}/install` | Cancel a download in progress and remove its temporary files; does nothing if none is running. Returns the tools result; `404` for an unknown id | Administrator |
| POST | `/settings/tools/{id}/run-installer` | Start Tesseract's downloaded and re-checked setup program on the machine running the Engine (Windows asks for approval); when it closes the Engine looks for Tesseract again and removes the installer. Only when the Engine runs in a signed-in desktop session, otherwise `400` says to reinstall with the Windows installer. `202` with the tools result, `409` while the installer is open, `404` for an unknown id | Administrator |
| GET | `/settings/notices` | The Settings attention lines the active profile dismissed with the X, each as `key` (`needs-review` or `missing-tools`) and the `fingerprint` that was true at the time (the Needs Review count, or the sorted comma-joined ids of the missing tools). The profile comes from the caller's own authority, never from the request | Administrator |
| POST | `/settings/notices/{key}/dismiss` | Remember that the active profile dismissed that line. Body: `fingerprint`. `204`; `400` for an unknown key or a blank or over-long fingerprint, saving nothing. A line returns only when something new needs attention (more review items than the stored count, or a missing tool id not in the stored set) | Administrator |
| GET | `/settings/ocr` | Text recognition settings, whether the helper and language data are installed, the language checklist, and the reading queue (length and current file name only) | Administrator |
| PUT | `/settings/ocr` | Save text recognition on or off, effort (`normal` or `low`) and the languages to read with | Administrator |
| POST | `/settings/ocr/languages/{code}` | Download the language data for one listed language; the only network use of text recognition | Administrator |
| POST | `/settings/ocr/reread` | Queue every PDF and photo without text to be read again, in batches (`202`; `409` when already running or not ready) | Administrator |
| GET | `/settings/incoming-sources` | Return universal unassigned intake folders | Administrator |
| PUT | `/settings/incoming-sources` | Validate and replace universal incoming folders | Administrator |
| POST | `/settings/libraries/{libraryId}/reorganization/plan` | Produce a read-only, expiring move/rename plan with stable source IDs, conflicts, blocked work, and an exact confirmation fingerprint | Administrator |
| POST | `/settings/libraries/{libraryId}/reorganization/execute` | Execute one confirmed plan once, revalidating source policy, files, collisions, and free space immediately before each operation | Administrator |
| POST | `/settings/libraries/folders/conversion/preview` | `{source_id, quick, file_limit}`. `quick: true` counts the folder's media files from folder listings only (`total_files`, `video_files`, time estimate, `duplicate_policy`). Otherwise a read-only dry run over the first `file_limit` files (0 = all): `to_rename`, `already_correct`, `duplicates_to_remove`, `needs_review`, `name_collisions`, `skipped` with `skip_reasons`, `skipped_links` (shortcuts inside the folder that the job never follows or touches), five `samples` of old to new path, and `estimated_seconds`. Nothing is changed | Administrator |
| POST | `/settings/libraries/folders/convert` | `{source_id}`. Makes the folder Managed (`conversion_state: converting`) and queues one `organise_folder` job on the `folder_jobs` queue; a folder that already has an open job gets that job back. Returns the job status | Administrator |
| POST | `/settings/libraries/folders/merge/preview` | `{source_id, target_source_id, mode, quick, file_limit}` (`mode` is `copy` or `move`). `quick: true` counts the source folder's media files from folder listings and file sizes only: `total_files`, `video_files`, `source_bytes`, `target_free_bytes`, a time estimate, `duplicate_policy` and `blocked_reason` when the target's drive lacks room. Otherwise a read-only dry run over the first `file_limit` files (0 = all): `to_transfer`, `already_in_target` (exact copies already held, in the target or elsewhere in the library), `needs_review` (files that go to the From merge folder), `name_collisions`, `skipped` with `skip_reasons`, five `samples`. Nothing is changed. `400` when the merge cannot go ahead (target not managed, another library, nested folders, Move out of an As-is folder, the New media folder as source, a merge already done) | Administrator |
| POST | `/settings/libraries/folders/merge` | `{source_id, target_source_id, mode}`. Records `merge_state: merging` on the source folder and queues one `merge_folder` job on the `folder_jobs` queue; a folder that already has a merge in progress gets that job back (`409` when a conversion is open on it). Refuses when the target is not a managed, writable folder of the same library or lacks room. Returns the job status (`job_type: merge_folder`) | Administrator |
| GET | `/settings/libraries/folders/jobs` | The open folder jobs and each folder's most recent finished job from the last week (`state`, `items_total/completed/failed`, `progress_percent`, `estimated_seconds_remaining`, `message`, `queued_behind`, `has_log`) | Administrator |
| GET | `/settings/libraries/folders/jobs/{id}` | One folder job | Administrator |
| POST | `/settings/libraries/folders/jobs/{id}/control` | `{action}` of `pause`, `resume` or `cancel`; `409` when the job has already finished (or is not paused) | Administrator |
| GET | `/settings/libraries/folders/jobs/{id}/log` | A page (`offset`, `limit`) of the job's log: every file, old path to new path, with its outcome | Administrator |
| GET | `/settings/libraries/folders/jobs/{id}/log/export` | The whole log as a CSV download (the Dashboard serves it at `/engine-folder-log/{id}`) | Administrator |
| POST | `/settings/libraries/mutations` | `{Library, ExpectedLibrary}` (or `{ViewStorage, ExpectedViewStorage}` for the View root). Saves one library's change (add, label, remove or switch a folder, move the New media marker) without replacing the rest of the configuration; `409` when the library changed since the screen read it. The folder pickers and the Libraries screen use this instead of the whole-configuration `PUT` | Administrator |
| GET | `/settings/server-folders/roots` | The places the folder picker can start from: the server's drives and root, plus any administrator-approved folders (`id`, `label`, `path`, `allow_write`, `available_bytes`, `file_system`). Tuvima's own library, import and View folders are never listed. An unreachable location is left out and logged | Administrator |
| POST | `/settings/server-folders/browse` | `{storage_location_id, relative_path, search}`. The sub-folders of one listed place (`directories` with `name`, `relative_path`, `modified_at`; `parent_relative_path`; `display_path`). Cannot leave the place or enter one of Tuvima's own folders; `400` when it tries | Administrator |
| POST | `/settings/server-folders/validate` | `{storage_location_id, relative_path}` or `{manual_path}`, plus `selection_mode` (`managed_library`, `existing_library`, `personal_space_managed`, `personal_space_existing`, and `tool_location` for choosing a tool's folder, which is read-only and skips the library-overlap and reserved-name rules but still refuses the Library's own folders) and optional `current_source_id`. Checks, from the server's side, that the folder exists, can be read (and written for the managed modes) and does not overlap another library, the import folder or a Tuvima folder. Returns `can_select`, `exists`, `has_read`, `has_write`, `contains_files` and `issues` (`code`, `message`, `library_name`, `severity`). `400` for a path outside every listed place | Administrator |
| GET | `/setup/v1/libraries` | Setup-session protected. The library configuration, so the first-run Libraries step can show the folders added so far (the same shape as `GET /settings/libraries`) | Setup session |
| PUT | `/setup/v1/libraries` | Setup-session protected. Replaces the library configuration during first-run setup, with the same checks as `PUT /settings/libraries`. Setup only prepares the folders; watching starts when setup is complete | Setup session |
| GET | `/setup/v1/server-folders/roots` | Setup-session protected. The same list as `GET /settings/server-folders/roots`, for the folder picker inside setup | Setup session |
| POST | `/setup/v1/server-folders/browse` | Setup-session protected. The same action and body as `POST /settings/server-folders/browse` | Setup session |
| POST | `/setup/v1/server-folders/validate` | Setup-session protected. The same action and body as `POST /settings/server-folders/validate` | Setup session |

Reorganization execution requires both the returned `plan_id` and its exact
`fingerprint`. A plan expires after 15 minutes and cannot be reused. Partial
outcomes are reported per item; an existing-library source is always blocked
from mutation.

## View

Personal View routes require the trusted profile assertion established by the
Dashboard. The server-to-server `X-Tuvima-View-Profile`,
`X-Tuvima-View-Timestamp`, and `X-Tuvima-View-Signature` headers bind the active
profile to the exact method/path/query through HMAC-SHA256 using the already
validated API key. They are accepted only for `/view` and `/collections`, must
be within the configured short skew window, and are not browser credentials.

Browser-supplied profile, library, Gallery, scope-profile, or asset IDs are
never treated as authority. Friendly Shared, Mine, and permitted Profile scopes
(plus `scope=othershared&scopeProfileId=…` for a server administrator reading another household's Shared Library, read-only and
never saved as the last scope; any look at another household is recorded as `view.other_household_opened`, at most once an hour
per viewer and space) are resolved by the Engine before any asset, Gallery, People, Places,
thumbnail, original-file, or personal-media Collection query runs. Direct
unauthorized identifiers return the same not-found shape as missing resources.

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/view/scopes` | Resolve the selected/default scope and return only authorized labeled scope options, plus `can_view_documents` (false when the profile's Documents permission is off, so the Dashboard hides the Documents tab) and `can_file_shared_inbox` (true for a household or server administrator with Documents, who can file Shared Library Inbox documents) | Required + trusted profile |
| GET | `/view/other-people` | List other households and their people for read-only browsing ("Other people"); never includes the caller's own household | Server administrator signed in as a person (household administrators, members and apps get 403) |
| GET | `/view/photo-views` | Page the times a server administrator opened your own photos, newest first. Read-only and answered only for a signed-in person (no profile or household parameter; a paired app's device token is refused with `403`): another person's personal-space views are never returned, not even to a household administrator, who additionally sees views of the household's Shared Library (`household_wide: true`) | Required |
| GET, PUT | `/view/preferences` | Read or persist the active profile's last scope, timeline density and viewer-info state. A person always reads and saves their own (the Mine scope); no one reaches another person's. The unscoped `/view/assets`, `/view/assets/timeline-index` and `/view/folders` follow the saved scope through this same permission | Required + trusted profile |
| GET | `/view/assets` | Cursor-page authorized mixed local assets by scope, search, kind, state, or Gallery. With `q`, each item carries a `snippet` (words around the match, matches wrapped in control characters U+0001 and U+0002 so text is never markup); every document carries `document_info` (`text_source`, `text_truncated`, `encrypted`, `has_text`). `kind=document` is not narrowed to Photos-timeline sources. A profile without the Documents permission never gets documents back, and asking only for documents returns 403. Document filters: `tag` (repeat; every tag must be present), `correspondent`, `docType`, `docFrom` and `docTo` (the document's date, or the day it was added when it has none), `filing` (`inbox` or `filed`), `field` (repeat, `<field id>:<value>`) and `docSort` (`added`, `document_date`, `title` or `correspondent`; a sorted list pages with an opaque position `cursor`, never a date boundary; any other value is 400). A saved Documents view is listed with `galleryId`, and the filters narrow it further; cards carry the correspondent and type in `document_info` | Required + scope read |
| GET | `/view/assets/timeline-index` | Month buckets (`year`, `month`, `asset_count`, `earliest_at`, `latest_at`) for the same filters as `GET /view/assets` (`scope`, `q`, `kind`, `favorite`, `hidden`, `galleryId`, `lifecycle`). A profile without the Documents permission is narrowed to the other kinds exactly like the list, so neither the counts nor a text search ever reach document words, and asking only for documents returns 403 | Required + scope read |
| GET | `/view/folders` | Page authorized source roots or one indexed folder hierarchy with URL-backed path breadcrumb, scoped search, and optional descendants. A profile without the Documents permission never sees documents: they are left out of the source counts, folder counts and pages themselves, not filtered after paging | Required + scope read |
| PUT | `/view/folders/pin` | Pin or unpin an authorized folder for the active profile | Required + scope read |
| PUT | `/view/folders/timeline-policy` | Set or clear the source-owner branch override inherited by the Photos timeline | Source owner or administrator |
| POST | `/view/uploads` | Upload to the caller's server-resolved Personal Space. A file View indexes as a document (by extension, e.g. PDF, Word, text) is refused with 403 before anything is stored or compared when the caller, or the person whose space it would land in, may not use Documents, so no "already added" answer can mention an existing document. Photos, videos and audio are unaffected | Required + Personal Space owner |
| GET | `/view/items/{id}` | Read authorized personal-asset metadata (documents also need the Documents permission, as do `content`, `thumbnail` and `preview`; `/view/folders` omits documents without it) | Required + asset read |
| PUT | `/view/items/{id}/description`, `/captured-at`, `/people`, `/location` | Change one item's description, captured date, identified people or place; each answers with the whole item. On a document they return 403 for a profile without the Documents permission, because the answer carries the document's notes, field values and suggestions | Required + asset owner |
| GET | `/view/items/{id}/content` | Stream an authorized original with range support | Required + asset read |
| GET | `/view/items/{id}/thumbnail` | Return a bounded local image preview when supported; a PDF returns a small picture of its first page (drawn once in the background and cached), other documents get the generic placeholder | Required + asset read |
| GET | `/view/items/{id}/text` | Return the readable text of one document (at most 200,000 characters, with `shown_truncated`) and its `document_info` for the Documents viewer text panel | Required + asset read + Documents permission |
| GET | `/view/items/{id}/document-details` | One document's organising details as `document_info`: `document_date`, `correspondent`, `document_type`, `archive_number`, `notes`, `filing_state` (`inbox` or `filed`), `tags` and `field_values` (the only read that fills `field_values`) | Required + asset read + Documents permission |
| PUT | `/view/items/{id}/document-details` | Replace those details in one request (`title`, `document_date`, `correspondent` and `document_type` by name, `archive_number`, `notes`, `filing_state`, `tags` by name, `tag_ids`, `field_values`). A name that exists is reused; a new name joins the global list. Anything not sent is cleared, except `field_values`: leave it out to keep the custom field values as they are, send an empty list to clear them | Required + asset owner + Documents permission |
| PUT | `/view/items/{id}/tags` | Replace an item's tags (`tags` by name, `tag_ids` for tags the person can already see). A tag id the person cannot see is answered like an unknown one (404). Documents also need the Documents permission | Required + asset owner |
| GET | `/view/tag-suggestions` | Type-ahead tag names for the person: tags they can see, most used first, plus their saved Library tags. A profile without the Documents permission is never offered (or weighted by) a tag that only documents carry | Required |
| GET | `/view/catalogue` | The tags, correspondents, document types and custom fields the person can see, with their own `usage_count` (narrow the counts with `kind`; with `scope` the counts describe that scope, without it the lists cover every scope they can open) and `can_manage` | Required + Documents permission |
| POST/PUT/DELETE | `/view/tags`, `/view/correspondents`, `/view/document-types` (PUT/DELETE take `/{id}`) | Add, rename (and recolour) or delete a name. A duplicate in any letter case is refused with 409 and a plain message. Rename and delete are allowed for the creator, a server administrator, and a household administrator for names made in their own household or by nobody (for example names carried over from the earlier tags); anyone else gets 403, and a name the person cannot see is 404. Delete removes links, never items | Required + Documents permission |
| POST/PUT/DELETE | `/view/document-fields` (PUT/DELETE take `/{id}`) | Define, rename (choices may be replaced; the kind cannot change) or delete a custom field of kind `text`, `number`, `date`, `money`, `bool` or `choice`. Same permission rules as above | Required + Documents permission |
| POST | `/view/documents/bulk` | Apply one bulk edit to up to 500 documents. Body: `item_ids`, `operation` (`add_tags`, `remove_tags`, `set_correspondent`, `set_document_type`, `set_document_date`, `mark_filed`, `move_to_trash`, `restore`) and what the operation needs: `tags` (names) and/or `tag_ids`, `correspondent`, `document_type`, `document_date`; for the three `set_` operations a blank value is refused unless `clear` is true. More than 500 ids, no ids, an unknown operation or a missing value is 400. The answer has `requested`, `done`, `skipped`, `failed` and one `items` entry per distinct id in the order sent, each with a `status` (`done`, `skipped`, `failed`) and a plain `reason`. Each document is checked on its own: an id outside the person's libraries, another person's document or a missing one is skipped as "not available" (the same answer for all three), a non-document is skipped, a Shared Library document is skipped ("only filing rules can change it"), except `mark_filed`, which a household or server administrator can use on Shared Library Inbox documents (anyone else gets "Filed by a household administrator."), and one that fails is reported without stopping the rest. Person-set values are recorded as the person's so rules never replace them. Trash and restore only mark the document; no file is deleted | Required + Documents permission + asset owner per document |
| GET | `/view/documents/export` | Download the filtered list as `text/csv` (UTF-8 with a byte-order mark, CRLF line ends, formula-looking cells prefixed with a single quote). Takes the same query as `GET /view/assets` for documents (`scope`, `scopeProfileId`, `q`, `galleryId`, `lifecycle`, the document filters and `docSort`). Columns: Title, File name, Document date, Correspondent, Type, Tags, Archive number, one per custom field the person can see, Added. Only documents the person can see; never document words or notes. At most 10,000 rows: a longer list is cut and the response carries `X-Tuvima-Export-Truncated: true`. A bad filter is 400 | Required + Documents permission |
| GET/PUT | `/view/preferences/documents-sort` | Read or save how the signed-in profile likes the Documents list ordered: `{"sort": "added" \| "document_date" \| "title" \| "correspondent" \| null}`. Saving any other word is 400; null returns to the default order. Saving the scope or density never forgets it. A profile without the Documents permission gets 403 | Required + Documents permission |
| GET | `/view/documents/inbox-summary` | How many documents wait in the Inbox of the scope (`scope`, `scopeProfileId`): `count`. Counts what `GET /view/assets?filing=inbox` lists | Required + Documents permission |
| POST | `/view/items/{id}/inbox/file` | File one Inbox document. `accept_suggestions` fills blank fields from the suggestions in `suggestions` (what the person was shown) or, when that is not sent, the stored ones (a value already set by a rule or a person is kept); `document_date`, `correspondent`, `document_type` sent here are the person's own choice and win, and sent `tags` are added. With nothing sent and `accept_suggestions` false it only marks the document filed. Returns the new `document_info`. Filing an already filed document changes nothing. A Shared Library document is also filed here by a household administrator or the server administrator; any other member, including a child profile, gets 403 "Filed by a household administrator." (`GET /view/scopes` returns `can_file_shared_inbox` so the Dashboard knows whether to offer File on a Shared Inbox card) | Required + asset owner (or household/server administrator for Shared Library documents) + Documents permission |
| GET/POST | `/view/document-rules` | List the person's own auto-filing rules in running order (with `sources` to choose from and `can_manage_shared`), or add one. A rule has a name, `enabled`, `match_mode`, `files_automatically`, `scope` (`mine`, or `shared` for household administrators only), conditions (`text_contains`, `filename_contains`, `folder_contains`, `source`, `file_kind`) and actions (`add_tag`, `set_correspondent`, `set_document_type`, `set_field`). Everything a rule points at must be something the person can see; refused with 400 (bad rule), 403 (Shared Library without being a household administrator) or 404 (a name the person cannot see) | Required + Documents permission |
| PUT/DELETE | `/view/document-rules/{id}` | Replace or delete one of the person's own rules (404 for anyone else's). The scope cannot change | Required + Documents permission |
| PUT | `/view/document-rules/order` | Give the person's rules the running order they are listed in; the list must name every rule they have, once each (otherwise 404); 204 | Required + Documents permission |
| POST | `/view/document-rules/test` | Try a rule (saved or not): up to ten matching document titles from documents the person can open, `documents_checked` and `has_more` | Required + Documents permission |
| POST | `/view/document-rules/apply` | Queue the person's rules to run again on their existing documents (their Personal Space, and the Shared Library when they have household rules): `queued` is how many documents will be queued; the queueing itself continues in the background | Required + Documents permission |
| PUT | `/view/items/{id}/favorite` | Set favorite state on an owned asset. On a document this and the four actions below return 403 for a profile without the Documents permission (after the usual 404 for anyone who does not own the item) | Required + asset owner |
| PUT | `/view/items/{id}/hidden` | Set hidden state on an owned asset | Required + asset owner |
| POST | `/view/items/{id}/archive` | Archive an owned asset without changing its file | Required + asset owner |
| POST | `/view/items/{id}/trash` | Soft-delete an owned asset without deleting its original | Required + asset owner |
| POST | `/view/items/{id}/restore` | Restore an archived or trashed owned asset | Required + asset owner |
| POST | `/view/shared/contributions/preview` | Preview a selected batch, compound-file totals, destination, and eventual move/copy behavior. A document is refused with 403 for its owner when their Documents permission is off, and answered like a missing item (404) for anyone else in the household | Shared submit grant + asset owner |
| POST | `/view/shared/contributions` | Create an idempotent, revision-bound pending contribution without changing files | Shared submit grant + asset owner |
| GET | `/view/shared/contributions` | Page the caller's submissions or their household's review queue. For a profile without the Documents permission a contribution that holds only documents is left out, and a mixed one lists only its non-document items, activity lines that count items or carry a transfer error are rewritten, and `includes_hidden_items` is true (never a number; always false with Documents) so the Dashboard shows only "Includes items you cannot view" and no Accept, Decline or Retry. The item list, the "N item(s) submitted" activity line and the activity log (transfer lines are dropped, since they can name files and count steps per item) all describe only the visible items | Required; household administrator for review mode |
| GET | `/view/shared/contributions/{id}` | Read an authorized contribution, item states, decision, and activity timeline. Filtered like the list for a profile without the Documents permission; a documents-only contribution is 404 for it | Contributor or household administrator (own household) / server administrator |
| POST | `/view/shared/contributions/{id}/cancel` | Cancel a caller-owned pending contribution with optimistic revision checking | Contributor |
| POST | `/view/shared/contributions/{id}/decision` | Accept or decline a pending contribution and queue accepted transfers. 403 for a reviewer without the Documents permission when the contribution holds documents (404 when it holds only documents, since it does not exist for them) | Household administrator (own household) or server administrator |
| POST | `/view/shared/contributions/{id}/retry` | Requeue recoverable accepted item transfers. Same Documents refusal as `decision` | Household administrator (own household) or server administrator |
| POST | `/view/shared/items/direct` | Create an auto-accepted contribution for household items using the same transfer worker | Household administrator or server administrator |
| GET, POST | `/view/galleries` | List owned/shared Galleries or create an owned Gallery. For a profile without the Documents permission `item_count` leaves documents out (manual and Smart Galleries, shared ones too) and a `cover_item_id` that is a document is returned as null; naming one of their own documents as a cover is 403. A saved Documents view is a Smart Gallery whose rule is one "all of" group that includes `media_type` is `document` (`ViewSmartGalleryRules.IsDocumentView`; rule fields `tags` (all of), `correspondent`, `document_type`, `document_date` (including `within` a relative range), `filing_state`, `text_contains` and `field:<field id>`). A profile without the Documents permission never gets one back in the lists, and creating one is 403 | Required + trusted profile |
| GET, PUT, DELETE | `/view/galleries/{id}` | Read or manage an authorized Gallery. Reading or changing a saved Documents view without the Documents permission is 403 | Required + Gallery permission |
| GET, POST, DELETE | `/view/galleries/{id}/items` | Page or mutate Manual Gallery membership. For a profile without the Documents permission the page leaves documents out, adding a document is 403 (nothing in the request is added), and remove and reorder never touch or count a document | Required + Gallery permission |
| GET, PUT | `/view/galleries/{id}/shares` | Read or replace selected-profile Gallery shares. A saved Documents view is always private: replacing its shares is refused with 403 ("A saved Documents view is private and cannot be shared.") | Owner + Gallery sharing enabled |
| GET | `/view/share-targets` | Return only enabled profiles the caller may select as Gallery share recipients | Required + trusted profile |
| GET | `/view/people` | Cursor-page named/reviewed, provenance-aware people annotations within an authorized scope; no face-recognition claim | Required + scope read |
| GET | `/view/places` | Cursor-page real GPS/place aggregates within an authorized scope; no map/tile request | Required + scope read |
| GET | `/view/admin/profiles/{profileId}/sources` | Return an administrator-safe source/device summary without paths, secrets, client identifiers, or invented capacity | Administrator |
| POST | `/view/admin/profiles/{profileId}/reconcile` | Recovery/admin reconciliation for a profile's Personal Space | Administrator |

## Works

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/works/{id}` | Work detail with canonical values, editions, and owned assets | Required |
| GET | `/works/{id}/editions` | Editions and owned assets for a work | Required |
| GET | `/works/{id}/cast` | Actor and character credits for a single work | Required |

---

## Collections

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/collections` | Browse-oriented collection list with works and canonical metadata | Required |
| GET | `/collections/catalog` | Collections catalog: system/user/managed collections plus broader rollups where trusted relationships connect multiple shelves | Required |
| GET | `/collections/{id}/summary` | One Collections summary for a detail page without loading the full catalog | Required |
| GET | `/collections/{id}/items` | Items for a collection detail page, including generated rollup aggregation | Required |
| POST | `/collections` | Create a collection (`collection_type`, `membership_mode`, `rule_definition`, `work_ids`, ownership and audience). A manually curated collection (`Custom`, manual membership) must arrive with at least one `work_ids` entry, so an empty one never exists: otherwise `400` with "A collection needs at least one item." Playlists and rule-driven collections may be created without items. | Effective administrator or precise Application permission |
| PUT | `/collections/{id}` | Update a collection's settings, rules or membership mode. `409` "A collection needs at least one item. Delete the collection instead." when the change would leave a curated collection with no items (for example switching a rule-driven collection to manual membership before anything is in it). | Effective administrator or precise Application permission |
| POST | `/collections/{id}/items` | Add a work to a saved/manual collection | Effective administrator or precise Application permission |
| DELETE | `/collections/{id}/items/{itemId}` | Remove an item from a saved/manual collection. `409` "A collection needs at least one item. Delete the collection instead." when it is the last item of a curated collection; playlists may be emptied. A curated collection whose last work leaves the library is deleted automatically by the Engine (an `AutoPurge` activity entry records it). | Effective administrator or precise Application permission |
| POST | `/collections/reconcile` | Dry-run or run collection shelf repair for already-ingested media. Body: `dry_run`, `batch_size`, `max_items`. Returns candidate, processed, assigned, skipped, failed, and elapsed counts. | Effective administrator or precise Application permission |
| GET | `/collections/{collectionId}/series-manifest` | Ordered Wikidata series checklist with total, owned, missing, provisional, ambiguous counts and named entries | Required |
| GET | `/collections/search?q=` | SQL-backed search across visible library works, canonical values, and collection names. Returns up to 20 work results. | Required |
| GET | `/collections/personal-media/galleries` | List Galleries eligible for the administrator's Collection editor without exposing individual assets | Administrator + trusted profile |
| GET | `/collections/{id}/personal-media` | Return count-free Gallery/rule sources after reapplying the viewer's View authorization | Required + trusted profile |
| POST, PUT, DELETE | `/collections/{id}/personal-media[/{sourceId}]` | Manage whole-Gallery references or a versioned View rule; individual asset identifiers are rejected. DELETE answers `409` "A collection needs at least one item. Delete the collection instead." when the source is the last member (no items, no other source) of a curated collection | Administrator + trusted profile |
| GET | `/persons?catalog=true&q=&role=&lane=&sort=&offset=&limit=` | Paged canonical primary contributors on owned works for Collections / People | Required |
| GET | `/persons/role-counts?catalog=true` | Canonical owned-library contributor counts grouped by role | Required |

---

## Library Item

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/items/{entityId}/detail` | Full item detail including claims, canonical values, pipeline projection fields, artwork truth, and linked persons | Server administrator or Application with `review.read`, plus catalogue access |
| GET | `/library/items/{entityId}/history` | Processing history timeline for an item (identity, metadata, artwork and ingestion events) | Server administrator or Application with `review.read`, plus catalogue access |
| GET | `/library/items/{entityId}/editor-preferences/{profileId}` | Profile-owned notes, local tags, hidden/recommendation flags, and optimistic editor revision | Required |
| PUT | `/library/items/{entityId}/editor-preferences/{profileId}` | Atomically save supported display overrides and profile-owned editor preferences; returns `409` for a stale revision | Effective administrator or precise Application permission |

---

## Library Overview

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/overview` | Aggregated library health and readiness view, including `hidden_by_quality_gate`, `art_pending`, `retail_needs_review`, `qid_no_match`, and `completed_with_art` | Required |

---

## Metadata

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/metadata/claims/{entityId}` | All claims for an entity, grouped by field, with source and confidence | Required |
| GET | `/metadata/conflicts` | All unresolved metadata conflicts across the library | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/work-versions` | Bounded Work, Edition and file (asset) tree for the owned Work behind any Work, Edition or asset id. Each file also carries the facts the Details tab shows, read only from stored rows and never probed: `file_size_bytes`, `added_at`, `bitrate_bps`, `container`, `audio_tracks` (`language`, `codec`, `is_default`) and `subtitle_languages`; each is null or empty when nothing is stored | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/canon-discrepancies` | Field-level mismatches between the canonical value and file-embedded metadata | Required |
| GET | `/metadata/{entityId}/file-coverage?assetId=` | Episodes a TV file covers plus the other episodes of its season it could cover (404 when the file is not a TV episode) | Effective administrator or precise Application permission |
| PUT | `/metadata/{entityId}/file-coverage` | Replace the full list of episodes one TV file covers. Must include the file's own episode, stay within its season, name at most 6 episodes and skip episodes that already have another file. Idempotent: saving the same list changes nothing. Resolves the file's pending review and records a History entry | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/artwork` | Artwork context for the media editor, including variants by artwork type and preferred selections | Effective administrator or precise Application permission |
| GET | `/metadata/{entityId}/artwork/{scopeId}` | Artwork variants for a specific editor scope | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{scopeId}/{assetType}` | Upload a user-owned artwork variant for the selected type | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{scopeId}/{assetType}/from-url` | Add an artwork variant from a provider or user-supplied image URL | Effective administrator or precise Application permission |
| POST | `/metadata/{entityId}/artwork/{assetType}` | Compatibility upload route for an artwork type | Effective administrator or precise Application permission |
| PUT | `/metadata/artwork/{variantId}/preferred` | Make an artwork variant the preferred image for its artwork type | Effective administrator or precise Application permission |
| DELETE | `/metadata/artwork/{variantId}` | Remove an artwork variant from the item. Shared provider/image cache files are retained when still referenced elsewhere. | Effective administrator or precise Application permission |

---

## Streaming

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/stream/{assetId}` | Stream media file. Supports HTTP 206 byte-range requests for seeking. | Required |
| GET | `/stream/{assetId}/cover` | Full-size cover art (JPEG) | Required |
| GET | `/stream/{assetId}/cover-thumb` | 200px-wide thumbnail (JPEG, quality 75, SkiaSharp-generated) | Required |

---

## Ingestion

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/ingestion/scan` | Dry-run scan of configured library folders. Reports what would be ingested without making changes. | Administrator |
| POST | `/ingestion/rescan` | Scans the watched folders now (optional `{root_path, include_subdirectories}` for one folder). Also looks at the Import folder, even one set after startup, and files anything waiting in it. | Administrator |
| POST | `/ingestion/library-scan` | Scan library folders and update known file paths. Triggers ingestion for new files. | Administrator |
| POST | `/ingestion/upload` | Upload a file to the library's explicit destination (a folder Tuvima manages, checked through the source-mutation gate as a destination, with no fallback folder) identified by multipart `destinationLibraryId`; catalogued files retain stable intake IDs, while personal files are indexed directly into View without catalogue/provider work | Administrator |
| GET | `/ingestion/import-folder/waiting` | What is being left untouched in the optional Import folder because a media type has no managed folder yet (`reason` `no_managed_folder`) or its New media folder is offline, full or not writable (`folder_unavailable`). One entry per type with `media_type`, `folder`, `file_count` and a plain-language `message`; empty when nothing is waiting. A situation, not a state of the Import folder. | Effective administrator or precise Application permission |
| GET | `/ingestion/operations` | Dashboard snapshot backed by durable ingestion operation counts, numbered `stage_progress` rows, current activity, review state, provider health, and recent batch summaries. | Effective administrator or precise Application permission |
| GET | `/ingestion/queue-progress` | The live ingestion numbers, worked out once by the Engine from the per-file intake rows: `state` (`working`, `waiting` or `idle`), `files_found`, `files_done`, `files_waiting` and `waiting_for` (tools holding files), `files_more_found`, `outcomes` (`added`, `needs_review`, `failed`, `skipped`), `tally` (files per `media_type`), `files_unsorted`, `steps_total`, `steps_completed`, `progress` (0 to 1, never decreasing within an active period, 1 only when idle), `started_at`, `last_activity_at`, `estimated_seconds_remaining` (null unless reliable) and `updated_at`. The same snapshot is pushed on the Intercom `IngestionQueueProgress` event whenever it changes. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches` | Recent ingestion batches. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches/{batchId}` | Single ingestion batch summary. | Effective administrator or precise Application permission |
| GET | `/ingestion/batches/{batchId}/items` | Durable per-file item ledger for a batch, sourced from `media_operations`. | Effective administrator or precise Application permission |
| GET | `/ingestion/watch-folder` | Returns the derived current watch folder view from configured library source folders. | Required |

`/ingestion/operations.stage_progress` contains numbered ingestion stage rows with `stage_number`, `stage_key`, `label`, `completed_files`, `total_files`, `count_unit`, `percent_complete`, `active_count`, `queued_count`, `status_label`, `active_item_label`, `active_group_label`, `active_group_count`, `label_accuracy`, `artifact_label`, `artifact_count`, `detail_items`, `last_updated_time`, and `is_stale`. `count_unit` identifies what the counts measure, such as files, albums, people, artwork assets, or links, so clients do not relabel grouped work as files. `detail_items` is an optional list of `{ label, value, tone?, icon? }` rows populated by the Engine so clients do not hardcode provider math. Grouped provider work, such as batched Wikidata resolution, uses group labels instead of fake exact file labels unless per-file correlation is available.

The Dashboard renders Stages 1-8 as compact progress rows. Review/attention state remains in the snapshot for API consumers, but the Dashboard surfaces it through the top **Need Review** metric and the `recent_batches` review count rather than as another progress bar. `recent_batches` rows include batch id, status, timing, file totals, media-type counts, registered/review/failed counts, and artifact totals such as people, artwork, metadata, matched, and review.

---

## Operations And Capabilities

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/operations` | Durable work queue ordered by queue priority and position. Supports `queueName` and `limit` (default 200, capped at 250). | Effective administrator or precise Application permission |
| GET | `/operations/{id}` | One operation plus its event timeline. | Effective administrator or precise Application permission |
| GET | `/operations/summary` | Counts by durable operation status. | Effective administrator or precise Application permission |
| POST | `/operations/{id}/retry` | Requeue a durable operation for another attempt. | Effective administrator or precise Application permission |
| POST | `/operations/{id}/cancel` | Cancel a durable operation. | Effective administrator or precise Application permission |
| GET | `/assets/{id}/capabilities` | Explicit capability/readiness states for one media asset. | Effective administrator or precise Application permission |
| GET | `/capabilities/summary` | Counts by capability and status. | Effective administrator or precise Application permission |

---

## Search

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/search/universe` | Search Wikidata for entity candidates by title, author, and media type; returns `candidates[]` (`qid`, `label`, `description`, `instance_of`, `year`, `author`, `director`, `cover_url`, `wikipedia_extract`, `resolution_tier`, `confidence`, `bridge_ids`, `media_type`, `media_type_metadata`, `match_scores`), `query` and `media_type` | Effective administrator or precise Application permission |
| POST | `/search/retail` | Search configured retail providers for matching candidates | Effective administrator or precise Application permission |
| POST | `/search/resolve` | Unified resolve search - queries all active providers and returns ranked candidates | Effective administrator or precise Application permission |

---

## Review Queue

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/review/pending` | All items currently in the review queue; `from_import_folder` is true for a file still in the Import folder (the "From the Import folder" badge); `from_merge` is true for a file inside a managed folder's From merge folder (the "From merge" badge) | Effective administrator or precise Application permission |
| GET | `/review/count` | Count of pending review items. Used for media library badge. | Required |
| GET | `/review/{id}` | Full detail for a single review item including candidates | Effective administrator or precise Application permission |
| POST | `/review/{id}/resolve` | Resolve a review item by selecting a candidate or confirming corrected local metadata | Effective administrator or precise Application permission |
| POST | `/review/{id}/dismiss` | Dismiss a review item without resolving it | Effective administrator or precise Application permission |
| POST | `/review/{id}/move-to-tv` | Accept a "Found as a TV title" suggestion: re-file the film under the matched TMDB show in the TV library and re-run identity as TV. When TMDB lists the parts as one season of at most six episodes numbered from 1, the file is placed on Season 1, Episode 1 and covers every part; otherwise it falls back to Season 0, Episode 1 (Specials). The response carries `season_number`, `episode_numbers` and `placement_reason` (why it fell back, else null). The file on disk is never moved. | Effective administrator or precise Application permission |
| POST | `/review/{id}/skip-universe` | Accept the item without a Wikidata QID. The item can still remain browse surfaces-visible if it passes the browse readiness gate. | Effective administrator or precise Application permission |

---

## Persons

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/persons/{id}` | Person detail - biographical data, roles, library presence, social links | Required |
| GET | `/persons/{id}/aliases` | Pseudonym list for a person, including resolved Wikidata aliases | Required |

---

## Universes

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/universes` | All narrative roots (Universes) in the library | Required |
| GET | `/universe/{qid}` | Universe summary: label, level, and counts of the entities (all, characters, locations, organizations) and relationships visible to the caller | Required |
| GET | `/universe/{qid}/graph` | Graph data in Cytoscape.js format for the Chronicle Explorer visualization | Required |
| GET | `/universe/{qid}/paths` | Find shortest paths between two entities within the universe graph | Required |
| GET | `/universe/{qid}/family-tree` | Character family tree rooted at a specified character entity | Required |
| GET | `/universe/{qid}/cast` | Characters with their linked performers, including era-correct actor data | Required |
| GET | `/universe/{qid}/adaptations` | Adaptation chain - all works derived from or adapted into each other | Required |
| GET | `/universe/{qid}/lore-delta` | Check for Wikidata revision changes since the last enrichment pass; returns a list of `entity_qid`, `label`, `cached_revision`, `current_revision`, `has_changed` | Required |

---

## AI

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/ai/status` | AI subsystem health - model load state, hardware tier, resource pressure | Required |
| GET | `/ai/models` | Status of all configured models (loaded, unloaded, downloading, unavailable) | Required |
| POST | `/ai/models/{role}/download` | Trigger download for a model role (`text_fast`, `text_quality`, `text_scholar`, `text_cjk`, `audio`) | Administrator |
| POST | `/ai/models/{role}/load` | Load a model into memory | Administrator |
| POST | `/ai/models/{role}/unload` | Unload a model from memory | Administrator |
| GET | `/ai/config` | Full AI configuration | Administrator |
| PUT | `/ai/config` | Update AI configuration | Administrator |
| GET | `/ai/profile` | Hardware profile - benchmark results, tier classification, GPU backend | Required |
| POST | `/ai/benchmark` | Re-run hardware benchmark and reclassify tier | Administrator |
| GET | `/ai/resources` | Live system resource usage - CPU load, RAM pressure, active transcoding tasks | Required |
| GET | `/ai/enrichment/progress` | Background enrichment batch progress | Required |
| GET | `/ai/enrich/tldr/{entityId}` | Generate a TL;DR summary for a work using its description | Effective administrator or precise Application permission |
| GET | `/ai/enrich/vibes/{entityId}` | Generate vibe tags for a work | Effective administrator or precise Application permission |
| POST | `/ai/enrich/search/intent` | Parse a natural-language search query into structured field filters | Required |
| POST | `/ai/enrich/extract-url` | Extract metadata from a URL (book page, IMDB entry, etc.) | Effective administrator or precise Application permission |

`/ai/config` preserves the complete settings graph, including `models`,
`model_catalog`, `operational_roles`, `role_requirements`, feature flags, and
scheduling/resource policy. `/ai/resources` includes numeric `cpu_pressure`;
clients should use that contract value rather than derive pressure from labels.

---

## Enrichment

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/metadata/pass2/trigger` | Manually trigger the optional Pass 2 deferred enrichment flow for one or more entity IDs | Effective administrator or precise Application permission |
| GET | `/metadata/pass2/status` | Current Pass 2 deferred-enrichment status, including whether two-pass mode is enabled | Required |

---

## Activity

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/activity/recent` | Recent system activity entries, newest first | Required |
| GET | `/activity/log?category=&search=&offset=&limit=` | The Activity log (the Events part of System > Logs): one page of plain-worded events, newest first, as `PagedResponse<ActivityLogEntryDto>` with `total_count`. `category` is `added`, `review`, `failed`, `system` or `all` (the default; unknown means all). `search` matches names and details. `limit` defaults to 50 (max 250). Each entry has `id`, `occurred_at`, `category`, `kind`, `title`, `detail`, `media_type`, `count` (photos and personal videos arrive as one count row) and `route`. Needs `system.activity.read` | Required |
| POST | `/activity/prune` | Prune now with the Logs page retention rule: entries older than `activity_retention_days`, then the oldest entries past `activity_max_events`. Returns `deleted` (both together) and `retention_days` | Administrator |
| GET | `/activity/stats` | Aggregated activity statistics - counts by type and outcome | Required |
| GET | `/activity/by-types` | Filter activity log by one or more activity type codes | Required |
| GET | `/activity/run/{runId}` | All activity entries for a specific ingestion or enrichment run | Required |

---

## Logs

The System > Logs page (`/settings/system/logs`) reads these. Logs can name files and other people, so every action is for the server administrator only. Pruning is silent: nothing is raised when it removes something, and the Scheduled tasks list (`GET /system/scheduled-tasks`) shows when each clean-up last ran.

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/logs/technical` | The Engine's log files (`tuvima-*.log`, in `TUVIMA_LOG_DIR` or `logs`), newest first. Query: `from` and `to` (date and time), `level` (the lowest level wanted: `error`, `warning`, `information` or `all`), `area` (`Ingestion`, `Playback`, `Network`, `Providers`, `Security`, `AI` or `Other`, grouped from the logger name), `q` (text in the message or logger name), `cursor` (from the previous page's `next_cursor`) and `limit` (default 100, most 500). Returns `lines` (each `time`, `level`, `area`, `message`; an error's detail follows its first line) and `next_cursor` (null when there are no older lines). Files are opened for sharing, so the file being written can be read; a line that cannot be read is skipped. `400` for an unknown level or area, or a range that ends before it starts | Administrator |
| POST | `/logs/export/preview` | How big a log export for these filters would be. Body: `from` and `to` (both required), `level` (`errors`, `warnings_and_above` (default) or `everything`), `areas` (empty means all), `item_or_run` (a run id or item id; only lines and events that mention it), `show_file_names` (default false) and `include_events` (default true). The filters go in the body so an id is not left in browser history. Returns `line_count`, `size_bytes`, `truncated` and `file_name`; the figures are exact because the preview counts the same lines the export writes (it never builds the file in memory). `areas` may be null or empty (every area). `400` with a sentence for a missing range or an unknown level or area | Administrator |
| POST | `/logs/export` | Same body. Returns one UTF-8 text file `tuvima-logs-{from}-{to}.txt` (times in UTC) with a header that lists the filters and says private details are hidden, then the matching log lines and, for an `everything` export with events included, the system activity events for the range, oldest first. Keys and tokens (bearer values, `api_key=`/`token=`/`secret=`/`password=` values, JWT-shaped text, long hex and base64 runs), sign-in details (email addresses and the user named in a sign-in message) and file and folder paths are hidden; with `show_file_names` only the last part of each file name is kept, never the folders. The file is streamed, never built in memory. The filters (time, level, areas, text) run first; then at most 200,000 lines and 50 MB are kept, dropping the oldest, and the header says which limit capped it. Files and folders written as network paths (`\\server\share\...`) are hidden like any other path, and a whole `Authorization` header value is hidden whatever its scheme. `Cache-Control: no-store` | Administrator |
| GET | `/logs/retention` | The four limits (`settings`: `activity_retention_days`, `activity_max_events`, `log_retention_days`, `log_max_total_mb`) and what is stored now (`activity_event_count`, `log_file_count`, `log_total_bytes`) | Administrator |
| PUT | `/logs/retention` | Change the four limits (body: the `settings` object). Days 1 to 3650, events 1,000 to 10,000,000, megabytes 100 to 102,400; `400` with a sentence when any is out of range, saving nothing. The next nightly clean-up uses them. Replaces `PUT /activity/retention` | Administrator |

---

## Plugins

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/plugins` | List built-in and dynamic plugins loaded by the Engine | Administrator or application with `plugins.read` |
| GET | `/plugins/approved` | Fetch the approved plugin discovery catalog from the configured GitHub source | Administrator or application with `plugins.read` |
| GET | `/plugins/{pluginId}` | Plugin detail, manifest metadata, settings, permissions, and load state | Administrator or application with `plugins.read` |
| POST | `/plugins/{pluginId}/enable` | Enable a plugin. Returns `409 Conflict` (problem detail with the plain-English reason) when the plugin does not fit this Library version or platform, is a kind this version cannot run, or failed to load. | Administrator or application with `plugins.manage` |
| POST | `/plugins/{pluginId}/disable` | Disable a plugin | Administrator or application with `plugins.manage` |
| PUT | `/plugins/{pluginId}/settings` | Save plugin user settings JSON | Administrator or application with `plugins.manage` |
| GET | `/plugins/{pluginId}/manifest` | Read dynamic plugin manifest JSON. Built-in manifests are compiled and are not returned here. | Administrator or application with `plugins.read` |
| PUT | `/plugins/{pluginId}/manifest` | Save dynamic plugin manifest JSON without changing plugin id | Administrator or application with `plugins.manage` |
| DELETE | `/plugins/{pluginId}` | Delete a dynamic plugin folder and saved plugin configuration | Administrator or application with `plugins.manage` |
| POST | `/plugins/{pluginId}/health` | Run plugin health checks | Administrator or application with `plugins.read` |
| GET | `/plugins/{pluginId}/jobs` | List recent durable plugin operation rows for one plugin. | Administrator or application with `plugins.jobs.read` |
| POST | `/plugins/jobs/segment-detection/run` | Run scheduled playback segment detector plugins immediately | Administrator or application with `plugins.jobs.run` |

Plugin summaries also carry `kind`, `trust_level`, `minimum_library_version`,
`maximum_library_version`, `supported_platforms`, `is_compatible` and
`compatibility_problem`; `enabled` is the effective state (saved as on and
compatible).

Plugin responses preserve manifest capabilities, permissions, tool
requirements and per-platform artifacts, AI permissions, settings schema,
health checks, durable job counters, and approved-catalog provenance. Dashboard
presentation types must not narrow that payload.

---

## Admin

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/admin/provider-configs` | List all provider configurations | Administrator |
| PUT | `/admin/provider-configs/{providerId}` | Update a provider configuration | Administrator |
| DELETE | `/admin/provider-configs/{providerId}` | Remove a provider configuration | Administrator |

---

## Settings

| Method | Path | Description | Auth |
|---|---|---|---|
| POST | `/settings/test-path` | Test whether a filesystem path is accessible by the Engine | Administrator |
| GET | `/settings/providers` | Provider configuration status - enabled state, last health check, rate limit info | Required |
| GET | `/settings/server-general` | Core server settings (name, language preferences, country) | Required |
| PUT | `/settings/server-general` | Update core server settings | Administrator |
| GET | `/network/app-addresses` | The two addresses a person types into a Tuvima app: `at_home` (the detected home-network address with the Dashboard port, or `null`) and `away_from_home` (the outside origin once a secure outside path is set up, otherwise `null`). Any signed-in person may read it, because the address is not a secret; it carries nothing else from the network settings or status (the Apps page under Personal > Overview shows it with a QR code of that one address). `/network/status` stays administrator-only | Required |
| POST | `/settings/organization-template/preview` | Validate an organization template and return a sample preview without saving | Administrator |
| PUT | `/settings/organization-template` | Save the organization template after validation | Administrator |

---

## Profiles

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/profiles` | List profiles granted to the current account. Each item includes `has_pin` so the *Who's using Tuvima?* page can show a lock; the other profile endpoints return `has_pin: false` | Human session |
| GET | `/access/profiles` | Managed profiles. A profile that has its own sign-in carries `own_sign_in`: `account_id`, `email`, `must_change_password`, `is_enabled` (never a password or code). A household administrator sees only their own household's. | Identity users read |
| POST | `/access/profiles` | Create a profile and its explicit account grant | Identity users write |
| GET | `/profiles/{id}` | Profile detail | Required |
| GET | `/profiles/{id}/settings/playback` | Return a person's playback, listening, reading and subtitle settings. 404 for an unknown profile; 403 ("Only an administrator of this household can change these settings.") for anyone else | The person themselves; a server administrator for anyone; a household administrator for their own household (administrator PIN unlocked when turned on). Kids profiles never manage another profile |
| PUT | `/profiles/{id}/settings/playback` | Save those settings. Same rules and responses as the GET above. The `reading` group carries the book reader defaults alongside the older fields: `theme` (`light`, `sepia`, `dark`, `system`; default `system`), `font` (`book`, `serif`, `sans`; default `book`), `fontSizePx` (12 to 32, clamped; default 18), `lineHeight` (1.2 to 2.4, clamped; default 1.5) and `pageWidth` (`narrow`, `standard`, `wide`; default `standard`); `readingMode` (`Paginated` or `Scroll`) is the reader's layout. Stored settings written before these fields existed read back with the defaults. An unknown `theme`, `font` or `pageWidth` is rejected | Same as the GET above |
| GET | `/profiles/{id}/settings/view` | Return a profile's View permissions: `view_enabled`, `access_shared_library`, `submit_to_shared_library`, `allow_gallery_sharing`, `allow_documents` and `updated_at`. A profile with no saved row gets the defaults, with `allow_documents` off for a child profile. 404 for an unknown profile | Server administrator, or household administrator for their own household |
| PUT | `/profiles/{id}/settings/view` | Save those permissions (`allow_documents` is the Documents switch under Settings > Libraries > View > Who can see Documents; leaving it out, or sending null, keeps the saved value, so an older client can never switch Documents on for a child profile by accident, while the other switches are off unless sent). Turning View on provisions the Personal Space. 403 outside the administrator's household, 404 for an unknown profile | Server administrator, or household administrator for their own household |
| PUT | `/profiles/{id}/experience` | Save name, avatar color, and navigation preferences without role fields | Exact active profile |
| PUT | `/access/profiles/{id}` | (Needs a recent sign-in.) Rename a profile and change its look. Body: `display_name`, `avatar_color` (`#RRGGBB`), `avatar_icon` (a built-in icon key; omit it to keep the current icon, send an empty string to go back to the initial). `content_limit` (omit to keep the current limit; an empty string means Everything; otherwise `G`, `PG`, `PG-13` or `R`; anything else is a 400) and `content_limit_allow_unrated` (omit to keep). A change to either is recorded as `profile.content_limit_changed`. Responses carry `avatar_icon` next to `avatar_color`, and `content_limit` and `content_limit_allow_unrated`; `GET /profiles` does too. A photo uses the existing `POST /profiles/{id}/avatar`. | Identity users write |
| DELETE | `/access/profiles/{id}?photos=move\|delete` | Delete through account/grant safety rules without deleting originals. Habits (likes, My List, progress, bookmarks, taste) always go. `photos=move` (the default) keeps the person's personal photos in their household's Shared Library under a folder and tag named `From <name> <date>` (managed files move, linked originals are copied); `photos=delete` deletes them. Nothing is removed if a photo can't be kept or a removal rule refuses (last profile, Owner, final administrator, an account's only profile). Needs a recent sign-in (`confirm_its_you` otherwise) and a person the caller may manage; another household's person is 403. | Identity users write |
| POST | `/access/profiles/{id}/avatar` | Set someone else's profile photo (multipart `file`, JPEG/PNG/WebP up to 5 MB). Same rules and checks as above; `POST /profiles/{id}/avatar` still only changes the active profile's own photo. | Identity users write |
| DELETE | `/access/profiles/{id}/avatar` | Remove someone's profile photo. | Identity users write |

---

## UI Settings

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/settings/ui/global` | Global UI defaults | Required |
| PUT | `/settings/ui/global` | Update global UI defaults | Administrator |
| GET | `/settings/ui/device/{class}` | Device profile settings for a device class (`web`, `mobile`, `television`, `automotive`) | Required |
| PUT | `/settings/ui/device/{class}` | Update device profile settings | Administrator |
| GET | `/settings/ui/profile/{id}` | Per-user UI preference overrides for a profile | Required |
| PUT | `/settings/ui/profile/{id}` | Update per-user UI preferences | Required |
| GET | `/settings/ui/resolved` | Effective resolved settings for the current request context (global + device + profile merged) | Required |
| GET | `/settings/ui/library-preferences` | Current per-media library display preferences (missing-item visibility defaults) | Required |
| PUT | `/settings/ui/library-preferences` | Save per-media library display preferences to `config/ui/library-preferences.json` | Administrator |

---

## Reader

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/read/{assetId}/metadata` | EPUB metadata - title, author, language, cover | Required |
| GET | `/read/{assetId}/toc` | Table of contents | Required |
| GET | `/read/{assetId}/chapter/{index}` | Chapter content by index | Required |
| GET | `/read/{assetId}/resource/{path}` | Embedded resource (CSS, image) by path within the EPUB | Required |
| GET | `/read/{assetId}/file` | The book or comic file itself (`.epub`, `.cbz`, `.cbr`, `.pdf`) with HTTP byte-range support (`206`, `Accept-Ranges`, `ETag`, `If-Range`, `304`). Same library-read scope, asset access and profile content limit as the other reader calls, and its own `reader_files` rate limit counted per signed-in session (default 600 a minute, `rate_limiting.reader_files` in `config/core.json`); any other kind of file answers `404`. Responses are `Cache-Control: private, no-cache` with `nosniff` and a sandbox policy. Browsers reach it through the Dashboard route `/engine-book/{assetId}/file`, never directly | Required |
| GET | `/read/{assetId}/search` | Full-text search within EPUB content | Required |
| GET | `/reader/{assetId}/bookmarks` | List bookmarks for an asset | Required |
| POST | `/reader/{assetId}/bookmarks` | Create a bookmark | Required |
| DELETE | `/reader/bookmarks/{bookmarkId}` | Delete a bookmark. Another profile's bookmark answers `404`. | Required |
| GET | `/reader/{assetId}/highlights` | List highlights. Rows made by the book reader carry `cfiRange`; rows from the retired reader have it `null`. | Required |
| POST | `/reader/{assetId}/highlights` | Create a highlight. The book reader sends `cfiRange` (an `epubcfi(...)` range, at most 4096 characters) with `chapterIndex`, `startOffset` and `endOffset` all `0`. `color` must be one of the supported highlight colours, `selectedText` and `noteText` at most 4000 characters; anything else answers `400`. | Required |
| PUT | `/reader/highlights/{highlightId}` | Change a highlight's colour and note (a missing note clears it). Another profile's highlight answers `404`. | Required |
| DELETE | `/reader/highlights/{highlightId}` | Delete a highlight. Another profile's highlight answers `404`. | Required |
| GET | `/reader/{assetId}/statistics` | Reading statistics - time spent, completion percentage, sessions | Required |
| POST | `/read/{assetId}/whispersync` | Sync reading position across devices | Required |
| GET | `/read/{assetId}/whispersync` | Retrieve last sync position | Required |
| DELETE | `/read/{assetId}/whispersync` | Clear sync position | Required |

---

## Player

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/player/state` | Current playback session, queue, position, and audiobook history | Required |
| POST | `/player/queue/replace` | Replace the active playback queue | Required |
| POST | `/player/queue/items` | Add items to the active playback queue | Required |
| POST | `/player/command` | Apply transport commands such as play, pause, seek, volume, or speed | Required |
| POST | `/player/heartbeat` | Persist current playback timing and resume progress. Optional `offline` and `client_timestamp` mark a replayed offline heartbeat; one older than the server's record for the asset is ignored | Required |
| GET | `/player/audiobooks/{workId}/history` | Recent audiobook listening checkpoints | Required |
| GET | `/player/audiobooks/{workId}/bookmarks` | Saved audiobook playback bookmarks | Required |
| POST | `/player/audiobooks/{workId}/bookmarks` | Save an audiobook playback bookmark at a position in seconds | Required |
| DELETE | `/player/audiobooks/bookmarks/{bookmarkId}` | Delete an audiobook playback bookmark | Required |
| POST | `/player/audiobooks/{workId}/chapters/suggest-names` | Suggest display-only audiobook chapter names using local AI | Required |
| GET | `/player/audiobooks/{workId}/chapter-overrides` | List saved display-only audiobook chapter title overrides | Required |
| POST | `/player/audiobooks/{workId}/chapter-overrides` | Save one display-only audiobook chapter title override | Required |
| DELETE | `/player/audiobooks/{workId}/chapter-overrides/{assetId}/{chapterIndex}` | Delete one display-only audiobook chapter title override | Required |

Player and playback contracts carry the complete queue/session state, delivery
manifest, resume data, tracks/subtitles/chapters, audiobook history and
bookmarks, chapter-title provenance, offline variants, diagnostics, and user
playback settings. Text-track contracts retain source and normalized formats,
timing mode, ownership/preference/export flags, confidence, and URL.

---

## Companion

A second device on the same profile (for example a phone) can follow where another of that person's devices is playing, or hold a position set by hand ("episode 3", "chapter 12"). Each device's live state is written from its own `POST /player/heartbeat` calls into a record separate from the shared player, so two devices playing on one profile never overwrite each other. Only the caller's own profile is ever visible: the profile and the companion's own device id come from the caller's token, a different `profileId` is refused with `404`, and there is no administrator bypass. Every item is checked against the caller's catalogue scope; a device playing something the caller cannot see is left out, and a work outside the caller's scope is refused with `404`. Nothing leaves the machine.

| Method | Path | Description | Scope |
|---|---|---|---|
| GET | `/companion/devices` | The caller's devices that are playing or paused right now, newest first. Each carries its device id, name (the paired device's name when known), client, state, position, duration, work and asset ids and the playing item | `queue.read` |
| GET | `/companion/session` | This device's choice: `mode` is `none`, `follow` or `manual`. When following, `followed` holds that device's latest state, or is null with `isFollowedDeviceLive` false when it is not live | `queue.read` |
| PUT | `/companion/session/follow` | Body `{ "deviceId": "..." }`. Follow one of the caller's own devices (one that has reported playback for the profile, or one of its paired devices); any other id is `404` | `progress.write` |
| PUT | `/companion/session/manual` | Body `{ "workId": "...", "chapterIndex": 12, "positionSeconds": 30 }`. Hold a position set by hand; a negative chapter or position is `400` | `progress.write` |
| DELETE | `/companion/session` | Stop following and clear any manual position. `204` | `progress.write` |

A device is **live** when its last heartbeat was playing or paused and arrived within 45 seconds (the same window that keeps the shared player live). A replayed offline heartbeat never counts, a stopped or finished player is ended and not live. Clients should poll `/companion/session` about every 10 seconds while following; a followed device's position is at most one heartbeat (10 to 15 seconds) old.

---

## Progress

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/progress/{assetId}` | Playback or reading progress for an asset | Required |
| PUT | `/progress/{assetId}` | Update progress. A paired app replaying progress it recorded offline sends `offline: true` and `client_timestamp`; the later of the server's and the client's record wins instead of a stale revision being rejected (see Offline media for paired apps) | Required |
| GET | `/progress/recent` | Recently accessed assets with progress | Required |
| GET | `/progress/journey` | Full reading/watching journey across all assets for the current profile | Required |

---

## Library Character and Asset Data

| Method | Path | Description | Auth |
|---|---|---|---|
| GET | `/library/portraits/{portraitId}` | Stream one character portrait image (used by Cast and person credits) | Required |
| POST | `/library/enrichment/universe/trigger` | Queue a universe enrichment sweep (takes no QID) | Effective administrator or precise Application permission |

---

## SignalR

| Collection | Path | Direction | Description |
|---|---|---|---|
| Intercom | `/intercom` | Server -> Client | Real-time push events: ingestion progress, enrichment completion, `MediaOperationChanged`, `CapabilityStateChanged`, pipeline state changes, and review queue updates. Authentication required. Server-push only - clients do not send messages to the collection. |

SignalR payloads use `MediaEngine.Contracts.Realtime` except for the exact
frozen Universe/Chronicle `UniverseEnrichmentProgressEvent`. New events and new payload fields are not covered by
that exception.

---

## Development

Available in development environments only. These endpoints are removed in production builds. Every one requires a signed-in administrator (with the administrator PIN unlocked when it is turned on); scripts that relied on the development localhost bypass alone are refused.

| Method | Path | Description |
|---|---|---|
| POST | `/dev/reset-and-seed` | Preserve application configuration, reset catalogued library state, create selected Standard or Stress fixtures, and queue normal ingestion. |
| POST | `/dev/reset-library-data` | Clear catalogued media and derived state without deleting source media or application configuration. |
| POST | `/dev/factory-reset` | Clear the development database and generated state without deleting configured source media. |
| POST | `/dev/seed-library` | Seed the library with 22 EPUB test cases covering edge cases (pen names, foreign languages, series grouping, multi-author) |
| POST | `/dev/wipe` | Lower-level automated harness reset; generated-state scope preserves configuration and unrelated source media. |
| POST | `/dev/full-test` | Run full ingestion and enrichment pipeline on the seeded test library |
| POST | `/dev/reingest-library` | Pause file watching, reset generated database/cache/artwork state, scan every configured library source path, and leave watching paused until restart or explicit resume. |
| POST | `/dev/integration-test` | Run the integration test suite and return an HTML report |

## Related

- [How to Build, Test, and Verify Changes](../guides/running-tests.md)
- [Database Schema Reference](database-schema.md)
- [Security Architecture](../architecture/security.md)

## Access, application events, and telemetry

These routes belong to the Access replacement under final integration. Inbound credentials resolve the current Application; they do not carry a household role. Metadata, provider, and other outbound service secrets remain separate.

| Method | Engine path | Responsibility |
|---|---|---|
| GET | `/auth/sign-in-methods` | Dashboard-only (service credential). Answers "what can this visitor use to sign in from where they are?" before anyone has signed in. Sends `X-Tuvima-Client-Ingress` and `?https=true` when the visitor came over HTTPS; the header is recorded but where a visitor is never decides which methods appear. Returns `{ "password": bool, "passkey": bool, "external_providers": [{ "id", "display_name" }], "invitation_code": bool }`, computed with the same rules the sign-in actions enforce (the method is switched on; passkeys and providers also need the one public address). It never returns account emails, profile names or counts. Nothing is cached: the Dashboard asks once per sign-in page load. |
| POST | `/auth/session/validate` | Dashboard-only (service credential). Sends `X-Tuvima-Client-Ingress` (`this_computer` or `home_network`); the place is recorded with the session but is not enforced, so a session is valid from any device that reaches the server. |
| GET | `/auth/this-computer` | Dashboard-only (service credential) and only for a request the Dashboard did not receive through a forwarding proxy or tunnel. Returns `{ "display_name" }` of the account that has no password yet; the Dashboard's `GET /auth/login` uses it to skip the sign-in page. Any other caller, or no such account, gets the same plain `404`, so a caller without the service credential never learns the account exists. Any device that reaches the server can use this account until a password or passkey is added. |
| POST | `/auth/this-computer` | Same conditions as the `GET`. Body: `device_id`, `device_name`, `client`. Signs in the this-computer-only account without a password and returns the usual session response with `authentication_method` `ThisComputer`. `404` for a forwarded request or with no such account. |
| POST | `/auth/confirm` | Signed-in caller. Body: `password` (plus `two_step_code` when two-step codes are on), or `credential_json` + `state` from `POST /auth/confirm/passkey-options`. Marks the current session as just authenticated (`auth_sessions.authenticated_at`). Wrong secrets answer `401`; a locked credential is refused. |
| GET | `/auth/confirm/recent` | Signed-in caller. Returns `{ "recent": bool }` for the current session; the Dashboard calls it before a redirect-based action (linking a provider). |
| POST | `/auth/confirm/passkey-options` | Signed-in caller. Starts a passkey assertion used to confirm. |
| POST | `/auth/login` (two-step) | When the account has two-step codes on and the password is right, answers `202` with `{ "two_step_required": true, "pending_token": "..." }` and no session. |
| POST | `/auth/two-step/verify` | Dashboard-only (service credential), rate limited. Body: `pending_token`, `code` (6-digit app code or a recovery code), `original_client_ingress`, `original_client_is_https`. Issues the session like a password sign-in. Any failure answers `401` with one generic message. The token lasts 5 minutes, works once and is bound to its ingress. |
| POST | `/auth/two-step/setup` | Signed-in, recent sign-in required. Returns `{ "secret", "otpauth_uri" }` for a new unconfirmed setup. `409` if already on or no password. |
| POST | `/auth/two-step/enable` | Signed-in, recent sign-in required. Body: `code`. Turns two-step on and returns new recovery codes. |
| POST | `/auth/two-step/disable` | Signed-in, recent sign-in required. Body: `code` (app code or recovery code). |
| POST | `/access/accounts/{accountId}/two-step/reset` | Administrator, recent sign-in required. Turns two-step off for another account and records `account.two_step_reset` in the audit log. |
| GET | `/auth/device-profile` | Signed-in caller. Returns `{ "profile_id": guid|null }`: the profile this browser or device "always opens as" for the caller's account. A saved profile the account no longer has reads as `null`. Always scoped to the caller's own account and the device of the caller's own session; no other account or device can be named. |
| PUT | `/auth/device-profile` | Signed-in caller. Body: `{ "profile_id" }`. Saves "always open as" for this device. `403` when the account has no access to that profile. |
| DELETE | `/auth/device-profile` | Signed-in caller. Clears "always open as" for this device. `204` whether or not one was set. |
| GET | `/auth/sessions` | Signed-in person. Lists the sessions of their own account for **Settings > Security**, with the current one marked. |
| DELETE | `/auth/sessions/others` | Signed-in person, recent sign-in required. Open to any signed-in person, a member included, never only administrators. Signs out every other session of their own account (the account and current session come from the caller's own sign-in, never the request); the current session stays. |
| DELETE | `/auth/sessions/{sessionId}` | Signed-in person, recent sign-in required. Signs out one of their own sessions; `403` for a session that is not theirs, including from an administrator. Someone else's session uses `/access/sessions/{sessionId}`. |
| POST | `/auth/password/change` | Signed-in person, recent sign-in required. Body: `current_password`, `new_password`. Rate limited. |
| POST | `/auth/password/recovery-codes` | Signed-in person, recent sign-in required. Creates a new set of recovery codes, returned once; the earlier set is replaced. |
| POST | `/auth/password/recover` | Dashboard-only (service credential, rate limited). Recovers the account with a saved one-time recovery code and returns a new set of recovery codes. |
| POST | `/auth/password/reset/begin`, `/auth/password/reset/complete` | Dashboard-only (service credential, rate limited). Email password reset. Begin answers `202` whether or not the address has an account; complete sets the new password from the emailed link. |
| GET | `/auth/passkeys` | Signed-in person. Lists the passkeys on their own account. |
| DELETE | `/auth/passkeys/{credentialId}` | Signed-in person, recent sign-in required. Removes one passkey. The last usable sign-in method cannot be removed. |
| POST | `/auth/passkeys/registration/options`, `/auth/passkeys/registration/complete` | Signed-in person, recent sign-in required. Adds a passkey: start, then finish with the browser's credential. |
| POST | `/auth/passkeys/login/options`, `/auth/passkeys/login/complete` | Dashboard-only (service credential, rate limited). Passkey sign-in. |
| PUT | `/auth/profiles/{profileId}/pin` | Administrator or household administrator, recent sign-in required. Sets a profile's PIN (**Profile details** on the person's page). |
| POST | `/auth/account/secure` | Signed-in this-computer-only account. Body: `password` and/or a passkey (`passkey_credential_json`, `passkey_state`, `passkey_name`), plus `device_id`, `device_name`, `client`, and `email`, which is required when the account has none (`400` "Add an email to secure this account."; `409` "That email is already used.") and ignored when it has one. Adds the credential, clears `this_computer_only`, replaces recovery codes, revokes old sessions and returns a new session. |
| POST | `/auth/external-link` | Dashboard service credential, signed-in person with a recent sign-in. Body: `transaction_ticket` from the outside sign-in, and optional `email`. Links the outside sign-in to the account. An account that has no email takes the provider's email when it supplies a usable, unused one; otherwise `email` is required (`400` "Add an email to link this sign-in."; `409` "That email is already used."). The Dashboard does not send `email` yet. |
| POST | `/setup/v1/begin` | Dashboard-only (service credential). Body: `original_client_ingress` (the Dashboard's own classification; missing or unknown means `remote`) and optional `setup_code`. From `this_computer` or `home_network` no code is needed (`setup_code` is accepted and ignored). `remote` is refused. Success returns the setup session token (header `X-Tuvima-Setup-Session`) and ends any earlier setup session. The refusal is `403` with `{"reason","message"}` where `reason` is `setup_remote_refused`. `409` once an administrator exists. |
| POST | `/setup/v1/administrator` | Setup-session protected. Creates the first administrator. Body adds `sign_in` (`password`, the default, or `this_computer`) and `original_client_ingress`. With `this_computer`: only `display_name` is required (`email` is ignored; the account has none until it is secured), `password` must be empty, and the request is refused with `409` and `code: "not_available_here"` unless setup began, and this request came, from this computer or the home network. The account is created with no password (`accounts.this_computer_only = 1`), no recovery codes are returned, and no network setting changes. The account works from any device that reaches the server, with administrator rights, until a password or passkey is added. The password path is unchanged. |
| GET/POST/PUT | `/access/accounts` and `/{accountId}` | Managed account lifecycle and explicit account grants. A change that would disable, demote or delete (`DELETE`) the last enabled server administrator who can sign in answers `409` ("This is the last administrator who can sign in. Make someone else an administrator first."); so do switching off administrator access on, or removing, that administrator's grant, and deleting the person they administer as. |
| POST | `/access/accounts/{accountId}/temporary-password` | Administrator only, and only once the administrator's own account is secured. Body: `temporary_password` (12–128 characters, the usual password rules, and not the account's email or name) and optional `must_change_password` (default `true`). With `true` the password works only to choose a new one and stops working after 7 days; with `false` it is stored as an ordinary credential (no `must_change_password`, no expiry). Either way it signs the account out everywhere and returns the account with `must_change_password` and `temporary_password_expires_at`. Audited as `account.temporary_password_set` or `account.password_set`. `POST /access/accounts` also accepts `temporary_password`. |
| PUT | `/access/accounts/{accountId}/email` | Server administrator, or the household administrator of the account's household (never for your own account, an administrator's account by a household administrator, or an application without `identity.users.write`); a recent sign-in is required. Body: `email`. The address is trimmed and validated like any other sign-in email (400 on a bad one). `409` when another account already has it; the same account in another letter case is accepted. Returns the account and records `account.email_changed`. The password and sign-in sessions are kept, but any password-reset or two-step challenge already sent for the old address stops working, and paired apps and TVs must pair again. |
| GET | `/access/households` | The households with `id`, `name`, `profile_count`, `account_count` and `created_at`, by name. A server administrator (or an application with `identity.users.read`) sees every household; a household administrator sees only their own; a member, a child profile or a locked administrator gets `403`. |
| POST | `/access/households` | Server administrator only (or an application with `identity.users.write`); a recent sign-in is required. Body: `name` (1 to 100 characters, trimmed; `400` otherwise). Adds an empty household and returns it (`201`). Audited as `household.created`. |
| PUT | `/access/households/{householdId}` | Server administrator only. Body: `name`. Renames the household and returns it; `404` when unknown. Audited as `household.renamed`. |
| DELETE | `/access/households/{householdId}` | Server administrator only. Removes a household, but only when no person (profile) and no sign-in belongs to it and its Shared Library in View holds no shared photos (an empty Shared Library goes with it): otherwise `409` ("This household still has shared photos in View. Remove them first." when shared photos remain) and nothing is changed (no person, sign-in or data is ever moved or deleted by this action). `204` on success, `404` when unknown. Audited as `household.deleted`. |
| POST | `/access/households/{householdId}/people` | Administrator only, once their own account is secured. Body: `display_name`, `avatar_color`, `avatar_icon` (optional, a built-in icon key such as `fox`; anything else is a 400), `is_child`, `pin` (optional, 4–12 digits), `content_limit` and `content_limit_allow_unrated` (optional; left out, a child person starts on `PG` and anyone else on Everything). Adds a person to the household (up to 8; refused with "A household can have up to 8 people." when full). A child person is a restricted profile. The household's main sign-ins can open them, not as default. Returns the profile (`is_restricted` shows the child role). |
| POST | `/access/profiles/{profileId}/own-sign-in` | Administrator only, once their own account is secured. Body: `email`, `temporary_password`, optional `must_change_password` (default `true`) and optional `invite` (default `false`). An own sign-in always has a password: `temporary_password` is required (400 "Set a starting password.") unless `invite` is `true`, which runs the invitation path and must not carry a password. With a password and `must_change_password` `true` the person replaces it at first sign-in; with `false` it is an ordinary password. Creates the person's own sign-in in their household: one default grant to that person, never an administrator, following the household's main sign-in for feature and library access (`grants_inherit_from_account_id` on the account). With `invite` the response carries an `invitation` like `POST /access/invitations`. Refused if the person already has one or the email is taken. |
| DELETE | `/access/accounts/{accountId}/own-sign-in` | Administrator only. Deletes a person's own sign-in, ending its sessions and paired devices. The person and their data stay. Refused for any account that is not a single-person own sign-in, and when nobody else in the household could open the person. |
| PUT | `/access/accounts/{accountId}/household-admin` | Server administrator only, recent sign-in required. Body: `household_admin` (boolean). Makes the sign-in the administrator of its own household (or removes it) and records `household.admin_granted` / `household.admin_removed`. A person's own sign-in can never be one. Returns the account, which now carries `household_admin`. |
| — | Household administrators | A household administrator (not a server administrator) may also call the household actions above: add person, own sign-in create/remove, temporary password, email change, two-step reset, profile rename/delete, `PUT /auth/profiles/{profileId}/pin`, access and grant changes. Only for their own household and only for non-administrator targets; access handed out cannot exceed the household's main sign-in. `GET /access/accounts`, `/access/accounts/{id}`, `/access/profiles` and `/access/libraries` return only their own household (and the libraries it holds). Anything else is 403. |
| POST | `/access/invitations` | Administrator only. Creates an invitation and returns `{ account_id, code, expires_at, public_address }`: `code` is the one-time `XXXXX-XXXXX` code (ten characters without 0, O, 1, I or L; only its SHA-256 is stored), `public_address` is the saved public address or null. The code is not returned again. |
| POST | `/auth/invitations/preview` | Dashboard-only (service credential, rate limited). Body: `code`, `original_client_ingress`, `original_client_is_https`. Returns `{ email, expires_at }` for a code that can still be used, otherwise a plain `404`. Used to show the email, read-only, before choosing a password. |
| POST | `/auth/invitations/accept` | Dashboard-only (service credential). Body: `code` (dashes, spaces and case are ignored), `password`, device fields. Creates the sign-in for the invited account and returns a session. A bad, expired or used code answers `401` with the same message. |
| POST | `/auth/password/change-temporary` | A signed-in person whose password is temporary. Body: `current_password`, `new_password`, `device_id`, `device_name`. Replaces the password, ends every session of the account and returns a new normal session. Refused with the administrator message once the temporary password has expired. While `must_change_password` is true, every other Engine action except `POST /auth/session/validate` and sign-out answers `403` with `code: "password_change_required"`. |
| GET | `/access/devices` | Paired phones and TVs for **Settings > Security > Devices**. A human session only. A server administrator whose administrator PIN is unlocked (or who uses none) sees every active device, a household administrator (with their administrator PIN unlocked, when they use one) sees the devices of the ordinary sign-ins in their own household, and anyone else (including a server administrator who has not unlocked yet, and a household administrator acting as a child profile) sees only their own account's: the same rule as `/access/sessions`. Each item: `id`, `device_name`, `platform` (`mobile`, `television`, `automotive`, `web`), `client_name`, `account_id`, `account_display_name`, `profile_id`, `profile_name`, `paired_at`, `last_seen_at`, `backs_up_to` (the name of the person the phone backs up, empty until chosen), `backup_profile_id`, `household_id`. |
| PUT | `/access/devices/{deviceId}/backup-profile` | Chooses (`profile_id`) or clears (`null`) whose photos a phone backs up, without a PIN. A server administrator, or the household administrator of the phone's household; recent sign-in required. `204`, `403` for anyone else, `404` when the device is unknown or revoked, or the person is not in the phone's household. Phone photo uploads (`POST /view/uploads` with a device token) are stored for this person whoever the phone is browsing as, and answer `409` with code `choose_backup_profile` until one is chosen. |
| GET | `/access/sessions` | Signed-in sessions for **Settings > Security > Devices**. A human administrator only: a server administrator sees every account, a household administrator the accounts of their own household except server administrators' and other household administrators' sign-ins; anyone else, and any app, gets `403`. Only active (not ended, not expired) sessions are listed, newest first, and accounts with none are left out. Each item: `account_id`, `account_label` (the email, else the person's name, else `No email`), `email` (`null` when the sign-in has none), `household_id`, `profile_display_name` (the person a person's own sign-in opens, otherwise `null`), `sessions` (the same items as `GET /auth/sessions`). |
| DELETE | `/access/sessions/{sessionId}` | Signs out one session of someone in the caller's scope (as above). Recent sign-in required. `204` on success, `400` (`Use Sign out for your own session`) for the caller's own session, `403` when the session belongs to someone outside the caller's scope, `404` when it is unknown or already ended. Audited as `account.session_revoked_by_administrator`. |
| GET | `/access/self-service/external-logins` | Signed-in person. Lists their linked sign-ins (provider and email) for **Settings > Security**. |
| DELETE | `/access/self-service/external-logins/{loginId}` | Signed-in person, recent sign-in required. Unlinks one of their own linked sign-ins. |
| GET | `/access/self-service/notices` | Signed-in person. Lists the one-time reminders their account has dismissed (`dismissed`: notice keys, today only `secure-account`). Read from the caller's own account only. |
| POST | `/access/self-service/notices/{key}/dismiss` | Signed-in person. Remembers "Not now" on a reminder for their own account. `204`; `400` for an unknown key (nothing saved). |
| GET | `/access/admin-unlock` | Administrator or household administrator. Reads the administrator unlock state of this session. |
| POST | `/access/admin-unlock` | Administrator or household administrator, rate limited. Unlocks admin settings for this session with the administrator PIN, when the grant's administrator protection is on. |
| DELETE | `/access/admin-unlock` | Administrator or household administrator. Ends this session's administrator unlock. |
| GET | `/access/libraries` | Administrator or household administrator. Lists the Libraries that the **Libraries and lanes** sheet can offer. |
| PUT | `/access/accounts/{accountId}/access` | Administrator or household administrator, recent sign-in required. Replaces an account's Read, Watch, Listen and View features and library access in one save. |
| PUT | `/access/accounts/{accountId}/grants/{profileId}` | Administrator or household administrator. Adds a profile grant to an account. |
| DELETE | `/access/accounts/{accountId}/grants/{profileId}` | Administrator or household administrator. Removes a profile grant from an account. |
| PUT | `/access/accounts/{accountId}/grants/{profileId}/admin-protection` | Administrator or household administrator. Turns a grant's administrator protection on or off, with its PIN and unlock rule. |
| DELETE | `/access/devices/{deviceId}` | Revokes the device and all its tokens, then sends `ApplicationEventRevoked` to its live connection so the app signs out at once. `204` on success, `403` unless the device belongs to the caller's own account, to a sign-in in the household of the caller (a household administrator with their PIN unlocked), or the caller is a server administrator with their administrator PIN unlocked (or none set), `404` when the device is unknown or already revoked. |
| GET/POST/PUT/DELETE | `/access/applications` and `/{applicationId}` | Application identity and registered permissions |
| POST/DELETE | `/access/applications/{applicationId}/credentials` and `/{credentialId}` | One-time credential issuance and revocation |
| GET/POST/PUT/DELETE | `/access/applications/{applicationId}/webhooks` and `/{webhookId}` | Validated destinations, event selection, and delivery status |
| POST | `/access/applications/{applicationId}/webhooks/{webhookId}/rotate` | Rotate the protected signing secret; display it once |
| SignalR | `/application-events` | Bounded subscription/replay with current principal and resource authorization |
| GET | `/api/v1/playback/sessions` | Current scoped playback sessions |
| GET | `/api/v1/playback/history` | Durable scoped playback history |
| GET | `/api/v1/analytics/playback` | Scoped playback totals |
| GET | `/api/v1/analytics/users`, `/libraries`, `/devices` | Aggregation only after scope filtering |
| GET | `/api/v1/activity` | A person's own activity record (watched, listened, read, added to My list), newest first, cursor-paged (`limit` up to 200, `next_cursor`). Query: `scope` (`me`, the default, `profile:{id}`, `household:{id}` or `everyone`; the Engine refuses with `403` what the caller may not see and `404` for an unknown person or household), `q` (title, subtitle, album and device; also the person's name when the scope includes others), `kind` (repeat or comma-separate `watch`, `listen`, `read`, `my_list`), `from`/`to`, `outcome` (`finished` or `not_finished`), `device`, `group=title` (one group per work with its attempts and totals) and `expand=songs`. Consecutive songs less than 30 minutes apart, with no other activity by that person between them, come back as one `music_session` item (song and album counts, up to nine random album covers, the songs with `expand=songs`). Each entry has its own snapshot of the item, so deleted items still show. Rows carry `person` only when the scope includes others. No IP addresses or sign-in details. A paired app gets its own person only. Opening a scope that includes others writes `activity.viewed_others` once per 30 minutes per actor and scope. A refusal because a server administrator has not yet entered their PIN is a `403` problem with `"code": "admin_unlock_required"` (also on export, delete-preview and delete), which is what the Dashboard asks for the PIN on; every other refusal has no `code`. Needs `progress.read` |
| GET | `/api/v1/activity/periods` | Counts per year and month (UTC) under the same scope and filters, for the date rail |
| GET | `/api/v1/activity/recent?limit=20` | The caller's own latest activity for the Overview strip, music collapsed into sessions; each tile has a natural `shape` (`portrait` for books and comics, `landscape` for episodes and movies, `square` for music and audiobooks) |
| GET | `/api/v1/activity/summary` | The caller's own lifetime totals (never anyone else's, whatever the caller's role; a paired app gets its own person's). Fields: `watch_seconds`, `read_seconds`, `listen_seconds` (`music_seconds` plus `audiobook_seconds`), `finished_count` (titles with at least one finished attempt), `in_progress_count` (titles whose latest attempt is not finished) and `first_activity_at` (UTC, `null` when there is no activity). Songs and My list adds are not counted as titles. Needs `progress.read` |
| GET | `/api/v1/activity/export.csv` | The same filters as a CSV file of exactly the matching entries. Signed-in people only. A scope that includes others needs an effective server administrator (`403` otherwise) and writes `activity.exported_others` every time |
| GET | `/api/v1/activity/delete-preview?before=&scope=` | How many entries before the date would be deleted. Server administrator only (unlocked when they use an administrator PIN); `403` for everyone else |
| DELETE | `/api/v1/activity?before=&scope=` | Deletes the entries that started before the date in the scope, and the small pictures nothing uses any more, and writes `activity.history_deleted` (scope, date and count) in the same transaction: if the audit entry cannot be written nothing is deleted. Server administrator only. This is the only way activity history is deleted; nothing prunes it |
| GET | `/api/v1/activity/artwork/{key}?scope=` | The small cover copy kept with an entry (`Cache-Control: private`). `404` unless the caller may see at least one entry that uses it |
| GET | `/api/v1/taste?scope=` | The taste of each person the scope covers (`me`, the default, `profile:{id}`, `household:{id}` or `everyone`; `403` for what the caller may not see, `404` for an unknown person or household, `400` for a malformed scope), with the person's own removals and additions applied on top of what the Library learned. Per person: `profile_id`, `person` (the name, only when the scope includes others), `is_you`, `genres`, `moods` and `eras` (each item `{value, weight, source}`: `weight` is 0 to 1 and `null` for a typed value, `source` is `learned` or `added`), `media_mix`, `summary` and `last_updated_at`; the body also carries `suggestions` (known genres, moods and decades) for the Add controls. A household administrator is limited to their own household, a server administrator may ask for anyone, a paired app gets its own person only. Opening a scope that includes others writes `taste.viewed_others` once per 30 minutes per actor and scope. Needs `progress.read` |
| POST | `/api/v1/taste/items` | Adds `{group, value}` (`group` is `genres`, `moods` or `eras`) to the signed-in person's own taste. Nobody can change anyone else's taste (the route carries no person), and a paired app cannot edit: `403`. Values are trimmed, lower-cased and at most 60 characters; an era is a decade such as `1990s`; otherwise `400`. Returns `204` |
| DELETE | `/api/v1/taste/items/{group}/{value}` | Hides that value from the signed-in person's own taste (the learned taste is not changed, so it stays hidden until the person adds it back). Same rules as the POST. Returns `204` |

Telemetry read operations have separate registered permissions; cross-session control remains unavailable. Delivery mode and transformed media properties remain unknown without trustworthy observed stream facts. Webhook retries use the same signed envelope and stable delivery identity; receivers must handle repeated deliveries idempotently. A replay gap requires a fresh authorized snapshot before resuming the stream.
