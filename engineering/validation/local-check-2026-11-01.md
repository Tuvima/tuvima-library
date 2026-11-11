# Local check, 2026-11-01 (walk-through checklist)

Walked with Shy on SHY-DESKTOP, in the order the app is used. Source list: `batch.md` (rows are named in *italics* after an item so results can be copied back) plus the still-open bugs in `follow-ups.md`.
Mark each box: `[x]` works, `[F]` fails (write what you saw after it), `[-]` skipped or not applicable, `[?]` unsure. Anything that needs a phone, TV or other device is in the last section so it does not stall the walk.

## Set-up for this check (Claude does this before you start)

- Main is pulled (b53dac415 plus anything newer). Your existing data store, config, tools and secrets are left exactly where they are; the fresh instance uses its own temporary folders under `.tmp/localcheck/instance` (data store, config, data protection keys), so nothing of yours is changed or deleted.
- Your git stash "local config before 2026-10-28 local check" is untouched; `config/libraries.json` (your local edit) is backed up to `.tmp/localcheck/backup`.
- One Engine only (the slot in `.tmp/engine-slot.md`). Engine on http://localhost:61495, Dashboard on http://localhost:5016.
- Your provider keys are not copied into the fresh instance, so the first-run Metadata step starts empty (your old keys are still in `config/secrets`). Real media: `Z:\Development\testing\media\real_media` (audiobooks, books, movies, music, personal_photos, personal_videos, tv).

---

## 1. First-run setup
*rows: First-run setup v3 (welcome, profile, tools, libraries, Vault, metadata, all set, narrow, real folder); First-run setup L1 to L5; Setup and Settings polish L1/L2; Sitting 1 setup*
- [ ] The first screen is a Welcome page and the steps are numbered (seven steps). Nothing says "failed" for something that is only still installing.
- [ ] Your profile: create the administrator without a password. Setup carries straight on (no "who is using Library?" choice in the middle of setup).
- [ ] Tools: FFmpeg, Tesseract and Local AI each give one honest line and a next step ("waiting" or "Download", never a red "failed" while installing). Download FFmpeg from here.
- [ ] Libraries step: shows the same cards/list as Settings > Libraries. Tick Watch, Listen, Read and View in "What do you have?". Try "Skip for now" once, then come back.
- [ ] Add folder from the setup: the folder picker is its own wide pop-up, uses the plain app font, shows the drive once, has an address bar, Modified dates line up, and it opens quickly.
- [ ] Add the real folders from `Z:\Development\testing\media\real_media` (movies, tv, books, audiobooks, music; photos and personal videos under View). The "safety pause" may hold a folder that is mostly unmatched; that is expected.
- [ ] A folder of phone clips offers View, not Watch.
- [ ] The Vault step ("Turn on the Vault?") is clear and works either way (try No for now).
- [ ] Metadata step: enter the TMDB and TheTVDB keys again; the wording is clear.
- [ ] Switching areas inside setup (clicking Watch, Listen, Read, View) stays inside setup and never drops you into Settings.
- [ ] The last step ("All set") shows a clear summary. Open http://localhost:5016 in a browser that is not signed in: it shows "could not finish checking" with Try again and Sign in, not an endless "Checking...".
- [ ] Narrow window (about 390 px wide): the setup still reads and works.
- [ ] After setup: sign out and sign in again. With one passwordless user it goes straight in; with more, "Who's watching" shows (other profiles first, yours not repeated).
- [ ] Engine log after start: no error stack for Local AI, one line saying it is off.

