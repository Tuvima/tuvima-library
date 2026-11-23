# Library for Samsung Tizen TV

Packaging for the shared TV web client (`clients/tv-web`) as a Tizen `.wgt`.
Nothing here is a copy of the client: `package.ps1` stages the shared files next
to `config.xml` in `build/` (ignored by git) and prints the Tizen commands.

## Package layout

`config.xml` uses `content src="index.html"`, so the package root is a folder that
holds, side by side: `config.xml`, `icon.png`, `tizen-keys.js`, and the shared
`index.html`, `api.js`, `universe.js`, `nav.js`, `find-another.js`, `player-logic.js`, `player.js`, `app.js`, `styles.css`, `assets/`
and `vendor/`. `package.ps1` builds that folder and adds one line to the staged
`index.html` (it loads `tizen-keys.js` first). The shared `index.html` is not
changed in git.

| File | Role |
| --- | --- |
| `config.xml` | Widget: id, version 0.1.0, name "Library", description, author, licence (MPL-2.0), `tv` profile, privileges `internet` and `tv.inputdevice`, `access origin="*"` |
| `tizen-keys.js` | Registers the media keys with `tizen.tvinputdevice.registerKey` and hands `nav.js` the Samsung codes for Back (Return, 10009), Rewind (412) and Fast-forward (417) through `TV_PLATFORM_KEYS`; registers nothing outside Tizen |
| `icon.png` | 117x117 icon rendered from `assets/tuvima-icon.svg` |
| `package.ps1` | Stages `build/` and prints the commands below (runs none of them) |

The app id is `TuvimaLib0.TuvimaLibrary` (package `TuvimaLib0`). Samsung requires
a package id of your own for a store release; change it before submitting.

## Before you build: allow packaged apps on the server

The packaged app runs from a non-web origin, so the Dashboard refuses it until you
turn this on. In `config/network.json` set **first**:

```json
"native_app_access": { "enabled": true, "allow_browser_apps": true }
```

(both switches; see `docs/reference/configuration.md` for what that allows).

## Turn on Developer Mode on the TV

1. On the TV open Apps, then press 1 2 3 4 5 on the remote.
2. Switch Developer mode On, and enter the IP address of the computer that runs Tizen Studio.
3. Restart the TV (hold the power button until it switches off, then on).

## Build, package, install, run

Needs Tizen Studio (the `tizen` and `sdb` tools on the path) and a certificate
profile made in Certificate Manager (a Samsung certificate for a TV).

```powershell
pwsh -NoProfile -File clients/tv-web/platforms/tizen/package.ps1 -Certificate <profile> -Tv <tv-name>
sdb connect <tv-ip>
sdb devices                      # note the TV name
```

then run the commands the script prints:

```text
tizen build-web -- clients/tv-web/platforms/tizen/build
tizen package -t wgt -s <profile> -- clients/tv-web/platforms/tizen/build/.buildResult
tizen install -n Library.wgt -t <tv-name> -- clients/tv-web/platforms/tizen/build/.buildResult
tizen run -p TuvimaLib0.TuvimaLibrary -t <tv-name>
```

Then enter the server address, pair the TV from the Dashboard, and test the remote
(arrows, Enter, Return, play, pause, stop, fast-forward, rewind).

## Samsung store submission (spec native-apps, WP13)

| What | Where |
| --- | --- |
| Package fields (name, description, author, licence, `tv` profile, privileges) | `config.xml` |
| Package icon, 117 x 117 | `icon.png` |
| Large store icon, 512 x 423 | `store/store-icon-512x423.png` (made by `node tools/Make-TvStoreImages.mjs`) |
| Listing text and the answers to Seller Office's privacy and content questions | [`engineering/validation/native-tv-store-listing-2026-11.md`](../../../../engineering/validation/native-tv-store-listing-2026-11.md) |
| What a person has to do by hand (accounts, certificate, upload) | [`engineering/validation/native-tv-store-submission-2026-11.md`](../../../../engineering/validation/native-tv-store-submission-2026-11.md) |

`tests/build/tv-store-packages.test.mjs` checks the fields above and the image sizes in CI. The `.wgt` itself is built and
signed with Samsung's own tools (see the commands above) because a store package must be signed with your Samsung certificate.

## Not done

- Build, signing, sideload and any run on a real TV (needs Tizen Studio and a TV).
- Samsung screenshots (1920 x 1080, taken from a real run) and HEVC and caption hardware sign-off.
- The Seller Office submission itself (see the checklist above).
