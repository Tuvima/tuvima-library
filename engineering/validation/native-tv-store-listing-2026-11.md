# TV store listing text and privacy answers (Roku, Samsung, LG)

> Spec: [native-apps](../specs/native-apps.md), WP13 · Written 2026-11-23 · Companion to [the submission checklist](native-tv-store-submission-2026-11.md)

Paste-ready text for the three TV stores. The app is called **Library**. "Tuvima" appears only as the publisher (the company), where a store asks who publishes the app. TV apps are free (spec D5, D20) and open source under MPL 2.0 (D1).

Where a line says **Shy to supply**, the answer is not in the repository and nothing here guesses it.

## Names and money

| Field | Answer |
|---|---|
| App name | Library |
| Publisher / developer name | Tuvima |
| Price | Free. No in-app purchases, no subscriptions, no ads. |
| Licence | Open source, Mozilla Public License 2.0 (code at https://github.com/Tuvima/tuvima-library, folder `clients`) |
| Support page | https://github.com/Tuvima/tuvima-library/issues |
| Support email | **Shy to supply** (a store may insist on an email; use a role address, not a personal one) |
| Privacy policy address | https://docs.tuvima.com/products/library/policies/privacy/ (open it once to confirm it loads before pasting) |
| Website | https://docs.tuvima.com/products/library/ |
| Version for the first upload | 0.1.0 (Roku: major 0, minor 1, build 0) |

## Listing text (all three stores)

**One line (80 characters or fewer):**
Your own movies, shows and music on your TV. Needs a Library server.

**Short description (about 200 characters):**
Library puts the movies, shows, music and audiobooks you already own on your TV, with your place saved and your own pictures and details. It needs a Library server running on your network.

**Long description:**

Library shows the movies, TV shows, music and audiobooks you already own on your television, organised the way the Library app on your computer organises them.

IMPORTANT: this app needs a Library server on your network. Library is free software that you run on your own computer or home server. It is not a streaming service, it does not come with any videos or music, and there is no Library account to create. Install the server first (https://docs.tuvima.com/products/library/), then open this app.

What you can do
- Pick up where you left off: Continue shows what you were watching or listening to, on any of your screens.
- Browse Watch, Read and Listen shelves, your collections, and each Universe (a world such as a film series and its books).
- Open an item to see its details, seasons and episodes, then play it.
- A player with the same buttons everywhere: previous, back 10 seconds, play, forward 30 seconds and next, with panels for episodes, audio and subtitles, bookmarks and playback settings.
- Switch between the people (profiles) in your household.
- Search by title.

How to connect
1. Type the address of your Library server (the one you use in your web browser).
2. The TV shows a short code.
3. On your phone or computer, an administrator opens Library and approves the TV. No password is typed on the TV.

(Samsung and LG only: the packaged app runs from a non-web origin, so the server answers it only while an administrator has turned on "Allow Samsung and LG TV apps" in Settings > Network & Remote Access > Home network. It is off by default, applies right away with no restart, and needs no file edit. The listing should tell people to turn it on before pairing. Roku is not affected.)

Your media stays on your network. This app talks only to the server address you type. It has no ads and no tracking.

(Roku only, add: Photos open full screen from the Photos row, and Find subtitles and Find lyrics are in the player.)
(Books and comics can be browsed on the TV but not read there.)

**Notes for the person who tests the app:** see "Reviewer notes" below.

**What's new (first version):** First release. Connect to your Library server, pair with a code, browse, search and play.

**Keywords / tags:** media library, movies, TV shows, music, audiobooks, personal media, home media server

**Category.** Pick the closest to "personal media" or "video players and media servers" in each store's own list (I have not confirmed the exact names of any store's categories; **Shy to choose** when the form shows the list).

**Age rating.** The app has no content of its own. It plays what the person has on their own server. Answer the store's questionnaire as: no violence, no sexual content, no gambling, no user-to-user chat, no purchases, with the note "content is supplied by the user's own server". If a store forces one rating for a player that can show anything, ask the store which option it wants for personal media players; do not pick a low rating on a guess.

## Privacy answers (the same wording for every store)

| Question a store asks | Answer |
|---|---|
| Does the app collect personal data? | The app collects nothing and sends nothing to Tuvima or to any other company. It sends requests only to the server address the person typed in (their own Library server). |
| What is stored on the TV? | The server address, the sign-in tokens for this TV, the chosen profile, and playback choices (video quality, skip seconds, autoplay). Samsung and LG keep these in the app's own storage; Roku keeps them in the channel's registry. |
| Does the app need an account? | No account with Tuvima, Roku, Samsung, LG or anyone else. The TV is paired by an administrator approving a code in their own Library Dashboard. |
| Ads, analytics, crash reporting, tracking, third-party software that collects data? | None. The only third-party code is hls.js (Apache 2.0, bundled in the Samsung and LG packages, no network calls of its own), used to play video streams. |
| Location, camera, microphone, contacts, other device data? | None. Samsung package asks for the `internet` and `tv.inputdevice` (remote control keys) permissions only. LG package asks for none. Roku channel asks for none. |
| Is data shared with third parties? | No. |
| Is data encrypted in transit? | The app uses the address exactly as typed. With no scheme the Samsung and LG apps try `https` first, then `http`; the Roku channel asks for the full address starting `http://` or `https://`. A server on a home network is often plain `http`, so traffic on that network is encrypted only if the person's server uses `https`. Say this plainly if a store asks about unencrypted traffic. |
| Can people delete their data? | Yes. Settings, Sign out, revokes this TV on the server and forgets the tokens on the TV. Uninstalling the app removes the rest. |
| Is the app for children? | No. It is a general-purpose player; what it shows depends on the person's own library and the profile's own settings. |
| Where is the privacy policy? | https://docs.tuvima.com/products/library/policies/privacy/ |

## Reviewer notes (what a store's testers need to know)

A store tester cannot reach a server on a private home network. Without a server the app only shows the "type your server address" screen, which a store may judge as "not working". This is an open decision, not solved here: see the checklist's "How will the stores test it?" step and the open question in the report for WP13. Wording to use once a way exists:

> Library is a player for the user's own server. To review it, connect to the demo server at **[address: Shy to supply]**. Enter that address, choose Connect, and the TV shows a code. The code is approved at **[Dashboard address and test administrator login: Shy to supply]**. After approval the app opens to Home. Try: Continue, Watch, a show's episodes, Play, Pause, Back, Search. Without a server the app stays on the server address screen by design.

## Pictures each store asks for

| Store | Picture | Size (pixels) | Where it is |
|---|---|---|---|
| Roku | Channel poster, HD and FHD | 336 x 210 and 540 x 405 | In the package: `clients/roku/images/channel-poster-hd.png`, `channel-poster-fhd.png` |
| Roku | Splash, HD and FHD | 1280 x 720 and 1920 x 1080 | In the package: `splash-hd.png`, `splash-fhd.png` |
| Roku | Store screenshots | Roku states the size on its form; 1280 x 720 is the usual TV capture | **Shy to take** from a real Roku run |
| Samsung | Package icon | 117 x 117 | In the package: `clients/tv-web/platforms/tizen/icon.png` |
| Samsung | Large store icon | 512 x 423 | `clients/tv-web/platforms/tizen/store/store-icon-512x423.png` |
| Samsung | Screenshots | 1920 x 1080 | **Shy to take** from a real or emulated run |
| LG | Icon and large icon | 80 x 80 and 130 x 130 | In the package: `clients/tv-web/platforms/webos/icon.png`, `largeIcon.png` |
| LG | Splash | 1920 x 1080 | In the package: `clients/tv-web/platforms/webos/splash.png` |
| LG | Screenshots | 1920 x 1080 | **Shy to take** from a real or simulated run |

The store sizes come from the stores' own pages as they were known when this was written; each store can change them. The checklist's first step is to compare them with the live forms. The sizes the repository's test enforces are in `tools/Make-TvStoreImages.mjs` and `tests/build/tv-store-packages.test.mjs`; if a store changes a size, change it in both and run `node tools/Make-TvStoreImages.mjs`.