## 2. Libraries and adding folders
*rows: Libraries redesign L1 to L9; Library setup L1 to L5, L13, L18 to L22, L26 to L29, L31; Library pages WP4 to WP13; Add library on a fresh start*
- [ ] Settings > Libraries: cards for Watch, Listen, Read and View with correct counts (no Vault while Vault mode is off).
- [ ] Click Watch, then a library: tabs for Folders, File handling, Activity and Lookup (no Advanced tab). Each folder row is clearly clickable.
- [ ] Add library: two screens, the content-type list has the right kinds. Add folder uses the same pop-up everywhere (no second dialog). Add a few named libraries ("Home videos", "Lectures") and check each is its own row.
- [ ] A YouTube-style folder: the YouTube switch says "this contacts YouTube"; with it off nothing is looked up.
- [ ] Rename or unplug a folder: the row and the group card turn amber with a warning; it clears when restored.
- [ ] File handling tab: change each card, Save changes, reload: it stuck. Turn on Rename: it shows the file count and asks to confirm.
- [ ] "Change what this folder is" (the "..." on a folder row) moves a folder from Movies to Videos with a clear preview.
- [ ] Drag a song from Explorer onto a Music folder list: a progress row appears and the file arrives (Edge or Chrome, with an Import folder set).
- [ ] The Activity tab shows only that library's events with a Needs Review link.
- [ ] Phone width: cards and counts show; nothing can be added.
- [ ] Use a copy of a few films in a Managed folder: they show in Watch at once and play from where they are, and are filed into place only after the batch runs.
- [ ] Settings > Libraries > Unidentified: files that cannot be matched stay put, are not in Watch/Listen/Home/collections, and each can be removed from the library.

## 3. Ingestion and Needs Review
*rows: Live ingestion page; TV shows grouped; Expanse grouping; Cibola Burn; Steph Curry MasterClass; Burnt (2015) - AVC; Needs Review volume; Events page; B17 tiles; Photo and video filing*
- [ ] Watch the Ingestion page during the run: columns line up for movie, show, album and book; at most 10 rows rotate; photos show a count, not file rows; no "batch" wording; no "Something wrong" screen.
- [ ] Library summary tiles and the Needs Review line appear within about a second, even while files are still arriving.
- [ ] TV episodes of one show land as one show with seasons (not one show per file).
- [ ] The Expanse: novels, novellas and TV show are three separate Series, numbered correctly, and shelf and item page agree.
- [ ] Cibola Burn opens with no "Something wrong" page.
- [ ] Stephen Curry MasterClass is one TV show with 17 lessons, matched on TheTVDB; the guidebook is an Extras card, not an episode.
- [ ] "Burnt (2015) - AVC" finds its movie match.
- [ ] Settings > System > Logs > Events: tabs line up with Sort by and Media type; episodes and songs group under their show or album.
- [ ] Engine log: no `database_integrity` or `search_index` health failures.
- [ ] Open Needs Review: write down, per item, the reason, title and match percentage (send back as a list). Check that Review opens the item's own page in the editor, and a book claimed by more than one Series offers "Choose Series".
- [ ] A photo/video import: dates filed correctly, duplicates caught, and files named from dates read from file names; a linked folder is left alone.

## 4. Home
*rows: Home page L1 to L9; Home bugs; Home hero and Listen shelves; Watch tiles without a landscape background; Looks at 1440 px*
- [ ] With something half-watched, half-read and half-listened: Home opens on the most recent of them, with its logo and the hero cover large (books and audiobooks too, audiobooks square).
- [ ] Hero shows only identified items with artwork. A show with 20-minute episodes says about "23 min", never "1361.4min". An album's length matches the player.
- [ ] Pick up where you left off: same tiles as the Continue rows on Watch, Read and Listen, one row, newest first, no albums.
- [ ] Recently Added: pills only for what exists; pills toggle on and off; photos and videos appear only behind the View pill.
- [ ] Home tiles are the same size as on the Read and Watch discover pages at 1440 px and a bit narrower.
- [ ] One hover: a tile with a background widens to landscape; a poster-only tile just highlights and its neighbours do not move. No lag.
- [ ] Open any details page: no flash of empty boxes before it draws.
- [ ] Banner settings and plugin slides on Home behave (Settings > Home).

