---
title: "Universal Parameterized Collection System"
description: "Developer reference for structural shelves, collection predicates, ownership, membership resolution, and presentation boundaries."
audience: "developer"
category: "architecture"
product_area: "concepts"
tags:
  - "collections"
  - "playlists"
  - "collections"
  - "rules"
  - "content-groups"
status: current
---

# Universal Parameterized Collection System

## In this page

Understand how structural shelves and rule-driven Collections resolve owned media. The shared catalog machinery supports distinct product experiences; profile bookmarks, reactions, and playback progress remain independent state.

## Where this lives in the code

- `src/MediaEngine.Storage/CollectionRepository.cs`
- `src/MediaEngine.Domain/Contracts/ICollectionRepository.cs`
- `src/MediaEngine.Storage/ProfileStateRepository.cs`
- `src/MediaEngine.Api/Endpoints`

## Overview

Tuvima Library uses collection records for structural grouping and for custom containers with explicit or rule-driven membership. Type, ownership, audience, and resolution determine how a container behaves. A shared internal record does not make an album, TV show, personal bookmark, and curated Collection interchangeable product concepts.

A collection is defined by membership or **rules**, its **type**, ownership/audience, and supported **placements**. My List bookmarks, Favorites reactions, and Continue progress use independent profile state. They must never be represented by hidden Collections or Playlists.

Product role note: lane-level shelves and top-level Collections are intentionally different surfaces. A generated book series, movie series, TV show, music album, or audiobook series is a **shelf** and belongs in its lane: books/comics in Read, movies/TV in Watch, and music/audiobooks in Listen. The Collections page shows broader rollups only when a shared series/franchise/universe relationship connects at least two shelves. A single shelf, even with multiple items, stays out of the Collections surface; ebook/audiobook variants of the same work also do not trigger a rollup.

## Shelf Identity, Ordering, And Counts

Lane shelves are immediate containers. A comic shelf is a Comic Vine volume/run,
a music shelf is the accepted album/release, TV uses show/season placement,
movies use true film collections when available, and books/audiobooks use an
ordered series container when supported by provider or Wikidata evidence.

Broader franchises, universes, publisher lists, production lists, and Wikimedia
list articles may support diagnostics or top-level discovery rollups, but they
must not be used as the lane shelf itself. This is what prevents two same-name
comic shelves with different totals, movie franchise totals replacing trilogy
counts, or list articles appearing as real series.

Counts are stored as facts about the accepted container. `sequence_total` records
the expected count, and `sequence_total_scope` states what the count means:
main sequence, extras included, standalone, collected edition, or broader
franchise. The UI should show `Owned X of Y` only for an authoritative
finite/known `Y` from the same immediate container shown to the user. Partial
manifest row counts degrade to `X owned`. Comics never present the provider's
current run count as a completion target; their UI shows issue identity and the
number of owned issues while retaining the count internally for matching and
diagnostics.

Wikidata manifest rows also carry `membership_scope`. Incoming P179 and direct
P527 works are `MainSequence`; incoming P361 works are `Supplementary`; children
reached by expanding a P527 collection are `CollectedContent`; explicitly
expanded P8345 franchise members are `BroaderContext`; ambiguous direct members
without ordinal or chain evidence beside a positioned run are `Unpositioned`. All factual rows remain
available, but lane totals use only the main sequence. Detail pages can switch
to Short Fiction & Extras or Collected Content without inserting those works
into the numbered main run.

Domain note: `Collection`, `Work`, and `Edition` expose aggregate children and
metadata property bags as read-only views. Code that changes membership or
metadata history should use explicit domain methods such as `AddWork`,
`AddRelationship`, `AddEdition`, `AddMetadataClaim`, and `AddCanonicalValue`.
Metadata claims remain append-only.

---

## Collection Rules - The Universal Predicate Model

Every collection stores its membership criteria as a JSON array of predicates. Each predicate is a simple object:

