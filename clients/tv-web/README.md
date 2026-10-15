# Tuvima for TV browsers (shared web client)

A plain JavaScript television client for the public API v1. It has its own
10-foot interface (it is not the Blazor Dashboard) and is the shared shell that
the Samsung Tizen (N7b) and LG webOS (N7c) packages wrap. There is no framework
and no build step: open `index.html` from any static host, or package the folder
as a Tizen or webOS app.

## What it does

- **Connect**: type the server address (the same address you use for the
  Dashboard). The client calls `GET /.well-known/tuvima` and needs API version 1.
  With no scheme it tries `https` and then `http`.
- **Pair**: starts the device-code flow (`POST /api/v1/oauth/device_authorization`),
  shows the code and the page to open, and polls `POST /api/v1/oauth/token`
  (`urn:ietf:params:oauth:grant-type:device_code`) at the server's interval,
  slowing down on `slow_down`. A person approves the TV in the Dashboard.
- **Home**: Continue first (a Continue card with a known file plays at once),
  then the Home shelves and Recently added.
- **Watch, Read, Listen**: the lane shelves from `/api/v1/display/browse`, with
  See all (`/api/v1/display/shelves/{key}`). **Read is browse-only**: books and
  comics open a details page but there is nothing to play.
- **Collections**: `/api/v1/collections`, one collection's items, and a Universe
  (`/api/v1/universe/{qid}`) with its shelves. Each entry is named by its
  `group_type` (show, series, collection, list, universe).
- **Search**: `/api/v1/display/search`.
- **Details**: `/api/v1/details/{type}/{id}`. A TV show lists its seasons and
  episodes; Play or Resume picks the episode in progress.
- **Player**: HLS through hls.js, captions from the manifest, resume from the
  manifest (and `/api/v1/player/state`), progress through
  `POST /api/v1/player/heartbeat` every 10 seconds and on pause, seek and close,
  and an Up next prompt from `/api/v1/details/show/{id}/next-episodes`.
- **Profiles**: `GET /api/v1/devices/current/profiles` and
  `POST /api/v1/devices/current/switch-profile`. A person without an avatar
  shows their initials. `428` opens an on-screen PIN pad; `403`, `409` and `429`
  show a plain message.
- **Settings**: video quality (caps the adaptive ladder), HEVC on or off,
  profile, server, and Sign out, which revokes the device
  (`DELETE /api/v1/devices/{deviceId}`) and forgets the tokens.

## Tokens and refresh

Tokens are kept in `localStorage` only (`tuvima.tv.tokens`). The access token
lasts 10 minutes. The client refreshes it before it expires and also after a
`401`, with one refresh at a time (refresh tokens rotate, so two uses of the same
token would look like a replay and revoke the device). A refused refresh
(`400`, `401` or `403`) clears the tokens and returns to the pairing screen.
Switching profile replaces the token pair with the one the server returns.

The client id is `tuvima-tv`, one of the ids the built-in native-client
Application accepts on a new data store.

## Playback model

A browser `<video>` element cannot send the app's bearer token, and the
Dashboard's app door needs it on every stream request. So the client plays all
video through hls.js, whose request hook adds `Authorization: Bearer ...`, and
it declares adaptive HLS only: `containers: []`, `video_codecs: ["h264"]`
(plus `"hevc"` when the setting is on), `audio_codecs: ["aac"]`,
`subtitle_formats: ["vtt"]`. The manifest is requested with `client=tv-web`
(or `tv-web-hevc`), which maps to the Engine's `tv-web` playback profile.
Artwork and subtitles are fetched with the token and shown from blob URLs.

If hls.js is not supported the player falls back to the platform's native HLS,
which cannot send the token and so only works where the server allows it.

A title reached from search or a collection has no file id on API v1 (the
details route works on works). The client finds the file through a browse search
for the title and otherwise says it cannot play it from there; starting it from
Home or Watch always works.

## hls.js

`vendor/hls.min.js` is an unmodified copy of hls.js 1.7.3 (the same file the
Dashboard serves from `src/MediaEngine.Web/wwwroot/vendor/hls/`), with its
license in `vendor/LICENSE-hls.js.txt`. It loads on first playback.

## Cross-origin use (packaged apps)

Served from the Dashboard origin, the client needs nothing special. A packaged
Tizen or webOS app runs from `file://`, `app://`, `tizen://` or `webos://` (or the
`null` origin), so the Dashboard answers cross-origin requests for exactly that
case: `/api/v1/**` and `/.well-known/tuvima`, only while `native_app_access` is
on, GET, POST, PUT, DELETE and the preflight, the `Authorization` and
`Content-Type` headers, no credentials, and only for routes the app door allows.
See `docs/reference/configuration.md`.

## Remote control and layout

Arrow keys move focus to the nearest control in that direction, Enter selects,
and Back (Escape, Backspace, Tizen `10009`, webOS `461`) goes back. Media keys
(play, pause, stop, fast-forward, rewind) work in the player. The root text size
is 24 px at 1080p (it scales with the screen height), every control is at least
56 px, content keeps 5 percent safe margins, and the focused control has a white
ring.

## Files

| File | Role |
| --- | --- |
| `index.html` | Page shell; loads the classic scripts in order |
| `api.js` | Server address, pairing, token rotation, display, details, playback calls, artwork |
| `nav.js` | Key map and spatial navigation |
| `player.js` | hls.js player, captions, resume, heartbeat, Up next |
| `app.js` | Screens and routing |
| `styles.css` | 10-foot styles |
| `vendor/` | hls.js and its license |
| `assets/` | Tuvima icon and logo |

## Checks

```powershell
pwsh -NoProfile -File tests/native/Test-TvWebClient.ps1
```

The gate runs `node --check` on every script, refuses hard-coded server
addresses and credentials, checks the required screens and routes, checks the
wire names against `tests/fixtures/native-client-v1`, and runs the Node
behaviour checks for the API layer and remote input.

## Not yet done

- Tizen and webOS packaging, store metadata and device sign-off (N7b, N7c). A
  packaged app should also add a content security policy that suits its runtime.
- Photos (View) are not part of this client.
- Search uses the TV's own on-screen keyboard through a text box.
