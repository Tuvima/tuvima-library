---
title: "Native Client Delivery"
description: "Implementation and release gates for Android TV, Android, iOS, Roku, CarPlay, and Android Auto."
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
| 1 | Android TV pilot | Shared Kotlin client, television UI, D-pad navigation foundation, pairing, browse, artwork, HLS/direct playback, captions, progress, and resume implemented | Android CI build, low/current hardware matrix, remote-control and adaptive-HLS sign-off |
| 2 | Android and iOS | Secure credentials, background/system playback, downloads, network recovery, deep links, and resume implemented | Android and Apple CI builds, phone/tablet device matrix, interruption/Bluetooth/download sign-off, store metadata |
| 3 | Roku | SceneGraph/BrightScript pairing, browse, search, detail, playback, captions, progress, and resume implemented | Sideload matrix, pairing-policy confirmation, artwork/localization, Roku certification |
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
  system controls, network switching, background downloads, and deep links.
- CarPlay audio-only Listen templates for music, audiobooks, playlists, recent
  items, and Now Playing.
- Roku SceneGraph client for pairing, browse, search, details, playback,
  captions supplied through native playback, progress, token rotation, and
  resume.

## Collections and Universes in apps

API v1 gives paired apps a read-only view of Collections: the same catalogue the
Dashboard Collections page shows, one collection's items, and a Universe with
its member shelves. Apps name and route each group by its `group_type`: a TV
show is a *show*, a book or movie sequence is a *series*, a rollup across
shelves or a rule-built group is a *collection*, a hand-picked or system group
is a *list*, and a franchise rollup opened by its identifier is a *universe*.
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

Offline media works in three small, additive pieces on API v1. First, an app
that watched or listened offline syncs back with `offline: true` and a
`client_timestamp` on its progress and heartbeat requests; the later of the
Engine's and the device's record wins for each item, a stale update from an
ordinary request is still refused, and a device clock in the future is treated
as "now". Second, the app can report which downloads it holds
(`GET/POST /api/v1/devices/current/downloads`, `DELETE .../{variantId}`) so a
second launch or a profile switch shows the right list; the list belongs to that
device and profile only, a download the profile may not access answers like one
that does not exist, and the Engine removes a download (and its list entry) when
it expires after `variant_retention_days`. Third,
`GET /api/v1/details/show/{id}/next-episodes?after={assetId}&count=3` gives the
next owned episodes in watch order for an Up Next row, respecting a Kids
profile's limit. Client impact is `client-optional`: released apps keep working
unchanged, and newer apps add offline sync and the download list. Executable
fixtures are `device-downloads.json` and `next-episodes.json` under
`tests/fixtures/native-client-v1`.

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
