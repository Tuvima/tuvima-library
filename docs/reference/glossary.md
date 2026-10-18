---
title: "Glossary"
description: "Reference the core product and architecture terms used throughout Tuvima Library documentation."
audience: "user"
category: "reference"
product_area: "concepts"
tags:
  - "glossary"
  - "terminology"
  - "concepts"
status: current
---

# Glossary

Look up a product term in Tuvima Library in a minute. These are the terms used throughout the documentation. User-facing terms are preferred; internal code names appear only when they help connect the docs to the repository.

## A

### Artwork Outcome

Whether artwork is present, missing after an explicit artwork pass, or still pending.

### Auto-filing rule

A rule a person makes that files new documents for them: when a document's words, file name, folder, source or file kind match, it adds tags, sets the correspondent, type or a custom field, and can file the document straight away. Rules run in order, never replace something a person set, belong to the person who made them (a household administrator can also make them for the Shared Library) and use plain words with no patterns. See [How documents are filed](../explanation/how-documents-are-filed.md).

## B

### Browse Readiness Gate

The rule that controls whether an item appears in Home, Read, Watch, Listen, Collections, or Search. The item needs a non-placeholder title, a resolved media type, and a settled artwork outcome.

## C

### Canonical Value

The resolved metadata value for a field. Provider claims use the Priority Cascade; supported presentation fields can also have durable editor overrides.

### Claim

A single metadata value from a specific source with confidence and provenance. Claims are append-only so earlier source data can be audited later.

### Collection

A grouping of media with explicit purpose and access. Automatic Collections connect trusted broader relationships; custom collections may belong to a profile or be published by an administrator. A Collection should not duplicate a single lane-level shelf. For example, a book series belongs in Read and in the cross-lane Shelves index; a wider world that connects novels and films can appear in Automatic.

### Combined file

One video file that holds several episodes of a show, such as `Show S01E01E02.mkv`. It stays one file: it is never split, and playing, resuming and watching it act on the file. Its covered episodes are recorded separately, and the organiser names it with the episode run (for example `s01e01-e02`). A whole miniseries in one film file becomes a combined file when it is moved to TV.

### Correspondent

The person or organisation a document came from or is about, such as a bank or a clinic. Correspondents are one list for the whole server. A person only sees the correspondents used on documents they can open, plus ones they added themselves.

### Custom field

A field a person defines for their documents, such as "Policy number" or "Amount due". Its kind is text, number, date, money (an amount with a currency code), yes/no, or one of a list of choices. Custom fields are one list for the whole server; each document stores its own value. Searching finds text values.

## D

### Dashboard

The Blazor Server web UI (`MediaEngine.Web`) used to browse, search, read, play, configure, and review the library.

### Detail Page

The page for a work, collection, book, movie, TV show, episode, person, or other supported entity. Detail pages are the normal place to inspect an item and launch inline corrections.

### Devices

The **Settings → Administration → Devices** page. It lists the phones and TVs paired to this server (revoke one, or choose whose photos a phone backs up), shows the *Allow apps to connect* switch and address to type in an app (server administrators only), and lists the signed-in sessions of the people the viewer looks after, with **Sign out** for each. A household administrator sees only their own household's devices and sessions.

### Document type

What kind of document it is, such as an invoice, letter or contract. Document types are one list for the whole server, shown to each person on the same terms as correspondents.

## E

### Edition

A specific release or format of a work, such as one ebook edition, one audiobook release, or one film cut.

### Engine

The backend application (`MediaEngine.Api`) that runs ingestion, enrichment, storage, background jobs, Local AI, and the API used by the Dashboard.

### Enrichment

Follow-up metadata work after basic identity is known, such as artwork, people, relationships, descriptions, summaries, and universe graph data.

### Extra protection

The **Extra protection** row under **Settings → Security**, which opens one sheet for optional sign-in protection: passkeys, two-step codes, linked sign-ins (when an external provider is set up) and recovery codes. The row appears once the account has a password and shows *Off*, *Passkey added*, *Two-step codes on*, or both.

## F

### Found as a TV title

A Needs Review reason for a Movies-library file whose only strong match is a TMDB miniseries or short TV title. Resolve it with Move to TV, Search again, or Keep as unmatched film. Move to TV attaches the file to every part TMDB lists (at most six, in one season), so a show of several parts becomes a combined file; only when the parts cannot be listed that clearly is it filed as a special, and the item's History says why.

### From merge

A Needs Review reason for a file that arrived in a managed folder through a folder merge but could not be named yet (it was not identified, was waiting in Review, or had no confirmed title). It waits in a folder called From merge inside the target until you confirm what it is.

## H

### Household

The people who live together and the sign-ins that open them. A household holds up to 8 people (profiles), and each sign-in opens only people from its own household. The first household is created when the owner finishes setup. Someone invited from outside starts a household of their own.

### Hydration

