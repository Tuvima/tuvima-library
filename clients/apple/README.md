# Tuvima for Apple platforms

The iOS application includes the CarPlay audio scene. Generate the Xcode project
with `xcodegen generate`, then build the `Tuvima` scheme. A CarPlay Audio managed
entitlement must be granted to the product's Apple Developer account before a
signed CarPlay build can be distributed.

The app consumes only the Dashboard `/api/v1` contract. Tokens are stored in
Keychain, downloaded files are excluded from backup, and signed HLS URLs are
never persisted.

## Screens

The iPhone app is a tab bar over API v1 only (no Engine port, no service key):

| Tab | What it does |
| --- | --- |
| Home | Continue first, then the `/display/home` shelves in the order the server gives them. |
| Libraries | Chips for Watch, Read, Listen, View and Collections. Watch, Read and Listen page `/display/browse?lane=` with the Dashboard's sort and filter in a sheet. View is the person's own photos and videos (month-grouped timeline, galleries, pinch-to-zoom viewer). Collections is the catalogue, one collection's items and a Universe's shelves. |
| Search | `/display/search`, each result opens its details. |
| Downloads | The offline library: artwork, title, size, quality and expiry for each download, Play from the device's own file (no connection needed), Pause and Resume, Delete, and "Download next episodes" for a show. Quality (Small, Standard, Audio) is picked each time a download is started from a poster or a details page. |
| Settings | Server address, profile switch (PIN prompt on a `428`), photo access state, default download quality and sign out (removes the device on the server). |

### Offline downloads

Downloads exist only on the device; the Dashboard never stores media. Each person has their own folder (excluded
from backups), and a background `URLSession` does the transfers so they continue with the app closed. Settings
holds the storage limit (2 to 50 GB, default 10 GB); new downloads are blocked once downloads use the whole limit.
The app never writes History: playing a downloaded file records progress on the device, and when a connection is
back the progress is replayed oldest first to `PUT /api/v1/progress/{assetId}` with `offline: true` and
`client_timestamp` (when the Engine kept a newer record the app adopts it). Stored and deleted copies are
reported to `/api/v1/devices/current/downloads` (204 is success; a failed report is retried later), and a copy
that the Engine's list no longer shows has expired and is removed from the device. Photos from View can be kept on
the device too (a gallery's **Keep on this device**, or **Keep on device** on the photo being viewed): the app saves
the 2048 px preview, not the original, to save space, under the same storage limit and in the same Downloads list
(View opens it from the device). Kept photos are fetched in the foreground, one after another, reported with
`kind: "photo"` and a `photo_item_id`, and removed when the Engine's list no longer shows them (the photo was deleted
or this person can no longer see it, and the app then sends the Engine a delete so a hidden row stops counting); a refusal (409) because the device's list is full stops the keeping and says "This device has reached its offline limit."; the photo viewer and the timeline read the device's copy first, and the
timeline falls back to the kept photos when the server cannot be reached. Videos are not kept. Books and comics are
not downloaded by this app yet; `LocalBookFiles` in `Shared/OfflineModels.swift` is the unwired hook for a future
reader. The pure rules are in `Shared/OfflineLogic.swift`.

Photos and videos are opt-in. The connect screen has a separate toggle, **Allow access to my photos
and videos**, off by default; only when it is on does the app add the three `view.*` scopes to the
pairing request. The Engine's pairing screen still asks for approval. A `403` on any photo call shows
"Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it".

Apple TV and the CarPlay scene are unchanged. The shared `TuvimaCore` framework holds the API client,
wire models and the pure logic (routes, sort and filter queries, photo timeline merging, typed
profile-switch results) so it can be tested without a device.

## Tests

`Tests/TuvimaCoreTests` holds XCTest sources (route building, wire-fixture decoding against
`tests/fixtures/native-client-v1`, and a test that the Listen root loads and the queue follows a profile
switch), and `OfflineLogicTests.swift` (the offline rules above and the device-downloads and next-episodes fixtures) and
`PhotoOfflineLogicTests.swift` (kept photos: the storage limit, the purge rule, kinds and reports). No test target is declared in `project.yml` yet, so CI does not run them. To enable them, add
this target and list it under the scheme's `test.targets`, then run
`xcodebuild test -project TuvimaApple.xcodeproj -scheme Tuvima -destination 'platform=iOS Simulator,name=iPhone 16'`:

```yaml
  TuvimaCoreTests:
    type: bundle.unit-test
    platform: iOS
    sources: [Tests]
    dependencies:
      - target: TuvimaCore
```
