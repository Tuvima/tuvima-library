# Google Play listing text, privacy answers and pictures (phone app and TV app)

> Spec: [native-apps](../specs/native-apps.md), WP12 (D5, D6, D15, D19) · Written 2026-11-23 · Companion to [the by-hand checklist](native-android-play-submission-2026-11.md) · Status: Text ready, not yet pasted into Play Console (needs Shy)

Paste-ready text for the two Google Play listings. The app is called **Library**. "Tuvima" appears only as the developer name (the company). I could not open Play Console from the build environment, so field limits and picture sizes below are the ones I know; the console shows its own limit next to each box and wins if it differs. Where a line says **Shy to supply** or **Shy to decide**, the answer is not in the repository and nothing here guesses it.

## Names and money

| Field | Phone and tablet app | TV app |
|---|---|---|
| App name (30 characters or fewer) | Library | Library (if Play refuses a name another app already holds, use "Library for TV" here and tell me) |
| Package name (cannot change once uploaded) | `com.tuvima.library` | `com.tuvima.library.tv` |
| Developer name | Tuvima | Tuvima |
| Price | $4.99, one time, phone and tablet together (spec D5, D15). No in-app purchases, no subscriptions, no ads. | Free (D5, D20). No in-app purchases, no subscriptions, no ads. |
| Licence | Open source, Mozilla Public License 2.0 (code at https://github.com/Tuvima/tuvima-library, folder `clients`) | same |
| Support page | https://github.com/Tuvima/tuvima-library/issues | same |
| Support email | **Shy to supply** (Play requires one and shows it; use a role address, not a personal one) | same |
| Privacy policy address | https://docs.tuvima.com/products/library/policies/privacy/ (open it once to confirm it loads before pasting) | same |
| Website | https://docs.tuvima.com/products/library/ | same |
| First version | 0.1.0 (versionCode 10000), see `clients/android/version.properties` | same |

Important about the price: Google Play does not let an app that has been published as free become paid later. The phone app must be set to paid before its first public release. A closed test does not charge your testers. Selling needs a Google payments profile on the developer account (the checklist covers it).

## Listing text, phone and tablet app

**Short description (80 characters or fewer):**
Your own movies, shows, music and books on your phone. Needs a Library server.

**Full description (4,000 characters or fewer):**

Library shows the movies, TV shows, music, audiobooks and photos you already own on your phone or tablet, organised the way the Library app on your computer organises them.

IMPORTANT: this app needs a Library server on your network. Library is free software that you run on your own computer or home server. It is not a streaming service, it does not come with any videos or music, and there is no Library account to create. If you do not have a Library server yet, this app has nothing to show. The server is free; find it at https://docs.tuvima.com/products/library/.

What you can do
- Pick up where you left off: Continue shows what you were watching or listening to, on any of your screens.
- Browse Watch, Read and Listen shelves, your collections, and each Universe (a world such as a film series and its books).
- Open an item to see its details, seasons and episodes, then play it.
- One player everywhere: previous, back 10 seconds, play, forward 30 seconds and next, with panels for episodes, audio and subtitles, bookmarks and playback settings.
- Listen to music with a queue, lyrics and a lock-screen card. Listen to audiobooks with chapters, speed, a sleep timer and bookmarks.
- Download movies, episodes, songs and audiobooks to play without a connection.
- Keep a My List of things you want to watch, read or hear.
- Switch between the people (profiles) in your household.
- Search by title.
- Picture-in-Picture while a video plays.
- Editing an item opens your Library Dashboard in the phone's browser.

How to connect
1. Type the address of your Library server (the one you use in your web browser).
2. The phone shows a short code.
3. An administrator opens Library on a computer and approves the phone. No password is typed on the phone.

Your media stays on your network. This app talks only to the server address you type. It has no ads and no tracking, and it sends nothing to Tuvima or to any other company.

One payment of $4.99 covers phone and tablet, with no subscription and no further payments. The code is open source under the Mozilla Public License 2.0.

(Add this line only when Android Auto ships, native-apps WP11: "In the car, Android Auto shows a short Listen list and a Now playing card; search is by voice.")

**What's new (first version):** First release. Connect to your Library server, pair with a code, browse, search, play and download.

**Category and tags.** Pick the closest to "personal media" in Play's own list (I believe "Video Players & Editors" or "Music & Audio"; **Shy to decide** when the form shows the list). Tags: media library, personal media, home media server, movies, TV shows, music, audiobooks.

## Listing text, TV app

**Short description (80 characters or fewer):**
Your own movies, shows and music on your TV. Needs a Library server.

**Full description:** the TV text in [native-tv-store-listing-2026-11.md](native-tv-store-listing-2026-11.md) (long description and "How to connect"), with the Samsung and LG and Roku-only lines removed. Add one sentence: "Works on Android TV and Google TV. Android-based Fire TV devices can install it by hand from the project's GitHub page; Google Cast is not offered on Fire TV." Nothing is promised for Fire TV sticks that run Amazon's Vega OS (spec D17).

**What's new (first version):** First release. Connect to your Library server, pair with a code, browse, search and play.

## Privacy and data safety answers

Play asks these in "Data safety" (and wants the privacy policy address). The wording is the same for both apps; the permissions differ (phone: internet, network state, foreground service for playback, notifications; TV: internet, network state).

| Question Play asks | Answer |
|---|---|
| Does the app collect or share any required user data types? | **Shy to decide, recommended: "No data collected" and "No data shared".** Reason: the app sends data only to the server address the person typed, which is the person's own Library server, never to Tuvima or to anyone else. Play's definition of "collect" is data sent off the device to the developer or a third party. If Play's review disagrees, the fallback is to declare the sign-in tokens and watch and listen progress (User IDs, App activity) as collected, not shared, optional, and processed ephemerally by the person's own server, with the same privacy policy. |
| Is data encrypted in transit? | Honest answer: not always. The app uses the address exactly as typed, and a server on a home network is often plain `http` (the apps allow it on purpose). If Play requires a yes or no, answer **No** unless the server address starts with `https`, and say so in the policy. **Shy to decide** (see the checklist's open items). |
| Can people ask for their data to be deleted? | Yes. Settings, Sign out revokes this device on the server and forgets the tokens on the phone. Uninstalling removes the rest, including downloads. The server holds everything else and belongs to the person. |
| Does the app need an account? | No account with Tuvima, Google or anyone else. The device is paired by an administrator approving a code in their own Library Dashboard. |
| Ads, analytics, crash reporting, tracking, third-party code that collects data? | None. The app includes only AndroidX, Jetpack Compose and Media3 (all Apache 2.0), which collect nothing and make no network calls of their own. |
| What is stored on the device? | The server address, the sign-in tokens for this device (kept in Android's secure storage), the chosen profile, playback choices (quality, skip seconds, autoplay) and any downloads the person chose. Backups are off (`allowBackup="false"`). |
| Location, camera, microphone, contacts, photos or files of the phone, advertising ID? | None. Photos shown in the app come from the person's server, not from the phone's gallery. |
| Permissions the app asks for and why (shown on the listing) | Internet and network state: to reach the server and notice a change of Wi-Fi. Foreground service (media playback): so music and audiobooks keep playing with the screen off. Notifications: the playback card and download progress. TV app: internet and network state only. |
| Is the app for children? Target audience | No. It is a general-purpose player; what it shows depends on the person's own library and profile settings. Choose an adult or general audience band (not "designed for children"). **Shy to confirm** the bands the form offers. |
| Government apps, financial features, health features, news? | No to all. |
| App access (Play asks reviewers how to get in) | A reviewer cannot reach a server on a private home network. This is the same open decision as the TV stores: see the checklist, "How will Google's reviewer test it". The text to paste once decided is under "Reviewer notes" below. |

## Reviewer notes (what Google's testers need to know)

> Library is a player for the user's own server. To review it, connect to the demo server at **[address: Shy to supply]**. Enter that address, choose Connect, and the phone shows a code. The code is approved at **[Dashboard address and test administrator login: Shy to supply]** under Settings, Devices. The app has no ads, no purchases beyond the one-time price, and sends data only to the server address typed.

## Content rating

The app has no content of its own; it plays what the person has on their own server. In Play's rating questionnaire (IARC) answer: no violence, no sexual content, no profanity, no controlled substances, no gambling, no user-to-user chat or sharing, no location sharing, no purchases inside the app, and add the note "content is supplied by the user's own server". Choose the category "utility, productivity, communication or other" if asked to pick one; the likely result is "Everyone" or "3+", but a reviewer may rate higher because the player can show whatever the person owns (**Shy to confirm** the result before accepting it).

## Pictures Play asks for

Sizes are the ones I know; the console shows the exact rule beside each upload.

| Picture | Size (pixels) | Phone app | TV app | Where it comes from |
|---|---|---|---|---|
| App icon | 512 x 512 PNG, up to 1 MB | Needed | Needed | **Not in the repository yet.** The phone app has no launcher icon at all (no `android:icon` in its manifest), so the phone app currently shows Android's plain default. A launcher icon and the 512 picture must be made from `assets/images/tuvima-icon.svg`; this is a separate small build step (see open questions). |
| Feature graphic | 1024 x 500 | Needed | Needed | To make (the logo on the near-black background used for the Roku splash). |
| Phone screenshots | at least 2, up to 8; 16:9 or 9:16, each side 320 to 3840 | Needed | n/a | See the shot list. |
| 7-inch and 10-inch tablet screenshots | same rules | Recommended (one listing covers phone and tablet, D15) | n/a | Emulator tablet profiles. |
| TV banner | 1280 x 720 | n/a | Needed | The app already has a launcher banner drawable (`tv_banner.xml`); the store banner is a separate picture. |
| TV screenshots | 1920 x 1080, at least 1 (Play asks for more for TV) | n/a | Needed | See the shot list. |

## Screenshots shot list

Use only the generated small test library (`MediaEngine.Api.dll --generate-baseline-samples <folder>`) and sample covers. Never a real library, a real server address or a real person's name. Capture on an Android emulator (Pixel profile for phone, a 10-inch tablet profile, an Android TV 1080p profile), in the app's own dark theme, with a server address that shows nowhere on screen.

Phone (portrait, take 6 to 8):
1. Home with Continue, the first shelves and My List.
2. A library shelf grid (Watch) with the dock visible.
3. A show's details page with seasons and episodes.
4. The video player with the transport row, and then the tool row open on the Audio & subtitles panel (two pictures).
5. The music Now playing screen with the timeline and the four tools.
6. The audiobook player with the chapters sheet open.
7. Downloads with a finished download.
8. The pairing screen with its code (a test server address hidden or generic).

Tablet (landscape, take 2 to 4): Home, a details page, the player.

TV (1920 x 1080, take 4): Home, a details page, the player with the tool row, an open panel on the right.

Each picture needs a one-line caption in the console in the app's words (Library, Series, Universe, Needs Review where they show); no caption says "Tuvima" except where the logo itself does.

## Closed test checklist (spec D19)

Google Play starts with a closed test with a few friends before the public release.

1. Upload the phone app's signed bundle to the **Closed testing** track, not Production. The TV app can run its own closed test after the phone app, or go straight to production since it is free; **Shy to decide**.
2. Make a tester list (by email, or a Google Group) and send each person the opt-in link Play gives. They install from Play, not from GitHub.
3. Important rule Google applies to some new accounts: a personal developer account created after November 2023 must have a closed test with at least 12 testers (the number was 20 before late 2024) opted in for 14 days in a row before it can apply for production access. An organisation account is exempt. "A few friends" may not be enough for a personal account. **Verify on the Play Console page "Testing requirements" before inviting friends**, and tell me the exact number it shows; that decides whether the test needs more people.
4. Each tester should pair, play a video, play a song, download one item and play it in airplane mode, then tell you anything off in words (the app collects nothing, so there is no crash report to read).
5. When the test passes (and the testing rule, if it applies, is met): apply for production access, set the countries and the price, send the release for review.
6. Fix anything found by raising `versionBuild` in `clients/android/version.properties` (the same versionName needs a higher versionCode each upload), tagging `android-v<versionName>-build<n>` and uploading the new bundle.