```json
{ "field": "genre", "op": "eq", "value": "Science Fiction" }
```

A collection's full rule set might look like:

```json
[
  { "field": "media_type", "op": "eq", "value": "Movie" },
  { "field": "genre", "op": "eq", "value": "Horror" },
  { "field": "vibe", "op": "in", "value": "atmospheric,tense,unsettling" }
]
```

### Supported Operators

| Operator | Meaning | Example |
|----------|---------|---------|
| `eq` | Equals | `genre eq "Horror"` |
| `neq` | Not equals | `media_type neq "TV"` |
| `contains` | Text contains substring | `title contains "Dune"` |
| `gt` | Greater than | `user_rating gt 3` |
| `lt` | Less than | `duration lt 480` |
| `between` | Range (inclusive) | `year between "1980,1989"` |
| `in` | Any of (comma-separated) | `vibe in "atmospheric,cerebral"` |
| `like` | Pattern match | `author like "Herbert%"` |

### Match Mode

Each collection has a `match_mode` that controls how predicates combine:

- **ALL** (default) - item must satisfy every predicate (AND logic)
- **ANY** - item must satisfy at least one predicate (OR logic)

### Field Vocabulary

Rules reference fields from six categories:

#### Identity fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `title` | Work title | `title contains "Dune"` |
| `author` | Creator / author | `author eq "Frank Herbert"` |
| `media_type` | Media category | `media_type eq "Book"` |
| `genre` | Genre tag | `genre eq "Science Fiction"` |
| `series` | Series name | `series eq "Dune Novels"` |
| `universe` | Universe name | `universe eq "Dune"` |
| `format` | File format label | `format eq "EPUB"` |
| `publisher` | Publisher name | `publisher contains "Penguin"` |
| `language` | Content language | `language eq "en"` |
| `narrator` | Audiobook narrator | `narrator eq "Steven Pacey"` |

#### Wikidata fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `wikidata_qid` | Wikidata Q-identifier | `wikidata_qid eq "Q190228"` |
| `director` | Director (from Wikidata P57) | `director eq "Denis Villeneuve"` |
| `composer` | Composer (from Wikidata P86) | `composer eq "Hans Zimmer"` |

#### Engagement fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `user_rating` | User star rating (1-5) | `user_rating gt 3` |
| `provider_rating` | Retail provider rating (0-5) | `provider_rating gt 4` |
| `play_count` | Times played/read | `play_count gt 5` |
| `completion_status` | Not Started / In Progress / Completed | `completion_status eq "Not Started"` |
| Profile state | Use canonical saved-item/reaction filters where supported | Do not infer Favorites from a named System collection |

#### Temporal fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `date_added` | When added to library | `date_added gt "2026-03-01"` |
| `date_published` | Publication/release year | `date_published between "1980,1989"` |
| `last_played` | Last consumption date | `last_played lt "2025-10-01"` |
| `date_completed` | When marked complete | `date_completed gt "2026-01-01"` |

#### File fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `file_size` | Size in bytes | `file_size lt 524288000` |
| `duration` | Length in minutes | `duration between "120,360"` |
| `quality` | Quality label (4K, 1080p, 320kbps) | `quality eq "4K HDR"` |
| `bitrate` | Audio bitrate in kbps | `bitrate gt 256` |

#### AI-derived fields

| Field | Description | Example use |
|-------|-------------|-------------|
| `vibe` | AI-generated vibe tag | `vibe in "atmospheric,cerebral"` |
| `tldr` | AI summary text | `tldr contains "space exploration"` |
| `taste_match` | AI taste match percentage | `taste_match gt 80` |

---

## Collection Types - Presentation Hints

The collection type is a **presentation hint** - it tells the Dashboard how to render the collection, who owns it, and what the user can do with it. The underlying rule mechanism is the same for all types.

