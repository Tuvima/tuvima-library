---
title: "Native Client Delivery"
description: "Implementation and release gates for Android TV, Android, iOS, Roku, TV web (Tizen and webOS), CarPlay, and Android Auto."
audience: administrator
category: "reference"
product_area: "native-clients"
tags:
  - "mobile"
  - "television"
  - "automotive"
status: early-access
---

# Native Client Delivery

Use this page to distinguish client code in the repository from apps you can install. The stages below still need device, signing or store acceptance; source availability is not a release announcement.

Native clients consume the frozen public API v1 through the Dashboard origin.
They never connect to the Engine port and never receive the Dashboard service
credential. The executable wire fixtures are under
`tests/fixtures/native-client-v1`.

## Delivery stages

| Stage | Deliverable | Repository status | Release evidence still required |
| --- | --- | --- | --- |
| 0 | API v1 entry gate | Implemented and covered by executable discovery, pairing, token, playback, ownership, and wire-contract tests | Run against the same candidate server build used for device certification |
| 1 | Android TV pilot | Shared Kotlin client, television UI, D-pad navigation foundation, pairing, browse, artwork, HLS/direct playback, captions, progress, and resume implemented. The Home screen now leads with Continue, every row is focusable with a visible focus border, and Collections and View (photos, when the person allowed them at pairing) rows are added; the shared client gained the View, Collections, Universe and profile-switch calls with unit tests for their routes and typed results | Android CI build, low/current hardware matrix, remote-control and adaptive-HLS sign-off |
| 2 | Android and iOS | Secure credentials, background/system playback, downloads, network recovery, deep links, and resume implemented. The Android phone app now has Dashboard-parity screens on API v1 only: bottom navigation (Home, Libraries, Search, Downloads, Settings); Home with Continue first; Libraries switching between Watch, Read, Listen (the Dashboard's sort and filter in a bottom sheet), View (month-grouped timeline, galleries and a pinch-to-zoom full-screen viewer) and Collections (catalogue, items and Universe shelves); Details with Play or Resume, a Download button that asks for a quality (Small, Standard or Audio), Download next episodes on a show, cast and editions; Settings with server address, profile switch with PIN prompt, default download quality, an offline storage limit (default 10 GB; new downloads are blocked once it is reached) and sign out. Downloads lists each saved copy with artwork, title, size, quality and expiry, with pause, resume (HTTP range) and delete; the queue is a WorkManager job, a finished copy plays from the local file, progress recorded on the phone is replayed oldest first with `offline: true` and `client_timestamp` (the app adopts the Engine's record when the Engine kept its own), every store and delete is reported to `/api/v1/devices/current/downloads` and retried when offline, and a copy missing from that list on the next read is treated as expired and removed. Photos from View can be kept on the phone too, under the same storage limit and in the same Downloads list: a gallery has Keep on this device (all its photos, newest first, until the limit) and the photo viewer has Keep on device for the photo on screen. The app saves the 2048 px preview rather than the original to save space, skips videos, reports each kept photo to the same device download list (`kind` `photo`), reads its local copy first in the viewer and the timeline (the timeline shows the kept photos when the server cannot be reached), and removes a kept photo whose row is gone from the Engine's list (deleted, or the person can no longer see it), telling the Engine to forget it. The Engine's 500-entry limit is per device across everyone who used it, so a refusal (409) when reporting a photo stops the keeping, drops that unreported copy and says "This device has reached its offline limit." Books are not downloaded by this app yet (`OfflineKind.Book` and `LocalBookFiles` are an unwired hook for a future reader). The offline paths are written but not yet run on a device or emulator. The photo scopes are requested only when the person ticks the optional photos and videos box before pairing The iPhone app now has Dashboard-parity screens on API v1 only: a tab bar (Home, Libraries, Search, Downloads, Settings); Home with Continue first, then the Home shelves in order; Libraries switching between Watch, Read, Listen (the Dashboard's sort and filter in a sheet), View (month-grouped timeline, galleries and a pinch-to-zoom full-screen viewer) and Collections (catalogue, items and Universe shelves); Details with Play or Resume, Download, cast and editions; and Settings with server address, profile switch with PIN prompt, default download quality and sign out. Downloads lists offline copies with artwork, title, size, quality, expiry, pause, resume and delete, with a storage cap in Settings (default 10 GB) and a quality picker, plays the local file when present, replays offline progress when back online and reports stores and deletes to the Engine; photos from View can be kept on the device too (a gallery's Keep on this device, or Keep on device in the viewer; 2048 px previews, no videos, fetched while the app is open), listed in the same Downloads list under the same cap and opened from the device; and the photo scopes are requested only when the person ticks the optional photos and videos box on the connect screen. Apple TV and CarPlay are unchanged | Android and Apple CI builds, phone/tablet device matrix, interruption/Bluetooth/download sign-off, store metadata |
| 3 | Roku | SceneGraph/BrightScript pairing, browse, search, detail, playback, captions, progress, and resume implemented; Continue, Collections, and Photos rows, a full-screen photo viewer, and a profile picker with PIN dialog added (no device test yet, no downloads) | Sideload matrix, pairing-policy confirmation, artwork/localization, Roku certification |
| 3b | TV web shell (shared by Samsung Tizen and LG webOS) | Plain JavaScript client in `clients/tv-web` on API v1 only: server address, device-code pairing, Home with Continue, Watch, Read (browse only), Listen and Collections rows, Universes, search, details with seasons and episodes, HLS playback through hls.js with captions, resume and progress, profile switch (initials when there is no picture, PIN pad on 428), and settings (quality, HEVC, sign out). It has its own TV interface, D-pad navigation with Tizen and webOS keys, and a visible focus ring. The Dashboard answers the cross-origin request a packaged app makes, for app origins only and only while app access is on | Tizen packaging and Samsung device sign-off (N7b), LG device sign-off (N7c; the webOS package is in `clients/tv-web/platforms/webos`, built with `ares-package`, and needs app access turned on first), hardware matrix for HEVC and captions |
| 3c | Samsung Tizen package | `clients/tv-web/platforms/tizen`: a Tizen `.wgt` project (config.xml with the internet and TV input privileges, icon, remote media-key registration) that wraps the shared TV web client. `package.ps1` stages the shared files and prints the Tizen CLI build, package, install and run commands; the README covers TV Developer Mode and sideloading. The packaged app runs from a non-web origin, so `native_app_access.enabled` and `native_app_access.allow_browser_apps` must be on in `config/network.json` first | Build, signing and sideload on a real Samsung TV (needs Tizen Studio), store listing, hardware sign-off |
| 4 | Android Auto and CarPlay | Listen-only music, audiobooks, playlists, recent items, queue-compatible media sessions, and native Now Playing surfaces implemented | Desktop Head Unit/vehicle tests, Apple managed CarPlay entitlement, both platform reviews |
| 5 | Release train | Cross-client CI and API/source boundary gates implemented | Signing credentials, store records, privacy/support declarations, phased rollout, telemetry review, and rollback ownership |

