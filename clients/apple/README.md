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
| Downloads | A placeholder; the offline list is a later step. Starting a download from a title's details already works and uses the default quality from Settings. |
| Settings | Server address, profile switch (PIN prompt on a `428`), photo access state, default download quality and sign out (removes the device on the server). |

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
switch). No test target is declared in `project.yml` yet, so CI does not run them. To enable them, add
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