| Type | Created by | Resolution | Scoped to | Editable | Primary surface |
|------|-----------|------------|-----------|----------|-----------------|
| **ContentGroup** | Engine (during ingestion) | Materialized | Library | Read-only | Lane shelf/detail route |
| **Smart** | Engine (auto-generated from templates) | Query-resolved | Library | Disable/enable, feature, adjust threshold | No |
| **System** | Internal/legacy type | Implementation-dependent | Internal | Not a My List or Favorites backing store | No |
| **Mix** | Engine + AI (from taste profile) | Materialized | User (per profile) | Enable/disable | No |
| **Playlist** | User | Materialized | User (per profile) | Full CRUD | No |
| **Custom** | Authorized profile or administrator | Manual or dynamic | Explicit owner and audience | Switch membership mode; edit complete rules | For Me or published Collections |

### Hybrid Resolution

Collections resolve their items in one of two ways:

- **Query-resolved** - the Engine evaluates the collection's predicates against the data store at display time. Items are always fresh. Used by Smart collections and Custom collections.
- **Materialized** - items are explicitly linked to the collection in the data store. Membership changes when files are ingested (ContentGroup), when an authorized user changes explicit membership, or when the AI refreshes recommendations (Mix).

Query-resolved collections store their predicates in the `rule_json` column. Materialized collections may also have rules (for display purposes or re-evaluation) but their membership is tracked via the `collection_works` junction table.

---

## ContentGroup Collections - Finalized During Readiness

ContentGroup collections represent natural groupings that emerge from the media itself: TV shows, music albums, book series, comic volumes, audiobook series, and movie series. They are materialized during the ingestion/readiness path so Read, Watch, and Listen can show lane shelves before every broader Wikidata relationship has been hydrated. Display rules are stricter than storage rules: TV shows may show with one owned episode, while non-TV/non-music series cards require at least two distinct owned works by default and use collection/root artwork before item-level artwork. Book, comic, and movie series cards expose a bounded structural preview of owned works. The Dashboard renders every non-TV series and collection container through the same larger fixed-size landscape card with an artwork-only rest state. Two to four representatives form a slightly angled, overlapping cluster, and hover/focus preserves that exact composition while revealing a lightly shaded compact top overlay for type, title, and one status line. On Watch, the `Series` shelf is movie-only and explains that the Engine dynamically aligned those movies; TV shows occupy a separate `TV Shows` shelf. TV shows use the rich media tile: show cover at rest, followed by the show-level cinematic backdrop and identity on hover; owned episodes remain on the show detail surface. Container cards never contain inline actions, rotate artwork, or navigate between children; the whole card opens the container detail surface where browsing and actions belong.

When a file is ingested:
1. Processors and retail providers extract grouping metadata such as show name, album name, series name, Comic Vine series/volume facts, or TMDB `belongs_to_collection`.
2. `CollectionFinalizationService` runs after quick hydration, or after retained-retail organization when Wikidata cannot resolve a QID.
3. `CollectionAssignmentService` builds a stable shelf identity for the item. A Wikidata `series_qid` is preferred when known; otherwise provider-backed keys such as `tmdb:collection:{id}`, `tmdb:tv:{id}`, `tvdb:tv:{id}`, album IDs, or a normalized local series/show/album label are used.
4. The service checks for an existing ContentGroup by QID or provider key (`rule_hash`) and creates one only when no matching shelf exists.
5. The Work is linked to the shelf collection inside the per-shelf lock. If a later enrichment pass discovers the QID, the provider-backed shelf is upgraded instead of duplicated.
6. Parent rollup resolution runs only after shelf assignment and only creates a broader parent when trusted relationship rows connect at least two shelves.

`CollectionBackfillService` repairs older works that have visible owned media assets but missed shelf assignment. It runs a bounded pass during library reconciliation and is also exposed through `POST /collections/reconcile` for manual repair.

ContentGroup collections have additional fields:
- **`group_by_field`** - the metadata field that defines the group (e.g. `series`, `album`, `show`)
- **`sort_field`** / **`sort_direction`** - default ordering for items (e.g. episode number ascending, track number ascending)