## 5. Watch, Read and Listen pages
*rows: Library area pages WP1 to WP5; Music fixes L1 to L5; Music player L1 to L6; Songs list and timelines; Videos wave 1 rows; Language-aware wording*
- [ ] Watch, Read, Listen: Continue is one sideways row that scales past 15 items; View all is a small square centred in the row.
- [ ] Browse tabs match the libraries you have (Movies, TV Shows, YouTube, Lectures...), with no empty tabs and an Unmatched filter that works.
- [ ] Watch Videos tab: a numbered course is one series card; a loose clip is its own card; the series page lists them in order; play video 3, return: Resume and Up Next video 4. Pictures beside a video (My Talk.jpg, cover.jpg) and `.srt` files show with no internet.
- [ ] Listen > Artists: grid of round photos with name and disc/note counts; hover names a count ("52 songs"); clicking opens the artist. Audiobook Authors and Narrators and Read/Watch people look the same (round, name and counts below); Cast and Crew on film, show, book and album pages are round photos.
- [ ] Songs list at 1440 and 1280: real song lengths (no "0:03"), centred heart, "..." stays on screen, Delete lives inside "..." with its confirmation, no yellow rings on rows.
- [ ] Timelines (music, audiobooks, books, movies): covers only, hover for details, one artist's albums in a period grouped together.
- [ ] Album page: columns stay put when you tick "Show missing tracks"; track numbers and names have space between them; artist shows under each track.
- [ ] Person page: Sort (Newest, Oldest, A to Z) and Find in works; the page opens in about a second.
- [ ] Settings > General > Language and region: switch to English (United Kingdom): "Favourites"; back to United States: "Favorites"; no reload needed, no raw keys like `Common_Save`.
- [ ] Set German: pills, buttons and tabs are not clipped at phone and desktop widths.

## 6. Item pages
*rows: Looks at 1440 px; One collapse arrow; Movie Series row; Show missing; TV artwork fixes; Outside links; Book reader; Details page loading; Extras L1 to L6*
- [ ] A movie in a Series: slim collapsed bar when alone ("Series title - 1 movie owned"), full-width scrolling row with more than six, "Jump to" as wide as its longest name; every row uses the same arrow and keeps its open/closed choice.
- [ ] "Show missing" tick box on a Series row: nothing extra until ticked; then "Not in library" entries appear. Works on a fresh setup.
- [ ] Cast pictures are sharper and a bit larger; a long Overview opens on two short paragraphs with Show more.
- [ ] The Continental: episode stills, show cover and logo are right (English logo), cast shows, and re-matching pulls stills in. Shimmer and Shine: show cover, not a season's.
- [ ] Links open: TheTVDB, Wikidata, TMDB, MusicBrainz; Evanescence's Amy Lee and Ben Moody open pages inside Library.
- [ ] Extras card on a show, an album (booklet PDF) and a comic series (bonus PDF); cover art is not listed as an Extra; episode/song/issue counts unchanged. A PDF opens in the reader, pictures in a gallery, a clip in the player; Back returns. Finishing an Extra clip does not change next-up or watched count.
- [ ] A numbered episode never appears under Extras on three real shows with bonus folders.
- [ ] Book page: scrolling a real book does not jump to the top. Illustrated EPUBs show their pictures; the reader's top bar is findable and the book search works.
- [ ] Unmatched TV show: a Match button is offered.