The identity enrichment process after ingestion. Retail providers gather practical matches and bridge IDs; Wikidata resolution uses those IDs to find canonical identity when possible.

## I

### Inbox

Where a new document waits on the Documents page until it is filed. Each card shows what was suggested (date, correspondent, type, tags) and can be filed in one tap, changed first, or marked filed without changes. A document from before filing existed counts as filed.

## L

### Library Folder

A configured source folder that tells the Engine where to scan, what media types to expect, and how files should be handled.

### Local AI

AI features that run on local model files through local runtimes. Local AI helps with classification, matching, summaries, vibe tags, intent parsing, and audio tasks, but it does not become the authority for factual metadata.

## M

### Media Asset

A single file on disk, such as one `.epub`, `.mkv`, `.m4b`, `.flac`, or `.cbz`.

### As-is

A folder handling mode. Tuvima only reads an As-is folder: it never moves, renames, tags or deletes its files, so other apps that use the folder are not affected. It is the recommended choice for folders that already have files. Stored as `management_mode: existing_library`.

### Convert to Managed

Switching an As-is folder to Managed. Tuvima shows a preview, then runs a background organising job (progress, pause, resume, cancel) that moves and renames the files to the type's standard layout. Sidecar files move with their media, every move is written to a downloadable log, and there is no automatic undo.

### Import folder

One optional folder, the seventh entry in Libraries, where new media is dropped. It holds one subfolder per media type, and each arrival is filed into that type's New media folder. Files that cannot be matched go to Review with the badge "From the Import folder". It is never a library folder.

### Managed

