<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/images/tuvima-logo-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="assets/images/tuvima-logo.svg">
  <img src="assets/images/tuvima-logo.svg" alt="Tuvima Library" height="90" />
</picture>

**One library. Every story.**

Your books, films, shows, music, audiobooks, comics and personal media, together in one private library that understands how they connect.

[![License: AGPLv3](https://img.shields.io/badge/License-AGPLv3-blue.svg)](LICENSE)
[![Status: Early Access](https://img.shields.io/badge/status-Early%20Access-8852FC.svg)](https://tuvima.github.io/tuvima_library/product/status/)
[![Docs](https://img.shields.io/badge/docs-tuvima.github.io-8852FC.svg)](https://tuvima.github.io/tuvima_library/)
[![Docker](https://img.shields.io/badge/docker-ghcr.io-2496ED.svg)](https://tuvima.github.io/tuvima_library/install/docker/)

**[Install](https://tuvima.github.io/tuvima_library/tutorials/getting-started/)** · **[Documentation](https://tuvima.github.io/tuvima_library/)** · **[What's ready](https://tuvima.github.io/tuvima_library/product/status/)** · **[Report an issue](https://github.com/Tuvima/tuvima_library/issues)**

</div>

---

## Your stories are scattered. Tuvima Library brings them together.

A single story often lives in five places: the ebook in one folder, the audiobook in another app, the film on a hard drive, the comic adaptation in a reader, and the soundtrack somewhere in your music. Each app shows its own slice. None of them knows they belong together.

Tuvima Library starts with the story instead of the file type. Point it at your folders and it identifies what you own, adds artwork and details, and connects the book, the film, the audiobook and the soundtrack into one place you can browse, read, watch and listen. It runs entirely on your own computer or server. No cloud account, no subscription, nothing leaving your home unless you choose it.

## Why people choose Tuvima Library

- **Every format in one library.** Books, comics, movies, TV, music and audiobooks live together, organised into **Read**, **Watch** and **Listen**. You don't need a separate app for each.
- **Your stories, connected.** Tuvima links a novel to its film adaptation, its audiobook narration and its soundtrack, and groups them into a Universe you can explore. *Dune* becomes one place: the novels, the films, the audiobooks and the score.
- **Read, watch and listen built in.** Read EPUBs in the browser. Play music with lyrics and a queue. Listen to audiobooks with chapters, bookmarks, speed and a sleep timer. Watch films and shows with subtitles and Up Next. **Continue Across Media** puts your unfinished book, show and audiobook in one row.
- **It organises itself, and asks when it isn't sure.** Tuvima reads each file's metadata, checks trusted catalogues and open knowledge from Wikidata and Wikipedia, and fills in titles, artwork, series order, cast and creators. When a match is uncertain, it goes to a **Review Queue** instead of being silently guessed.
- **A private space for personal media.** **View** gives every profile its own Personal Space for photos, home videos and documents, with a timeline, folders, galleries and favourites. Personal files never go to online catalogues.
- **Made for households.** Accounts, profiles, optional profile PINs and per-library access let each person keep their own progress, lists and favourites.
- **Free and open source, for good.** There is no premium tier, no feature paywall and no product telemetry. The AGPLv3 license keeps the source open, so your library never depends on one company staying in business.

## How Tuvima Library compares to Plex and other apps

Plex, Jellyfin, Emby, Audiobookshelf, Kavita and calibre are excellent at what they were designed for. Most were built around one kind of media. Tuvima Library was built around the story, across every kind of media you own.

**If you use Plex, Jellyfin or Emby today, Tuvima Library gives you:**

- **Books, comics and audiobooks as first-class media**, not add-ons or workarounds next to your movies and music.
- **Connections across formats.** Your library knows that a film is based on a book you own, that an audiobook narrates it, and that an album is its soundtrack.
- **No outside account.** Plex requires a Plex account and sells premium features through Plex Pass. Tuvima Library needs no account with anyone, and every feature is free.
- **Identification you can see and correct.** Tuvima shows how confident it is about each match. You fix mistakes in place from the item's own page, without renaming files to satisfy a scanner.

| Kind of app | Examples | What they do really well | What Tuvima Library adds |
| --- | --- | --- | --- |
| Media servers | [Plex](https://www.plex.tv/), [Jellyfin](https://jellyfin.org/), [Emby](https://emby.media/) | Polished video and music streaming, transcoding, live TV and apps on many devices | Books, comics and audiobooks in the same library, connected to their films, shows and soundtracks |
| Media centres | [Kodi](https://kodi.tv/about/) | Flexible, customisable playback on a TV | A shared, server-side catalogue of every work, person, series and collection you own |
| Reading libraries | [calibre](https://calibre-ebook.com/about), [Kavita](https://www.kavitareader.com/), [Komga](https://komga.org/) | Ebook management, conversion and dedicated comic and manga readers | Your reading next to its audiobooks, screen adaptations and creators |
| Audio libraries | [Audiobookshelf](https://www.audiobookshelf.org/), [Navidrome](https://www.navidrome.org/) | Focused audiobook, podcast and music playback with mobile apps | Each audiobook or album linked to its source work, other formats and wider series |
| Download automation | [Sonarr](https://sonarr.tv/), [Radarr](https://radarr.video/), [Lidarr](https://lidarr.audio/) | Monitoring releases and organising new downloads | Understanding what you already own and how it all fits together |

**You don't have to replace anything.** Tuvima Library can watch the same folders your other apps use, as read-only sources it never changes. Automation tools can keep feeding it files, and specialist apps can stay on the devices where they shine.

**Where others are ahead today.** Tuvima Library is in Early Access. It runs in your web browser, and native TV and phone apps are still in development. If you depend on living-room TV apps, live TV or long-established remote streaming, Plex and Jellyfin are more mature today. Many people run Tuvima alongside them.

## What we mean by a Universe

A **Universe** is the map of a creative world. It connects the works you own (books, comics, audiobooks, films, TV and music) with the series they belong to, the adaptations between them, and the people who made them: authors, directors, narrators, performers and composers.

Owning a film trilogy gives you a tidy shelf in Watch. Owning the novels, the films, the audiobooks and the score gives you a Universe that brings them all together. Tuvima builds these connections only from trusted evidence, such as shared identifiers in Wikidata, never from similar titles alone.

Learn more in [How Universes and Series work](https://tuvima.github.io/tuvima_library/explanation/how-universes-work/).

## Quick start

The recommended way to run Tuvima Library is Docker Compose. Download the maintained configuration:

```sh
curl -fsSL https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -o docker-compose.yml
```

Edit the folder paths under `volumes` to point at your media and app-data folders, set your timezone and user/group IDs, then start it:

```sh
docker compose up -d
```

Open **http://YOUR-SERVER:5016**, create your administrator account, save your recovery codes, and add your media folders. The [Docker guide](https://tuvima.github.io/tuvima_library/install/docker/) explains every setting, and what to do if your host can't download the image.

Other ways to install:

- **Windows 10 or 11:** the [Windows installer](https://tuvima.github.io/tuvima_library/install/windows/) (Early Access) runs Tuvima as Windows services.
- **NAS:** step-by-step guides for [Unraid](https://tuvima.github.io/tuvima_library/install/unraid/), [Synology](https://tuvima.github.io/tuvima_library/install/synology/), [QNAP](https://tuvima.github.io/tuvima_library/install/qnap/) and [TrueNAS SCALE](https://tuvima.github.io/tuvima_library/install/truenas-scale/).
- **From source:** for developers and evaluators, see [Run from source](https://tuvima.github.io/tuvima_library/install/from-source/).

## Private by design

- Your media, catalogue, artwork and optional AI models stay on your own machine.
- No Tuvima account, no subscription and no built-in tracking.
- Online metadata, artwork, lyrics and subtitle services are contacted only when they are configured and needed.
- Optional local AI improves matching and descriptions entirely on your hardware.
- Uncertain matches are shown to you, never silently treated as correct.

Some features, such as maps in View, can load data from the internet. [Privacy and local-first behavior](https://tuvima.github.io/tuvima_library/explanation/privacy-local-first/) explains exactly what connects where.

## Powered by open knowledge

[Wikidata](https://www.wikidata.org/wiki/Wikidata:Introduction) gives Tuvima Library structured identities and relationships: which works form a series, which film adapts which book, who wrote, directed or performed. [Wikipedia](https://en.wikipedia.org/wiki/Wikipedia:About) adds the readable history and context. Tuvima attributes both and keeps outside knowledge clearly separate from the files you own.

Both are built by volunteers. You can [contribute to Wikipedia](https://en.wikipedia.org/wiki/Wikipedia:Contributing_to_Wikipedia), [help with Wikidata](https://www.wikidata.org/wiki/Wikidata:Contribute) or [support Wikimedia](https://donate.wikimedia.org/).

## Why I built Tuvima Library

I loved Plex, Audiobookshelf and the other tools in my setup, but I was still the one who had to remember how everything connected. The book was in one app, its audiobook in another, the film adaptation somewhere else and the soundtrack somewhere else again. I wanted to read a few chapters at home and pick up the audiobook in the car, the way Kindle and Audible do for the books they sell, but for media I owned myself.

That grew into a bigger idea: a library that understands the story, not just the files. The name comes from **túvima**, a word in Tolkien's Quenya recorded as meaning "discoverable". Tuvima Library is the library I wanted for myself, built in the hope that it becomes that library for you too.

[Read the full story and where it's heading](https://tuvima.github.io/tuvima_library/product/story/).

## Project status

Tuvima Library is in **Early Access** and under active development. Browsing, reading, playback, editing, personal media, profiles and library management work today. Cross-format position syncing between ebooks and audiobooks, native device apps and richer recommendations are on the way. See [Product Status](https://tuvima.github.io/tuvima_library/product/status/) and the [Beta Roadmap](https://tuvima.github.io/tuvima_library/product/beta-roadmap/) for details.

## Documentation

| I want to… | Start here |
| --- | --- |
| Use Tuvima Library | [Getting started](https://tuvima.github.io/tuvima_library/tutorials/getting-started/) · [Find your way around](https://tuvima.github.io/tuvima_library/guides/dashboard-tour/) |
| Run a server | [Install with Docker](https://tuvima.github.io/tuvima_library/install/docker/) · [Library settings](https://tuvima.github.io/tuvima_library/guides/library-settings/) · [Troubleshooting](https://tuvima.github.io/tuvima_library/guides/troubleshooting/) |
| Build plugins or providers | [Build a plugin](https://tuvima.github.io/tuvima_library/guides/building-a-plugin/) · [Add a provider](https://tuvima.github.io/tuvima_library/guides/adding-a-provider/) |
| Contribute code or docs | [Contributing](CONTRIBUTING.md) · [Developer setup](https://tuvima.github.io/tuvima_library/tutorials/dev-setup/) · [Technical overview](https://tuvima.github.io/tuvima_library/architecture/technical-overview/) |

## Contributing

Bug reports, documentation fixes, metadata providers, plugins and code are all welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Please report vulnerabilities privately, as described in [SECURITY.md](SECURITY.md). Don't post security details in public issues.

## Community

Use [Issues](https://github.com/Tuvima/tuvima_library/issues) for questions, ideas and bug reports.

## License

Tuvima Library is free and open-source software under the [GNU AGPLv3](LICENSE). Anyone can run, study, improve and share it, and anyone who offers a modified version over a network must share their changes too. See [what the license means for you](https://tuvima.github.io/tuvima_library/product/license/) and the [third-party notices](THIRD-PARTY-NOTICES.md).

---

<div align="center">

**You already own the stories. Tuvima Library makes them easier to find, understand and enjoy.**

</div>
