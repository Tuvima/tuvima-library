<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/images/tuvima-logo-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="assets/images/tuvima-logo.svg">
  <img src="assets/images/tuvima-logo.svg" alt="Tuvima Library" height="90" />
</picture>

**One library. Every story.**

Your books, films, shows, music, audiobooks, comics, and personal media—together in one library you control.

[AGPLv3](LICENSE) · [Docker setup](https://tuvima.github.io/tuvima_library/install/docker/) · [Documentation](https://tuvima.github.io/tuvima_library/) · [Early Access](https://tuvima.github.io/tuvima_library/product/status/)

**[Install](https://tuvima.github.io/tuvima_library/tutorials/getting-started/)** · **[Documentation](https://tuvima.github.io/tuvima_library/)** · **[Product Status](https://tuvima.github.io/tuvima_library/product/status/)** · **[Issues](https://github.com/Tuvima/tuvima_library/issues)**

</div>

## What is Tuvima Library?

Your media may be spread across folders, drives and formats. Tuvima Library identifies the files you own, adds metadata and artwork, and brings related works together. You can explore a story through its books, screen adaptations, music, creators and series without keeping those connections in your head.

The Engine builds the local catalog. The Dashboard gives you a place to browse, search, read, watch and listen. Personal files have their own View space and do not need a retail identity to belong.

## Highlights

- **Every format in one library.** Browse books, comics, films, TV, music and audiobooks through Read, Watch and Listen.
- **Universes connect the story.** Trusted relationships connect owned works, series, adaptations and people across formats.
- **Reading and playback built in.** Open a book or start audio and video from its detail page, with progress where supported.
- **A Personal Space for your own media.** View organizes photos, short videos, documents and other local files separately from catalog matching.
- **Identification with a human fallback.** The Engine reads file metadata and uses configured providers; uncertain items go to the Review Queue.
- **Local control.** Your catalog and media stay on your machine. Network-backed features have separate privacy considerations.

## Quick start

Docker Compose is the maintained container setup. Anonymous access to its configured registry image could not be confirmed on October 8, 2026 (the registry required authentication). Check the [image access and local-build options](https://tuvima.github.io/tuvima_library/install/docker/#image-availability) first, or [run from source](https://tuvima.github.io/tuvima_library/install/from-source/).

If you have access to the configured image, install Docker with Compose and download the maintained configuration:

~~~sh
curl -fsSL https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docker-compose.yml -o docker-compose.yml
~~~

Open that file and change its host folder paths before starting. Keep the mounts for your library, configuration, database, models, artwork/cache, backups and transcode workspace. Set the user/group IDs to match the folders on your host, and choose your timezone.

~~~sh
docker compose up -d
~~~

Open **http://YOUR-SERVER:5016**. The Engine's internal port is not published by the standard Compose setup. Follow first-run setup, save your recovery codes, and add media folders when you are ready.

Image pulls and optional model downloads depend on your connection and hardware. See the [Docker guide](https://tuvima.github.io/tuvima_library/install/docker/) for storage, permissions, updates and troubleshooting.

- **Windows installer:** no installer release is published yet. See [Windows installation status](https://tuvima.github.io/tuvima_library/install/windows/) before choosing this route.
- **NAS:** [Unraid](https://tuvima.github.io/tuvima_library/install/unraid/) · [Synology](https://tuvima.github.io/tuvima_library/install/synology/) · [QNAP](https://tuvima.github.io/tuvima_library/install/qnap/) · [TrueNAS SCALE](https://tuvima.github.io/tuvima_library/install/truenas-scale/).
- **From source:** use the [.NET setup guide](https://tuvima.github.io/tuvima_library/install/from-source/).

## Find your way around

| Area | What you can do |
| --- | --- |
| Home | Discover the library, continue across media and find recent additions. |
| For Me | Open your saved items, Favorites, progress and personal collections, playlists and galleries. |
| Read, Watch, Listen | Browse a media lane and open an item to read or play. |
| View | Browse your personal files, folders, galleries, named people and available locations. |
| Collections | Explore trusted automatic groups or collections someone has created. |
| Search | Search across media, people and groups without losing your current page. |
| Details and editing | Learn about one item and correct its metadata in context. |
| Review and administration | Resolve uncertain matches or manage the server, according to your access. |

For a guided introduction, read the [Dashboard tour](https://tuvima.github.io/tuvima_library/guides/dashboard-tour/).

## How Tuvima Library compares

Different tools can serve different parts of a collection. These categories explain Tuvima Library's focus; they are not a feature-by-feature compatibility promise.

| Kind of tool | Examples | Where Tuvima Library fits |
| --- | --- | --- |
| Media servers | [Plex](https://www.plex.tv/), [Jellyfin](https://jellyfin.org/), [Emby](https://emby.media/) | Connect screen media with reading, audio and the wider creative world. |
| Media centers | [Kodi](https://kodi.tv/about/) | Maintain a shared catalog of owned works and relationships behind the browsing experience. |
| Reading libraries | [calibre](https://calibre-ebook.com/about), [Kavita](https://www.kavitareader.com/), [Komga](https://komga.org/) | Place reading alongside adaptations, audiobooks and music. |
| Audio libraries | [Audiobookshelf](https://www.audiobookshelf.org/), [Navidrome](https://www.navidrome.org/) | Connect recordings to their works, creators and other owned formats. |
| Acquisition automation | [Sonarr](https://sonarr.tv/), [Radarr](https://radarr.video/), [Lidarr](https://lidarr.audio/) | Begin with files already available to you and organize their identity and relationships. |

These tools do not always need to be replaced. Automation tools can prepare files that Tuvima watches, while specialist players may remain useful on particular devices.

Tuvima Library's purpose is to make the connections between owned works part of the library itself. Matching narrative positions between a book and an audiobook remains a future goal, not a current playback promise.

## Built around ownership and privacy

- Your media, catalog, managed artwork and optional AI inference live on your host.
- No Tuvima-hosted account or subscription is required; local sign-in protects your installation.
- The product has no built-in telemetry pipeline.
- Configured metadata, artwork, lyrics, subtitle and model services can make external requests.
- View's Places map can request external map styling/tiles and has a local fallback. Local-first does not mean every screen is network-free.
- Low-confidence matches remain visible for human review.
- The project is free and open source, with no premium feature tier.

Read [Privacy and Local-First Behavior](https://tuvima.github.io/tuvima_library/explanation/privacy-local-first/) before choosing network-backed features.

## Powered by open knowledge

[Wikidata](https://www.wikidata.org/wiki/Wikidata:Introduction) supplies structured identities and relationships. [Wikipedia](https://en.wikipedia.org/wiki/Wikipedia:About) supplies readable context. Tuvima connects these sources to trusted media identities while preserving attribution and distinguishing outside knowledge from files you own.

These projects are maintained by communities. You can [contribute to Wikipedia](https://en.wikipedia.org/wiki/Wikipedia:Contributing_to_Wikipedia), [participate in Wikidata](https://www.wikidata.org/wiki/Wikidata:Contribute), or [support Wikimedia](https://donate.wikimedia.org/).

## Why I built Tuvima Library

I enjoyed Plex, Audiobookshelf and other media tools, but I still had to remember how the book, audiobook, adaptation and soundtrack connected. Whispersync suggested the kind of continuity I wanted for media I owned myself.

That grew into a wider idea: a library that understands a story, not just its files. The name comes from **túvima**, a Quenya word recorded as “discoverable.” Tuvima Library is the library I wanted for myself, built in the hope that it can become that library for others too.

[Read the full story and longer-term vision](https://tuvima.github.io/tuvima_library/product/story/).

## Project status

Tuvima Library is Early Access and under active development. The Engine and Dashboard are usable, while some experiences and deployment paths still have limitations. Check [Product Status](https://tuvima.github.io/tuvima_library/product/status/) and the [Beta Roadmap](https://tuvima.github.io/tuvima_library/product/beta-roadmap/) before relying on a particular feature.

## Documentation

| Your goal | Start here |
| --- | --- |
| Use Tuvima | [Getting started](https://tuvima.github.io/tuvima_library/tutorials/getting-started/), [Dashboard tour](https://tuvima.github.io/tuvima_library/guides/dashboard-tour/) |
| Run a server | [Install](https://tuvima.github.io/tuvima_library/install/docker/), [Library settings](https://tuvima.github.io/tuvima_library/guides/library-settings/), [Troubleshooting](https://tuvima.github.io/tuvima_library/guides/troubleshooting/) |
| Build plugins or providers | [Build a plugin](https://tuvima.github.io/tuvima_library/guides/building-a-plugin/), [Add a provider](https://tuvima.github.io/tuvima_library/guides/adding-a-provider/) |
| Contribute | [Contributing](CONTRIBUTING.md), [Developer setup](https://tuvima.github.io/tuvima_library/tutorials/dev-setup/), [Technical overview](https://tuvima.github.io/tuvima_library/architecture/technical-overview/) |

## Contributing

Bugs, documentation fixes, provider integrations, plugins and code contributions are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Read [SECURITY.md](SECURITY.md) before reporting a possible vulnerability. Keep sensitive details out of public issues.

## Community

Use [Issues](https://github.com/Tuvima/tuvima_library/issues) for questions, ideas and reproducible non-security bugs.

## License

Tuvima Library is licensed under the [GNU AGPLv3](LICENSE). Its source remains available so people can inspect, modify and share the software under those terms. See [license guidance](https://tuvima.github.io/tuvima_library/product/license/) and [third-party notices](THIRD-PARTY-NOTICES.md).

<div align="center">

**One library. Every story.**

</div>