## 7. Editor
*rows: Universal editor (bar, side panel, Details, Match, Artwork, Review, several rows, write-back, people, Series, collections, keyboard); Editor v2 rows; Details overlay rows*
- [ ] Desktop only: Edit in the "..." menu opens the editing bar (Details, Artwork, Match, Cancel, Save changes) and a right-hand panel. On a tablet or phone the entry says "Edit on a computer".
- [ ] Details: lean form; nothing is saved until Save changes, which shows Review changes first as a page in the window; a saved ribbon names the details that arrive.
- [ ] Match: search is pre-filled; for a TV episode you can move it to another show or season and the Save button enables; a show re-match updates; Review shows four switches; a match never moves files unless its switch is on.
- [ ] Artwork picker floats over the page; filters and language order work; Remove asks first with a preview.
- [ ] Move a song to another album: the entry is pre-selected with the reason, the dialog names the new file name and folder, taken tracks offer only "Add as another version" or "Pick another", As-is files stay put.
- [ ] Move a clip between Extras and an episode and back: card and counts update, nothing renamed on disk, works with Windows paths with spaces.
- [ ] Tick several rows (four songs, three episodes): "N selected" with Edit together; Mixed fields; Apply once; removing the last genre is refused.
- [ ] Switch items while editing (show to season to episode) from the bar's dropdown; Esc with a typed field asks "Save or discard?" (buttons on the right).
- [ ] A person, a Series, a curated collection and a Universe edit in the same way, in line, no drawer.
- [ ] Write-back: one control; Music on, Movies off behaves as set; edits reach files on schedule (Write now).
- [ ] History tab shows past saves; a title deleted in another tab while open offers Copy for your typed text.
- [ ] Keyboard only: Tab, Enter and Esc work through the whole editor; focus rings visible.
- [ ] Re-match a book or comic issue to the right title: it lands on the correct Series at its position.
- [ ] A Series override survives a re-scan; "Go back to the Library's choice" restores it.

## 8. Players
*rows: Video player rows; Music player; Bookmarks in videos; Bookmark packs; Book reader*
- [ ] Video bar at 1440 and 1280: play/pause first on the left, title centred while paused, icons the same size at rest as on hover, every right-hand control has a hover colour and its box closes when the mouse leaves.
- [ ] One-click bookmark; the B key adds one; private and shared bookmarks both work and show as markers on the seek bar with a quick name on hover.
- [ ] Subtitles & Audio menu; second audio track plays.
- [ ] Delete with a real mouse works in the player.
- [ ] Song players: Up Next (grip, title, time, "..." all visible, drag or Alt+Up/Down to reorder), History (no sideways scroll, "Played 3 min ago"), Clear looks like a button.
- [ ] Lyrics: X closes it (keyboard too); with no source it explains where lyrics come from and offers Look up lyrics now and Add a lyrics file; "Not right? Find another" works; a `.lrc` or `.txt` is written beside the song with write-back on, never overwriting your own file.
- [ ] Subtitles setting "Only when the audio is another language": English subtitles on for anime with Japanese audio, off for an English show.
- [ ] Companion (Home menu or "..."): one sentence says what it is; the Up to here / Everything switch matches Settings > Playback preferences > Spoilers.

## 9. Collections, lanes, imports, Trakt
*rows: Collections and lists WP1 to WP11; Trakt public lists; Trakt sign-in; Chronicle explorer*
- [ ] Collections page: Discovery, Automatic, Curated, Shelves, People tiles are the right shapes, nothing clipped at 1280 or on a phone; Universe pages show Series in order with a source for each connection and "still learning" when not yet enriched.
- [ ] New collection: four ways in, sentence rules with Exceptions and Advanced, preview beside the rules showing covers; the "+" in Watch/Read/Listen pre-ticks the media; Listen makes a playlist. Note whether the 350 ms preview delay feels right (spec says 1 second).
- [ ] Per-profile rules (Your activity: unwatched, in progress, My List, your rating) change when you switch profile.
- [ ] Display options: "At most 5" with Random keeps the same 5 on reload; Minimum items hides it; a seasonal window hides it with "Hidden until...".
- [ ] Starter packs: counts sensible; Decades and Genres right; Halloween and Festive windows right; Award winners only for awards you own.
- [ ] Lanes: "Show on Home and Watch" adds a lane to both; "Hide this lane" persists; Kids lane under its minimum disappears.
- [ ] Import real exports: IMDb, Letterboxd, Trakt, Goodreads shelves, StoryGraph. Counts believable; fix a "Couldn't match" by hand; following a list refreshes (drops removed titles, keeps hand-added). A pack file drops into Settings > Profile > Import and Set tiles export and remove with confirmation.
- [ ] Who can make and share: second adult sees "Only you can see this collection."; Kids profile has no New collection button; Add to collection lists only collections you may edit.
- [ ] Web lists: paste a real TMDb and an MDBList address: preview and lane appear; switch the source off in Plugins: the 4-hour message and link appear.
- [ ] Trakt: Client ID and secret entered as administrator (never shown again); public list previews with "You own X of Y"; official list and public watchlist read.
- [ ] Trakt sign-in per profile: consent box, Sign in on Trakt shows a code and trakt.tv/activate, approve on a phone, the page flips by itself; private and "users/me" lists import; another profile pasting the address is refused; sign out shows "Sign in again".
- [ ] Chronicle explorer: edit one field at a time and switch Universe; characters hidden by the Spoilers setting never show anywhere (cast, person page, search) while "Up to where I am" is on.

