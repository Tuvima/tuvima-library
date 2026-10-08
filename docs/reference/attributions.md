---
title: "Attributions"
description: "Acknowledgements for major open-source libraries, public knowledge sources, metadata providers, and media tooling used by Tuvima Library."
audience: "user"
category: "reference"
product_area: "attributions"
tags:
  - "attributions"
  - "licenses"
  - "dependencies"
status: current
---

# Attributions

## In this page

Identify the major software, fonts, and knowledge sources used by Tuvima Library and its documentation. Dependency manifests and retained license notices provide the exact version and license records.

## Where this lives in the code

- `Directory.Packages.props`
- `THIRD-PARTY-NOTICES.md`
- `website/package-lock.json`
- `website/LICENSES.md`

Tuvima Library is built on open-source software, public knowledge projects, and optional metadata providers. This page is a practical acknowledgement list, not a substitute for each dependency's license file.

The authoritative package version list is `Directory.Packages.props`; the project license is AGPLv3.

## Core Platform

| Project | Role |
|---|---|
| .NET, ASP.NET Core, Blazor Server | Runtime, API host, and Dashboard framework |
| First-party `App*` components | Dashboard controls maintained in this repository |
| SignalR | Live Engine-to-Dashboard updates |
| SQLite, SQLitePCLRaw, and Microsoft.Data.Sqlite | Local database |
| Dapper | Data access |
| Serilog | Structured logging |
| Polly / Microsoft.Extensions.Http.Resilience | Resilient outbound HTTP calls |
| Swashbuckle | Swagger/OpenAPI UI |
| Cronos | Cron schedule parsing |
| xUnit, bUnit, coverlet | Automated tests |
| Astro and Starlight | GitHub Pages documentation site; exact versions and licenses below |

## Media Processing

| Project | Role |
|---|---|
| FFmpeg | Media probing, extraction, direct-play inspection, and adaptive HLS generation. Windows installers use the checksum-pinned BtbN GPLv3 build recorded in `tools/ffmpeg/README.md`; its license is shipped with the installer. |
| Xabe.FFmpeg | .NET wrapper around FFmpeg operations |
| MediaInfo.Wrapper | Stream-level codec, track, and chapter inspection |
| TagLibSharp | Audio and video tag reading/writing |
| VersOne.Epub | EPUB metadata and content reading |
| SkiaSharp | Image processing, thumbnailing, and generated artwork support |
| SharpCompress | Archive reading for comic formats such as CBZ/CBR |

## Local AI

| Project | Role |
|---|---|
| LLamaSharp | Local LLM inference through llama.cpp bindings |
| Whisper.net | Local speech-to-text and language detection through whisper.cpp bindings |
| llama.cpp and whisper.cpp ecosystems | Underlying local model execution projects used by the .NET bindings |

Model weights are downloaded separately according to configured model URLs and their own license terms.

## Public Knowledge and Metadata Sources

| Source | Role |
|---|---|
| Wikidata | Canonical identity, structured facts, bridge identifiers, and relationship data |
| Wikipedia | Human-readable summaries and contextual article content where available |
| Wikimedia Commons | Public media assets such as images when linked through Wikimedia data |
| Tuvima.Wikidata | Tuvima's .NET integration library for Wikidata/Wikipedia reconciliation and graph behavior |
| MusicBrainz | Music metadata and identifiers |
| TMDB | Movie and TV metadata, identifiers, images, and ratings where configured |
| Apple APIs | Book, audiobook, and music metadata where configured |
| Comic Vine identifiers | Comics metadata and bridge identifiers where configured |
| LRCLIB | Lyrics where configured |
| SubDL | Subtitle lookup where configured |
| OpenSubtitles | Historical source of previously downloaded subtitle tracks; no longer queried |

Some providers require credentials or API keys. Provider trademarks and data remain owned by their respective organizations.

## Design and Documentation Assets

Tuvima's own logos, documentation styling, and product copy are maintained in this repository unless otherwise noted. Documentation screenshots are deferred. The documentation site stages its canonical Markdown and MDX from `docs/` and publishes through GitHub Pages.

| Documentation dependency | Locked version | License |
|---|---|---|
| Astro | 7.3.8 | MIT |
| Starlight (`@astrojs/starlight`) | 0.42.6 | MIT |
| Starlight Sidebar Topics | 0.9.0 | MIT |
| Pagefind | 1.5.2 | MIT |
| Expressive Code | 0.44.2 | MIT |
| Shiki | 4.5.0 | MIT |
| Montserrat | Locally bundled font assets | SIL Open Font License 1.1 |
| JetBrains Mono | Locally bundled font assets | SIL Open Font License 1.1 |

These records cover the shipped documentation stack; no image-zoom plugin is used. See [site licenses](../../website/LICENSES.md) for retained notices, [the package lock](../../website/package-lock.json) for the complete dependency graph, and [site maintenance](../../website/README.md) for verification commands.

## Related

- [Providers Reference](providers.md)
- [Configuration Reference](configuration.md)
- [Privacy and Local-First Behavior](../explanation/privacy-local-first.md)
