# Tuvima Library for LG webOS (N7c)

Packaging for the shared TV web client in `clients/tv-web`. Nothing here is a second copy of the client.

## Package layout

`package.ps1` builds a `build/` folder (git-ignored output) holding:

- the shared client: `index.html`, `*.js`, `styles.css`, `assets/`, `vendor/`, copied from `../../`
- this folder's `appinfo.json`, `icon.png` (80x80), `largeIcon.png` (130x130) and `webos-keys.js`
- `index.html` gets one extra line, `<script src="webos-keys.js">`, before `api.js`

`appinfo.json` sets `disableBackHistoryAPI: true` because the shared client handles Back itself
(`nav.js` maps 461 to its back stack, and leaves through `webOS.platformBack()` or `window.close()` from the first screen).

`webos-keys.js` lists the webOS remote codes (Back 461, Play 415, Pause 19, Stop 413, Rewind 412, Fast-forward 417).
`nav.js` already turns them into actions, so no change to the shared client was needed. In desktop Chrome it does nothing.

## Before you install: turn on app access

The packaged app runs from a non-web origin, so the Dashboard rejects it until you set, in `config/network.json`,
`native_app_access.enabled` and `native_app_access.allow_browser_apps` to `true`. Do this FIRST, then restart the Dashboard.

## TV setup

1. On the LG TV install the **Developer Mode** app from the LG Content Store, sign in with an LG developer account,
   switch Dev Mode Status on and turn on **Key Server**. The TV restarts; note its IP and the passphrase.
2. Install the webOS TV CLI (`@webos-tv/ares-cli`, npm) on your computer. It is not installed by this repo.

## Commands

```
pwsh -File clients/tv-web/platforms/webos/package.ps1 -Device tv
```

It builds the folder and prints, without running them:

```
ares-package <build dir> -o dist
ares-setup-device                       # add device: host = TV IP, port 9922, user prisoner; name it tv
ares-novacom --device tv --getkey       # enter the passphrase from the Developer Mode app
ares-install --device tv dist/com.tuvima.library.tv_0.1.0_all.ipk
ares-launch --device tv com.tuvima.library.tv
ares-inspect --device tv --app com.tuvima.library.tv --open   # optional
```

## Owed

- Not built, installed or run on a TV: no webOS SDK or LG device was available.
- Sign-off on hardware (HEVC, captions, remote keys) and LG Content Store submission.