These ContentGroup records power **lane shelves** in Read, Watch, and Listen, but the user-facing surface follows the media concept rather than the storage type. When Watch groups TV by show, clicking a show opens `/watch/tv/show/{collectionId}` on the unified detail page with seasons and episodes. The Collections page uses broader rollups only when relationships such as series, franchise, or fictional universe connect multiple shelves.

### ContentGroup by Media Type

| Media type | Group identity | Sort default | Example |
|-----------|---------------|-------------|---------|
| TV | Show name | Season -> Episode ascending | "Breaking Bad" |
| Music | Album name (+ artist) | Track number ascending | "OK Computer - Radiohead" |
| Books | Series name (+ author) | Sequence index ascending | "Dune Novels - Frank Herbert" |
| Audiobooks | Series name (+ author) | Sequence index ascending | "First Law - Joe Abercrombie" |
| Comics | Volume name | Issue number ascending | "Saga - Brian K. Vaughan" |
| Movies | Film series name | Release year ascending | "The Matrix film series" |

---

## Smart Collections - Auto-Generated from Library Data

Smart collections are auto-generated from library metadata using templates. One collection is created per qualifying instance (one per genre, one per decade, etc.). Minimum item thresholds prevent clutter from one-off entries.

### Template Categories

| Template | Rule pattern | Default threshold | Media types | Icon |
|----------|-------------|-------------------|-------------|------|
| **By Genre: [name]** | `genre eq "{name}"` | 3+ items | Any | Tag |
| **By Vibe: [tag]** | `vibe eq "{tag}"` | 5+ items | Any | Mood/wave |
| **By Author: [name]** | `author eq "{name}"` | 3+ works | Books, Audiobooks, Comics | Person |
| **By Director: [name]** | `director eq "{name}"` | 3+ works | Movies, TV | Person |
| **By Narrator: [name]** | `narrator eq "{name}"` | 3+ works | Audiobooks | Person |
| **By Decade: [period]** | `date_published between "{start},{end}"` | 5+ items | Any | Calendar |
| **Recently Added** | `date_added gt "{30_days_ago}"` | Always shown | Any | Clock |
| **Highest Rated** | `provider_rating gt 4` | Always shown | Any | Star |
| **Unrated** | `user_rating eq "unrated"` | Always shown | Any | Circle-dashed |

Smart collections refresh automatically - their rules are re-evaluated as the library changes.

Entity-backed rules store a Wikidata QID plus a display label. The editor groups factual dimensions under Media, People & Organizations, Story & World, Recognition, Production, Relationships, and My Library. Missing structured metadata is not treated as a negative assertion: `neq` means “known and different,” while `is unknown` only matches a field that a completed structured-discovery lookup could not populate. Relationship rules currently cover adaptations and local ownership of their source/adaptation work.

### Configuration

- **Generation threshold** - adjustable minimum item count per template (default 3 or 5 depending on template)
- **Featured toggle** - pin a collection to appear prominently on the relevant media lane page
- **Enabled toggle** - disable to hide from browsing without deleting

These are managed on the Collections page (`/collections`).

---

## Profile lists and state

The former Reading List, Watchlist, Currently Watching, Listening Queue, and name-based System Collection behavior are retired. `/for-me?view=my-list` reads saved references; `/for-me?view=favorites` reads reactions. `ProfileStateRepository` persists those independent states in `profile_saved_items` and `profile_reactions`.

Continue comes from playback/reading progress, without requiring list membership. Profile-owned Collections, Playlists, and Galleries appear automatically only in their respective **Your** shelves. Accessible library Collections and shared Playlists enter My List only when explicitly saved.

## Personalised Mixes

The following mix table preserves design examples rather than a list of shipped automatic collections. It must not be used to synthesize hidden Collections for current For Me shelves. Continue is a current progress projection; the other proposed mixes require individual implementation and acceptance evidence.

