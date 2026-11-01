# Spec: Collections and lists (easy builder, imported lists, lanes on Home)

> **Status:** Built, awaiting your Local checks (2026-11-01; all packets merged, Local checks in engineering/validation/batch.md) · **Version:** 3 · **Last updated:** 2026-10-31 (locked by Shy) · **Related docs:** [Mockups](../../design/mockups/collections-and-lists.html) · [Product overview](../../product/overview.md) · [Plugin architecture](../plugin-architecture.md) · [Universes](../universe.md) · [Library area pages](../library-area-pages.md) · [Universal editor](../universal-editor.md) · Public presentation rules (tuvima-docs, `content/products/library/product/presentation-rules.md`, "Collections presentation")

## 0. Plain-English product walkthrough

**What a collection is in Library.** A collection is a named shelf of titles from your own library. Library already makes some for you (Universes such as Dune, Series such as the Dune novels, Automatic groups such as "Books by Author"), and an administrator can make **curated collections** by hand or with rules. Profiles make **playlists** in Listen the same way. Formats of one title (the ebook and the audiobook of Dune) are **variants** of that one title, never a collection.

**What changes with this spec.**

1. **One "New collection" flow with four ways in.** Pick titles by hand; set rules in plain sentences ("Movies, directed by Denis Villeneuve, released after 2010"); **start from a list** you paste or upload (an IMDb export, a Letterboxd list, a Goodreads export, a Trakt or MDBList list, a plain list of titles); or **start from a ready-made pack** (Decades, Genres, Award winners, Directors and Authors you own a lot of, Based on a book you own, Seasonal, Unwatched). Every way shows a live preview of what is in your library before you save.
2. **Rules that read like sentences, with power kept behind "Advanced".** Today's builder asks for rule groups, "Combine groups with", "Match ALL / ANY", Field, Condition and Value. The new builder starts with the media type chips, then one rule per line, joined by "and", plus an **Exceptions** line ("but never titles rated R"). Groups and any/all logic stay available under Advanced for the few who want them.
3. **New things you can filter on.** Watched, unwatched or in progress (per profile), play count and last played, your own rating, My List and Favourites, age rating and Kids levels, length (runtime or pages), release date relative to today ("in the last 2 years"), "is in" or "is not in" another collection, tags, and "random" ordering with a cap ("20 at random, fresh each day").
4. **Lists you import stay in step.** A collection made from a list keeps its source. You choose whether it refreshes (daily, weekly, never) and whether titles that leave the list leave the collection. Titles in the list that you don't own are counted and can be shown greyed out on the collection's own page, so you can see what is missing, but they never appear on Home or lanes.
5. **Collections become lanes.** Every curated collection, playlist and imported list has a **Show on** setting: Home, Watch, Read, Listen (and always the Collections page). An administrator orders the shared Home lanes once under Settings; each profile can hide a lane it does not want, and a profile's private collections appear only on that profile's Home. A collection can also be seasonal ("show from 1 October to 1 November").
6. **Privacy stays first.** Importing from a file never leaves the machine. Importing from a web address sends only that address and the list's own identifiers to that site, through the plugin architecture, with the same "what leaves this machine" label and approval every source has. Nothing about your library, profiles or viewing is sent anywhere.

**What stays the same.** Universes, Series and Automatic groups keep working as they do today; the editor's Details, Artwork and History tabs stay; playlists remain per profile; the Engine's permission rules decide who may edit; editing stays desktop-only; the collection page itself keeps its hero, tiles and "..." menu.

**Representative journeys.**

- *The film buff.* Shy pastes the address of a Letterboxd "Top 250 narrative features" export (or uploads the CSV). The preview says "You own 83 of 250". Shy ticks Show on Home and Watch, sets Refresh weekly, saves. A "Letterboxd Top 250" lane with 83 covers appears on Home for every adult profile; Kids profiles see only what their levels allow.
- *The parent.* A parent opens New collection → Set rules, taps Movies and TV, adds "Age rating is at most PG", "Length is under 100 minutes", exception "Genre is not Horror", names it "Saturday morning", ticks Show on Home. On the kids' profile it is the first lane.
- *The reader.* Shy picks the pack "Based on a book you own". It creates one collection listing every film and show whose source book is in the library. It lives on the Collections page and Read, and refreshes itself because it is rule-based.
- *The listener.* A profile makes a smart playlist "Unplayed this year, Jazz, 30 songs at random" and pins it to Listen. It is private to that profile.