A folder handling mode. Tuvima may move, rename and (when the type's tag setting is on) tag files in a Managed folder, including the ones already there. Stored as `management_mode: managed_by_tuvima`.

### New media goes here

The one managed folder per media type that receives new files. The first managed folder added becomes it; any other managed folder can be made the place for new media from its row menu. There is no priority order.

### Media Lane

One of the main browse surfaces: Read, Watch, or Listen.

### Media Type

The resolved category for a file: Books, Audiobooks, Movies, TV, Music, or Comics.

### Merge

A one-time job that brings the files of one existing folder into a managed folder of the same kind of media, with the same naming, duplicate and tag rules as the rest of the library. Copy (the default) never changes the original folder; Move removes each original only after its copy has been checked byte for byte. The Import folder is the way to keep feeding files in over time.

### Managed Artwork

Artwork copied into Tuvima Library's managed `.data/assets` store and served back
through Engine media URLs. Provider image URLs are source inputs, not stable UI
display URLs.

### Needs Review

The one place for items the Engine is not sure about: blocked, uncertain, low-confidence, or unresolved items that need a person to confirm them. It is pinned at the top of Settings → Recently Added and opens from the account menu. Only server administrators, and apps a server administrator has given review permission, can see and resolve it.

## O

### Own sign-in

An email sign-in that belongs to one person in a household and opens straight to them, without the profile picker. The person stays in the household. An own sign-in is never an administrator, and it follows the library and lane access of the household's main sign-in, so later changes to the household reach the person. An administrator gives it from the person's page under **Settings → Profiles** (the **Sign-in** row) and can remove it at any time; removing it keeps the person and everything they have saved.

## P

### Priority Cascade

The rules that decide which metadata source wins. Supported personal-field locks win first, followed by configured field priorities, default Wikidata authority, and remaining confidence rules. Display-title and other supported presentation overrides use a separate editor mechanism.

### Processor

Code that opens a file format and extracts embedded metadata, artwork, and technical facts.

### Provider

An external metadata source such as Apple, TMDB, MusicBrainz, Comic Vine, or Wikidata. Historical bridge identifiers may remain in records even when their original provider is no longer active.

## Q

### QID

A Wikidata entity identifier such as `Q190804`. A QID is a strong identity anchor, but an item can still be usable without one if it passes the browse readiness gate.

### QID Not Found

A controlled outcome where Retail matching succeeded but Wikidata could not resolve a trustworthy QID. The item keeps its available metadata and may still be visible if it is otherwise ready.

## R

### Readiness Label

A plain-English status summary such as Ready, Pending artwork, Needs review, or Engine unavailable.

### Retail Stage

The provider stage that searches external catalogues for practical metadata such as covers, descriptions, ratings, and bridge IDs.

### Review queue

The Engine's internal store behind [Needs Review](#needs-review). People see it as Needs Review.

## S

### Saved view

A set of Documents filters a person keeps under a name: a Smart Gallery whose rule says "these are documents" and lists tags (all of them), correspondent, type, a document date range (fixed or relative, such as the last 30 days), the Inbox, custom field values and words inside the document. It shows at the top of the Documents filter rail and in Galleries, is always private to its owner (it cannot be shared and is never offered as a Collection source), and can be renamed or deleted from its menu. Deleting one never changes a document.

### Series

A lane-level shelf, such as a book series in Read, a film series in Watch, or an album/audio series in Listen. Comics use volume/issue wording on user-facing surfaces even when the stored metadata field is still `series`.

### Shelf

An immediate browse group inside a media lane. A single shelf does not automatically create a Collections tile.

### Sequence Total

The expected count for the immediate shelf currently being shown, such as issue
count for a comic volume, track count for an album, episode count for a season,
or book count for an ordered book series.

### Sequence Total Scope

The meaning of a sequence total: main sequence, extras included, standalone,
collected edition, or broader franchise. UI counts should only use totals whose
scope matches the displayed container.

### Source Attribution

The provider/source name, source title, URL, license, retrieved timestamp, and
modified/summarized status attached to text shown in the Dashboard.

### Staging

The safe on-disk and database holding state between file discovery and final organization. Staging is not the same as browse visibility.

## T

### Tag

A short label on a View photo, video or document, such as "Taxes" or "Holiday". Tags are one list for the whole server, matched without regard to letter case. A person only sees the tags used on items they can open, plus ones they created; deleting a tag removes it from items and never removes an item.

### Text recognition (OCR)

Reading the words in a picture of text. Tuvima Library uses it for scanned PDF pages and photos of paper in View, so those documents can be searched. It runs on your own computer with Tesseract, one file at a time, and never changes the original. English is included; other languages are an optional download, the only time it uses the internet.

## U

### Universe

A larger world or franchise that can connect multiple shelves. Tuvima uses universe-style relationships to decide when a broader Collection is useful.

## W

### Who can connect

The single setting under Settings > Network that decides how far from the server a visitor may be: **This computer**, **Home network** or **Anywhere**. It applies to every page, sign-in and app connection. Anywhere still requires a secure HTTPS path and sign-in.


### Wikidata

The canonical identity and structured-fact authority used after provider bridge IDs make resolution precise enough.

### Work

The underlying title independent of file, edition, or format.

### Writeback

Writing resolved metadata back into supported file tags after enrichment or user correction.


## Personal and shared experiences

### Public Address

The single HTTPS address people use to reach this server from outside the home, set once as `remote.public_hostname` under Settings → Network. Passkeys, linked sign-in callbacks and password-reset emails are all built from it.

### Personal Space

The one personal-media space owned by an enabled profile. Multiple folder or device sources can feed it. View provides its Photos, Folders, Galleries, People, and Places experiences.

### Household photo space

Everyone in a household can open (read only) each other's Personal Space and send photos from it to the household's Shared Library, listed under **Household** in the View scope picker. Other households' spaces are invisible, including counts, thumbnails, search results, map pins and timeline.

### Shared Library

Separate household-owned View storage containing accepted contributions. Each household has its own, and people only ever see their own household's. Shared access does not grant access to every profile's Personal Space. Household administrators review what people send to it; there is no separate curator role.

### Household administrator

The person who looks after one household: adds and removes people, gives people their own sign-ins, sets PINs, and hands out libraries and features the household already has. They work from **Settings → Profiles**, and from **Settings → Administration → Devices** for their own household's devices and sessions. They never reach another household or the server's settings, which stay with the **server administrator**.

### Account

A sign-in, always identified by an email address. An account can open the profiles it has been granted. Someone who does not need their own sign-in is a profile in another account's household, not an account.

### Profile

The identity whose progress, bookmarks, reactions, preferences, and Personal Space you are using. An account can switch only among profiles it is permitted to access.

### For Me

The active profile's personal hub for Continue, My List, Favorites, and its own Collections, Playlists, and Galleries.

### My List

Bookmarks for supported media, accessible collections, and library/shared playlists. Saved items remain separate from favorites and progress. Songs use Favorite or Add to Playlist.

### Favorites

Items marked with a favorite reaction. A favorite is distinct from Like/Dislike ratings and My List bookmarks.

### Continue

A shelf of supported in-progress media, based on the active profile's reading or playback progress.

### Gallery

A manual or rule-driven group of View media. Sharing follows profile access policy. Deleting a Gallery does not delete its media.

### Discovery

A starting surface with selected shelves that help you find something to open. Full browse routes provide the complete filterable scope.

### Operations

The administrator destination for current ingestion work, waits, outcomes, and recent batch history. The header activity indicator opens it for authorized system activity.

### Plugin

Optional compiled code loaded by the Engine to add behavior. Plugins run in-process and are not sandboxed; administrators install and manage trusted plugins.

## Retired Terms

### Removed all-in-one workspace

Old management concepts that should not be reintroduced. Current browse and correction flows use Home, Read, Watch, Listen, Collections, Search, detail pages, Needs Review, and Settings/Admin. The retired all-in-one management screen is gone: fixing one item happens in Editing Mode on its detail page, and items the Engine is unsure about go to Needs Review.

## Next steps

- [Product Status](../product/status.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [How Review Works](../guides/resolving-reviews.md)