| Mix | Logic | Refresh |
|-----|-------|---------|
| **Continue** | In-progress items across all lists | Real-time |
| **Heavy Rotation** | Most consumed in last 30 days | Daily |
| **Discovery Queue** | In your library, matches your taste, untouched | Daily |
| **New For You** | Recently added items matching taste profile | On new ingestion |
| **Because You Liked [X]** | Items similar to a specific highly-rated item (vibe + genre overlap) | On rating change |
| **Taste Mix: [cluster]** | Per taste cluster - your atmospheric sci-fi cluster, your cozy mystery cluster | Weekly |
| **On Repeat** | Current obsessions (high consumption recently) | Daily |
| **Rediscover** | Things you loved 6+ months ago but haven't touched since | Weekly |

Personalised mixes use: genres (from Wikidata/retail), vibe tags (AI-generated), consumption history, user ratings, and the Taste Profile model. See `docs/architecture/ai-integration.md` for the Genre vs Vibe discovery model.

### Configuration

- **Enabled toggle** - per mix type, disable to hide for all users
- No other configuration - the AI handles everything

---

## Playlists & Curated Collections

These are separate products. Playlists are Listen queue/edit surfaces. Custom Collections have an explicit owner and audience and can span media types; profile-owned Collections are private unless supported sharing policy says otherwise, while administrator-managed library Collections may be published to everyone or selected profiles.

### Playlists (Materialized)

Traditional playlists where the user hand-picks items. Items are explicitly linked.

- **Create** - from the Listen playlists surface
- **Rename / Delete** - from within the playlist detail page
- **Add items** - from library browsing via the "Add to Playlist..." picker
- **Remove items** - from within the playlist detail page
- **Reorder** - drag to reorder within the playlist
- **Progress tracking** - optional toggle per playlist
- **Artwork** - auto-composed from items' cover art. User can upload a custom override.

### Curated Collections (`Custom`, Query-Resolved or Hand-Picked)

Authorized users create custom Collections with manual membership or dynamic rules. Profile-owned Collections appear in For Me > Your Collections. Only administrators can publish or manage library-wide Collections. Dynamic mode requires at least one complete rule before saving; the editor can switch membership mode in either direction.

- **Create** - the authorized New Collection action is available throughout Collections, including Discovery
- **Edit rules** - from the collection detail page (opens collection builder)
- **Rename / Delete** - from the collection detail page
- **Live update** - items auto-add/remove as they match or stop matching rules (toggleable via `live_updating` flag, default on)
- **Artwork** - auto-composed from matched items' cover art. User can upload a custom override.

Rule-driven curated collections do not support manual add/remove or manual reordering because their rules control membership and sort.

---

## Collection Builder

The collection builder is an Apple Music Smart Playlist-inspired filter interface for creating Custom collections. It lives on the Collections page (`/collections`) and follows a row-based filter pattern.

### Builder Layout

1. **Collection name** - text field at top
2. **Match mode** - toggle: Match ALL rules / Match ANY rule
3. **Filter rows** - each row is: Field dropdown -> Operator dropdown -> Value input. Plus/minus buttons to add/remove rows. A "Make group" button creates nested ALL/ANY logic for complex queries.
4. **Live preview** - section below the rules showing how many items currently match and a scrollable list of the first 20 matches. Updates as rules are added or changed. Powered by `POST /collections/preview`.
5. **Limit and sort** - optional item limit (10, 25, 50, 100, 200, or no limit) and sort controls (field + direction)
6. **Live updating toggle** - when on, items auto-add/remove as they match or stop matching rules
7. **Save** - creates the Custom collection

### Field Autocomplete

The value input adapts based on the selected field:
- **Genre, Vibe** - multi-select picker populated from `GET /collections/field-values/genre`
- **Author, Director, Narrator** - person picker with search
- **Media Type** - media type picker
- **Format, Quality** - format/quality picker
- **Series, Universe** - text picker with search
- **Dates** - date picker or duration selector (last N days/weeks/months)
- **Numbers** - numeric input with range support

