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

## D

### Dashboard

The Blazor Server web UI (`MediaEngine.Web`) used to browse, search, read, play, configure, and review the library.

### Detail Page

The page for a work, collection, book, movie, TV show, episode, person, or other supported entity. Detail pages are the normal place to inspect an item and launch inline corrections.

## E

### Edition

A specific release or format of a work, such as one ebook edition, one audiobook release, or one film cut.

### Engine

The backend application (`MediaEngine.Api`) that runs ingestion, enrichment, storage, background jobs, Local AI, and the API used by the Dashboard.

### Enrichment

Follow-up metadata work after basic identity is known, such as artwork, people, relationships, descriptions, summaries, and universe graph data.

## F

### Found as a TV title

A Review Queue reason for a Movies-library file whose only strong match is a TMDB miniseries or short TV title. Resolve it with Move to TV, Search again, or Keep as unmatched film.

## H

### Household

The people who live together and the sign-ins that open them. A household holds up to 8 people (profiles), and each sign-in opens only people from its own household. The first household is created when the owner finishes setup. Someone invited from outside starts a household of their own.

### Hydration

The identity enrichment process after ingestion. Retail providers gather practical matches and bridge IDs; Wikidata resolution uses those IDs to find canonical identity when possible.

## L

### Library Folder

A configured source folder that tells the Engine where to scan, what media types to expect, and how files should be handled.

### Local AI

AI features that run on local model files through local runtimes. Local AI helps with classification, matching, summaries, vibe tags, intent parsing, and audio tasks, but it does not become the authority for factual metadata.

## M

### Media Asset

A single file on disk, such as one `.epub`, `.mkv`, `.m4b`, `.flac`, or `.cbz`.

### Media Lane

One of the main browse surfaces: Read, Watch, or Listen.

### Media Type

The resolved category for a file: Books, Audiobooks, Movies, TV, Music, or Comics.

### Managed Artwork

Artwork copied into Tuvima Library's managed `.data/assets` store and served back
through Engine media URLs. Provider image URLs are source inputs, not stable UI
display URLs.

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

### Review Queue

The exception workflow for blocked, uncertain, low-confidence, or unresolved items that need human confirmation.

## S

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

### Shared Library

Separate household-owned View storage containing accepted contributions. Shared access does not grant access to every profile's Personal Space.

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

Old management concepts that should not be reintroduced. Current browse and correction flows use Home, Read, Watch, Listen, Collections, Search, detail pages, Review Queue, and Settings/Admin.

## Next steps

- [Product Status](../product/status.md)
- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [How Review Works](../guides/resolving-reviews.md)