## 10. Profiles, households, Vault, language
*rows: Users, profiles and households; Library access L1 to L6; Profile pages L1 to L4; Settings and top navigation; Vault rows; Backup and restore L2/L3; Language-aware wording*
- [ ] Create the Shy / Mom / Kid 1 / Kid 2 example: household administrator, own sign-in, a profile with no sign-in. Change an email: same profile and progress.
- [ ] Households is an administrator-only page under Users and Sign-in. Household library on means on for every profile in it; Kid's profile follows household levels; unrated items for a Kid behave as the rule says.
- [ ] Profile pages: Overview figures and heatmap, Taste sentence and monthly chart, Kids profile page, desktop and phone, light and dark; the profile menu lists other profiles first.
- [ ] Top bar links (Read, Watch, Listen, View, Collections, Library logo, wordmark "LIBRARY") work from every Settings page.
- [ ] Vault (only with Vault mode on): no Vault word anywhere for non-administrators or Kids; one profile PIN; Move to Vault and Move out; Vault titles missing from search, history and activity until unlocked; the Vault folder must be its own folder.
- [ ] Sign-in and first-run follow the server language (try French), first-run default follows the browser.

## 11. Settings, plugins, integrations
*rows: Settings as a flat menu; Plugins v3; Integrations v3; Developer tools rows; Scheduled tasks; Local AI; Provider settings; Refresh every 90 days*
- [ ] Settings: one flat menu with page tabs, red for problems with the reason on the page, one Devices place, Escape closes side panels, nothing duplicated; same look at desktop, phone and TV widths.
- [ ] Plugins page at 1440 and 1280: tiles only (metadata providers and Commercial Skip each have their own tile), coloured counts, search and filters, Clear filters only when a filter is set; plugin detail pages show Access, Needs, Activity, update and remove; the old provider address is gone; Upload plugin works.
- [ ] A new plugin source joins without a restart; a saved Ingestion Flow is used by the next ingestion; plugin memory limit counts every program a plugin starts (try a plugin that launches a helper).
- [ ] Integrations page (not a drawer) and New integration wizard; webhook Send test shows a Failed row marked Test; health goes Needs attention after 3 failures and Broken after 5.
- [ ] System > Logs > Scheduled tasks lists the five background jobs with sensible last-run times.
- [ ] Local AI pages (Status, Model and hardware, Budget, Usage) open and read well; developer area rows are hidden on phone and tablet.
- [ ] StashBox plugin wording is plain; ListenBrainz links on a second profile and a played song appears there.

## 12. Deleting and the Recycle bin
*rows: Delete groups (all); Safer deleting; Remove from library on Unidentified*
- [ ] Delete from library (use copies): the dialog says what goes, what stays on disk and names any hand-picked collection; Cancel is focused; not offered on a phone or to non-administrators.
- [ ] Delete a season, a show, an album and a Series: dialogs name the group and counts; with the bin on, files move to the Recycle bin. (Seasons and Series with real records could not be reached in the cloud; use the real TV and a book Series here.)
- [ ] Recycle bin entry on a library page: Restore, Delete now and Empty (asks first). Put back, then Scan now: the film returns with its progress. Another profile sees Ask to keep; automatic purge and "leaving soon" read correctly. A binned film should not be counted under In progress (known, left).
- [ ] Delete a photo in View: it disappears; a missing file is found by search for you, stays private, can be put back, and an offline folder is handled; Empty Trash with a vanished file works.