### Example Custom Collections

| Collection name | Rules | Limit / Sort |
|----------------|-------|--------------|
| "Unread Sci-Fi" | Genre is "Science Fiction" + Completion Status is "Not Started" | Sort by Provider Rating, descending |
| "Short Listens" | Media Type is "Audiobook" + Duration < 8 hours + User Rating > 3 | Sort by Duration, ascending |
| "Atmospheric Horror" | Genre is "Horror" + Vibe includes any of "atmospheric, haunting, unsettling" | Limit 50, sort by Taste Match |
| "Denis Villeneuve Marathon" | Director is "Denis Villeneuve" | Sort by Date Published, ascending |
| "4K Movie Night" | Media Type is "Movie" + Quality is "4K HDR" + Duration between 90-180 min | Sort by Random, limit 10 |
| "New This Month" | Date Added is in the last 30 days + Taste Match > 70% | Sort by Taste Match, descending |

---

## Save, react, and queue actions

Media and group cards remain one semantic detail link without inline playback, My List, reaction, or remove buttons. Actions live on details and purpose-built playback/queue surfaces.

My List saves a movie, TV show, book, comic, audiobook, album, accessible Collection, or library/shared Playlist as one reference. Songs use Favorite and Add to Playlist. Favorites are separate reactions; saving or favoriting never creates a hidden collection.

## Progress Tracking

Progress is profile state for the experienced asset or work. Not Started, In Progress, and Completed are independent of My List and playlist membership. Continue projects in-progress media across lanes and retains the actual episode/track playback identity where applicable.

Partial long-form items may show artwork-edge progress. Albums, songs, and View assets do not show completion bars. Ownership counts on authoritative finite structural containers are separate from playback completion.

## Collection Artwork

Collections can use preferred managed artwork or a bounded structural preview. Derived previews are presentation data rather than new canonical artwork assets.

### Auto-composed artwork

The shared `MediaArtworkGroupPreview` renders up to four representative owned images with their natural aspect ratios in a fixed landscape composition. It does not generate or persist a 2x2 collage, rotate children, or add child navigation. Ordered series previews preserve sequence order; curated Collections may use decorative representative stacks.

### Auto-generated banner

Standard Collection details use a neutral cinematic wash and the same representative artwork cluster at hero scale. They do not derive a backdrop from a member or generate a composite banner to stand in for canonical artwork.

### User override

The shared artwork editor manages supported semantic roles for the selected container. A preferred user override belongs to its entity-artwork link; it does not replace the source image's identity or turn the derived preview into a stored collage. Do not give an ordinary Collection a person's Headshot role solely because it contains that person's works.

---

## Collection Placements - Location as Display Constraint

The `collection_placements` table maps collections to UI locations. A single collection can appear in multiple locations with different display constraints.

### Placement Model

Each placement record contains:

| Field | Description |
|-------|-------------|
| `collection_id` | Which collection |
| `location` | Where it appears (e.g. `home`, `media_lane_books`, `read_lane_books`, `my_library`, `collections_page`) |
| `display_limit` | Maximum items to show at this location (e.g. 10 for a home swimlane, null for full detail) |
| `sort_override` | Optional sort that overrides the collection's default for this location |
| `position` | Display order within the location |

### Location Vocabulary

| Location | What appears there | Collection types shown |
|----------|-------------------|----------------|
| `home` | Personalised dashboard | Mix, Smart (Recently Added), System (shortcuts) |
| `media_lane_{type}` | Category browsing by lane | Smart (filtered to media type) |
| `my_library` | Profile lists | System, Playlist |
| `collections_page` | Collections section (`/collections`) | Automatic broader rollups and Curated |
| `read_lane_books`, `watch_lane_video`, `listen_lane_audio` | media lane browse surfaces | ContentGroup (filtered to media type) |