**Scope boundaries.** No poster overlays (Kometa-style badges on artwork), no Radarr/Sonarr/Overseerr hand-off, no sharing collections to other households, no Plex or Jellyfin sync, no editing of Universes or Series membership here (that is the Universe spec and the editor's Match tool).

**Observable acceptance criteria** (numbered; each maps to a work packet in section 6).
- **A1.** From Collections, Watch, Read or Listen, any adult profile can open New collection (administrators also see Share with everyone; WP11) and choose Pick titles, Set rules, From a list or Start from a pack. (WP3)
- **A2.** Set rules shows media type chips, one plain-language rule per line, an Exceptions line, a live preview with a count and sample covers, and an Advanced switch that reveals today's rule groups. (WP3)
- **A3.** The new fields in section 4c work in rules and previews, and per-profile fields (watched, My List, your rating) evaluate for the profile looking. (WP1)
- **A4.** A collection can be capped ("show at most N"), ordered at random, hidden under a minimum count, and given a seasonal window; each is visible in the editor and honoured on every surface. (WP2)
- **A5.** Uploading a CSV or text file, or pasting a supported address, shows a preview with "You own X of Y", a list of titles that could not be matched, and refresh and sync options; saving creates the collection with its source recorded. (WP5, WP6)
- **A6.** Supported web sources appear on the Plugins page as sources with the data-leaving label and audience, and can be switched off; file import works with every source off. (WP6)
- **A7.** Show on Home/Watch/Read/Listen puts a lane of the collection's items (up to 20 plus View all) on that page; Settings → Home lanes orders the shared lanes; a profile can hide a lane; private collections appear only for their owner. (WP4)
- **A8.** Start from a pack lists the packs in section 4e, shows how many collections each would create from this library, and creates them as a set that can be removed in one go. (WP7)
- **A9.** The Collections page shows imported lists and packs with their source and last refresh; a collection page can show missing titles greyed (when allowed in Q3). (WP8)
- **A10.** Public docs have a "Collections and lists" guide; tests cover rules, imports, lanes and permissions. (WP9)

## 0b. Where Library stands against Kometa, Agregarr, Plex and Jellyfin

Read from Library's code and from the other tools' public documentation (Kometa's wiki and Agregarr's site could not be opened from this session, so their columns come from Agregarr's GitHub page, Kometa's defaults guide on GitHub and general knowledge; marked "unverified" where it matters).

| Capability | Library today | Kometa (Plex, YAML) | Agregarr (Plex, web app) | Plex built-in | Jellyfin built-in (+ plugins) | This spec |
|---|---|---|---|---|---|---|
| Hand-picked collections and playlists | Yes (desktop editor) | Yes | No (lists only) | Yes | Yes (Box sets, playlists) | Kept; "Add to collection" from item menus (WP8) |
| Rule-based collections | Yes, ~40 fields across all media, groups any/all, sort | Yes (Plex search filters, smart labels) | Filters on lists only | Smart collections per library | No (community plugins) | Sentence builder, Exceptions, Advanced (WP3) |
| Per-person fields (unwatched, your rating, My List) | No | No (server-wide) | No | Smart filters per account, not per viewer | Smart Playlist plugin, per user | Yes, evaluated for the viewer (WP1) |
| Cross-media collections (films + books + music) | Yes | No (one Plex library each) | No | No | No | Kept |
| Story and world fields (franchise, based on, characters, awards) | Yes (Wikidata) | Awards and franchises via TMDb/IMDb lists | No | No | No | Kept; packs reuse them (WP7) |
| Import a list from a file | No | No | No | No | No | IMDb, Letterboxd, Trakt, Goodreads, StoryGraph, CSV, text (WP5) |
| Import a list from the web | No | TMDb, IMDb (scraped), Trakt, Letterboxd (scraped), MDBList, MAL, AniList, Tautulli, more | Trakt, IMDb, TMDB, Letterboxd, MDBList, FlixPatrol, AniList, MAL, Tautulli, Overseerr | No | No (JFC and similar tools) | TMDb, MDBList, Open Library, MusicBrainz now; Trakt, Hardcover later; no scraping (WP6, D8) |
| Keep an imported list in sync | No | Yes (sync or append) | Yes (every 12 h by default) | No | No | Daily or weekly, follow or keep (D5) |
| Titles you don't own from a list | Series pages show missing books | "Missing" reports, Radarr/Sonarr | Preview, exclusions, Radarr/Sonarr/Overseerr | No | No | Counted; greyed on the collection page if Q3 allows; no requests (D13) |
| Show a collection as a Home row | Store exists, no control in the app; Home shows one row of collection tiles | visible_home / visible_library / visible_shared | Home and Recommended order, randomise, time restrictions (Plex Pass) | Pin collection as a hub | Home Screen Sections plugin | Show on Home / Watch / Read / Listen, ordering, per-profile hide (WP4) |
| Seasonal visibility | No | Seasonal defaults with schedules | Time restrictions | No | No | Optional window (D12) |
| Ready-made packs | 9 built-in "Automatic" browse views | ~60 default sets (awards, charts, genres, decades, studios…) | Presets per source | Auto TMDb collections | TMDb Box Sets plugin | Seven packs from your own data (WP7) |
| Caps, random, minimum items | Minimum stored but unused | limit, minimum_items, collection_order random | List position filter | Smart playlist limits | Plugin-dependent | All three (WP2) |
| Poster overlays and badges | No | Yes | Poster templates | No | No | Out of scope |
| Who can build | Administrators (desktop); profiles for playlists | Server owner editing YAML | Server owner in a web app | Server owner | Server admin | Unchanged; the point is that Shy's household never needs YAML |
| Privacy | Local; providers opt-in | Sends library contents to TMDb/Trakt as configured | Sends list requests; Tautulli stats local | Plex account | Local | File import local; web sources send only the address (D8) |

## 1. Goal

Make Library's collections as capable as the best Plex and Jellyfin add-ons (Kometa, Agregarr) while staying easier than any of them: one guided builder, plain-language rules, imported lists that keep themselves fresh, and collections that show up as lanes on Home and the area pages, all local-first.

## 2. Scope

**In scope**
- The New collection flow (four ways in), the plain-language rule builder with Advanced groups, and new rule fields.
- Collection options: cap, random order, minimum items, seasonal window, refresh and sync for lists.
- Importing lists from files (IMDb export, Letterboxd export, Trakt export, Goodreads and StoryGraph exports, generic CSV and plain text) and from web sources built as plugin sources (wave 1 candidates in D7).
- Lanes: Show on Home/Watch/Read/Listen, Settings → Home lanes ordering, per-profile hiding, private collections on the owner's Home only.
- Ready-made packs built from the person's own library and the metadata Library already holds (Wikidata awards, genres, people, "based on").
- Collections page tidy-ups needed to present lists, packs and missing titles; docs and tests.

**Out of scope**
- Poster overlays and artwork badges; download managers of any kind. Requests to Radarr, Sonarr or Seerr are not built here; only the missing-title action slot that a later Seerr integration fills (D13).
- Scraping sites whose terms forbid it (IMDb and Letterboxd pages; both are supported by their own export files instead, D8).
- Changing how Universes, Series or Automatic groups are formed, or the Universe explorer.
- A redesign of the whole Collections page navigation (Discovery / Automatic / Curated / Shelves / People). Only the additions in WP8; a fuller tidy is Q7.
- Sharing collections between households, publishing lists to any site, and sync with Plex or Jellyfin.
- Gallery collections in View (they keep their own gallery editor).

## 3. Decisions made

Numbered so packets can cite them. Shy confirmed D1 to D16 on 2026-10-31 (all recommendations, with the changes noted on D7, D13 and D14) and added D17. Only Q9 remains open.

- **D1 (Shy, 2026-10-31). One entry point, four ways in.** New collection always opens the same first step: Pick titles, Set rules, From a list, Start from a pack. The same step serves playlists in Listen (with Start from a pack limited to music packs). Reason: today "New Collection" starts in Manual membership and "Smart" is a switch inside Details, so people never find rules.
- **D2 (Shy, 2026-10-31). Rules read as sentences, joined by "and".** The default is one group: media type chips, then rules joined by "and", then an Exceptions list (each exception is a "never include when" rule). "Match any of these instead" is a single switch. Rule groups with their own any/all and "Combine groups with" stay exactly as today behind an Advanced switch, and a definition that already uses several groups opens in Advanced automatically. Reason: nearly every real collection is "all of these, except those"; groups are for the rare case.
- **D3 (Shy, 2026-10-31). Preview is always on.** The preview panel (count, count per media type, first 12 covers, Show all) sits beside the rules on desktop and below them on tablet, updating as rules change, with the same 1-second debounce the shared builder uses today. Saving is blocked while a hand-picked collection is empty (existing rule) and warns, but allows, when rules currently match nothing.
- **D4 (Shy, 2026-10-31). Per-profile fields evaluate for the viewer.** Watched, unwatched, in progress, play count, last played, your rating, My List and Favourites are evaluated for the profile looking at the collection, so one "Unwatched classics" lane shows each person their own unwatched films. Previews in the editor evaluate for the editing profile and say so. Reason: this is what Plex smart collections cannot do per user and what people expect from Netflix-style rows.
- **D5 (Shy, 2026-10-31). Imported lists keep their source and refresh on a schedule.** A list collection stores where it came from (file name or address), the raw entries (title, year, identifiers, list position), which entry matched which title, and the unmatched ones. Refresh: never (file imports default), daily or weekly (web sources default weekly). Sync mode: "Follow the list" (titles that leave the list leave the collection; default) or "Keep everything ever added". A refresh never touches hand-added items.
- **D6 (Shy, 2026-10-31). Matching uses identifiers first, titles second.** IMDb, TMDB, TVDB, ISBN, ASIN, MusicBrainz and ComicVine identifiers in the list are matched against the identifiers Library already stores; entries without identifiers match by title and year through the existing identity matching, at the existing confidence line, and anything below it is listed as "Couldn't match" inside the collection editor (not in Needs Review, which is for files). Reason: Needs Review is about the person's files; a list entry is not a file.
- **D7 (Shy, 2026-10-31, chose Q4 C). Wave 1 web sources: TMDb lists, MDBList, Open Library lists, MusicBrainz collections, and Trakt.** Trakt uses the plugin architecture's approve-on-another-device sign-in (plugin spec C13) for a person's own lists and watchlist, and plain public list addresses without sign-in; its current terms and limits are checked before WP6e is built (reserved for Shy). Original text: TMDb and MusicBrainz already have provider files; MDBList has a free key and carries IMDb and TMDB identifiers; Open Library lists are public and keyless. Trakt and Hardcover follow as their own small sources (plugin spec D17 already lists them). IMDb and Letterboxd are file-export only (D8). Each web source needs its own terms check before it is built (reserved for Shy).
- **D8 (Shy, 2026-10-31). No scraping.** Library reads a site only through a documented API or the person's own export file. Kometa and Agregarr scrape IMDb and Letterboxd pages; Library will not, to keep Reliability (pages change) and to respect site terms.
- **D9 (Shy, 2026-10-31). Lanes come from collections, through "Show on".** The existing placements store (location, position, limit, display mode, visible) is used as-is; the editor writes it for the first time. Locations: home, area:watch, area:read, area:listen. Lanes render with the shared shelf (up to 20 items, View all tile, same tile sizes) so they look like every other row.
- **D10 (Shy, 2026-10-31). Administrators order shared lanes; profiles hide.** Settings → Home → Lanes lists the shared lanes in order (drag to reorder, switch to hide for everyone). Each profile's Home shows shared lanes in that order minus the ones the profile hid (an "x" in the lane's "..." menu), followed by the profile's own private collections and playlists marked Show on Home, in the order the profile set. A lane with fewer items than its minimum (default 3, D11) is skipped silently.
- **D11 (Shy, 2026-10-31). Minimum items and caps.** Every collection has Minimum items (default 3 for lanes, 1 for the collection page), At most N (default none; "N at random" refreshes the sample daily), and Order (list order for imports; title, release, added, rating, your rating, last played, random for others; today's Then by stays).
- **D12 (Shy, 2026-10-31). Seasonal windows are optional and simple.** "Show only between [day month] and [day month]" per collection (Agregarr's time restrictions, Kometa's Seasonal). Outside the window the collection is hidden from lanes and from the Collections page, shown in the editor with a "Hidden until 1 October" note.
- **D13 (Shy, 2026-10-31, chose Q3 B plus a placeholder). Missing titles are shown but not acted on in this spec, with a named slot for "Send to Seerr" later.** Each greyed tile and the "Not in your library" section carry a **missing-title action slot**: an integration plugin can put one action there (the first planned one is "Send to Seerr", for a household's own Overseerr, Jellyseerr or Seerr server, administrators only, sending just that title's identifiers to the person's own server). This spec builds the slot and nothing in it; the Seerr integration is its own small follow-up spec with its own terms check. Original text: When Q3 allows, a list collection's own page can show "Not in your library (167)" with greyed tiles (title, year, artwork from the provider if already cached, otherwise a plain tile). They never appear on lanes, Home, search or counts, and never for Kids profiles. There is no "get it" button.
- **D14 (Shy, 2026-10-31, chose Q5 A as a starter set). Packs create sets, and packs are files that come through the shared Import.** The seven packs in 4e ship built in as the **starter packs**. A pack is also a small definition file (names, rules, windows, artwork hints; no user data), so Tuvima or anyone can publish more and a person imports one through the same Import function as a list or a bookmark pack (D17). Exporting a set as a pack file is included so a household can share its own. Original text: A pack creates one collection ("Based on a book you own") or a set of collections ("Decades" creates 1960s … 2020s, one per decade with at least Minimum items). A set is a parent collection of type Set; its children are ordinary rule collections the person can edit, and removing the set removes its children. Packs are built from data Library already has, never from a new download.
- **D15 (Shy, 2026-10-31). Desktop-only editing, same permissions.** `EditorDeviceRule.CanEdit` gates the builder as it gates the editor; administrators make library collections, any profile makes its own playlists and private collections (today's owner and audience rules). Lanes, imports and packs follow the same rule.
- **D17 (Shy, 2026-10-31). One Import function for every file that comes into Library.** The "From a list" step, "Import a pack" and the existing bookmark pack import (video player spec, "More…") use one shared importer: drop a file or paste an address, Library recognises what it is (collection list, pack file, bookmark pack, plugin settings file from plugin spec C15), shows a preview in that kind's own words, and hands it to the right place. It is reachable from the New collection flow, from the player's bookmark packs and from Settings → Overview → Import, and every kind added later (for example a Goodreads library import that also sets reading status) plugs into the same recogniser. Reason: one place to learn, one set of file rules, one privacy label ("Files stay on this machine").
- **D18 (Shy, 2026-10-31, chose Q10 A and 10b-a). One tile family where the shape says what opens.** Portrait for one title (unchanged); round for a person (the shared Artist tile the music thread proposes: photo, name, icon-plus-count line; the same tile on the Artists page, people pages and Home); square for an album or playlist (playlists share the square so they sit beside albums in Listen and keep today's 2×2 mosaic of album covers; chosen artwork replaces the mosaic); landscape with a fanned cover stack for every group of titles (curated collection, Series, Universe, imported list, pack set), with a small corner word naming the kind and custom artwork replacing the stack. Text always sits below the picture as on item tiles (today the collection and series cards put it inside and nothing below), one name line and one icon-plus-count line per media type, drawn by the same count component as the Artist tile. One hover for all (lift and glow, no popover). Replaces today's four group looks (collection card, ordered series card, people atmosphere tile, the Artists page grid). 
- **D19 (Shy, 2026-10-31, Q9). Everyone makes their own; administrators push to everyone.** Today the data model already holds both ideas (a collection owned by a profile with an audience of Only me or Selected profiles, and a Library-owned collection every profile sees), but the Dashboard shows the New collection button only to administrators, so a non-administrator can make playlists and nothing else. From this spec: every adult profile can create and fill its own collections and playlists (audience Only me) through the same New collection flow; administrators can in addition make Library collections for everyone ("push", today's Library owner) or share one with selected profiles. "Add to collection" in an item's "..." menu and on ticked rows lists exactly the collections the person may edit, so a non-administrator sees their own and an administrator sees everything they manage. Kids profiles keep My List and playlists and do not create collections. Editing stays desktop-only (D15).
- **D16 (Shy, 2026-10-31). Vocabulary.** "Collection" everywhere for curated, rule-based, imported and pack collections; "Playlist" in Listen; "List" only in the phrase "From a list" and in the source line ("From a Letterboxd list, refreshed weekly"). Never "smart" in the interface ("rules" instead).

## 4. UI behaviour

### 4a. Entry points

| Where | Who | What |
|---|---|---|
| Collections page header, "New collection" | Any adult profile (desktop); administrators also get the "Share with everyone" choice | Opens the New collection flow, kind Collection |
| Watch, Read, Listen rail: "+" next to Collections / Playlists | Any adult profile | Same flow, media type pre-ticked for that area |
| An item page's "..." menu: "Add to collection" | Any profile that may edit | Adds to an existing hand-picked collection or playlist, or "New collection with this title" |
| Ticked rows in lists ("Edit N items") | As above | "Add N to collection" |
| Phone or TV | Everyone | No builder; collections and lanes are shown, the "..." menu shows "Hide this lane" only |

### 4b. The New collection flow (one window, three steps, Back always available)

**Step 1. How should it fill up?** Four large cards, each with one sentence and an icon: **Pick titles** ("Search and add titles yourself"), **Set rules** ("Library keeps it up to date from rules you set"), **From a list** ("Paste a link or upload a file; keep it in sync"), **Start from a pack** ("Ready-made sets built from your library"). Media type chips (Movies, TV, Books, Audiobooks, Music, Comics, Videos, Everything) sit above the cards, pre-ticked from where the flow opened.

**Step 2. Build it** (differs per card).
- *Pick titles:* today's search-and-add, with a drag order list. Unchanged except the layout matches the other cards.
- *Set rules:* media chips → rule rows → Exceptions → Order and limits → Advanced switch. A rule row is three controls that read left to right: field (grouped menu with search: Media, People, Story and world, Recognition, Your activity, My library), condition (words, not symbols: is, is not, is any of, contains, is before, is after, is between, at least, at most, is known, is unknown), value (the existing value picker with search and chips). "Add rule" adds a row; the Exceptions section has the same rows under "but never when". Preview panel on the right.
- *From a list:* one field "Paste a link or drop a file", with a line under it naming what is accepted (see 4d). After the paste or drop: a source line ("Letterboxd export, 250 titles"), the preview ("You own 83 of 250", covers of the owned ones, "Couldn't match 4" with an expandable list showing title and year so the person can search and pick the right title or ignore it), Order (List order, Release date, Title), Refresh (Never, Daily, Weekly; shown only for web sources), and "When a title leaves the list" (Remove it, Keep it).
- *Start from a pack:* a grid of pack cards (4e), each with "Would create 7 collections from your library" and a Preview that expands to show the collections and counts. Ticking several packs is allowed.

**Step 3. Name and show it.** Name (pre-filled: "Movies directed by Denis Villeneuve", the list's title, the pack's name), Description (optional), Artwork (automatic stack by default; the existing picker), Who can see it (today's owner and audience controls), **Show on** (Home, Watch, Read, Listen; the Collections page is always on), Seasonal (off; "Only between … and …"), Minimum items. Save creates the collection, and a confirmation row glows on the Collections page (no toasts).

Editing an existing collection opens the same window on Step 2 with Step 3's settings in the Details tab, keeping today's tabs (Details, Artwork, Membership, Companions, History). "Membership" is renamed **Contents** and shows the way-in the collection uses (Hand-picked, Rules, From a list, Pack), with "Change how it fills up" that warns when a change would discard rules or the list source.

### 4c. Rule fields (full list after this spec)

| Category | Fields today | Added by this spec |
|---|---|---|
| Media | Media type, Genre, Year, Decade, Language, Original language, Country of origin | **Release date** (relative: in the last N months/years; absolute range), **Age rating** (with Kids levels mapping), **Length** (minutes, pages or tracks by media type), **Tags** (local tags and Description insights words), **Has variants** (for example "has both ebook and audiobook"), **Studio / Network** already covered under People & organisations |
| People & organisations | Person, Author, Narrator, Director, Cast, Voice actor, Artist, Composer, Screenwriter, Illustrator, Production company, Publisher, Record label, Network | unchanged |
| Story and world | Series, Franchise, Based on, Narrative location, Set in period, Main subject, Character, Universe, Is an adaptation, Source work owned, Adaptation owned | **In collection / Not in collection** (any other collection or playlist), **In a Universe / Standalone** |
| Recognition | Provider rating, Award won, Award nominated, Award family, Nomination family | **Your rating** (per profile), **Household average rating** |
| **Your activity (new)** | — | **Watched / Read / Listened** (completed), **Unwatched**, **In progress**, **Play count** (at least / at most), **Last played** (within / not within N days), **On My List**, **Favourite** |
| My library | Added within days | **Added** (absolute range), **Library** (which library folder), **File location** (folder path contains), **Quality tier** of the preferred variant (for example 4K) as a filter only, never a grouping |

### 4d. Accepted list formats

| Source | How | Needs | Notes |
|---|---|---|---|
| IMDb list or ratings export | CSV file | nothing | IMDb's own export has `Const` (IMDb id), Title, Year |
| Letterboxd export (watchlist, lists, diary) | CSV or ZIP file | nothing | Name, Year, Letterboxd URI; matched by title and year, then by TMDB id where Library has one |
| Trakt export, generic CSV | CSV file | nothing | Columns detected by header: title, year, imdb, tmdb, tvdb, isbn, asin, musicbrainz |
| Goodreads, StoryGraph export | CSV file | nothing | Title, Author, ISBN/ISBN13, shelves (each shelf can become its own collection, offered at import) |
| Plain text | TXT file or paste | nothing | One title per line, optional "(Year)" |
| TMDb list, TMDb collection | web address | existing TMDb key | `themoviedb.org/list/…` |
| MDBList list | web address | free MDBList key (new source) | carries IMDb and TMDB ids |
| Open Library list | web address | nothing | `openlibrary.org/people/…/lists/…` |
| MusicBrainz collection | web address | nothing (existing provider) | albums and recordings |
| Trakt list, Hardcover list | web address | own follow-up sources | not in this spec's waves |

### 4e. Packs (wave 1)

| Pack | Creates | Built from |
|---|---|---|
| Decades | one collection per decade with ≥ minimum items | release year |
| Genres | one per genre with ≥ minimum items (per area) | genre |
| Award winners | Oscars Best Picture, Emmy Outstanding Drama/Comedy, Hugo, Nebula, Booker, Pulitzer Fiction, Grammy Album of the Year, Eisner (only those with ≥ minimum items) | Wikidata awards already stored |
| Directors / Authors / Artists you collect | one per person with ≥ 3 titles | credits |
| Based on a book you own | one collection | Source work owned |
| Seasonal | Halloween (horror and "Halloween" subjects, window 1 Oct to 1 Nov), Festive (window 1 Dec to 2 Jan) | genre and main subject, D12 |
| Pick up again | Unwatched classics (unwatched, rating ≥ 7.5), Unread on the shelf (books unread, added over a year ago), Short and sweet (films under 100 min, books under 250 pages) | activity fields, D4 |

### 4f. Lanes

| Situation | What the person sees |
|---|---|
| Normal | A row titled with the collection's name and a small source line under it where there is one ("From a Letterboxd list · refreshed Tuesday"), up to 20 tiles and a View all tile that opens the collection page. Lanes sit after Pick up where you left off and Recently Added on Home; after the area's own rows on Watch, Read and Listen. The "..." on the row offers Hide this lane (profile) and, for editors, Edit collection and Show on |
| Empty (fewer than minimum items for this profile) | The lane is not shown |
| Loading | Lanes load with the rest of the page from the display composer; a lane that is not ready yet is skipped on this load, not shown as a skeleton |
| Error (Engine unreachable, not allowed) | The page's existing error state; no separate lane error |
| Partly ready | Imported list still matching: the lane shows what has matched so far; the collection page shows "Matching 120 of 250 titles…" |
| Kids profile | Only items the Kids levels allow; a lane whose remaining items fall under the minimum is skipped |
| Phone and TV | Same rows, same tiles; no editing controls |

### 4g. Collections page and collection page additions

- Curated section: each tile gets a one-line source ("Rules", "Hand-picked", "From a list", "Pack: Decades") and imported lists show the last refresh and "Couldn't match N" as a small amber badge that opens the editor.
- Collection page: hero as today; for a list collection, "You own 83 of 250" in the hero facts and, when Q3 allows, a "Not in your library" section at the end with greyed tiles.
- Sets (packs) appear as one tile that opens a page listing the child collections, with "Remove set" in its "..." menu.

### 4h. States for the builder itself

| Situation | What the person sees |
|---|---|
| Rules match nothing | Preview says "Nothing matches yet" with a hint naming the strictest rule; Save allowed with a warning line |
| List file not understood | "Couldn't read this file. Accepted: CSV from IMDb, Letterboxd, Trakt, Goodreads or StoryGraph, or a text file with one title per line" |
| Web source off or missing key | The address field says "TMDb lists are switched off. Turn the source on in Settings → Plugins" with a link; file import still works |
| Source down | Import saved with "Will try again" and the collection shows what matched; refresh follows the plugin failure rules (plugin spec D7) |
| Not allowed (not an administrator, phone, no fine pointer) | The New collection button is not shown; a deep link shows the existing "Editing needs a desktop" message |

## 5. Data / model changes

Plain language; pre-release, so no migration shims (a reset is acceptable).

- **Collection source.** Each collection records how it fills up: hand-picked, rules, list or pack; for a list, the address or file name, the source kind, refresh choice, sync mode, last refreshed time and counts (entries, matched, unmatched).
- **List entries.** For list collections, the raw entries are kept: position, title, year, every identifier found, the matched title (if any) and the status (matched, unmatched, ignored, hand-fixed). Refresh compares new entries against these.
- **Placements.** The existing placement store is reused; new locations for the three areas; a per-profile "hidden lanes" list and per-profile order for private collections.
- **Collection options.** Minimum items (exists, unused), at most N, random order with a daily seed, seasonal window (from and to as day-month), set parent (exists as parent collection; new type Set).
- **Rule fields.** New fields read existing data: play progress and history per profile, My List and favourites, user ratings, age ratings, runtime and page counts, tags, collection membership. No new provider data.
- **Sources.** MDBList and Open Library become provider source files with the plugin architecture's shared declaration block (audience, network list, data-leaving label); TMDb and MusicBrainz gain a "lists" capability line.
- **Backups.** Collections, their sources, entries and placements are already in the backup set as collection tables; new tables join the same class.

## 6. Work packets

Waves by dependency; at most two packets at once.

### WP1 — Rule fields and per-profile evaluation
- **What it does:** Adds the fields in 4c to the rule evaluator and the Dashboard rule catalog, with the viewer's profile passed into evaluation for activity fields; sentence-style condition labels.
- **Files likely touched:** `src/MediaEngine.Storage/CollectionRuleEvaluator.cs`, `src/MediaEngine.Web/Components/Rules/CollectionRuleCatalog.cs`, `src/MediaEngine.Web/Components/Rules/RuleOperators*.cs` (to be confirmed), `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs` (preview and item reads take a profile), `src/MediaEngine.Contracts/Collections/`, tests in `tests/MediaEngine.Storage.Tests/` and `tests/MediaEngine.Api.Tests/`
- **Depends on:** none · **Size:** Complex
- **Done when:** a rule "Unwatched and provider rating at least 7.5" returns different items for two profiles with different history in a test, and every new field appears in the builder's field menu with a working preview.

### WP2 — Collection options: cap, random, minimum, seasonal, order
- **What it does:** Honours minimum items, adds at most N and random order with a daily seed, seasonal windows, and the order list in D11, on every read path (collection page, lanes, Collections page).
- **Files likely touched:** `src/MediaEngine.Domain/Aggregates/Collection.cs`, `src/MediaEngine.Storage/Schema/schema.snapshot.sql` and the collection repository, `src/MediaEngine.Api/Services/ReadServices/Collection*ReadService.cs`, `src/MediaEngine.Api/Models/ManagedCollectionMapper.cs`, Contracts and fixtures
- **Depends on:** none · **Size:** Standard
- **Done when:** a collection with window 1 Oct to 1 Nov is absent from reads on 15 Nov and present on 15 Oct in a clock-controlled test; "20 at random" returns the same 20 within a day and a different set the next.

### WP3 — New collection flow and sentence rule builder
- **What it does:** Replaces the first step of the collection editor with the four-way start, builds the sentence rule layout with Exceptions and the Advanced switch over the existing `SharedRuleBuilder`, keeps the preview panel always visible, and renames Membership to Contents.
- **Files likely touched:** `src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor(.css)`, `CollectionRuleBuilder.razor`, `src/MediaEngine.Web/Components/Rules/SharedRuleBuilder.razor(.css)`, `src/MediaEngine.Web/Services/Editing/CollectionEditorLauncherService.cs`, `CollectionEditorModels.cs`, bUnit tests in `tests/MediaEngine.Web.Tests/`
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** the flow opens from Collections, Watch, Read and Listen with the right chips pre-ticked; a two-group definition opens in Advanced; a plain definition shows no group controls; screenshots at 1440 and tablet widths match the mockups' layout.

### WP4 — Lanes: Show on, Home lanes settings, profile hiding
- **What it does:** Writes placements from the editor's Show on; renders collection lanes on Home and the three areas through the display composer with the shared shelf; adds Settings → Home → Lanes ordering; per-profile hidden lanes and private collection order; lane "..." menu.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayComposerService.cs`, `DisplayHomeCollectionProjectionReader.cs`, `src/MediaEngine.Api/Security/AuthorizedDisplayProjectionReadService.cs`, `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs` (placements), `src/MediaEngine.Web/Components/MediaHub/`, `src/MediaEngine.Web/Components/Settings/` (Home settings page from the Home redesign WP7), Contracts and fixtures, tests
- **Depends on:** WP2 · **Size:** Complex
- **Done when:** a collection with Show on Home appears as a lane for an adult profile, is absent for a profile that hid it, and a private collection appears only for its owner, all in display composer tests; the "Collections & Lists" tile shelf is replaced by the lanes.

### WP5 — Shared Import, list import core (files) and refresh job
- **What it does:** The shared Import recogniser and dialog (D17: file kind detection for lists, pack files, bookmark packs and plugin settings, one preview frame, reachable from New collection, the player's bookmark packs and Settings → Overview), then Engine import of CSV and text files (formats in 4d), entry matching by identifiers then title and year, unmatched handling and hand-fixing, source and entries storage, the refresh job with sync modes, and the "You own X of Y" preview.
- **Files likely touched:** new `src/MediaEngine.Api/Services/Collections/ListImport/` (parsers, matcher, refresh job as a durable job row), `src/MediaEngine.Storage/` (new repositories and schema), `CollectionEndpoints.cs` (preview-list, import, refresh), Contracts, `src/MediaEngine.Web/Components/Collections/` (From a list step), tests with fixture CSVs from each site's export format
- **Depends on:** WP3 · **Size:** Complex
- **Done when:** dropping a bookmark pack, a pack file and a Goodreads CSV on the same Import dialog opens the right preview for each; fixture exports from IMDb, Letterboxd, Trakt, Goodreads and StoryGraph each import against a seeded test library with the expected matched and unmatched counts; a refresh with "Follow the list" removes a dropped entry and keeps a hand-added one.

### WP6 — Web list sources as plugin sources
- **What it does:** Adds list fetching for TMDb lists, MDBList, Open Library lists, MusicBrainz collections and Trakt as sources with the shared declaration block, audience, data-leaving label and failure rules; address recognition in the From a list step. Trakt (WP6e) adds the approve-on-device sign-in for a person's own lists and watchlist through the plugin architecture's per-profile connections. Each source is its own small packet (WP6a to WP6e) so a terms check can hold one back.
- **Files likely touched:** `config/providers/tmdb.json`, `musicbrainz.json`, new `mdblist.json`, `openlibrary.json`, `src/MediaEngine.Api/Services/Collections/ListImport/Sources/`, Plugins page catalog entries, docs
- **Depends on:** WP5 · **Size:** Standard each
- **Done when:** each source appears on the Plugins page with its label, pasting its address shows the preview, switching it off disables only that address kind; recorded responses make the tests offline.

### WP7 — Packs
- **What it does:** Builds the starter packs in 4e as rule collections (and sets), the Start from a pack step with counts, the pack file format with import through the shared Import and export of a set, and Remove set.
- **Files likely touched:** new `src/MediaEngine.Api/Models/CollectionPackCatalog.cs`, `CollectionEndpoints.cs` (packs preview and create), `src/MediaEngine.Web/Components/Collections/` (pack step), tests
- **Depends on:** WP1, WP2, WP3 · **Size:** Standard
- **Done when:** a set exported as a pack file imports again on a second test library and creates the same collections; on the seeded test library Decades creates only decades with ≥ minimum items, Award winners creates only awards present, and Remove set removes the children and their placements.

### WP8 — Collections page and collection page additions
- **What it does:** Source lines and badges on Curated tiles, set tiles and page, "You own X of Y" hero fact, "Not in your library" section with the empty missing-title action slot (D13), "Add to collection" in item "..." menus and bulk edit.
- **Files likely touched:** `src/MediaEngine.Web/Components/Collections/CollectionsPage.razor`, detail page collection modules, `src/MediaEngine.Web/Components/MediaEditor/` menus, Contracts
- **Depends on:** WP5, WP7 · **Size:** Standard
- **Done when:** the Curated tab shows each tile's source; an item's "..." adds it to a chosen hand-picked collection; a list collection page shows the owned count.

### WP10 — One tile family (D18)
- **What it does:** Replaces the collection card, ordered series card and people atmosphere tile with the group tile (landscape, stack, corner kind word, text below), adopts the shared round person tile and count line from the music thread's packet, and moves playlists to the square tile; updates the Collections page, Series shelves, Universe tiles, people pages and the editor's automatic-stack preview.
- **Files likely touched:** `src/MediaEngine.Web/Components/MediaTiles/MediaTile.razor(.css)`, `MediaGroupTile.razor(.css)`, `src/MediaEngine.Web/Services/MediaTiles/CollectionSurfaceTileComposer.cs`, `MediaPersonGroupTileComposer.cs`, `src/MediaEngine.Web/Components/Collections/CollectionsPage.razor`, `PeopleCatalogList.razor`, bUnit tests
- **Depends on:** PR 589 on main (music thread: `Components/Shared/MediaCountLine.razor`, the shared icon-plus-count line taking `MediaTileMediaCountViewModel` items, and `Components/MediaTiles/PersonTile.razor`, the round person tile built by `MediaPersonGroupTileComposer`; the Artists page already uses both). This packet builds the square album/playlist tile and the landscape group tile on `MediaCountLine`, and reuses `PersonTile` on people pages and Home, which the music thread has not touched; WP2 · **Size:** Complex
- **Done when:** the Collections page, a Watch Series shelf, a Universe tile and the Artists page each show the tile for their kind with the name and counts below, screenshots at 1440 px and phone width match the mockup's "Tile family" screen, and no page draws the old collection or series card.

### WP11 — Everyone makes their own, administrators push (D19)
- **What it does:** Opens the New collection flow to every adult profile for collections it owns (audience Only me), keeps Library ownership and Selected profiles for administrators, has the Engine enforce the same rule per request (today the create action is checked as if every caller had write rights), and fills "Add to collection" menus from the collections the caller may edit.
- **Files likely touched:** `src/MediaEngine.Api/Models/CollectionAccessPolicy.cs`, `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs` (create, update, items), `src/MediaEngine.Web/Components/Collections/CollectionsPage.razor`, `CollectionEditorShell.razor` (ownership step), `src/MediaEngine.Web/Services/Editing/CollectionEditorLauncherService.cs`, tests in `tests/MediaEngine.Api.Tests/`
- **Depends on:** WP3 · **Size:** Standard
- **Done when:** a non-administrator creates a private collection and cannot set Library ownership or Selected profiles (the Engine refuses it in a test); an administrator can; a Kids profile sees no New collection button; the "..." menu lists only editable collections for each.

### WP9 — Docs, tests, Local checks
- **What it does:** Public guide "Collections and lists" in tuvima-docs, updates to presentation rules (Collections presentation, Home lanes), plugin catalog page for the new sources, drift tests, and the Local checks appended to the batch list.
- **Files likely touched:** `tuvima-docs/content/products/library/guides/collections-and-lists.md`, `product/presentation-rules.md`, `reference/plugin-catalog.md`, `tuvima-library/engineering/validation/batch.md`
- **Depends on:** WP4, WP8 · **Size:** Mechanical
- **Done when:** the docs build passes and the drift tests pass.

Waves: 1 = WP1 + WP2 · 2 = WP3 + WP4 · 3 = WP5 + WP7 · 4 = WP6 (two at a time) + WP11 · 5 = WP8 + WP10 (WP10 also waits for the music thread's shared Artist tile) · 6 = WP9.

## 7. Local checks

- **L1.** On the real library, New collection → Set rules → Movies, "Director is Denis Villeneuve", "Released after 2010": the preview shows the right films within a second of the last change; Save; the collection lane appears on Home and Watch for an adult profile. Failure: preview lags over 3 seconds, wrong films, no lane.
- **L2.** Import a real Letterboxd export ZIP and an IMDb ratings CSV from SHY-DESKTOP downloads: the owned count is believable, "Couldn't match" lists only genuinely odd titles, and fixing one by hand works.
- **L3.** Import a Goodreads CSV with shelves: each chosen shelf becomes a collection; ISBN matches land on the right editions.
- **L4.** Paste a TMDb list address and an MDBList address (with a key): preview and lane appear; switch the source off in Plugins and confirm the address is refused with the message in 4h.
- **L5.** Kids profile: a lane with an exception "Age rating at most PG" shows nothing above PG; a lane that drops under the minimum disappears.
- **L6.** Phone (Android app and phone web) and TV web: lanes render with the shared tiles, no editing controls, Hide this lane works and persists.
- **L7.** Seasonal: set the Halloween pack's window to today's date range and confirm it appears; set it to next month and confirm it is hidden with the note in the editor.
- **L9.** Tile family: Collections page, a Watch Series shelf, a Universe tile, the Artists page and a playlist in Listen all read as the right shape with the name and counts below; nothing is clipped at 1280 px and on a phone.
- **L8.** Look and feel at 1440 px: the builder matches the mockups; the preview panel never pushes the rules below the fold.

## 8. Risks and open questions

**Risks**
- *Per-profile rule evaluation* (D4) adds a profile to every collection read; the display composer caches per profile already, but the collection page and lanes must not fall back to the shared cache for activity fields. Noticed by: two profiles seeing the same "Unwatched" lane. Mitigation: tests in WP1 and the 30-second per-profile cache that Home already uses.
- *List matching quality*: titles without identifiers (plain text, some Letterboxd rows) will sometimes match the wrong film. Mitigation: identifiers first, the existing confidence line, and the Couldn't match list with hand-fixing; never auto-match below the line.
- *Terms and keys*: MDBList needs a key per install; each web source needs a terms check (Shy). File import has no such risk.
- *Performance*: packs can create dozens of rule collections at once; each is a query-time collection. Mitigation: minimum items prunes most; Collections page reads come from the catalog read service's cache; measure on the real library in L1.
- *Scope creep into the Collections page redesign*: kept to Q7.

**Open questions**
- None. Shy answered Q1 to Q10 on 2026-10-31 (recorded in D7, D13, D14, D17, D18, D19). The spec waits only for Shy's "lock it".

## 9. Gaps and drift

Not applicable (new spec).

## Follow-ups outside this spec (recorded, not built here)
- Seerr integration plugin filling the missing-title action slot (D13): own spec, terms check, administrators only.
- Collections page navigation simplification (Q7 B): own small spec.
- Hardcover list source and Goodreads library import with reading status (plugin spec D17): own sources, through the shared Import.

## Plain English summary

Library already has a capable collection engine underneath (rules across films, books and music, Universes, Series, permissions), but the way in is hidden behind a developer-style rule screen, no collection can be put on Home or the area pages, and nothing can be imported from the lists people already keep on IMDb, Letterboxd, Goodreads or Trakt. This spec adds one guided "New collection" flow with four ways in (pick titles, set plain-language rules, import a list, start from a ready-made pack), new rule fields such as unwatched and your rating that work per person, imported lists that keep themselves fresh and show how many titles you own, and a "Show on" setting that turns any collection into a lane on Home, Watch, Read or Listen, ordered once by the administrator and hideable by each profile. It matches what Kometa and Agregarr do for Plex, without scraping sites or sending anything about your library anywhere, and it works across every media type Library holds, which neither of them does.
