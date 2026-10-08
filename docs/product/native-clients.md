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

## Product boundaries

The currently frozen client authorization contract binds one device grant to
one profile. Switching profile therefore re-pairs the client. A future in-app
household profile switch requires a separately approved additive API contract.

Automotive clients expose only Listen content and native media controls. They
do not expose Watch, Read, View, administration, metadata correction, or the
general Dashboard.

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