This model replaces hardcoded rules about where each collection type appears. Adding a new location or changing which collections appear where is a data change, not a code change.

---

## The Collections Section (`/collections`)

A component-driven section for browsing organization across the whole library. It shares the persistent rail, compact route navigation, filters, tiles, and page-state components used by Read, Watch, and Listen. Collection management stays in this top-level product surface rather than a separate media-fixing workspace.

### Page Layout

- **Overview** - preview rows for Automatic, Curated, and Shelves, plus role shortcuts into People
- **Automatic** - trusted fictional-universe, franchise, based-on/adaptation, and series rollups with search, lane, relationship, sort, and tile-size filters. Broad cross-media identity is evaluated before lane-local parent or series structure, so adaptations such as Batman films and comics can share one rollup when their trusted QID relationship matches.
- **Curated** - administrator-authored, library-published collections with search, lane, membership, publication, sort, and tile-size filters
- **Shelves** - one cross-lane index of book series, comic volumes, movie series, TV shows, albums, and audiobook series
- **People** - a paged list with managed headshots, canonical roles, owned Read/Watch/Listen counts, and search, role, lane, and sort filters

Only administrators see **New Collection**. Playlists are intentionally absent because their sole browse and management home is Listen.

### Collection Detail

Click into any collection to see:
- Collection artwork (auto-composed or custom)
- Name, description, type indicator
- Rules displayed as readable chips (for Smart and Custom types)
- Items as a grid or list
- Add/remove/reorder controls (only for System lists and Playlists)
- Progress indicators (only for lists/playlists with progress tracking)
- Configuration controls (thresholds for Smart, rules for Custom, placements for all)

### Navigation

The Collections page is accessible from the primary Dashboard navigation alongside Read, Watch, Listen, and Search. The navigation entry uses the collections/grid icon.

---

## Engine Actions

### Rule Evaluation

`CollectionRuleEvaluator` (Storage layer) translates collection predicates into SQL queries against the data store. It:

1. Parses the JSON predicate array
2. Maps each field to its data store column (joining across works, editions, canonical values, metadata claims as needed)
3. Applies the match mode (AND/OR)
4. Applies sort and limit constraints
5. Returns matching Work IDs

For performance, Smart collections cache a `rule_hash` - the hash of the serialized rule JSON. When rules haven't changed, cached results can be served. The `live_updating` flag controls whether materialized collections re-evaluate on library changes.

### Available Actions

| Action | Method | Purpose |
|--------|--------|---------|
| Collections surface catalog | `GET /collections/catalog` | Server-classified catalog for `/collections`: system/user/managed collections plus broader multi-shelf rollups |
| Collection summary | `GET /collections/{id}/summary` | One catalog summary for a detail page without loading the full catalog |
| Collection items | `GET /collections/{id}/items` | Detail-page items, including generated rollup aggregation |
| Repair shelf assignments | `POST /collections/reconcile` | Dry-run or run collection backfill for already-ingested media |
| Resolve collection items | `GET /collections/resolve/{id}` | Evaluate rules, return matching items |
| Preview rules | `POST /collections/preview` | Evaluate rules without saving - powers the live preview in the collection builder |
| Collections at location | `GET /collections/by-location/{location}` | All collections placed at a UI location, with display limits applied |
| Field values | `GET /collections/field-values/{field}` | Distinct values for a field - powers autocomplete in the collection builder |
| Create collection | `POST /collections` | Create a profile playlist or an administrator-only, library-published Curated (`Custom`) collection |
| Update collection | `PUT /collections/{id}` | Update collection properties or rules |
| Delete collection | `DELETE /collections/{id}` | Delete an editable Playlist or Curated (`Custom`) collection |
| Get placements | `GET /collections/{id}/placements` | Where a collection appears |
| Set placements | `PUT /collections/{id}/placements` | Update collection placements |

---

## Platform Inspiration

