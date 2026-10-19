---
title: "Understand privacy and network access"
description: "Learn what stays on your server and when providers, models, plugins, maps, and remote access use the network."
audience: user
category: explanation
product_area: privacy
status: current
---

# Understand privacy and network access

Understand Tuvima Library's storage and network boundaries in about three minutes. Your library runs on your own host, while some optional services and the Places map can contact external systems.

## Keep your library on your host

Your media files and SQLite database stay in configured local storage. Profiles, progress, settings, and review state are stored there too. Managed artwork, thumbnails, caches, staging data, and generated metadata use configured local paths.

The [Engine](../reference/glossary.md#engine) and Dashboard run on that host. Local AI uses its CPU or GPU. There is no Tuvima-hosted account service or built-in telemetry pipeline.

Photos stay with the person who took them. Everyone in a household can browse one another's photos, and the household has one Shared Library. A server administrator can also open other households' photos through **Other people** in View, read-only. That is deliberate and visible: each time an administrator opens someone's space or a household's Shared Library, Tuvima records who looked and what they opened (at most once an hour per space), and the person whose photos were opened can see that record, read-only, under **Settings > Security > Who viewed your photos** (the household's own administrator, the household owner, also sees views of the household's Shared Library; a server administrator who does not look after that household does not). Nobody else can read it, and nobody can delete or clear it from there. People outside your household start with View switched off until an administrator turns it on for them.

This describes where Tuvima stores data. Your own network shares, backup destinations, reverse proxy, and installed plugins have their own access and privacy implications.

## Know when external requests happen

Enabled metadata providers can request identifiers, descriptions, artwork, lyrics, subtitles, and relationship data. Examples include Apple, MusicBrainz, TMDB, TheTVDB, Comic Vine, LRCLIB, SubDL, Wikidata, and Wikimedia Commons.

Provider behavior depends on configuration, credentials, media type, and processing state. Model downloads retrieve local model files from external hosts when you ask; subsequent supported inference runs locally. [Everything that can be downloaded on request](#know-what-can-be-downloaded-when-you-ask) is listed below.

Refreshing the approved plugin catalog contacts GitHub. Installed plugins can use external services according to their implementation and declared permissions. Only install code you trust.

## Know how scanned documents are read

Scanned pages and photos of paper are read by Tesseract, a program that runs on your own host. The text stays in your data store and is private to the owner's space. Nothing is uploaded.

Text recognition uses the network in exactly one case: when you choose an extra language under **Settings > Libraries > View > Documents** and press **Download**. That downloads the language file from GitHub. English is included with Tuvima Library and needs no download.

The words of every document, whether read from the file or recognised from a scan, are used only for search on the Documents page. They never appear on Home, in the global search or in recommendations. The PDF viewer is bundled with the Dashboard and loads no outside files. Inbox suggestions and auto-filing rules run on your host without AI and send nothing anywhere, and the CSV export holds names and labels, never the words inside documents or your notes. See [Documents in View](documents-in-view.md).

## Know what can be downloaded when you ask

Tuvima never downloads a helper tool or a model by itself. The only downloads are the ones below, and each starts when an administrator presses a button under **Settings > Tools and requirements** (or **Install for me** on the banner that appears when a Required tool is missing). Pressing **Not now** on the banner does not download anything and the banner then stops asking about that tool.

| What | Where from | When |
|---|---|---|
| FFmpeg and FFprobe (one pinned Windows build) | `github.com` (the BtbN/FFmpeg-Builds release), which hands the file on from GitHub's own release storage | **Install for me** or **Download again**, for a run from source or a repair; the Windows installer and the container image already include it |
| Tesseract's Windows setup program, plus English and page-orientation language data | `github.com` (UB-Mannheim/tesseract and tesseract-ocr/tessdata_fast) | **Install for me** on a run from source. You then run the setup program yourself and Windows asks for your approval |
| Extra text recognition languages | `github.com` (tesseract-ocr/tessdata_fast) | Choosing a language under **Settings > Libraries > View > Documents** |
| Local AI model files | The model host named for each model on the Local AI pages | **Download** on the Tools and requirements page |

The address and checksum of every FFmpeg and Tesseract download (the setup program and its English and page-orientation data) are written in `config/tool_downloads.json`. Extra text recognition languages use `language_download_url` in `config/ocr.json`, and each Local AI model uses the address and checksum in `config/ai.json`. For FFmpeg and Tesseract the Library only accepts fixed `https` addresses on `github.com`, checks each download (and each FFmpeg program inside its archive) against the recorded SHA-256 value, and throws away anything that does not match. Each attempt, including a refusal or a cancel, is written to the system activity log.

GitHub hands release and raw files on to its own storage, so a download from `github.com` may be redirected. The Library follows a redirect only over `https`, only to `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` or `raw.githubusercontent.com`, and for at most five hops; a redirect anywhere else is refused and nothing is saved.

**No library data is sent.** A download is a plain request for one file. The only thing the host can learn from it is that someone fetched that file from that internet address; no titles, file names, folders, profile or account details leave your host. Containers never download tools into themselves.

## Understand Places map requests

**View > Places** opens Tuvima Atlas. It first attempts OpenFreeMap's dark map style, which can load external map resources. If that attempt fails, the map falls back to locally served country data.

Opening Places can therefore make external requests even though the media and place aggregates come from your authorized library. Do not describe this surface as always offline or free of third-party requests.

## Use local AI with clear expectations

Local model files run through LLamaSharp and Whisper.net rather than a cloud prompt endpoint. Model-download URLs retrieve artifacts; they do not send prompts for remote inference.

AI can assist with classification, matching, descriptions, search intent, and supported audio tasks when the relevant feature is enabled and ready. It does not replace the Priority Cascade's factual metadata rules.

Evaluation fixtures, outputs, reports, and models stay on the Engine host unless you deliberately export them. Gated models need deliberate license acceptance and a verified artifact. A source URL is provenance, rather than proof of an installed runtime.

## Protect credentials and remote access

Keep provider keys in ignored secret files, rather than committing them to the repository. Missing credentials should remain visible as a configuration problem, not as a successful connection.

Tuvima is open from the start: any device that can reach the server can use it, and Tuvima sign-in is always required. Closed router ports are what keep the internet out, and a desktop install started without a password can be used from any device that reaches the server until its administrator adds a password or passkey. The Dashboard serves plain http, so for access from outside use a verified Tailscale Serve or HTTPS reverse-proxy path. Advanced router mapping is opt-in. [Remote access guidance](../guides/remote-access.md) explains the deployment checks.

## Plan an offline session

Browsing already indexed media and using ready local models can avoid provider lookups. For a restricted-network deployment, account for metadata providers, downloads, catalog refreshes, plugins, and the Places map.

Disabling metadata providers alone does not prove the entire Dashboard makes no external requests. Network policy and the surfaces you open determine that boundary.

<details>
<summary>Technical details</summary>

Long-lived provider credentials belong under `config/secrets/`. Base provider definitions remain under `config/providers/`; a blank base file does not establish that secrets are absent.

The Places client attempts `https://tiles.openfreemap.org/styles/dark`. Its local fallback uses `/maps/world-countries.geojson`. Local media authorization is separate from the map's network source selection.

AI evaluation and hardware benchmarking follow explicit opt-in controls. Ordinary recorded-fixture tests do not need to load model weights. See [security architecture](../architecture/security.md) for authentication and server boundaries.

</details>

## Next steps

- [Configure providers](../guides/configuring-providers.md)
- [Understand local AI readiness](how-ai-works.md)
- [Use View and Shared Library](../guides/view-personal-space.md)
- [Manage plugins](../guides/using-plugins.md)
