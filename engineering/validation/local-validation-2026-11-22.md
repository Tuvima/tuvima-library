# Local validation, 2026-11-22

Run by a local thread on the Windows desktop against latest main (20b99b712), at the coordinator's request, so Shy does not have to click through the batch by hand. Results per item are in `engineering/validation/batch.md` (rows marked 2026-11-22). New bugs are GitHub issues 1313, 1314 and 1315; issue 1191 was reopened.

## How it was run (and what it could not do)
- The fresh-start wipe (`Reset-TuvimaData.ps1`) was refused by the session's permission guard, so the existing data store was kept. Everything was read-only except one song dragged into the "test" playlist, which was removed again (the playlist is back to 0 songs). No playlists were created or deleted.
- The Engine and Dashboard were restarted on main, are running now (http://localhost:5016), and were left running for Shy.
- The Browser pane slows background timers, so page-load timing from inside the page was not reliable. Speed numbers come from the Engine's own request log instead.
- Widths: 1024, 1280, 1440 and phone (390) were used, but not every item at every width (see each row).

## Results in short
| Area | Result |
|---|---|
| Playlists: drag a song onto a hand-made playlist, open it, play it | Pass (1024, phone) |
| Playlists page side margin | Pass (1024, 1440, phone) |
| Find lyrics results (rows, Synced / Fits chips, six-line preview) | Pass at 1280 |
| "Not these lyrics? Find different lyrics" link | Pass |
| Phone player bar (cover 12 px from the left, 64 px bar) | Pass at 390 |
| Series page title clear of covers | Pass at 1280, 1440, phone; very tight at 1024 |
| Video folder tiles on phone | Pass |
| Home: own "Collections and series" row, no sideways scroll on phone | Pass |
| Player bar stays closed after Close and reload | Pass |
| Series counts (issue 1191) | Fail: pill says 11, tile and line say 15 |
| Playlist songs show their artist | Fail (issue 1315) |
| Home Vault notice on phone | Fail (issue 1314) |
| Windows-only test failures | 6 tests (issue 1313) |
| Page speed | Engine answers 0.3 to 0.5 s on average; details pages 0.27 to 0.30 s; slowest single Home answer 2.2 s while tests were running |

## Short follow-up list for Shy (needs a real device or your eye)
1. Real phone on the home network: open a film, a show and a Series and time hero plus Play (target under 1 second), play Ferrari and resume a part-watched film (issues 1181, 1183), and check the phone player after a long song title.
2. Roku, Android TV, Android phone and Apple devices: Find another on TV (rows L2 to L7 in the batch), including Back order and the lyrics view.
3. Subtitle checks that need a SubDL key: Find subtitles results (three real lines on the best match, "Fits the film").
4. The Series editor said "Editing is not ready" for The Expanse and Percy Jackson. Try it again when the Engine has been idle; if it still happens, tell me and I will file it with the Engine's reason.
5. Series page title at 1024 nearly touches the first cover. Your call: smaller title, or a visible gap?
6. Listen > Songs at 1024 cuts the Artist, Album and Date columns hard. Your call: is that acceptable, or should the table give them room?
7. Fresh-start check: run `Reset-And-Launch-Library.bat` yourself (the guard refused the wipe here) and let me re-run the batch on a clean library with real media ingested.
8. Not yet run, listed for the real-media session: Needs Review volume, Extras row on the real MasterClass show, Away from home (Tailscale, router door, invites), Notifications (real phones), Settings alignment on real hardware.

## (a) Features that need screenshots and updated user docs
The public Library docs already describe most of these in text; none of the pages carry screenshots today, so each item below is "add screenshots, then re-read the text against the build". Pages are in the tuvima-docs repository under `content/products/library/guides/` unless noted.
1. Playing music: Now playing (lyrics view, Queue, History tabs), player bar, phone player (`playback-and-reading.md`).
2. Find another for lyrics and subtitles: the new result rows, the preview, the "Fits the song / Fits the film" checks, "Not these lyrics?" link, on computer and phone (`playback-and-reading.md`, `troubleshooting.md`).
3. Playlists: the Playlists page of square tiles, a playlist's own page, dragging songs onto a playlist in the sidebar, drag to the Playlists heading to create one (`collections-and-lists.md`).
4. Series and collection pages: hero covers, In order grid, missing titles, "You own N of M", Show missing (`collections-and-lists.md`).
5. Home: Pick up where you left off, Recently Added, Collections and series row, one tile design (`dashboard-tour.md`).
6. Watch > Videos: video folders, Extras row on a show or film, handbook beside a lone video (`adding-media.md`, `naming-supporting-files.md`).
7. The universal editor: header navigation, Details / Artwork / Match / History, Review changes, supporting-files browser, People tab from Editor v9 (`editing-items.md`).
8. Needs Review: four groups, choices in the row, one row per show or album (`resolving-reviews.md`).
9. First-run setup and Settings: seven steps, Libraries table, Tools page, graphics card, one save rule (`first-run-setup.md`, `library-settings.md`).
10. Notifications and phone alerts: Mail server form, ntfy, Discord and others, privacy line per channel, daily summary (`phone-alerts.md`).
11. Away from home: Tailscale, router door, tunnel or Caddy, invite friends, outside sign-in rules (`guides/away-from-home/*`).
12. Plugins and Integrations pages (v6) and Developer tools (`using-plugins.md`, `developer-tools.md`).
13. Photos, galleries and documents in View, including dragging photos onto a Gallery (`view-personal-space.md`).
14. TV, phone and tablet apps: Find another on TV, Roku, Android TV, Apple TV, TV web (`product/native-clients.md`, `reference/ui-supported-devices.md`).
15. Vault and "Show Vault in the menu" (`explanation/vault.md`).

## (b) Topics for explainer videos for users
1. A 3-minute tour: add a folder, watch Library find and match your media, open Home.
2. Read, Watch, Listen and View: what each one is and how a book, its audiobook and its film sit together (formats of one title).
3. Series and Universes: how Library groups a Series, what "You own 10 of 11" means, how to show missing titles.
4. Fixing a wrong match: Needs Review, then the editor's Match tab; what Library does to your files in a Managed folder versus a Read-only folder.
5. Lyrics and subtitles: playing a song with synced lyrics, and "Not these lyrics?" to pick a better match; adding subtitles to a film.
6. Playlists made easy: drag songs onto a playlist, smart playlists, Favorites.
7. Collections and My List: building a collection by hand or by rule, saving things for later.
8. Playing on your phone and TV: pairing a device, resuming where you stopped, Find another on a TV.
9. Away from home: sharing your Library safely with your own phone and with friends (Tailscale, router, invite links).
10. Privacy and local-first: what stays on your computer, what optional providers send, the Vault.
11. Your photos and personal videos in View: galleries, people, places, documents.
12. Plugins and Integrations for the curious: what a plugin can and cannot do.

## Plain English summary
Most of what was recently built works on the PC: playlists accept songs by dragging, the lyric picker looks right, the phone player bar is where it should be, and Library answers quickly. A few things failed or need your look: the Series count still disagrees (reopened), songs in a playlist lose their artist, the Home notice on a phone cuts its button, and six tests fail only on Windows. Anything needing a real phone, TV or Mac, and a clean-library run, is on your follow-up list above.