The universal collection model draws from patterns proven by major media platforms:

| Platform | Pattern | How it maps to collections |
|----------|---------|-------------------|
| **Netflix** | "Because you watched X", category rows, "My List" | Mix (Because You Liked), Smart (genre rows), Independent My List saved references |
| **Spotify** | Daily Mixes, Discover Weekly, user playlists, album pages | Mix (Taste Mix), Mix (Discovery Queue), Playlist, ContentGroup (albums) |
| **Apple Music** | Smart Playlists with field+operator+value rules | Custom collections via collection builder - direct inspiration for the filter UI |
| **Plex** | Auto-generated collections by genre/decade/director, user collections | Smart collections (auto-generated templates), Custom collections |

---

## Data Store Changes (Migration M-070)

New columns on the `collections` table:

| Column | Type | Purpose |
|--------|------|---------|
| `resolution` | TEXT | `query` or `materialized` |
| `rule_json` | TEXT | JSON array of predicates |
| `rule_hash` | TEXT | SHA-256 of rule_json for cache invalidation |
| `group_by_field` | TEXT | Field that defines ContentGroup identity |
| `match_mode` | TEXT | `all` or `any` |
| `sort_field` | TEXT | Default sort field |
| `sort_direction` | TEXT | `asc` or `desc` |
| `live_updating` | INTEGER | 1 = re-evaluate on changes, 0 = static |

New table: `collection_placements`

| Column | Type | Purpose |
|--------|------|---------|
| `id` | INTEGER PK | Auto-increment |
| `collection_id` | TEXT FK | References collections |
| `location` | TEXT | UI location key |
| `display_limit` | INTEGER | Max items at this location (null = unlimited) |
| `sort_override` | TEXT | Optional sort override |
| `position` | INTEGER | Display order |

Backfill migration converts existing collections to the new schema, generating `rule_json` from existing collection properties.

---

## Implementation Stages

| Stage | Scope |
|-------|-------|
| **Stage 1 - Foundation** | Collection entity changes, migration M-070, `CollectionRuleEvaluator`, predicate model, actions (resolve/preview/field-values) |
| **Stage 2 - ContentGroup** | Ingestion-time creation in `MediaEntityChainFactory`, browse container views driven by ContentGroup collections |
| **Stage 3 - Smart Collection Generation** | Template engine, auto-generation from library data, threshold configuration |
| **Stage 4 - Collections Page & Collection Builder** | `/collections` page, collection builder UI, Custom collection CRUD, placement management |
| **Stage 5 - Migration** | Collections page removal, navigation updates, existing collection backfill |

---

## Related

- [How Universes and Series Work](../explanation/how-universes-work.md)
- [Universe Graph](universe-graph.md)
- [Dashboard UI Architecture](dashboard-ui.md)
- [Target State](target-state.md)

## Wikidata Series Manifests

When a ContentGroup collection has a canonical Wikidata series QID, hydration can attach a factual manifest from Tuvima.Wikidata. The manifest is stored separately from owned Works: owned entries link to local `works`, while missing entries remain lightweight `series_manifest_items` rows with names, QIDs, ordering, parent collection labels, and provenance.

This lets the UI show views such as "owned 2 of 18" for a series without creating fake media assets. For example, an Expanse collection can show owned novels plus missing novels, novellas, and short fiction when Wikidata models them. Manifest warnings indicate Wikidata modeling gaps or local ambiguity; user display overrides may affect presentation, but QID-based manifest facts remain canonical.

## Collections and People presentation rules

Collections owns one padded content scroller beside its fixed rail; the document must not add a second scrollbar. People catalog counts use distinct top-level works, so tracks count once as their album and episodes count once as their show.

Person linked identities distinguish authorship aliases from music-group membership. A collective pen name uses alias relationships. A musical group uses every canonical Wikidata `has parts` identity as a `person_group_members` relationship, queues incomplete member records for full person hydration, and displays those people as linked member cards.