## 13. Backup and restore
*rows: Backup and restore L1 to L5; Developer tools backup destinations; Vault backup*
- [ ] Back up now (System > Backup); add a second folder as a destination (no passphrase asked); the copy checks out. Choose "Keep restored progress waiting for" (30 to 365 days); it is kept after reload and hidden on a phone.
- [ ] Restore onto an empty data store: progress, bookmarks and your corrections come back; the Vault area returns empty with its settings until its folder is re-scanned; two-step sign-in still works.
- [ ] Export or import of people is no longer offered; "Export this household" is folded into the backup.

## 14. Remaining bugs and terms checks
*rows: TV/movie/book data bugs; Setup items F2/F9; terms checks*
- [ ] Windows-only: after a full run the 16 Windows-only tests (listed earlier) pass on this PC.
- [ ] Still open from your files: Shimmer and Shine loses cast after a re-match; a stray ComicVine ID on The Continental; a job that ends with no match but no Needs Review item; episode stills download one at a time; wrong-language logos.
- [ ] Wikipedia descriptions follow the metadata language (set French, refresh a famous film: description in French?). If English, it is a follow-up.
- [ ] Terms checks for Shy: StashDB (may it ship enabled), Trakt, MDBList and Open Library per-source terms.
- [ ] Docs site: Dashboard help links open the right docs page, in light and dark and on a phone; Tuvima Sans font.

## 15. Needs hardware or a long run (after the walk, not blocking it)
- [ ] Phone/tablet at real widths: Companion on an iPhone (Safari) and an Android phone, touch targets at least 44 px, Now playing card follows a TV playback (about 10 seconds), another profile never shows.
- [ ] Devices: iPhone book reader and Home; Android play past 10 minutes then seek, pause for 15 minutes, picture-in-picture, TV cast, QR pairing; Roku, TV web and Apple helper screens.
- [ ] Large uploads over the real home network; a View folder inside OneDrive (one upload per file per day); end the Engine mid-move (Task Manager) and confirm the file is in one place after restart.
- [ ] Long ingestion comparisons (one run per Engine turn, record-and-replay on real_media): same answer every run (comic series, shared people), universe placement, adaptation and character links, subtitle index tables, ten-minute idle indexing; 100,000-work speed numbers (benchmark seeder).
- [ ] People search and presence counts on the whole real library (with more than about 32,000 titles Search still loads).
- [ ] Real ListenBrainz account; real YouTube key; StashDB account key for the Vault lookup rows.

## Results

The walk stopped early, at Shy's request, inside first-run setup (sections 1 and 2 only partly walked: welcome, profile, tools, libraries step, vault step, metadata step and the folder picker). Ingestion of `real_media` started but was cut short, so sections 3 to 14 were not walked and every batch row stays Awaiting. The reason: the folder picker and file-reading speed on Z: (items 4 and 22) are a large enough problem to fix before a full walk is worth doing. Nothing was marked Validated. The fresh instance was stopped and the Engine slot released.

The fix list below is what Shy reported during the walk (nothing was fixed or changed).

### Fix list (failures and decisions)