“Implemented” in this table means source is present and repository-verifiable;
it does not mean a vendor has approved a binary. A stage moves to released only
when all evidence in its final column is attached to the release candidate.

## Implemented foundations

- Android shared discovery, pairing, rotating credentials, display, playback,
  progress, queue, and offline-delivery client.
- Android TV Compose/Media3 pilot surface.
- Android phone Compose surface, background `MediaLibraryService`, system
  media session, Android Auto browse tree, and private resumable downloads.
- iOS SwiftUI client, Keychain credentials, AVPlayer background transport,
  system controls, network switching, background downloads, and deep links, plus
  the Home, Libraries, Search, Downloads and Settings tabs described in stage 2.
- CarPlay audio-only Listen templates for music, audiobooks, playlists, recent
  items, and Now Playing.
- Roku SceneGraph client for pairing, browse, search, details, playback,
  captions supplied through native playback, progress, token rotation, and
  resume.
- TV web client (`clients/tv-web`) for Samsung and LG televisions: pairing,
  browse, collections, search, details, hls.js playback with captions, resume,
  progress, token rotation, profile switch, and settings. It is checked by
  `tests/native/Test-TvWebClient.ps1` and the `tv-web` CI job.

## Collections and Universes in apps

API v1 gives paired apps a read-only view of Collections: the same catalogue the
Dashboard Collections page shows, one collection's items, and a Universe with
its member shelves. Apps name and route each group by its `group_type`: a TV
show is a *show*, a book or movie sequence is a *series*, a rollup across
shelves or a rule-built group is a *collection*, a hand-picked or system group
is a *list*, and a franchise rollup opened by its identifier is a *universe*.
A Universe in the collections list carries its `universe_qid`, which an app uses to
open the Universe; other entries have none, and an app is never handed an
identifier its profile cannot open.
The content limit (Kids and maturity) of the paired profile applies to every
item, count and preview, so a Kids phone never shows, or counts, an item over its
limit; a collection that is unknown, someone else's, or empty for that profile
looks the same to the app. Apps cannot create, edit, reorder or share
collections, cannot read a Universe's relationship graph or lore, and never see
personal-media sources or Documents through collections. Routes and fields are in the
[API reference](../reference/api-endpoints.md#collections-and-universes-read-only).

## Product boundaries

A paired phone or television switches profile inside the app without
re-pairing. The app lists the people its account may use
(`GET /api/v1/devices/current/profiles`) and switches with
`POST /api/v1/devices/current/switch-profile`. The device keeps its identity and
approved scopes; each device has exactly one active profile, so a switch issues
a new token pair for the chosen person and revokes the old one at once. The
person's PIN is checked at every switch, including when moving out of a Kids
profile, with the same lockout as the Dashboard. Only people the account is
granted can be listed or chosen, and a Kids profile keeps its content limit
after the switch. Choosing the profile already in use is refused (`409`), and
each device may list or switch at most 30 times a minute.

A TV app packaged for Tizen or webOS is not served from the Dashboard's address.
When an administrator turns on `native_app_access.allow_browser_apps` in
`config/network.json` (off by default, in addition to app access), the Dashboard
gives it a narrow cross-origin permission: only app origins (`file:`, `app:`,
`tizen:`, `webos:` and the `null` origin), only `/api/v1` and `/.well-known/tuvima`,
no credentials. Because any website can claim those origins, turn it on only for a
TV web app; a person must still approve each pairing on the pairing page. See
[Configuration](../reference/configuration.md). The TV
web client plays video as adaptive HLS (H.264 and AAC; HEVC optional) through the
`tv-web` playback profile, because a browser video element cannot send the app's
token.

Offline media works in three small, additive pieces on API v1. First, an app
that watched or listened offline syncs back with `offline: true` and a
`client_timestamp` on its progress and heartbeat requests; the later of the
Engine's and the device's record wins for each item, a stale update from an
ordinary request is still refused, and a device clock in the future is treated
as "now". An offline play that is applied also appears in the profile's History
once the app reconnects, stamped with the time the device recorded (never a
future time), and syncing the same play again never adds a second entry; a play
the profile may not access, or one the Engine already has something newer for,
adds nothing. Second, the app can report which downloads it holds
(`GET/POST /api/v1/devices/current/downloads`, `DELETE .../{variantId}`) so a
second launch or a profile switch shows the right list; the list belongs to that
device and profile only, a download the profile may not access answers like one
that does not exist, and the Engine removes a download (and its list entry) when
it expires after `variant_retention_days`. The same list also carries photos the
person chose to keep on the phone (`kind` `photo`, with a `photo_item_id`, no
expiry, `DELETE .../photos/{itemId}`), judged by the photo routes' own rules and
counted under the same 500-entry cap; the `book` kind is reserved and refused
until books reach the native apps. Third,
`GET /api/v1/details/show/{id}/next-episodes?after={assetId}&count=3` gives the
next owned episodes in watch order for an Up Next row, respecting a Kids
profile's limit. Client impact is `client-optional`: released apps keep working
unchanged, and newer apps add offline sync and the download list. Executable
fixtures are `device-downloads.json` and `next-episodes.json` under
`tests/fixtures/native-client-v1`.

Offline records belong to one person, and a sync is tied to the person who was
using the app when it started. The Android and iPhone apps check that the
signed-in profile is unchanged before each request and before applying an
answer; if the profile is switched mid-sync the run stops without reporting,
replaying or removing anything, and the new profile gets its own run. When the
Engine refuses to refresh a device's sign-in with the OAuth error
`invalid_grant` (read from the answer's JSON body; the Engine answers every
token failure with 400 and every real revocation carries that code) the app
drops the sign-in, deletes its downloads and unsent records like a sign-out, and
returns to pairing. Any other failure, including a 400 without that code, a 401
or 403 from a proxy, an HTML page, a network error or a server error, never
signs the person out. Refreshes are single-flight and a refreshed token is stored
only for the profile it was requested for. Because the Engine's profile switch
revokes the device's old tokens, a refresh refused while a switch is pending, or
one that overlapped a switch, is treated as a bad moment and not a revocation. These paths are covered by unit tests
on Android; the iPhone tests are written but not yet run in CI.

Automotive clients expose only Listen content and native media controls. They
do not expose Watch, Read, View, administration, metadata correction, or the
general Dashboard.

## Photos on API v1

The Engine serves a paired phone or tablet its own photos and videos under
`/api/v1/view`: a month-grouped timeline, Galleries, people, places, item
details, thumbnails, previews, the original file, and a playback manifest for a
personal video. It is read-only and shows only the person the device was paired
to; the app cannot ask for anyone else's space or for a scope. Documents are not
part of it: a document never appears, and asking for one answers exactly like an
id that does not exist, so a profile without Documents cannot tell one is there.
The routes and wire types are in the [API reference](../reference/api-endpoints.md)
and the executable fixtures `view-timeline.json`, `view-item.json` and
`view-galleries.json` under `tests/fixtures/native-client-v1`. Client impact is
`client-required`: no released app has a Photos screen yet.

An app only sees a person's photos and personal videos when that person allows
it on the pairing screen. The screen shows one separate item, **Allow access to
my photos and videos**, unticked by default and explained in plain words
("browse your own photos and videos, view and download originals, and your
galleries, including galleries shared with you"). It stands for three scopes
(`view.personal.read`, `view.originals.read` and `view.galleries.read`), which
an app may request at pairing but which are never part of the default scopes.
Leaving the item unticked or pairing before this change gives the device none
of them, and every `/api/v1/view` route answers `403`. Ticking it grants all
three scopes the app asked for, to that device only.

An app gets only what the person approves. If every ordinary item and the photo
item are unticked, the pairing screen refuses the approval with "Choose at least
one thing this device may do, or deny the request." and nothing is paired; a
visible note beside **Approve device** says so beforehand, and the check is made
on the Dashboard's server, so it does not depend on scripts. The photo item is
shown only when the app asked for photos and its Application holds the three photo
permissions. A Media Player preset app, or the built-in app after an administrator
removed the permissions, does not show it, and a decision that ticks photos for
such an app is refused with a clear message instead of creating a device that
could not sign in.

Re-pairing is the only way to change the photo choice: there is no separate
switch for photos on a paired device. Removing the device (Settings, Devices)
revokes its tokens, so its photo access ends at once (`401`). Removing even one of
the three photo permissions from the built-in app later (an administrator action in Settings)
signs out every device that opted in, because a token is only valid while its
Application holds every scope the token carries; those devices must be paired
again. On an existing data store, a one-time startup step added the three photo
permissions to the built-in app even when an administrator had trimmed its
permissions earlier; they do nothing for a device until its person ticks the photo
item. The Dashboard's app door forwards only the eleven read-only `GET` routes of
`/api/v1/view` (no write, no other `view/` path).

## Release and update behavior

Native clients are versioned and released independently from the Engine and
Dashboard. A server release that stays within API v1 can add data or behavior
that an existing client already knows how to render without requiring a new
binary. A feature that adds native screens, controls, capabilities, platform
permissions, or a new API contract requires a client release.

Published client releases use the platform's normal update channel:

- Apple App Store for iPhone, iPad, Apple TV, and CarPlay;
- Google Play for Android phones, tablets, Android TV, and Android Auto; and
- the Roku Channel Store for Roku.

Those stores can deliver published releases automatically when automatic
updates are enabled on the device. CI builds and validates candidates but does
not publish them automatically. Production submission remains an explicit,
signed release action followed by phased rollout and monitoring. The server
must retain the unchanged API v1 contract for every still-supported published
client; incompatible work requires a separately versioned API and a client
migration window.

Each product feature must declare one of three client impacts during planning:

1. `server-only` — existing released clients consume it without binary changes;
2. `client-optional` — older clients remain functional while newer clients add
   the experience; or
3. `client-required` — the server feature remains disabled until the minimum
   supported client versions are available through every affected store.

## Release gates

Source completion is not store approval. Production release additionally
requires:

- physical Android TV and Roku tests across low and current hardware;
- physical iPhone/Android network, interruption, Bluetooth, and download tests;
- a non-loopback adaptive HLS pass using `tests/playback/Test-AdaptiveHlsClient.ps1`;
- Android Auto Desktop Head Unit and vehicle tests;
- Apple CarPlay managed audio entitlement plus simulator and vehicle tests;
- Roku confirmation that browser-approved device pairing satisfies its current
  on-device authentication criteria;
- store signing, screenshots, localization, privacy declarations, support
  details, phased beta rollout, and final platform review.

Every released binary must pass against the same server build. Client-specific
API forks, Engine-origin access, service-key forwarding, and server filesystem
paths are release blockers.

## Next steps

- [Check product status](status.md).
- [Set up development](../tutorials/dev-setup.md).
- [Understand network and remote access](../architecture/network-and-remote-access.md).
