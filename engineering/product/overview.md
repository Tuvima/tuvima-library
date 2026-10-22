# Tuvima Library — product overview

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Architecture overview](../architecture/overview.md) · [Design system](../design/design-system.md) · [Specs](../specs/) · [What is Tuvima? (public)](../../docs/explanation/what-is-tuvima.md) · [Feature status (public)](../../docs/product/status.md)

The anchor for planning conversations: what Tuvima is, who it's for, and the principles every feature must respect.

## What it is

**Tuvima Library** (by the company **Tuvima**) is a home media library for people who already own their stories — ebooks, comics, audiobooks, films, TV, music and personal photos. Those files are usually scattered across folders, formats and separate apps. Tuvima finds them, works out what each one is, fills in artwork and details, connects related works across formats ("Dune" the novel, audiobook and films), and presents everything as one coherent, beautiful library you can read, watch and listen to.

The guiding word is **Presentation**: Tuvima doesn't create a library, it presents one.

It runs entirely on the person's own computer or home server. There is no Tuvima account, subscription or cloud. It is in **Early Access**.

## Who it's for

- **Collectors and self-hosters** who own media in many formats and today juggle several apps (one for films, one for books, one for audiobooks, one for music).
- **Households** sharing one library: each person gets a profile with their own progress, lists and limits; administrators manage libraries and access.
- People who care about **privacy** and **owning** their media, and want something that looks as good as a streaming service.

## The experience

| Area | What it's for |
|---|---|
| **Home** | Discovery: continue where you left off, recently added, suggestions from your own library |
| **Read · Watch · Listen** | Browse by how you consume media (books and comics · films and TV · music and audiobooks) |
| **Collections** | Broader groupings that span formats and series — a Universe such as Dune or Marvel |
| **For Me** | Your profile's progress, saved items and favourites |
| **View** | A personal space for photos, videos and documents, with optional household sharing |
| **Search** | Across the whole library |
| **Detail pages** | One page per title, person, series or universe: play, explore connections, and (for administrators) correct it |
| **Needs Review** | Where administrators confirm items Tuvima wasn't sure about |
| **Settings** | Libraries, profiles and access, playback, providers, operations |
| **Native apps** | Android (phone and TV), Apple, Roku and TV web clients connect to the same Engine |

## Words we use

| Say | Means |
|---|---|
| Library | Everything you own |
| Universe | A franchise or shared world spanning series and formats (Dune, Marvel) |
| Series | An ordered run within one kind of media (Dune Novels); a TV show is a "show" |
| Work | One title (Dune Part One) |
| Edition | A specific version (4K HDR remux) |
| Media Asset | The file on disk |
| Needs Review | Items waiting for a person to confirm |
| Engine / Dashboard | The background service / the web app people use |

Formats of the same title (ebook + audiobook) are variants of one Work, not a collection.

## UX principles

1. **Present, don't manage.** Screens show stories, not file trees. There is no all-in-one admin workbench; corrections happen where you find the problem.
2. **Fix it in place.** "Edit" opens the shared editor over the page you're on, then puts you back.
3. **Truthful, not invented.** Only show connections, progress and facts backed by evidence; uncertain matches go to Needs Review rather than being guessed.
4. **Private by default.** Everything stays on the person's machine; profiles can't see each other's private spaces; outside requests only for providers the person enabled.
5. **One way to do each thing.** Shared components and patterns across every surface; a new screen reuses existing building blocks.
6. **Every state is designed.** Empty, loading, error and "still processing" states are part of the feature, not afterthoughts.
7. **Works on every screen.** Phone, tablet, desktop and TV are all first-class; editing is desktop-only (a mouse or trackpad), so phones, tablets and TVs are for browsing and playing.
8. **Calm and cinematic.** Dark, artwork-led, restrained motion; the media is the star (see the [design system](../design/design-system.md)).

## Current status (October 2026)

Core browsing, playback/reading, identification and administration work today. Partly built or planned: some discovery and recommendation features, plugins (the platform is built: four kinds, approval, signed installs, a blocklist, Local AI features as plugins; one-click install stays off until plugin keys are encrypted on disk), personal-media intelligence (faces, scenes), bulk editing, and live updates on detail pages. The public [feature status page](../../docs/product/status.md) is the authority on what's ready.

## Open product questions

- The public site calls the cross-format page **Collections** while the concept is a **Universe** — confirm the user-facing name for each place it appears.