1. First-run setup, first page (section 1): "Restore a backup" sits on a different row from the Continue button, and it has no visible border, so it does not read as a button.
2. First-run setup: the "server name" and "network name" fields are confusing for someone starting out; they need a plain explanation of what each is for.
3. First-run setup, Tools page: the status pills ("Ready", "Not installed") are not aligned and look different on every row. Shy's idea (image saved at `C:\Users\shaya\.claude\uploads\936092b2-95a5-47f9-9717-f06e5b49ba76\9499fb87-image.png`): one row layout with the tool name and version, a one-line description under each tool, the status pill in a fixed column, and the action (Install or a dash) in a fixed column at the right; a summary line on top ("5 tools detected", "4 ready", "1 needs setup"); Back, Recheck and Continue along the bottom.
4. Folder picker (setup, 10:54 local): very slow to load, Windows Explorer is nearly instant; the Z: drive does not load at all. Engine log lines "Server folder browse took N ms" are in `.tmp/localcheck/engine.log` (a first browse took about 1.5 s; at 10:55:04 four browse calls each took about 35 seconds, which fits the Z: drive; four calls at once suggests the picker asks several times). Same as the earlier folder-picker speed reports; not diagnosed yet, per Shy's instruction to hold fixes.
5. First-run setup, Libraries step: the page is long and confusing; it should be more consolidated. Folders that already exist cannot be edited from this step. Cause of the pre-filled folders: the repo's default `config/libraries.json` ships seven libraries (Books, Audiobooks, TV Shows, Music, Comics, Videos, Movies), each pointing into the Library data folder, so a fresh first run shows them already added (it is not Shy's local config). Decide whether a true first run should start empty, and make existing rows editable (rename, change folder, remove) in setup.
6. First-run setup, Vault step: the "Add a Vault library" button does not match the Libraries section (different look); the two need one uniform style.
7. First-run setup, Vault step: the Vault's folder picker is a different size and shape from the one in Libraries, and the library name field sits in a strange place. Likely cause (read from code, not yet confirmed on screen): there is one shared `ServerFolderPicker`, but it is embedded inline inside the Add library pop-up (`CreateLibraryDialog`), opens as a stand-alone dialog with the shared size in several pages, and `LibraryFoldersPanel` opens it with its own dialog size. Fix is one shared size and one shared place for the name field.
8. App-wide theme: secondary buttons ("Back", "Skip" and similar) have no border or other cue that they are buttons (same as item 1, "Restore a backup"). Question from Shy for the fix plan: add a clear primary / secondary / quiet button hierarchy (filled colour for the main action, outlined for secondary, text-only for the least important) in the shared button component, and apply it everywhere. Needs Shy's yes on the hierarchy before it is built.
9. First-run setup, Metadata step: question from Shy whether the "Connect" key pop-up is the same screen as on the plugin page, and a wish for uniformity. From the code: both open the same dialog (`ProviderOnboardingDialog`, Connect in setup, Replace on the plugin's page), so the key entry itself is shared. Differences to check on screen: the plugin page also has its own "Your key" card, "Replace key" and "Test connection" buttons with different wording, and the setup page shows its own badges ("Key saved", "Needs a key"). Compare the two and make wording and layout match.
10. First-run setup, Metadata step: in the Cards view only a snippet of each source's description shows; a tooltip (or similar) should give the full text. In the List view the descriptions are inconsistent in length (the Stash-box one is very long); give every source a short one-line description of similar length. The source should be named "StashDB", not "Stash-box" (related to the older StashBox wording bug on the Plugins page).
11. Settings > Plugins page: the metadata providers' icons (and others) are missing, although the same icons show on the setup Metadata step. Not diagnosed yet.
12. Plugin detail page: cluttered, confusing and busy. Shy asks for a Fable review of form, function and UI/UX before any rebuild (Fable use needs Shy's approval under the model-routing rule; this note counts as the request, to be confirmed when the fix list is planned).
13. A "Compare" feature: Shy cannot tell what purpose it serves. Screen not yet confirmed (I found no "Compare" label in the Dashboard's Settings or Setup screens by search, so it may be built from a resource string or a different page). Ask Shy which page it was on, then either explain it on the page or drop it.
14. Plugins, planning item (not a bug fix): group plugins by when they run, so it is clear which providers are needed to get items into the library. Shy's idea: a pre-processing group (the major metadata providers, used to identify and bring items in) and a post-processing group (lyrics, subtitles and similar, used after an item is in). Needs a short plan and Shy's decision on the category names before any change.
15. Adding movies or TV with no metadata provider set up: Library adds the files anyway, so it ends up adding random files. It needs a check when a folder of that kind is added, and a clear warning (and probably a hold on matching) until a provider such as TMDB or TheTVDB is connected. Applies to setup and to Settings > Libraries.
16. Plugins page tiles (include in the Fable review with item 12): too many pill boxes. Drop the "Working" pill and show health as one status dot at the top left of the tile (green, amber, red). The "..." menu is redundant next to the Configure button. Tiles should not say what a plugin targets; that detail belongs where the plugin is used (for example in ingestion). Product principle from Shy: plugins are "dumb" and are made available on the screens where they make sense (ingestion, lyrics and so on); the Plugins page is only for setting a plugin up, getting its access working and tested, and explaining in one standard way what it provides to the app. This changes how the Plugins page and the plugin detail page (item 12) should be designed, so plan them together with item 14.
17. Plugins that need Local AI should say so on their tile and detail page, and cannot be switched on until Local AI is working (the switch stays off with a plain reason and a link to Settings > Local AI).
18. Safety decision for Shy: Settings > Integrations has a "Tuvima native clients" integration that can be deleted, and official Tuvima plugins can be removed. Recommendation: the native-clients integration and any plugin the app itself relies on cannot be deleted (only switched off or have keys revoked, with a warning that paired apps stop working); other official plugins can be switched off but not removed, or removal asks for the administrator's confirmation and says what stops working. Needs Shy's yes (spec change, not a bug fix).
19. Follow-on to item 18: if an official plugin or the native-clients integration is deleted anyway, should Library restore it? Recommendation: do not restore silently (the person chose to delete it, and an automatic download is an outside request). Instead offer a visible "Restore official plugins" action on the Plugins page that re-adds what is missing from the approved list (the Engine already reads `approved-plugins.json` from GitHub), checks the signature, and keeps the old settings and keys where it can; the native-clients integration is re-created with fresh keys and the paired apps are told to pair again. Needs Shy's yes.
20. Settings > Library and Ingestion overview page: feels redundant next to the Libraries page. Shy asks for a Fable review of its UI and detail, aiming at one uniform look and one clear job for each page (decide what the overview keeps, such as ingestion state and Needs Review, and what moves to Libraries).
21. Settings as a whole (planning, covers items 5, 6, 7, 9, 12, 16 and 20): Shy wants a Fable review of every Settings category, one category at a time, covering layout, UI/UX, logic, redundancy and ease of use. Principles: make it simpler for ordinary users; anything advanced goes under one logically placed "Advanced" sub-menu; Developer tools is left out (it will be deprecated). Steps: (1) review each menu and write down its pain points and a proposed logical layout; (2) once Shy agrees the layout, a lower-cost model produces wireframes and diagrams so Shy can validate them; (3) only then build. There is already an untracked draft, `engineering/validation/settings-redesign-brief.md`, to start from. Needs Shy's approval to use Fable and a decision on whether this is its own planning thread.
22. Setup, "Identifying the media" step takes a long time even with no metadata plugin enabled; it should be near-instant when there is nothing to look up. Evidence from the Engine log (read only, no fix): the time is not the lookup. A 1.2 GB episode took 462 s in total, of which 419 s was fingerprinting (reading the whole file to hash it), about 3 MB per second, which points at reading from Z: (the same slow Z: behaviour as item 4). The lookup itself returned at once ("tmdb requires an API key but none is configured, skipping"). Ideas for the fix list: show "Reading file 3 of 38" progress instead of a silent wait, hash in the background after the item appears, and skip the retail lookup entirely (say so) when no provider is configured (ties to item 15).
23. Found in the same log: a TV episode from `real_media\tv\The Continental (2023)\Season 1` was ingested as a Movie ("Library folder prior override: assigned Movies at 0.95"). Likely because that folder was added to the Movies library (check which library the tv folder was added to; if it was TV, this is a real bug). Also all 38 TV files were "paused: did not match", which is the safety pause working, with no key.
   Answer from Shy: the TV folder was added to Movies by accident, so this is not an ingestion bug. It leads to item 24.
24. Folder picker: when it opens it should say plainly which kind of folder you are choosing (for example "Choose your Movies folder", in the title and on the confirm button), and if the chosen folder looks like another kind (a name or contents that say "TV", "Music" and so on, or season folders and episode-style file names in a Movies library) it should warn before adding ("This looks like a TV folder. Add it as TV instead?" with one click to switch). The existing safety pause on a new folder caught it only after files were read, so this warning should come first.
